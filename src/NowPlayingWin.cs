using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UxPlayMirror
{
    /// "Now Playing" screen for music sent from the phone over AirPlay.
    class NowPlayingWin
    {
        readonly Controller c;
        public readonly Window Window;
        readonly DispatcherTimer tick;
        int coverVersion = -1;
        bool hasCover;

        public bool UserClosed { get; private set; }

        T E<T>(string n) where T : class { return (T)Window.FindName(n); }

        static string G(int code) { return ((char)code).ToString(); }

        public NowPlayingWin(Controller controller)
        {
            c = controller;
            Window = (Window)Xaml.Load("NowPlaying.xaml");
            try { Window.Icon = BitmapFrame.Create(new Uri(Paths.IconFile)); } catch { }
            Window.SourceInitialized += (o, e) =>
            {
                var h = new WindowInteropHelper(Window).Handle;
                int round = 2, dark = 1;
                Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                Native.DwmSetWindowAttribute(h, 20, ref dark, 4); // immersive dark mode for the resize border
            };
            E<Button>("NpClose").Click += (o, e) => { UserClosed = true; Window.Hide(); };
            E<Button>("NpPin").Click += (o, e) =>
            {
                Window.Topmost = !Window.Topmost;
                var b = E<Button>("NpPin");
                b.Content = G(Window.Topmost ? 0xE77A : 0xE718);
                b.ToolTip = Window.Topmost ? "เลิกปักหมุด" : "ปักหมุดไว้บนสุด";
            };
            Window.SizeChanged += (o, e) =>
            {
                double side = Math.Max(160, Math.Min(340, Math.Min(Window.ActualWidth - 68, Window.ActualHeight - 330)));
                var f = E<Grid>("ArtFrame");
                f.Width = side; f.Height = side;
                UpdateProgress();
            };
            tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            tick.Tick += (o, e) => UpdateProgress();
            Window.Closed += (o, e) => tick.Stop();
        }

        public void Show()
        {
            UserClosed = false;
            if (!Window.IsVisible) Window.Show();
            tick.Start();
        }

        public void Close() { tick.Stop(); Window.Close(); }

        public void Update()
        {
            var t = c.Server.Track;
            E<TextBlock>("SourceText").Text = "AirPlay จาก " + (c.Server.ClientName ?? "iPhone");
            E<TextBlock>("TitleText").Text = string.IsNullOrEmpty(t.Title) ? "กำลังเล่นเพลง" : t.Title;
            E<TextBlock>("ArtistText").Text = t.Artist ?? "";
            E<TextBlock>("AlbumText").Text = t.Album ?? "";
            Window.Title = (string.IsNullOrEmpty(t.Title) ? "กำลังเล่น" : t.Title) + " – UxPlay Mirror";
            if (t.CoverVersion != coverVersion)
            {
                coverVersion = t.CoverVersion;
                LoadCover(t.HasCover);
            }
            UpdateProgress();
        }

        void LoadCover(bool available)
        {
            BitmapImage img = null;
            if (available)
            {
                try
                {
                    img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    img.UriSource = new Uri(NowPlaying.CoverFile);
                    img.EndInit();
                    img.Freeze();
                }
                catch { img = null; }
            }
            hasCover = img != null;
            var art = E<Border>("ArtImage");
            art.Background = hasCover ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : null;
            art.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
            E<Border>("ArtPlaceholder").Visibility = hasCover ? Visibility.Collapsed : Visibility.Visible;
            E<Image>("BgArt").Source = img;
        }

        void UpdateProgress()
        {
            var t = c.Server.Track;
            double pos = t.LivePosition;
            var track = E<Border>("ProgressFill");
            double full = ((FrameworkElement)track.Parent).ActualWidth;
            track.Width = t.Duration > 0 ? Math.Max(0, Math.Min(1, pos / t.Duration)) * full : 0;
            E<TextBlock>("PosText").Text = t.Duration > 0 ? Time(pos) : "";
            E<TextBlock>("RemainText").Text = t.Duration > 0 ? "-" + Time(t.Duration - pos) : "";
            E<Border>("PausedChip").Visibility = !t.IsPlaying && t.Duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static string Time(double s)
        {
            int v = (int)Math.Max(0, s);
            return (v / 60) + ":" + (v % 60).ToString("00");
        }
    }
}
