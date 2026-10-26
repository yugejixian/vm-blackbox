// VM Tester — screen-level tools for a Windows guest reached through the reverse tunnel.
// The VM itself is external infrastructure: this plugin only connects to the tunnel HTTP API.
import { mkdirSync, writeFileSync, readdirSync, statSync, unlinkSync } from 'node:fs'
import { join } from 'node:path'

export const inject = ['tools', 'attachments']

const DEFAULT_TUNNEL = 'http://127.0.0.1:8890'
const DEFAULT_SHOTS = join(process.env.LOCALAPPDATA || process.env.TEMP || '.', 'dsh-vm-tester', 'shots')

function q(value) {
  // searchParams decodes '+' as a space, so keep literal plus signs escaped.
  return encodeURIComponent(String(value)).replace(/\+/g, '%2B')
}

export function apply(ctx, config) {
  const base = String(config?.tunnelUrl ?? DEFAULT_TUNNEL).replace(/\/+$/, '')
  const shotsDir = String(config?.shotsDir ?? DEFAULT_SHOTS)
  // 严格像素模式：只允许「截图 + 键鼠」；禁用无障碍树等额外观测通道
  const strictPixelOnly = Boolean(config?.strictPixelOnly)

  // ---- 自动清理规则：截图目录不无限增长 ----
  // 保留最近 N 小时 或 最新 M 个文件，其余自动删除；每分钟最多检查一次。
  const keepHours = Number(config?.shotsKeepHours ?? 72)
  const keepFiles = Number(config?.shotsKeepFiles ?? 300)
  let lastPrune = 0
  function pruneShots() {
    const now = Date.now()
    if (now - lastPrune < 60000) return
    lastPrune = now
    try {
      const files = readdirSync(shotsDir)
        .filter((f) => /^shot-\d+\.jpg$/.test(f))
        .map((f) => {
          const fp = join(shotsDir, f)
          let t = 0
          try { t = statSync(fp).mtimeMs } catch { return null }
          return { fp, t }
        })
        .filter(Boolean)
        .sort((a, b) => b.t - a.t)
      for (let i = 0; i < files.length; i++) {
        if (i >= keepFiles || now - files[i].t > keepHours * 3600000) {
          try { unlinkSync(files[i].fp) } catch { }
        }
      }
    } catch { }
  }

  async function request(path) {
    let res
    try {
      res = await fetch(base + path, { signal: AbortSignal.timeout(30000) })
    } catch (error) {
      throw new Error(`VM tunnel unreachable at ${base}: ${error instanceof Error ? error.message : String(error)}`)
    }
    if (!res.ok) {
      const detail = await res.text().catch(() => '')
      throw new Error(`VM tunnel ${path} failed: HTTP ${res.status} ${detail}`.trim())
    }
    return res
  }

  async function requestJson(path) {
    const res = await request(path)
    return JSON.parse(await res.text())
  }

  ctx.effect(() => ctx.tools.register({
    name: 'vm_status',
    description:
      'Check whether the guest VM is reachable; report screen size, logged-in user and the FOREGROUND WINDOW TITLE. ' +
      'Call this before any other vm_* tool; when it reports connected=false the tunnel or the guest agent is not running. ' + 'Before vm_type/vm_click, use foregroundWindow to confirm the target window.',
    parameters: { type: 'object', properties: {}, additionalProperties: false },
    output: {
      schema: {
        type: 'object',
        properties: {
          connected: { type: 'boolean' },
          width: { type: 'number' },
          height: { type: 'number' },
          user: { type: 'string' },
          error: { type: 'string' },
        },
        required: ['connected'],
        additionalProperties: true,
      },
      render(_args, value) {
        if (value.connected) {
          return [{ type: 'text', text: `guest connected: ${value.width}x${value.height}, user "${value.user}"` }]
        }
        return [{ type: 'text', text: `guest NOT connected: ${value.error ?? 'unknown reason'}` }]
      },
    },
    async execute() {
      const status = await requestJson('/status')
      if (!status.connected) {
        return { connected: false, error: 'tunnel host has no guest session' }
      }
      const info = await requestJson('/info')
      const base = { connected: true, width: info.w, height: info.h, user: info.user }
      if (!strictPixelOnly) base.foregroundWindow = info.fg || ''
      return base
    },
  }))

  ctx.effect(() => ctx.tools.register({
    name: 'vm_ui',
    description:
      'Return the UI Automation tree of the FOREGROUND window: control types, names, AutomationIds and screen rectangles (with cx/cy click centers). ' +
      'Use this instead of estimating coordinates from screenshots: find the element by name/type, then vm_click its cx/cy. ' +
      'Call vm_status first to confirm the foreground window. ' + 'Disabled when config.strictPixelOnly=true (screenshot-only black-box mode).',
    parameters: {
      type: 'object',
      properties: {
        depth: { type: 'number', description: 'max tree depth, default 5' },
        maxNodes: { type: 'number', description: 'max elements returned, default 300' },
      },
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          window: { type: 'string' },
          count: { type: 'number' },
          elements: { type: 'array', items: { type: 'object', additionalProperties: true } },
        },
        additionalProperties: true,
      },
    },
    async execute(args) {
      if (strictPixelOnly) {
        return { error: 'vm_ui disabled: strictPixelOnly mode allows screenshots + mouse/keyboard only' }
      }
      const depth = Number(args?.depth ?? 5)
      const maxNodes = Number(args?.maxNodes ?? 300)
      return requestJson(`/ui?depth=${depth}&maxNodes=${maxNodes}`)
    },
    isConcurrencySafe: () => false,
  }))

  ctx.effect(() => ctx.tools.register({
    name: 'vm_screenshot',
    description:
      'Capture what the guest screen shows right now and return it as an image plus the saved file path. ' +
      'This is your only view of the VM: always look at the returned image before deciding the next action, ' +
      'and treat coordinates in it as the coordinate system for vm_click.',
    parameters: {
      type: 'object',
      properties: {
        quality: { type: 'number', description: 'JPEG quality, 30-95 (default 80). Lower is smaller and faster.' },
      },
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          path: { type: 'string' },
          width: { type: 'number' },
          height: { type: 'number' },
          bytes: { type: 'number' },
          via: { type: 'string' },
          attachment: { type: 'object' },
        },
        required: ['path', 'width', 'height', 'bytes'],
        additionalProperties: true,
      },
      render(_args, value) {
        const blocks = [{
          type: 'text',
          text: `guest screen ${value.width}x${value.height} (${value.bytes} bytes, capture=${value.via || 'guest'}) saved at ${value.path}` + (value.via === 'host' ? ' [3D window detected: this frame came from the host framebuffer]' : ''),
        }]
        if (value.attachment) blocks.push({ type: 'image', attachment: value.attachment })
        return blocks
      },
    },
    async execute(args) {
      const quality = Math.min(95, Math.max(30, Number(args?.quality ?? 80)))
      const masks = Array.isArray(args?.mask)
        ? args.mask.filter((m) => m && [m.x, m.y, m.w, m.h].every((v) => Number.isFinite(v)) && m.w > 0 && m.h > 0)
        : []
      const maskQuery = masks.length ? '&mask=' + masks.map((m) => `${m.x},${m.y},${m.w},${m.h}`).join(';') : ''
      const res = await request(`/shot?q=${quality}${maskQuery}`)
      const via = res.headers.get('x-shot-via') || 'guest'
      const bytes = new Uint8Array(await res.arrayBuffer())
      const info = await requestJson('/info')
      mkdirSync(shotsDir, { recursive: true })
      const path = join(shotsDir, `shot-${Date.now()}.jpg`)
      writeFileSync(path, bytes)
        pruneShots()
      const attachment = await ctx.attachments.saveImage({ data: bytes, mediaType: 'image/jpeg' })
      return { path, width: info.w, height: info.h, bytes: bytes.length, via, attachment }
    },
    isConcurrencySafe: () => false,
  }))

  ctx.effect(() => ctx.tools.register({
    name: 'vm_click',
    description:
      'Click a pixel position on the guest screen with a real mouse event. Coordinates use the screenshot coordinate system ' +
      '(origin at the top-left, no scaling).',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'number', description: 'Horizontal pixel position from the left edge of the screenshot.' },
        y: { type: 'number', description: 'Vertical pixel position from the top edge of the screenshot.' },
        double: { type: 'boolean', description: 'Perform a double click instead of a single click.' },
      },
      required: ['x', 'y'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: { ok: { type: 'boolean' }, x: { type: 'number' }, y: { type: 'number' } },
        required: ['ok'],
        additionalProperties: true,
      },
      render(args, value) {
        return [{ type: 'text', text: `${value.ok ? 'clicked' : 'click failed'} at (${args.x}, ${args.y})${args.double ? ' double-click' : ''}` }]
      },
    },
    async execute(args) {
      const x = Math.round(Number(args?.x))
      const y = Math.round(Number(args?.y))
      if (!Number.isFinite(x) || !Number.isFinite(y)) throw new Error('vm_click needs numeric x and y')
      await request(`/click?x=${x}&y=${y}&double=${args?.double ? 1 : 0}`)
      return { ok: true, x, y }
    },
    isConcurrencySafe: () => false,
  }))

  ctx.effect(() => ctx.tools.register({
    name: 'vm_type',
    description:
      'Type text into whatever control currently has keyboard focus in the guest, using real Unicode keystrokes ' +
      '(Chinese works). Click the target field first, then type.',
    parameters: {
      type: 'object',
      properties: { text: { type: 'string', description: 'Exact text to type, including newlines.' } },
      required: ['text'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: { ok: { type: 'boolean' }, chars: { type: 'number' } },
        required: ['ok'],
        additionalProperties: true,
      },
      render(args) {
        return [{ type: 'text', text: `typed ${Array.from(args.text ?? '').length} characters` }]
      },
    },
    async execute(args) {
      const text = String(args?.text ?? '')
      await request(`/type?text=${q(text)}`)
      return { ok: true, chars: Array.from(text).length }
    },
    isConcurrencySafe: () => false,
  }))

  ctx.effect(() => ctx.tools.register({
    name: 'vm_type_secret',
    description:
      'Type a secret (password / token) from the host-side vault into the focused field. ' +
      'The secret value NEVER enters the model context: you only pass the credential NAME. ' +
      'After typing, screenshots are automatically redacted over that field for a few minutes. ' +
      'Ask the operator for available credential names; never ask for the secret itself.',
    parameters: {
      type: 'object',
      properties: {
        name: { type: 'string', description: 'Vault credential name, e.g. "qa_login".' },
      },
      required: ['name'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: { ok: { type: 'boolean' }, masked: { type: 'boolean' } },
        required: ['ok'],
        additionalProperties: true,
      },
      render(args, value) {
        return [{
          type: 'text',
          text: `typed secret "${args?.name}" from vault (field redacted in screenshots: ${value?.masked ? 'yes' : 'no'})`,
        }]
      },
    },
    async execute(args) {
      const name = String(args?.name ?? '')
      if (!/^[A-Za-z0-9_.-]{1,64}$/.test(name)) throw new Error('invalid secret name')
      return await requestJson(`/type-secret?name=${encodeURIComponent(name)}`)
    },
    isConcurrencySafe: () => false,
  }))

ctx.effect(() => ctx.tools.register({
    name: 'vm_key',
    description:
      'Press a key or key combination in the guest, e.g. "enter", "escape", "tab", "ctrl+s", "alt+f4", "win+r", "f5". ' +
      'Use vm_type for text and vm_key for named keys.',
    parameters: {
      type: 'object',
      properties: { combo: { type: 'string', description: 'Key name or "+"-joined combination, lower case.' } },
      required: ['combo'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: { ok: { type: 'boolean' }, combo: { type: 'string' } },
        required: ['ok'],
        additionalProperties: true,
      },
      render(args, value) {
        return [{ type: 'text', text: `${value.ok ? 'pressed' : 'unknown key'}: ${args.combo}` }]
      },
    },
    async execute(args) {
      const combo = String(args?.combo ?? '')
      await request(`/key?combo=${q(combo)}`)
      return { ok: true, combo }
    },
    isConcurrencySafe: () => false,
  }))

  ctx.effect(() => ctx.tools.register({
    name: 'vm_wait',
    description: 'Wait for the guest UI to settle before taking the next screenshot. Prefer this over guessing that an action finished.',
    parameters: {
      type: 'object',
      properties: { ms: { type: 'number', description: 'Milliseconds to wait, 0-60000.' } },
      required: ['ms'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: { waitedMs: { type: 'number' } },
        required: ['waitedMs'],
        additionalProperties: false,
      },
      render(_args, value) {
        return [{ type: 'text', text: `waited ${value.waitedMs} ms` }]
      },
    },
    async execute(args) {
      const ms = Math.min(60000, Math.max(0, Math.round(Number(args?.ms ?? 0))))
      await new Promise((resolve) => setTimeout(resolve, ms))
      return { waitedMs: ms }
    },
  }))

  // A non-technical user still presses Enter, Tab and the arrow keys, but never
  // a modifier combination. This tool is the whole keyboard for the naive tier.
  const BASIC_KEYS = new Set([
    'enter', 'tab', 'escape', 'space', 'backspace',
    'up', 'down', 'left', 'right', 'home', 'end', 'pageup', 'pagedown', 'delete',
  ])

  ctx.effect(() => ctx.tools.register({
    name: 'vm_key_basic',
    description:
      'Press one ordinary key in the guest: enter, tab, escape, space, backspace, arrows, home/end, pageup/pagedown, delete. ' +
      'Any combination such as "ctrl+s" or "win+r" is rejected — this is exactly the keyboard a non-technical user has.',
    parameters: {
      type: 'object',
      properties: { key: { type: 'string', description: 'One key name from the allowed set.' } },
      required: ['key'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: { ok: { type: 'boolean' }, key: { type: 'string' } },
        required: ['ok'],
        additionalProperties: true,
      },
      render(args, value) {
        return [{ type: 'text', text: `${value.ok ? 'pressed' : 'rejected'}: ${args.key}` }]
      },
    },
    async execute(args) {
      const key = String(args?.key ?? '').toLowerCase().trim()
      if (!BASIC_KEYS.has(key)) {
        throw new Error(`vm_key_basic accepts only plain keys (${[...BASIC_KEYS].join(', ')}); got "${key}"`)
      }
      await request(`/key?combo=${q(key)}`)
      return { ok: true, key }
    },
    isConcurrencySafe: () => false,
  }))
}
