// RulerOverlay: draws the coordinate ruler (and optional last-click crosshair) onto a frame
// captured by VirtualBox, then writes a JPEG. Used when the guest-side GDI capture cannot see
// the real framebuffer (3D / GPU-composited windows).
//   RulerOverlay <in.png|jpg> <out.jpg> [clickX clickY] [quality]
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace RulerOverlay
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: RulerOverlay <in> <out> <clickX> <clickY> <quality> [cursorX cursorY] [noruler]");
                Environment.Exit(2);
            }
            int quality = 80;
            int cx = -1, cy = -1, mx = -1, my = -1;
            bool drawRuler = true;
            if (args.Length >= 4) { int.TryParse(args[2], out cx); int.TryParse(args[3], out cy); }
            if (args.Length >= 5) int.TryParse(args[4], out quality);
            if (args.Length >= 7) { int.TryParse(args[5], out mx); int.TryParse(args[6], out my); }
            for (int i = 2; i < args.Length; i++) if (args[i] == "noruler") drawRuler = false;
            quality = Math.Max(30, Math.Min(95, quality));

            using (var bmp = new Bitmap(args[0]))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    if (drawRuler) DrawRuler(g, bmp.Width, bmp.Height);
                    if (cx >= 0 && cy >= 0) DrawClick(g, bmp.Width, bmp.Height, cx, cy);
                    if (mx >= 0 && my >= 0) DrawCursor(g, mx, my);
                }
                var enc = GetEncoder(ImageFormat.Jpeg);
                using (var ps = new EncoderParameters(1))
                {
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    bmp.Save(args[1], enc, ps);
                }
            }
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == format.Guid) return c;
            return null;
        }

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

        // 合成光标：绕开「抓屏不含系统光标」的限制，让围观者能看到指针在哪。
        private static void DrawCursor(Graphics g, int x, int y)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Point[] arrow =
            {
                new Point(x, y),
                new Point(x, y + 22),
                new Point(x + 6, y + 17),
                new Point(x + 10, y + 26),
                new Point(x + 14, y + 24),
                new Point(x + 10, y + 15),
                new Point(x + 17, y + 15)
            };
            using (var fill = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            using (var outline = new Pen(Color.FromArgb(230, 0, 0, 0), 1.6f))
            {
                g.FillPolygon(fill, arrow);
                g.DrawPolygon(outline, arrow);
            }
            using (var ring = new Pen(Color.FromArgb(180, 255, 215, 0), 1.4f))
                g.DrawEllipse(ring, x - 9, y - 9, 18, 18);
        }

        private static void DrawClick(Graphics g, int w, int h, int x, int y)
        {
            x = Math.Max(30, Math.Min(w - 30, x));
            y = Math.Max(30, Math.Min(h - 30, y));
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
                string s = "click " + x + "," + y;
                SizeF sz = g.MeasureString(s, font);
                float lx = Math.Min(w - sz.Width - 10, x + 16);
                float ly = Math.Max(4, y - sz.Height - 10);
                g.FillRectangle(back, lx - 2, ly - 1, sz.Width + 6, sz.Height + 2);
                g.DrawString(s, font, fore, lx, ly);
            }
        }
    }
}
