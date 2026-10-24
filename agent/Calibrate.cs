// Calibrate - full-screen click target that records where clicks actually land.
// The host clicks known points; every received WM_LBUTTONDOWN is appended to
// C:\qa\calib.log as "<x>,<y>" in screen coordinates, so the host can fit the
// requested -> landed mapping.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Calibrate
{
    internal sealed class CalibForm : Form
    {
        private readonly string _log = @"C:\qa\calib.log";
        private Point _last = new Point(-1, -1);
        private int _count;

        public CalibForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            try { File.WriteAllText(_log, ""); } catch { }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _last = e.Location;
            _count++;
            try { File.AppendAllText(_log, e.X + "," + e.Y + "\r\n"); } catch { }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var grid = new Pen(Color.FromArgb(35, 35, 35), 1f))
            {
                for (int x = 0; x < Width; x += 100) g.DrawLine(grid, x, 0, x, Height);
                for (int y = 0; y < Height; y += 100) g.DrawLine(grid, 0, y, Width, y);
            }
            using (var strong = new Pen(Color.FromArgb(90, 90, 90), 1f))
            {
                for (int x = 0; x < Width; x += 500) g.DrawLine(strong, x, 0, x, Height);
                for (int y = 0; y < Height; y += 500) g.DrawLine(strong, 0, y, Width, y);
            }
            using (var font = new Font("Consolas", 14f))
            using (var brush = new SolidBrush(Color.FromArgb(120, 120, 120)))
            {
                for (int x = 500; x < Width; x += 500) g.DrawString(x.ToString(), font, brush, x + 4, 4);
                for (int y = 500; y < Height; y += 500) g.DrawString(y.ToString(), font, brush, 4, y + 2);
            }
            using (var title = new Font("Consolas", 22f, FontStyle.Bold))
            using (var tb = new SolidBrush(Color.Lime))
            {
                g.DrawString("CALIBRATION — clicks recorded: " + _count, title, tb, 40, 40);
            }
            if (_last.X >= 0)
            {
                using (var red = new Pen(Color.Red, 3f))
                {
                    g.DrawLine(red, _last.X - 45, _last.Y, _last.X + 45, _last.Y);
                    g.DrawLine(red, _last.X, _last.Y - 45, _last.X, _last.Y + 45);
                    g.DrawEllipse(red, _last.X - 12, _last.Y - 12, 24, 24);
                }
                using (var font = new Font("Consolas", 18f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("landed " + _last.X + "," + _last.Y, font, brush, _last.X + 50, _last.Y + 12);
                }
            }
        }

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new CalibForm());
        }
    }
}
