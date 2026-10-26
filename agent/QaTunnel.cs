// QaTunnel - reverse channel agent. Dials OUT to the host, so no inbound firewall rule is needed.
// Protocol (UTF-8 lines; one optional binary payload after a header line):
//   host -> guest : "<id> <OP> [args...]\n"
//   guest -> host : "<id> OK [len|json]\n" [+ <len> raw bytes]   |   "<id> ERR <message>\n"
// Ops: INFO | SHOT [quality] | CLICK x y [dbl] | TYPE <base64-utf8> | KEY <combo> | EXIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace QaTunnel
{
    internal static class Program
    {
        private static string _host = "10.0.2.2";
        private static int _port = 8801;
        private static string _log = @"C:\qa\tunnel.log";
        private static int _lastClickX = int.MinValue, _lastClickY = int.MinValue;
        private static long _lastClickTick;
        // 敏感区域：SECRET 操作后自动把焦点控件遮蔽一段时间，防止密码进截图。
        private static List<Rectangle> _secretMasks = new List<Rectangle>();
        private static long _secretUntilTicks = 0;
        private static IntPtr _secretHwnd = IntPtr.Zero; // SECRET 时的前台窗口：窗口一关/切走就解除遮蔽
        private static Mutex _singleInstance; // 持有到进程退出，防止被 GC 回收后锁失效

        private static void Main(string[] args)
        {
            // 单实例：两条自启路径（启动文件夹 + 计划任务）同时存在时只允许一个代理进程
            bool createdNew;
            _singleInstance = new Mutex(true, "Global\\QaTunnelAgentMutex", out createdNew);
            if (!createdNew) return;
            // Offline test mode: QaTunnel.exe --render <in.jpg> <out.png>
            if (args.Length >= 3 && args[0] == "--render")
            {
                using (var src = new Bitmap(args[1]))
                {
                    using (var g = Graphics.FromImage(src))
                        DrawRuler(g, src.Width, src.Height);
                    src.Save(args[2], ImageFormat.Png);
                }
                return;
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--host" && i + 1 < args.Length) _host = args[i + 1];
                if (args[i] == "--port" && i + 1 < args.Length) int.TryParse(args[i + 1], out _port);
            }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { try { SetProcessDPIAware(); } catch { } }
            Log("start host=" + _host + " port=" + _port);
            while (true)
            {
                try { Session(); }
                catch (Exception ex) { Log("session ended: " + ex.Message); }
                Thread.Sleep(2000);
            }
        }

        private static void Session()
        {
            using (var client = new TcpClient())
            {
                client.Connect(_host, _port);
                client.NoDelay = true;
                // 虚拟机保存/恢复或网络中断后，旧连接不会自己报错；打开 TCP keepalive，
                // 让读循环在 ~13 秒内发现死连接，从而走外层重连。
                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    byte[] ka = new byte[12];
                    BitConverter.GetBytes((uint)1).CopyTo(ka, 0);
                    BitConverter.GetBytes((uint)10000).CopyTo(ka, 4);
                    BitConverter.GetBytes((uint)3000).CopyTo(ka, 8);
                    client.Client.IOControl(IOControlCode.KeepAliveValues, ka, null);
                }
                catch { }
                Log("connected");
                using (NetworkStream ns = client.GetStream())
                {
                    var reader = new LineReader(ns);
                    while (true)
                    {
                        string line = reader.ReadLine();
                        if (line == null) return;
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        string[] parts = line.Split(' ');
                        string id = parts[0];
                        string op = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
                        try
                        {
                            switch (op)
                            {
                                case "CURSOR": { POINT cp; if (GetCursorPos(out cp)) SendOk(ns, id, "{\"x\":" + cp.X + ",\"y\":" + cp.Y + "}"); else SendErr(ns, id, "cursor failed"); break; }
                                case "INFO": SendOk(ns, id, "{\"w\":" + W() + ",\"h\":" + H() + ",\"user\":\"" + Environment.UserName + "\"}"); break;
                                case "SECRET":
                                    {
                                        string txt = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                                        Rectangle fr = FocusRect();
                                        _secretMasks = new List<Rectangle>();
                                        int sw = Math.Max(1, GetSystemMetrics(0)), sh = Math.Max(1, GetSystemMetrics(1));
                                        // Chromium/Electron 的 UIA 焦点有时是整个文档 —— 面积过大时降级为点击点附近的条带
                                        if (!fr.IsEmpty && (long)fr.Width * fr.Height > (long)sw * sh / 2) {
                                            POINT cp; int px = sw / 2, py = sh / 2;
                                            if (GetCursorPos(out cp)) { px = cp.X; py = cp.Y; }
                                            fr = new Rectangle(0, Math.Max(0, py - 40), sw, 90);
                                        }
                                        if (!fr.IsEmpty) {
                                            _secretMasks.Add(fr);
                                            var wr = WindowRect(GetForegroundWindow());
                                            if (!wr.IsEmpty) _secretMasks.Add(new Rectangle(wr.X, wr.Y, wr.Width, Math.Min(56, wr.Height)));
                                            _secretUntilTicks = DateTime.Now.AddSeconds(180).Ticks;
                                            var c = new POINT { X = fr.X + fr.Width / 2, Y = fr.Y + fr.Height / 2 };
                                            _secretHwnd = WindowFromPoint(c);
                                            if (_secretHwnd != IntPtr.Zero) _secretHwnd = GetAncestor(_secretHwnd, 2);
                                        }
                                        TypeText(txt);
                                        var parts2 = new List<string>();
                                        foreach (var m in _secretMasks) parts2.Add("[" + m.X + "," + m.Y + "," + m.Width + "," + m.Height + "]");
                                        SendOk(ns, id, "{\"masked\":" + (_secretMasks.Count == 0 ? "false" : "true") + ",\"masks\":[" + string.Join(",", parts2.ToArray()) + "]}");
                                        break;
                                    }
                                case "MASKS":
                                    {
                                        var ms = new List<string>();
                                        if (SecretMaskStillValid()) foreach (var m in _secretMasks) ms.Add("[" + m.X + "," + m.Y + "," + m.Width + "," + m.Height + "]");
                                        SendOk(ns, id, "{\"masks\":[" + string.Join(",", ms.ToArray()) + "]}");
                                        break;
                                    }
                                case "CLEARMASKS":
                                    _secretMasks.Clear(); _secretHwnd = IntPtr.Zero; _secretUntilTicks = 0;
                                    SendOk(ns, id, "{\"cleared\":true}");
                                    break;
                                case "SHOT":
                                    {
                                        int q = parts.Length > 2 ? int.Parse(parts[2]) : 80;
                                        bool ruler = !(parts.Length > 3 && parts[3] == "plain");
                                        var masks = new List<Rectangle>();
                                        for (int mi = 4; mi + 3 < parts.Length; mi += 4)
                                        {
                                            int mx, my, mw, mh;
                                            if (int.TryParse(parts[mi], out mx) && int.TryParse(parts[mi + 1], out my)
                                                && int.TryParse(parts[mi + 2], out mw) && int.TryParse(parts[mi + 3], out mh)
                                                && mw > 0 && mh > 0)
                                                masks.Add(new Rectangle(mx, my, mw, mh));
                                        }
                                        if (SecretMaskStillValid()) masks.AddRange(_secretMasks);
                            else { _secretMasks.Clear(); _secretHwnd = IntPtr.Zero; }
                                        byte[] jpg = CaptureJpeg(q, ruler, masks);
                                        byte[] head = Encoding.ASCII.GetBytes(id + " OK " + jpg.Length + "\n");
                                        ns.Write(head, 0, head.Length);
                                        ns.Write(jpg, 0, jpg.Length);
                                        ns.Flush();
                                        break;
                                    }
                                case "CLICK":
                                    {
                                        int x = int.Parse(parts[2]); int y = int.Parse(parts[3]);
                                        bool dbl = parts.Length > 4 && parts[4] == "1";
                                        MoveTo(x, y); Thread.Sleep(40);
                EnsureForegroundAt(x, y); // 非前台窗口的第一下点击只会激活窗口，先激活再点
                Click(dbl);
                                        _lastClickX = x; _lastClickY = y;
                                        _lastClickTick = Environment.TickCount;
                                        SendOk(ns, id, "{}");
                                        break;
                                    }
                                case "TYPE":
                                    {
                                        string b64 = parts.Length > 2 ? parts[2] : "";
                                        string text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                                        TypeText(text);
                                        SendOk(ns, id, "{\"chars\":" + text.Length + "}");
                                        break;
                                    }
                                case "KEY":
                                    {
                                        string combo = parts.Length > 2 ? parts[2] : "";
                                        string refused = DangerousKeyCheck(combo);
                                        if (refused != null) { SendErr(ns, id, refused); break; }
                                        bool ok = PressCombo(combo);
                                        SendOk(ns, id, ok ? "{}" : "{\"err\":\"unknown key\"}");
                                        break;
                                    }
                                case "EXIT":
                                    SendOk(ns, id, "{}");
                                    return;
                                default:
                                    SendErr(ns, id, "unknown op");
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            try { SendErr(ns, id, ex.Message.Replace('\n', ' ')); } catch { }
                        }
                    }
                }
            }
        }

        private static void SendOk(NetworkStream ns, string id, string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(id + " OK " + json + "\n");
            ns.Write(b, 0, b.Length);
            ns.Flush();
        }

        private static void SendErr(NetworkStream ns, string id, string msg)
        {
            byte[] b = Encoding.UTF8.GetBytes(id + " ERR " + msg + "\n");
            ns.Write(b, 0, b.Length);
            ns.Flush();
        }

        private static int W() { return GetSystemMetrics(0); }
        private static int H() { return GetSystemMetrics(1); }

        private static byte[] CaptureJpeg(int quality, bool ruler, List<Rectangle> masks = null)
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    if (ruler) { DrawRuler(g, w, h); DrawClickMarker(g, w, h); }
                    DrawMasks(g, w, h, masks);
                }
                using (var ms = new MemoryStream())
                {
                    var enc = GetEncoder(ImageFormat.Jpeg);
                    using (var ps = new EncoderParameters(1))
                    {
                        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(30, Math.Min(95, quality)));
                        bmp.Save(ms, enc, ps);
                    }
                    return ms.ToArray();
                }
            }
        }

        // Crosshair at the LAST click position so the model can see where its click actually landed.

        private static Rectangle UiaFocusRect()
        {
            try
            {
                var el = System.Windows.Automation.AutomationElement.FocusedElement;
                if (el == null) return Rectangle.Empty;
                var r = el.Current.BoundingRectangle;
                if (r.Width >= 8 && r.Height >= 8 && r.Width <= 4096 && r.Height <= 4096)
                    return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            }
            catch { }
            return Rectangle.Empty;
        }

        private static Rectangle WindowRect(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero) return Rectangle.Empty;
                RECTW r;
                if (!GetWindowRect(hWnd, out r)) return Rectangle.Empty;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return Rectangle.Empty;
                return new Rectangle(r.Left, r.Top, w, h);
            }
            catch { return Rectangle.Empty; }
        }

        private static Rectangle FocusRect()
        {
            // Chromium / Electron 应用的「焦点窗口」是整个浏览器窗口，直接用 HWND 会把整页遮黑。
            // 优先用 UI Automation 取真正获得焦点的元素矩形。
            var uia = UiaFocusRect();
            if (!uia.IsEmpty) return uia;
            try
            {
                IntPtr hFocus = FindFocusWindow();
                if (hFocus == IntPtr.Zero) return Rectangle.Empty;
                RECTW r;
                if (!GetWindowRect(hFocus, out r)) return Rectangle.Empty;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return Rectangle.Empty;
                return new Rectangle(r.Left, r.Top, w, h);
            }
            catch { return Rectangle.Empty; }
        }

        // 把敏感区域涂黑（必须在编码/落盘之前做）
        private static void DrawMasks(Graphics g, int w, int h, List<Rectangle> masks)
        {
            if (masks == null || masks.Count == 0) return;
            using (var black = new SolidBrush(Color.Black))
            using (var pen = new Pen(Color.FromArgb(255, 255, 60, 60), 3f))
            using (var font = new Font("Consolas", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fore = new SolidBrush(Color.FromArgb(255, 255, 80, 80)))
            {
                foreach (var mRaw in masks)
                {
                    Rectangle m = Rectangle.Intersect(new Rectangle(0, 0, w, h), mRaw);
                    if (m.Width <= 0 || m.Height <= 0) continue;
                    g.FillRectangle(black, m);
                    g.DrawRectangle(pen, m.X - 1, m.Y - 1, m.Width + 1, m.Height + 1);
                    g.DrawString("REDACTED", font, fore, m.X + 6, m.Y + 4);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECTW { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECTW r);

        private static void DrawClickMarker(Graphics g, int w, int h)
        {
            if (_lastClickX == int.MinValue) return;
            int x = Math.Max(30, Math.Min(w - 30, _lastClickX));
            int y = Math.Max(30, Math.Min(h - 30, _lastClickY));
            using (var pen = new Pen(Color.FromArgb(235, 0, 255, 255), 2f))
            {
                g.DrawLine(pen, x - 28, y, x + 28, y);
                g.DrawLine(pen, x, y - 28, x, y + 28);
                g.DrawEllipse(pen, x - 13, y - 13, 26, 26);
            }
            using (var font = new Font("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var back = new SolidBrush(Color.FromArgb(210, 0, 0, 0)))
            using (var fore = new SolidBrush(Color.FromArgb(255, 0, 255, 255)))
            {
                string s = "click " + _lastClickX + "," + _lastClickY;
                SizeF sz = g.MeasureString(s, font);
                float lx = Math.Min(w - sz.Width - 10, x + 16);
                float ly = Math.Max(4, y - sz.Height - 10);
                g.FillRectangle(back, lx - 2, ly - 1, sz.Width + 6, sz.Height + 2);
                g.DrawString(s, font, fore, lx, ly);
            }
        }

        // Coordinate ruler: lines every 200px, labels every 200px in NATIVE pixels.
        // Labels are drawn with high-contrast boxes so they survive downscaling by the model's
        // image pipeline — the reader never has to guess a scale factor.
        private static void DrawRuler(Graphics g, int w, int h)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            using (var thin = new Pen(Color.FromArgb(105, 255, 32, 32), 1f))
            using (var bold = new Pen(Color.FromArgb(170, 255, 32, 32), 1f))
            using (var font = new Font("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var back = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
            using (var fore = new SolidBrush(Color.FromArgb(255, 255, 235, 90)))
            {
                for (int x = 200; x < w; x += 200)
                {
                    g.DrawLine(x % 1000 == 0 ? bold : thin, x, 0, x, h);
                    string s = x.ToString();
                    SizeF sz = g.MeasureString(s, font);
                    g.FillRectangle(back, x + 2f, 2f, sz.Width + 6f, sz.Height + 2f);
                    g.DrawString(s, font, fore, x + 5f, 2f);
                    g.FillRectangle(back, x + 2f, h - sz.Height - 4f, sz.Width + 6f, sz.Height + 2f);
                    g.DrawString(s, font, fore, x + 5f, h - sz.Height - 4f);
                }
                for (int y = 200; y < h; y += 200)
                {
                    g.DrawLine(y % 1000 == 0 ? bold : thin, 0, y, w, y);
                    string s = y.ToString();
                    SizeF sz = g.MeasureString(s, font);
                    g.FillRectangle(back, 2f, y + 2f, sz.Width + 6f, sz.Height + 2f);
                    g.DrawString(s, font, fore, 4f, y + 3f);
                    g.FillRectangle(back, w - sz.Width - 8f, y + 2f, sz.Width + 6f, sz.Height + 2f);
                    g.DrawString(s, font, fore, w - sz.Width - 6f, y + 3f);
                }
            }
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == format.Guid) return c;
            return null;
        }

        private static void MoveTo(int x, int y)
        {
            // 分步移动：围观者（1 帧/秒的抓帧）也能看出指针是从哪划过来的，而不是瞬移。
            POINT cur;
            int sx = x, sy = y;
            if (GetCursorPos(out cur)) { sx = cur.X; sy = cur.Y; }
            // 距离越远步数越多（约 350ms 走完），让 1 帧/秒的围观也能看出方向
            int dx = x - sx, dy = y - sy;
            int dist = (int)Math.Sqrt((double)dx * dx + (double)dy * dy);
            int steps = Math.Max(3, Math.Min(14, dist / 45));
            int delay = Math.Max(18, Math.Min(45, 350 / Math.Max(1, steps)));
            for (int i = 1; i <= steps; i++)
            {
                SendMouseAbs(sx + dx * i / steps, sy + dy * i / steps);
                Thread.Sleep(delay);
            }
            SendMouseAbs(x, y);
        }

        private static void SendMouseAbs(int x, int y)
        {
            int w = Math.Max(1, GetSystemMetrics(0) - 1), h = Math.Max(1, GetSystemMetrics(1) - 1);
            int nx = (int)((long)x * 65535 / w), ny = (int)((long)y * 65535 / h);
            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0);
        }

        private static void EnsureForegroundAt(int x, int y)
        {
            try
            {
                var pt = new POINT { X = x, Y = y };
                IntPtr h = WindowFromPoint(pt);
                if (h == IntPtr.Zero) return;
                IntPtr root = GetAncestor(h, 2 /* GA_ROOT */);
                if (root == IntPtr.Zero) root = h;
                if (root == GetForegroundWindow()) return; // 已是前台，直接点
                SetForegroundWindow(root);
                Thread.Sleep(150);
            }
            catch { }
        }

        private static bool SecretMaskStillValid()
        {
            if (_secretMasks.Count == 0) return false;
            if (DateTime.Now.Ticks >= _secretUntilTicks) return false;
            if (_secretHwnd == IntPtr.Zero) return true;
            if (!IsWindow(_secretHwnd)) return false; // 目标窗口已销毁
            // 遮罩中心点下面的窗口换了（用户切走/关窗）→ 立即解除
            Rectangle r0 = _secretMasks[0];
            var c = new POINT { X = r0.X + r0.Width / 2, Y = r0.Y + r0.Height / 2 };
            IntPtr under = WindowFromPoint(c);
            if (under != IntPtr.Zero) under = GetAncestor(under, 2);
            if (under != IntPtr.Zero && under != _secretHwnd) return false;
            return true;
        }

        private static void Click(bool dbl)
        {
            SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0); Thread.Sleep(30);
            SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
            if (dbl) { Thread.Sleep(60); SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0); Thread.Sleep(30); SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0); }
        }


        private static bool SetClipboardText(string text)
        {
            const uint CF_UNICODETEXT = 13;
            IntPtr hMem = IntPtr.Zero;
            try
            {
                int bytes = (text.Length + 1) * 2;
                hMem = GlobalAlloc(0x0042 /*GMEM_MOVEABLE|GMEM_ZEROINIT*/, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) return false;
                IntPtr p = GlobalLock(hMem);
                if (p == IntPtr.Zero) return false;
                byte[] data = Encoding.Unicode.GetBytes(text + "\0");
                Marshal.Copy(data, 0, p, data.Length);
                GlobalUnlock(hMem);
                for (int i = 0; i < 10; i++)
                {
                    if (OpenClipboard(IntPtr.Zero))
                    {
                        try
                        {
                            EmptyClipboard();
                            if (SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero) hMem = IntPtr.Zero;
                        }
                        finally { CloseClipboard(); }
                        return true;
                    }
                    Thread.Sleep(50);
                }
                return false;
            }
            finally { if (hMem != IntPtr.Zero) GlobalFree(hMem); }
        }

        private static void TypeText(string text)
        {
            // 首选剪贴板粘贴：Unicode 键盘事件在中文 Windows 上会被输入法/编辑器改写（实测错字），粘贴是精确的。
            if (SetClipboardText(text))
            {
                Thread.Sleep(80);
                bool ok = PressCombo("ctrl+v");
                Thread.Sleep(150);
                try { SetClipboardText(""); } catch { }   // 用完立刻清空，避免密码留在剪贴板
                if (ok) return;
            }
            // 兜底：逐字 Unicode 注入（并临时解绑输入法），导致字母丢失/字符重复。
            // 打字前把输入法上下文从焦点窗口解绑，打完再恢复。
            IntPtr hFocus = FindFocusWindow();
            IntPtr oldImc = IntPtr.Zero;
            if (hFocus != IntPtr.Zero) { try { oldImc = ImmAssociateContext(hFocus, IntPtr.Zero); } catch { } }
            try
            {
                foreach (char ch in text)
                {
                    if (ch == '\n') { TapKey(0x0D); Thread.Sleep(12); continue; }
                    if (ch == '\t') { TapKey(0x09); Thread.Sleep(12); continue; }
                    SendCharPair(ch);
                    Thread.Sleep(10);
                }
            }
            finally
            {
                if (hFocus != IntPtr.Zero) { try { ImmAssociateContext(hFocus, oldImc); } catch { } }
            }
        }

        private static IntPtr FindFocusWindow()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                uint tid = GetWindowThreadProcessId(fg, IntPtr.Zero);
                var gti = new GUITHREADINFO();
                gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                if (GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero) return gti.hwndFocus;
            }
            catch { }
            return fg;
        }

        // 一个字：keydown + keyup 放在同一次 SendInput 里，避免抬起事件丢失造成重复字符
        private static void SendCharPair(char ch)
        {
            var kd = new KEYBDINPUT(); kd.wVk = 0; kd.wScan = ch; kd.dwFlags = KEYEVENTF_UNICODE;
            var ku = new KEYBDINPUT(); ku.wVk = 0; ku.wScan = ch; ku.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            var ud = new INPUTUNION(); ud.ki = kd;
            var uu = new INPUTUNION(); uu.ki = ku;
            var a = new INPUT(); a.type = INPUT_KEYBOARD; a.u = ud;
            var b = new INPUT(); b.type = INPUT_KEYBOARD; b.u = uu;
            SendInput(2, new INPUT[] { a, b }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void TapKey(ushort vk) { SendKey(vk, 0, 0); SendKey(vk, 0, KEYEVENTF_KEYUP); }

        private static readonly Dictionary<string, ushort> KeyMap = new Dictionary<string, ushort>
        {
            {"backspace",0x08},{"tab",0x09},{"enter",0x0D},{"return",0x0D},{"shift",0x10},
            {"ctrl",0x11},{"control",0x11},{"alt",0x12},{"esc",0x1B},{"escape",0x1B},{"space",0x20},
            {"left",0x25},{"up",0x26},{"right",0x27},{"down",0x28},{"delete",0x2E},{"home",0x24},{"end",0x23},
            {"a",0x41},{"b",0x42},{"c",0x43},{"d",0x44},{"e",0x45},{"f",0x46},{"g",0x47},{"h",0x48},
            {"i",0x49},{"j",0x4A},{"k",0x4B},{"l",0x4C},{"m",0x4D},{"n",0x4E},{"o",0x4F},{"p",0x50},
            {"q",0x51},{"r",0x52},{"s",0x53},{"t",0x54},{"u",0x55},{"v",0x56},{"w",0x57},{"x",0x58},
            {"y",0x59},{"z",0x5A},{"0",0x30},{"1",0x31},{"2",0x32},{"3",0x33},{"4",0x34},{"5",0x35},
            {"6",0x36},{"7",0x37},{"8",0x38},{"9",0x39},{"f1",0x70},{"f2",0x71},{"f3",0x72},{"f4",0x73},
            {"f5",0x74},{"f6",0x75},{"f7",0x76},{"f8",0x77},{"f9",0x78},{"f10",0x79},{"f11",0x7A},{"f12",0x7B},
            {"win",0x5B},{"lwin",0x5B}
        };

        // 危险按键防护：桌面/任务栏上按 Alt+F4 会弹出「关闭 Windows」，测试员可能误关机。
        private static string DangerousKeyCheck(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return null;
            string c = combo.ToLowerInvariant().Replace(" ", "");
            if (c != "alt+f4") return null;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return "refused: no foreground window";
            var cls = new StringBuilder(256);
            GetClassName(fg, cls, cls.Capacity);
            string clsName = cls.ToString();
            if (clsName == "Progman" || clsName == "WorkerW" || clsName == "Shell_TrayWnd" || clsName == "NotifyIconOverflowWindow")
                return "refused: Alt+F4 on the desktop/taskbar would open the shutdown dialog";
            var title = new StringBuilder(256);
            GetWindowText(fg, title, title.Capacity);
            if (title.ToString().Length == 0)
                return "refused: foreground window has no title (likely the desktop)";
            return null;
        }

        private static bool PressCombo(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return false;
            var vks = new List<ushort>();
            foreach (string raw in combo.ToLowerInvariant().Split('+'))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                ushort vk;
                if (!KeyMap.TryGetValue(p, out vk)) return false;
                vks.Add(vk);
            }
            foreach (ushort vk in vks) SendKey(vk, 0, 0);
            Thread.Sleep(30);
            for (int i = vks.Count - 1; i >= 0; i--) SendKey(vks[i], 0, KEYEVENTF_KEYUP);
            return true;
        }

        private static void SendKey(ushort vk, int scan, uint flags)
        {
            var ki = new KEYBDINPUT(); ki.wVk = vk; ki.wScan = (ushort)scan; ki.dwFlags = flags;
            var u = new INPUTUNION(); u.ki = ki;
            var input = new INPUT(); input.type = INPUT_KEYBOARD; input.u = u;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendMouse(uint flags, int dx, int dy, int data)
        {
            var mi = new MOUSEINPUT(); mi.dx = dx; mi.dy = dy; mi.mouseData = (uint)data; mi.dwFlags = flags;
            var u = new INPUTUNION(); u.mi = mi;
            var input = new INPUT(); input.type = INPUT_MOUSE; input.u = u;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("imm32.dll")] private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

        private static void Log(string m)
        {
            try { File.AppendAllText(_log, DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + "\r\n"); } catch { }
        }

        private sealed class LineReader
        {
            private readonly NetworkStream _ns;
            private readonly byte[] _one = new byte[1];
            public LineReader(NetworkStream ns) { _ns = ns; }
            public string ReadLine()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    int n = _ns.Read(_one, 0, 1);
                    if (n <= 0) return null;
                    char c = (char)_one[0];
                    if (c == '\n') return sb.ToString();
                    if (c != '\r') sb.Append(c);
                    if (sb.Length > 8192) return null;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public INPUTUNION u; }

        private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
        private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    }
}
