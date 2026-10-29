using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace UxPlayMirror
{
    /// Small floating toolbar that sticks to the right side of the video window.
    class ToolbarWin
    {
        readonly Controller c;
        readonly Window w;
        readonly Button shot, rec, draw, full, pin, folder, home;
        readonly TextBlock recTime, fps, fpsUnit;
        readonly Border fpsBox;
        IntPtr hwnd;
        bool visible = true;
        int lastX = int.MinValue, lastY = int.MinValue;

        public IntPtr Handle { get { return hwnd; } }

        static string G(int code) { return ((char)code).ToString(); }

        public ToolbarWin(Controller controller)
        {
            c = controller;
            w = (Window)Xaml.Load("Toolbar.xaml");
            shot = (Button)w.FindName("TbShot");
            rec = (Button)w.FindName("TbRec");
            draw = (Button)w.FindName("TbDraw");
            full = (Button)w.FindName("TbFull");
            pin = (Button)w.FindName("TbPin");
            folder = (Button)w.FindName("TbFolder");
            home = (Button)w.FindName("TbHome");
            recTime = (TextBlock)w.FindName("TbRecTime");
            fps = (TextBlock)w.FindName("TbFps");
            fpsUnit = (TextBlock)w.FindName("TbFpsUnit");
            fpsBox = (Border)w.FindName("TbFpsBox");

            shot.Click += (o, e) => c.TakeScreenshot();
            rec.Click += (o, e) => c.ToggleRecording();
            draw.Click += (o, e) => c.ToggleDrawing();
            full.Click += (o, e) => c.ToggleFullscreen();
            pin.Click += (o, e) => c.TogglePinned();
            folder.Click += (o, e) => c.OpenSaveFolder();
            home.Click += (o, e) => c.ShowMain();

            w.SourceInitialized += (o, e) =>
            {
                hwnd = new WindowInteropHelper(w).Handle;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            };
        }

        public void Attach(IntPtr video)
        {
            new WindowInteropHelper(w).Owner = video;
            w.Left = -10000; w.Top = -10000;
            w.Show();
            Refresh();
            Follow(video, c.VideoFullscreen);
        }

        public void Close()
        {
            try { w.Close(); } catch { }
        }

        public void SetVisible(bool on)
        {
            if (on == visible) return;
            visible = on;
            if (on) w.Show(); else w.Hide();
            lastX = int.MinValue;
        }

        /// Position next to the video window (outside its right edge, or inside when there is no room).
        public void Follow(IntPtr video, bool fullscreen)
        {
            if (hwnd == IntPtr.Zero || video == IntPtr.Zero || !visible) return;
            var f = Native.FrameBounds(video);
            Native.RECT me;
            Native.GetWindowRect(hwnd, out me);
            IntPtr hmon;
            var mi = Native.Monitor(video, out hmon);
            var bounds = fullscreen ? mi.rcMonitor : mi.rcWork;
            int x = f.Right - 6;               // the window has a 12px transparent margin for its shadow
            if (fullscreen || x + me.Width > bounds.Right) x = f.Right - me.Width - 4;
            int y = f.Top + (f.Height - me.Height) / 2;
            if (y < bounds.Top) y = bounds.Top;
            if (x == lastX && y == lastY) return;
            lastX = x; lastY = y;
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        bool hiddenFromCapture;

        public void Refresh()
        {
            bool recording = c.Rec.IsRecording;
            if (hwnd != IntPtr.Zero && recording != hiddenFromCapture)
            {
                // While recording, keep the toolbar out of the captured video (matters in fullscreen).
                hiddenFromCapture = recording;
                Native.SetWindowDisplayAffinity(hwnd, recording ? Native.WDA_EXCLUDEFROMCAPTURE : 0);
            }
            rec.Content = G(recording ? 0xE71A : 0xE7C8);
            rec.Foreground = recording ? Theme.B("Danger") : Theme.B("Text");
            rec.ToolTip = recording ? "หยุดบันทึกวิดีโอ (Ctrl+Alt+R)" : "เริ่มบันทึกวิดีโอ (Ctrl+Alt+R)";
            recTime.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            if (recording)
            {
                var t = DateTime.Now - c.Rec.Started;
                recTime.Text = ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
            }
            draw.Foreground = c.IsDrawing ? Theme.B("Accent") : Theme.B("Text");
            draw.Background = c.IsDrawing ? Theme.B("AccentSoft") : System.Windows.Media.Brushes.Transparent;
            draw.ToolTip = c.IsDrawing ? "เลิกวาด (Esc)" : "ปากกาวาดบนจอ (Ctrl+Alt+D)";
            full.Content = G(c.VideoFullscreen ? 0xE73F : 0xE740);
            full.ToolTip = c.VideoFullscreen ? "ออกจากเต็มจอ (Esc)" : "เต็มจอ (F11 หรือ Ctrl+Alt+F)";
            pin.Content = G(c.VideoPinned ? 0xE77A : 0xE718);
            pin.Foreground = c.VideoPinned ? Theme.B("Accent") : Theme.B("Text");
            pin.ToolTip = c.VideoPinned ? "เลิกปักหมุด" : "ปักหมุดไว้บนสุด";
            foreach (var b in new[] { shot, rec, draw, full, pin, folder, home })
                System.Windows.Automation.AutomationProperties.SetName(b, (string)b.ToolTip);
            RefreshFps();
        }

        public void RefreshFps()
        {
            bool show = c.Server.FpsFresh;
            fpsBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            fps.Text = Math.Round(c.Server.ClientFps).ToString("0");
            fpsUnit.Text = "FPS";
        }

        /// Quick white flash on the camera button after a screenshot.
        public void Flash()
        {
            var a = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(450));
            shot.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }
}
