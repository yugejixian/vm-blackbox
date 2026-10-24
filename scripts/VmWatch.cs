// VmWatch — 桌面看板：把虚拟机的实时画面显示在一个独立小窗里。
// 不依赖 DSH、不装插件，直接读宿主隧道 http://127.0.0.1:8890。
//   - 默认置顶，可拖动、可缩放
//   - Esc 关闭；T 切换置顶；空格 暂停/继续
//   - 隧道断开时保留最后一帧并显示红点
using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace VmWatch
{
    internal sealed class WatchForm : Form
    {
        private const string Endpoint = "http://127.0.0.1:8890";
        private readonly PictureBox _view = new PictureBox();
        private readonly Label _status = new Label();
        private readonly System.Windows.Forms.Timer _pump = new System.Windows.Forms.Timer();
        private Image _current;
        private volatile bool _busy;
        private bool _paused;
        private string _lastError = "";
        private int _frames;
        private DateTime _fpsSince = DateTime.Now;

        public WatchForm()
        {
            Text = "虚拟机实时画面";
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(60, 60, 720, 480);
            TopMost = true;
            MinimumSize = new Size(320, 240);
            BackColor = Color.FromArgb(20, 20, 22);

            _view.Dock = DockStyle.Fill;
            _view.SizeMode = PictureBoxSizeMode.Zoom;
            _view.BackColor = Color.FromArgb(20, 20, 22);

            _status.Dock = DockStyle.Bottom;
            _status.Height = 22;
            _status.ForeColor = Color.Gainsboro;
            _status.BackColor = Color.FromArgb(30, 30, 34);
            _status.Text = "连接中…";

            Controls.Add(_view);
            Controls.Add(_status);

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.T) { TopMost = !TopMost; UpdateStatus(); }
                else if (e.KeyCode == Keys.Space) { _paused = !_paused; UpdateStatus(); }
            };

            _pump.Interval = 900;
            _pump.Tick += (s, e) => Pump();
            _pump.Start();
            Pump();
        }

        private void Pump()
        {
            if (_paused || _busy) return;
            _busy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[] jpg = Download(Endpoint + "/shot?q=45&nosave=1&t=" + DateTime.Now.Ticks);
                    if (jpg != null && jpg.Length > 1000)
                    {
                        var img = Image.FromStream(new System.IO.MemoryStream(jpg));
                        BeginInvoke(new Action(() =>
                        {
                            var old = _current;
                            _current = img;
                            _view.Image = _current;
                            if (old != null) old.Dispose();
                            _lastError = "";
                            _frames++;
                            UpdateStatus();
                        }));
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    try { BeginInvoke(new Action(UpdateStatus)); } catch { }
                }
                finally { _busy = false; }
            });
        }

        private static byte[] Download(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            req.Timeout = 6000;
            req.ReadWriteTimeout = 6000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var ms = new System.IO.MemoryStream())
            {
                resp.GetResponseStream().CopyTo(ms);
                return ms.ToArray();
            }
        }

        private string GuestStatus()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Endpoint + "/status");
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new System.IO.StreamReader(resp.GetResponseStream()))
                {
                    string s = sr.ReadToEnd();
                    return s.Contains("\"connected\":true") ? "guest=up" : "guest=down";
                }
            }
            catch { return "tunnel=down"; }
        }

        private void UpdateStatus()
        {
            var span = (DateTime.Now - _fpsSince).TotalSeconds;
            if (span >= 3)
            {
                _fps = _frames / span;
                _frames = 0;
                _fpsSince = DateTime.Now;
            }
            string guest = GuestStatus();
            string mode = _paused ? "已暂停" : (string.IsNullOrEmpty(_lastError) ? "实时" : "重试中");
            _status.Text = string.Format(" {0} · {1} · {2:0.0} 帧/秒 · 置顶={3} · Esc关闭 T置顶 空格暂停",
                guest, mode, _fps, TopMost ? "开" : "关");
            _status.ForeColor = guest == "guest=up" && string.IsNullOrEmpty(_lastError) ? Color.Gainsboro : Color.OrangeRed;
        }

        private double _fps;

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new WatchForm());
        }
    }
}
