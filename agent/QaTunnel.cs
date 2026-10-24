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

        private static void Main(string[] args)
        {
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
                                case "INFO": SendOk(ns, id, "{\"w\":" + W() + ",\"h\":" + H() + ",\"user\":\"" + Environment.UserName + "\"}"); break;
                                case "SHOT":
                                    {
                                        int q = parts.Length > 2 ? int.Parse(parts[2]) : 80;
                                        bool ruler = !(parts.Length > 3 && parts[3] == "plain");
                                        byte[] jpg = CaptureJpeg(q, ruler);
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
                                        MoveTo(x, y); Thread.Sleep(40); Click(dbl);
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
                                        bool ok = PressCombo(parts.Length > 2 ? parts[2] : "");
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

        private static byte[] CaptureJpeg(int quality, bool ruler)
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    if (ruler) { DrawRuler(g, w, h); DrawClickMarker(g, w, h); }
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
            int w = Math.Max(1, GetSystemMetrics(0) - 1), h = Math.Max(1, GetSystemMetrics(1) - 1);
            int nx = (int)((long)x * 65535 / w), ny = (int)((long)y * 65535 / h);
            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0);
        }

        private static void Click(bool dbl)
        {
            SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0); Thread.Sleep(30);
            SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
            if (dbl) { Thread.Sleep(60); SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0); Thread.Sleep(30); SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0); }
        }

        private static void TypeText(string text)
        {
            foreach (char ch in text)
            {
                if (ch == '\n') { TapKey(0x0D); continue; }
                if (ch == '\t') { TapKey(0x09); continue; }
                SendKey(0, ch, KEYEVENTF_UNICODE);
                SendKey(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
                Thread.Sleep(6);
            }
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

        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    }
}
