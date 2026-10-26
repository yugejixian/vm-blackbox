// Host side of the reverse channel: accepts the guest's outbound connection,
// forwards HTTP calls from local tooling, and saves screenshots to disk.
//
// Screenshot path: the guest agent's GDI capture cannot see GPU-composited (3D) windows,
// so /shot first tries the guest and falls back to the VirtualBox framebuffer capture,
// which is then decorated with the same coordinate ruler by RulerOverlay.exe.
const net = require('net');
const crypto = require('crypto');
const http = require('http');
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const TCP_PORT = 8801;
const HTTP_PORT = 8890;
const os = require('os');
const SHOTS = process.env.QA_SHOTS_DIR || path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'dsh-vm-tester', 'shots');
const AGENT_DIR = process.env.QA_AGENT_DIR || __dirname;
// vault-get.ps1 可能在 agent/（自包含包）或 ../scripts/（仓库布局）——两处都找
const vaultScriptCandidates = [path.join(AGENT_DIR, 'vault-get.ps1'), path.join(AGENT_DIR, '..', 'scripts', 'vault-get.ps1')];
const VAULT_GET = vaultScriptCandidates.find((f) => { try { return fs.existsSync(f); } catch (e) { return false; } }) || vaultScriptCandidates[0];
const PS = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');

const VBOX = process.env.QA_VBOXMANAGE || 'C:\\Program Files\\Oracle\\VirtualBox\\VBoxManage.exe';
const VM = process.env.QA_VM_NAME || 'qa-win11-zh';
const OVERLAY = path.join(AGENT_DIR, 'RulerOverlay.exe');

let sock = null;
let buffer = Buffer.alloc(0);
let nextId = 1;
let lastClick = null; // {x, y} of the most recent click, drawn as a crosshair
const pending = new Map();

function cleanupTempShots() {
  try {
    const now = Date.now();
    for (const f of fs.readdirSync(SHOTS)) {
      if (!/^(gc|gco|raw)-/.test(f)) continue;
      const fp = path.join(SHOTS, f);
      try { if (now - fs.statSync(fp).mtimeMs > 10 * 60 * 1000) fs.unlinkSync(fp); } catch (e) { }
    }
  } catch (e) { }
}
cleanupTempShots();

function log(m) { console.log(new Date().toISOString().slice(11, 19), m); }

function processBuffer() {
  for (;;) {
    const idx = buffer.indexOf(10);
    if (idx < 0) return;
    const line = buffer.slice(0, idx).toString('utf8').replace(/\r$/, '');
    const m = line.match(/^(\d+) (OK|ERR) ?(.*)$/);
    if (!m) { buffer = buffer.slice(idx + 1); continue; }
    const id = Number(m[1]);
    const kind = m[2];
    const rest = m[3];
    const p = pending.get(id);
    if (kind === 'ERR') {
      buffer = buffer.slice(idx + 1);
      pending.delete(id);
      if (p) p.reject(new Error(rest));
      continue;
    }
    if (p && p.op === 'SHOT') {
      const len = parseInt(rest, 10);
      if (!Number.isFinite(len)) { buffer = buffer.slice(idx + 1); pending.delete(id); if (p) p.reject(new Error('bad len')); continue; }
      if (buffer.length < idx + 1 + len) return;
      const payload = buffer.slice(idx + 1, idx + 1 + len);
      buffer = buffer.slice(idx + 1 + len);
      pending.delete(id);
      p.resolve(payload);
      continue;
    }
    buffer = buffer.slice(idx + 1);
    pending.delete(id);
    if (p) p.resolve(rest);
  }
}

function call(op, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    if (!sock) return reject(new Error('no guest connection'));
    const id = nextId++;
    pending.set(id, { op, resolve, reject });
    sock.write([id, op].concat(args || []).join(' ') + '\n');
    setTimeout(() => {
      if (pending.has(id)) { pending.delete(id); reject(new Error('timeout:' + op)); }
    }, timeoutMs || 20000);
  });
}

function run(cmd, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    let done = false;
    const p = spawn(cmd, args, { windowsHide: true });
    const t = setTimeout(() => {
      if (done) return;
      done = true;
      try { p.kill(); } catch (e) { }
      reject(new Error('timeout:' + path.basename(cmd)));
    }, timeoutMs);
    p.on('error', (e) => { if (!done) { done = true; clearTimeout(t); reject(e); } });
    p.on('exit', (code) => {
      if (done) return;
      done = true;
      clearTimeout(t);
      if (code === 0) resolve(); else reject(new Error(path.basename(cmd) + ' exit ' + code));
    });
  });
}

let shotSeq = 0;
// 敏感区域（来自 /type-secret）：宿主机抓帧也要遮蔽，否则会绕过代理侧遮罩。
let activeMasks = [];
function pushMask(m) {
  if (!m || !Number.isFinite(m.x) || !Number.isFinite(m.y) || !Number.isFinite(m.w) || !Number.isFinite(m.h)) return;
  activeMasks.push({ x: m.x, y: m.y, w: m.w, h: m.h, until: Date.now() + 180000 });
}
function liveMaskTokens() {
  const now = Date.now();
  activeMasks = activeMasks.filter((m) => m.until > now);
  return activeMasks.map((m) => m.x + ',' + m.y + ',' + m.w + ',' + m.h);
}
// 陈旧帧检测状态：访客 GDI 抓不到 D3D/3D 画面时会返回上一帧，这里自动改走宿主帧缓冲。
let lastGuestHash = null;
let lastHostHash = null;
let staleUntil = 0;
let lastProbeAt = 0; // 宿主帧探测节流（静止桌面不该每帧都探测）
let hostProbeBlockedUntil = 0; // 探测失败后的退避期
const sha1 = (b) => crypto.createHash('sha1').update(b).digest('hex');

// 跑一条命令并拿到 stdout（凭据解密用；绝不打日志）
function runCapture(cmd, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    const p = spawn(cmd, args, { windowsHide: true });
    const outChunks = []; let err = '', done = false;
    const t = setTimeout(() => { if (done) return; done = true; try { p.kill(); } catch (e) { } reject(new Error('timeout:' + path.basename(cmd))); }, timeoutMs);
    p.stdout.on('data', (d) => { outChunks.push(d); });
    p.stderr.on('data', (d) => { err += d.toString('utf8'); });
    p.on('error', (e) => { if (!done) { done = true; clearTimeout(t); reject(e); } });
    p.on('exit', (code) => {
      if (done) return;
      done = true; clearTimeout(t);
      if (code === 0) resolve(Buffer.concat(outChunks).toString('utf8')); else reject(new Error('exit ' + code + ' ' + err.slice(0, 200)));
    });
  });
}

async function guestCursor() {
  try {
    const j = await call('CURSOR', [], 1200);
    const o = JSON.parse(j);
    if (Number.isFinite(o.x) && Number.isFinite(o.y)) return { x: o.x, y: o.y };
  } catch (e) { }
  return { x: -1, y: -1 };
}

// 抓屏不含系统光标（光标由宿主绘制），这里把代理报告的指针位置画成合成箭头。
async function overlayCursor(buf, quality, maskTokens) {
  const stamp = Date.now() + '-' + (shotSeq++);
  const ip = path.join(SHOTS, 'gc-' + stamp + '.jpg');
  const op = path.join(SHOTS, 'gco-' + stamp + '.jpg');
  try {
    fs.mkdirSync(SHOTS, { recursive: true });
    fs.writeFileSync(ip, buf);
    const c = await guestCursor();
    const maskArgs = (maskTokens || []).map((m) => 'mask=' + m);
    await run(OVERLAY, [ip, op, '-1', '-1', String(quality), String(c.x), String(c.y), 'noruler', ...maskArgs], 15000);
    return fs.readFileSync(op);
  } catch (e) {
    return buf;
  } finally {
    // 失败也必须删，否则抓帧失败会疯狂堆积临时文件
    try { fs.unlinkSync(ip); } catch (e) { }
    try { fs.unlinkSync(op); } catch (e) { }
  }
}
async function currentMaskTokens() {
  try {
    const r = await call('MASKS', [], 800);
    const o = JSON.parse(r);
    if (Array.isArray(o.masks)) return o.masks.map((m) => m.join(','));
  } catch (e) { }
  return liveMaskTokens();
}

async function hostShot(quality, persist, maskTokens) {
  fs.mkdirSync(SHOTS, { recursive: true });
  const stamp = Date.now() + '-' + (shotSeq++);
  const raw = path.join(SHOTS, 'raw-' + stamp + '.png');
  const out = path.join(SHOTS, 'shot-' + stamp + '.jpg');
  try {
    await run(VBOX, ['controlvm', VM, 'screenshotpng', raw], 5000);
    const cx = lastClick ? lastClick.x : -1;
    const cy = lastClick ? lastClick.y : -1;
    const cur = await guestCursor();
    const maskArgs = (maskTokens || []).map((m) => 'mask=' + m);
    await run(OVERLAY, [raw, out, String(cx), String(cy), String(quality), String(cur.x), String(cur.y), ...maskArgs], 5000);
    const buf = fs.readFileSync(out);
    return persist ? { buf, file: out } : { buf, file: null };
  } finally {
    try { fs.unlinkSync(raw); } catch (e) { }
    if (!persist) { try { fs.unlinkSync(out); } catch (e) { } }
  }
}
net.createServer((s) => {
  sock = s;
  buffer = Buffer.alloc(0);
  s.setNoDelay(true);
  // 访客异常断连（ECONNRESET）若无人监听 'error'，Node 会直接退出。
  s.on('error', (e) => { log('guest socket error: ' + (e && e.code ? e.code : e)); });
  s.on('data', (d) => { try { buffer = Buffer.concat([buffer, d]); processBuffer(); } catch (e) { log('buffer error: ' + e.message); } });
  s.on('close', () => {
    log('guest disconnected');
    sock = null;
    for (const [, p] of pending) p.reject(new Error('guest disconnected'));
    pending.clear();
  });
  log('guest connected from ' + s.remoteAddress);
}).listen(TCP_PORT, '0.0.0.0', () => log('tcp listening ' + TCP_PORT));

http.createServer(async (req, res) => {
  const u = new URL(req.url, 'http://x');
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Headers', '*');
  if (req.method === 'OPTIONS') { res.statusCode = 204; res.end(); return; }
  try {
    switch (u.pathname) {
      case '/masks/clear': {
        try {
          activeMasks.length = 0;
          staleUntil = 0;
          await call('CLEARMASKS', []);
          res.setHeader('Content-Type', 'application/json');
          res.end('{"cleared":true}');
        } catch (e) { res.statusCode = 500; res.end(String(e.message)); }
        return;
      }
      case '/status':
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ connected: !!sock, pending: pending.size }));
        return;
      case '/info': {
        const r = await call('INFO', []);
        res.setHeader('Content-Type', 'application/json');
        res.end(r);
        return;
      }
      case '/viewer': {
        const html = `<!doctype html><html lang="zh"><head><meta charset="utf-8">
<title>虚拟机实时画面</title>
<style>
  html,body{margin:0;height:100%;background:#111;color:#ddd;font:13px/1.4 system-ui,"Microsoft YaHei",sans-serif}
  header{display:flex;align-items:center;gap:10px;padding:8px 12px;background:#1b1b1b;border-bottom:1px solid #333}
  #dot{width:9px;height:9px;border-radius:5px;background:#c0392b}
  #wrap{position:absolute;top:41px;left:0;right:0;bottom:0;display:flex;align-items:center;justify-content:center;overflow:hidden}
  #view{max-width:100%;max-height:100%;image-rendering:auto;background:#000}
  a{color:#7cb7ff}
  .mono{font-family:Consolas,monospace;opacity:.7}
</style></head><body>
<header>
  <span id="dot"></span><b>虚拟机实时画面</b>
  <span class="mono" id="st">连接中…</span>
  <span style="flex:1"></span>
  <span class="mono" id="ts"></span>
  <a href="/live" target="_blank">原始流</a>
</header>
<div id="wrap"><img id="view" src="/live" alt="vm"></div>
<script>
  const dot = document.getElementById('dot');
  const st = document.getElementById('st');
  const ts = document.getElementById('ts');
  const img = document.getElementById('view');
  async function tick(){
    try{
      const r = await fetch('/status',{cache:'no-store'});
      const j = await r.json();
      dot.style.background = j.connected ? '#2ecc71' : '#d8a12a';
      st.textContent = j.connected ? '访客已连接' : '访客离线';
    }catch(e){ dot.style.background='#c0392b'; st.textContent='隧道不可用'; }
    ts.textContent = new Date().toLocaleTimeString();
  }
  tick(); setInterval(tick, 2000);
  img.onerror = () => { setTimeout(() => { img.src = '/live?t=' + Date.now(); }, 1500); };
</script></body></html>`;
        res.setHeader('Content-Type', 'text/html; charset=utf-8');
        res.end(html);
        return;
      }
      case '/live': {
        // MJPEG live view: keep sending fresh frames until the client disconnects.
        res.writeHead(200, {
          'Content-Type': 'multipart/x-mixed-replace; boundary=frame',
          'Cache-Control': 'no-store, no-cache, must-revalidate',
          Connection: 'close',
        });
        let closed = false;
        req.on('close', () => { closed = true; });
        (async () => {
          while (!closed) {
            try {
              const r = await hostShot(55, false);
              if (closed) break;
              res.write('--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ' + r.buf.length + '\r\n\r\n');
              res.write(r.buf);
              res.write('\r\n');
            } catch (e) {
              if (closed) break;
              await new Promise((r2) => setTimeout(r2, 1000));
            }
            await new Promise((r2) => setTimeout(r2, 600));
          }
          try { res.end(); } catch (e) { }
        })();
        return;
      }
      case '/exit': {
        try { await call('EXIT', [], 3000); } catch (e) { }
        log('sent EXIT to guest');
        res.end('{"ok":true}');
        return;
      }
      case '/shot': {
        const q = parseInt(u.searchParams.get('q') || '80', 10);
        const src = u.searchParams.get('src');
        let buf = null, file = null, via = 'guest';
        const shotMasks = [];
        const maskParam = u.searchParams.get('mask') || '';
        for (const part of maskParam.split(';')) {
          const f = part.split(',').map((v) => parseInt(v, 10));
          if (f.length === 4 && f.every((v) => Number.isFinite(v)) && f[2] > 0 && f[3] > 0) shotMasks.push(...f.map(String));
        }
        const maskTokens = shotMasks.length
          ? shotMasks.reduce((acc, v, i) => { if (i % 4 === 0) acc.push(shotMasks.slice(i, i + 4).join(',')); return acc; }, [])
          : [];
        const activeTokens = liveMaskTokens();
        const allTokens = maskTokens.concat(activeTokens);
        const flatMasks = allTokens.reduce((acc, t) => { acc.push(...t.split(',')); return acc; }, []);
        const shotArgs = [String(q), 'ruler']; // 遮罩由代理按窗口生命周期处理
        if (src !== 'host') {
          try { buf = await call('SHOT', shotArgs, 8000); } catch (e) { buf = null; }
        }
        // ---- 陈旧帧检测 ----
        // 访客 GDI 截不到 3D/D3D 交换链时不会报错，而是返回上一帧桌面。
        // 判据：访客帧与上一张完全相同，但宿主帧缓冲却在变 → 判定为陈旧，改走宿主抓帧（保持 60 秒）。
        if (buf) {
          const gh = sha1(buf);
          const guestRepeated = lastGuestHash && gh === lastGuestHash;
          const inStaleMode = Date.now() < staleUntil;
          const probeAllowed = Date.now() > hostProbeBlockedUntil;
          const shouldProbe = probeAllowed && (inStaleMode || (guestRepeated && Date.now() - lastProbeAt >= 8000));
          if (shouldProbe) {
            lastProbeAt = Date.now();
            try {
              const r = await hostShot(q, false, await currentMaskTokens());
              const hh = sha1(r.buf);
              const hostChanged = !lastHostHash || hh !== lastHostHash;
              lastHostHash = hh;
              if (inStaleMode || hostChanged) {
                if (!inStaleMode) log('guest frame stale while host frame changed -> host capture for 60s');
                staleUntil = Date.now() + 60000;
                buf = r.buf; file = null; via = 'host';
              }
            } catch (e) {
              hostProbeBlockedUntil = Date.now() + 60000; // 失败后退避，别拖慢每一次截图
              if (inStaleMode) staleUntil = 0; // 宿主抓不到就回到访客帧，别死循环
              log('host capture probe failed (backing off 60s): ' + e.message);
            }
          }
          lastGuestHash = gh;
        }
        if (!buf) {
          via = 'host';
          const r = await hostShot(q, false, await currentMaskTokens());
          buf = r.buf;
          file = r.file;
          log('guest capture failed, used host framebuffer');
        }
        if (buf && via === 'guest') buf = await overlayCursor(buf, q, allTokens);
        if (!file && u.searchParams.get('nosave') !== '1') {
          fs.mkdirSync(SHOTS, { recursive: true });
          file = path.join(SHOTS, 'shot-' + Date.now() + '.jpg');
          fs.writeFileSync(file, buf);
        }
        res.setHeader('Content-Type', 'image/jpeg');
        res.setHeader('X-Shot-Path', file);
        res.setHeader('X-Shot-Bytes', String(buf.length));
        res.setHeader('X-Shot-Via', via);
        res.end(buf);
        return;
      }
      case '/click': {
        const x = parseInt(u.searchParams.get('x'), 10);
        const y = parseInt(u.searchParams.get('y'), 10);
        await call('CLICK', [String(x), String(y), u.searchParams.get('dbl') ? '1' : '0']);
        if (Number.isFinite(x) && Number.isFinite(y)) lastClick = { x, y };
        res.end('{"ok":true}');
        return;
      }
      case '/type-secret': {
        const name = u.searchParams.get('name') || '';
        if (!/^[A-Za-z0-9_.-]{1,64}$/.test(name)) { res.statusCode = 400; res.end('{"ok":false,"err":"bad name"}'); return; }
        let secret = '';
        try {
          secret = await runCapture(PS, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', VAULT_GET, '-Name', name], 20000);
        } catch (e) {
          log('vault get failed for ' + name + ': ' + e.message);
          res.statusCode = 404; res.end('{"ok":false,"err":"secret not found"}'); return;
        }
        secret = secret.replace(/\r?\n$/, '');
        if (!secret) { res.statusCode = 404; res.end('{"ok":false,"err":"secret empty"}'); return; }
        const b64 = Buffer.from(secret, 'utf8').toString('base64');
        let masked = false;
        try { const rj = await call('SECRET', [b64], 20000); try { const o = JSON.parse(rj); masked = !!o.masked; if (masked) { if (Array.isArray(o.masks)) { for (const m of o.masks) pushMask({ x: m[0], y: m[1], w: m[2], h: m[3] }); } else pushMask(o); } } catch (e) { } }
        catch (e) { secret = ''; res.statusCode = 500; res.end('{"ok":false,"err":"guest failed"}'); return; }
        secret = ''; // 用完即弃
        res.setHeader('Content-Type', 'application/json');
        res.end('{"ok":true,"masked":' + masked + '}');
        return;
      }
      case '/type': {
        const t = Buffer.from(u.searchParams.get('text') || '', 'utf8').toString('base64');
        await call('TYPE', [t]);
        res.end('{"ok":true}');
        return;
      }
      case '/key':
        await call('KEY', [u.searchParams.get('combo') || '']);
        res.end('{"ok":true}');
        return;
      default:
        res.statusCode = 404;
        res.end('no route');
    }
  } catch (e) {
    res.statusCode = 500;
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ error: String(e.message) }));
  }
}).listen(HTTP_PORT, '127.0.0.1', () => log('http listening ' + HTTP_PORT));
