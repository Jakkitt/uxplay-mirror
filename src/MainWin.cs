using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace UxPlayMirror
{
    static class Xaml
    {
        public static object Load(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("UxPlayMirror." + name))
                return XamlReader.Load(s);
        }
    }

    class MainWin
    {
        readonly Controller c;
        public readonly Window Window;
        readonly DispatcherTimer snackTimer;
        Storyboard pulse;
        bool mediaDirty = true, checksDirty = true;
        string snackFile;

        public bool IsVisible { get { return Window.IsVisible && Window.WindowState != WindowState.Minimized; } }

        T E<T>(string name) where T : class { return (T)Window.FindName(name); }

        public MainWin(Controller controller)
        {
            c = controller;
            Window = (Window)Xaml.Load("MainWindow.xaml");
            try { Window.Icon = BitmapFrame.Create(new Uri(Paths.IconFile)); } catch { }
            try { E<Image>("LogoImage").Source = AppIcon.Largest(Paths.IconFile); } catch { }

            Window.SourceInitialized += (o, e) =>
            {
                var h = new WindowInteropHelper(Window).Handle;
                int round = 2;
                Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            };
            Window.Closing += (o, e) => c.MainClosing(e);
            Window.StateChanged += (o, e) =>
            {
                E<Grid>("RootGrid").Margin = Window.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                E<Button>("BtnMax").Content = Window.WindowState == WindowState.Maximized ? "" : "";
            };
            E<Button>("BtnMin").Click += (o, e) => Window.WindowState = WindowState.Minimized;
            E<Button>("BtnMax").Click += (o, e) => Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            E<Button>("BtnClose").Click += (o, e) => Window.Close();

            // navigation
            E<RadioButton>("NavHome").Checked += (o, e) => ShowPage("PageHome");
            E<RadioButton>("NavMedia").Checked += (o, e) => ShowPage("PageMedia");
            E<RadioButton>("NavSettings").Checked += (o, e) => ShowPage("PageSettings");
            E<RadioButton>("NavHelp").Checked += (o, e) => ShowPage("PageHelp");

            // home
            E<Button>("PowerButton").Click += (o, e) => c.ToggleServer();
            E<Button>("ActShot").Click += (o, e) => c.TakeScreenshot();
            E<Button>("ActRec").Click += (o, e) => c.ToggleRecording();
            E<Button>("ActFull").Click += (o, e) => c.ToggleFullscreen();
            E<Button>("ActDraw").Click += (o, e) => c.ToggleDrawing();
            E<Button>("NowPlayingBtn").Click += (o, e) => c.ShowNowPlaying();
            E<Button>("ActPin").Click += (o, e) => c.TogglePinned();
            E<Button>("RecBannerStop").Click += (o, e) => c.ToggleRecording();

            // media
            E<Button>("MediaRefresh").Click += (o, e) => LoadMedia();
            E<Button>("MediaOpenFolder").Click += (o, e) => c.OpenSaveFolder();

            // settings
            BuildSettings();
            E<Button>("SettingsSave").Click += (o, e) => SaveSettings();
            E<Button>("SettingsReset").Click += (o, e) =>
            {
                var d = new AppSettings();
                d.SaveFolder = AppSettings.DefaultSaveFolder;
                d.TrayHintShown = c.S.TrayHintShown;
                FillSettings(d);
                E<TextBlock>("SettingsNote").Text = "คืนค่าเริ่มต้นแล้ว กด “บันทึกการตั้งค่า” เพื่อใช้งาน";
            };

            // help
            BuildTips();
            E<Button>("RecheckBtn").Click += (o, e) => RunChecks();
            E<Button>("LogCopy").Click += (o, e) => { try { Clipboard.SetText(string.Join(Environment.NewLine, c.LogLines)); ShowSnack("คัดลอก log แล้ว", null, false); } catch { } };
            E<Button>("LogOpen").Click += (o, e) => { try { Process.Start("explorer.exe", "\"" + Paths.LogDir + "\""); } catch { } };
            E<Button>("UpdateBtn").Click += (o, e) =>
            {
                var r = MessageBox.Show(Window,
                    "โปรแกรมจะหยุดรับการเชื่อมต่อชั่วคราว แล้วเปิดหน้าต่าง MSYS2 เพื่อดาวน์โหลดและ build UxPlay เวอร์ชันล่าสุดจาก GitHub\n\nใช้เวลาประมาณ 1–3 นาที ดำเนินการต่อไหม?",
                    "อัปเดต UxPlay", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.OK) c.UpdateUxPlay();
            };

            // snackbar
            snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            snackTimer.Tick += (o, e) => { snackTimer.Stop(); HideSnack(); };
            E<Button>("SnackAction").Click += (o, e) => { if (snackFile != null) Controller.OpenFile(snackFile); HideSnack(); };

            c.Changed += Refresh;
            c.LogLine += AppendLog;
            c.MediaChanged += () => { mediaDirty = true; if (E<Grid>("PageMedia").Visibility == Visibility.Visible) LoadMedia(); };
            Window.Closed += (o, e) => { c.Changed -= Refresh; c.LogLine -= AppendLog; };

            BuildPulse();
            Refresh();
        }

        public void Show()
        {
            if (!Window.IsVisible) Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
            Window.Topmost = true; Window.Topmost = false;
            Window.Focus();
        }

        public void ThemeChanged()
        {
            BuildPulse();
            Refresh();
            BuildSettingsKeepValues();
            if (E<Grid>("PageMedia").Visibility == Visibility.Visible) LoadMedia(); else mediaDirty = true;
            checksDirty = true;
            if (E<ScrollViewer>("PageHelp").Visibility == Visibility.Visible) RunChecks();
        }

        void ShowPage(string name)
        {
            foreach (var p in new[] { "PageHome", "PageMedia", "PageSettings", "PageHelp" })
                ((FrameworkElement)Window.FindName(p)).Visibility = p == name ? Visibility.Visible : Visibility.Collapsed;
            if (name == "PageMedia" && mediaDirty) LoadMedia();
            if (name == "PageSettings") { FillSettings(c.S); E<TextBlock>("SettingsNote").Text = ""; }
            if (name == "PageHelp")
            {
                if (checksDirty) RunChecks();
                var box = E<TextBox>("LogBox");
                box.Text = string.Join("\n", c.LogLines);
                box.ScrollToEnd();
            }
        }

        // ================= HOME =================

        void BuildPulse()
        {
            if (pulse != null) pulse.Stop();
            var ring = E<Ellipse>("PulseRing");
            pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var sx = new DoubleAnimation(0.8, 1.22, TimeSpan.FromSeconds(1.8)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var sy = sx.Clone();
            var op = new DoubleAnimation(0.7, 0, TimeSpan.FromSeconds(1.8));
            Storyboard.SetTarget(sx, ring); Storyboard.SetTargetProperty(sx, new PropertyPath("RenderTransform.ScaleX"));
            Storyboard.SetTarget(sy, ring); Storyboard.SetTargetProperty(sy, new PropertyPath("RenderTransform.ScaleY"));
            Storyboard.SetTarget(op, ring); Storyboard.SetTargetProperty(op, new PropertyPath("Opacity"));
            pulse.Children.Add(sx); pulse.Children.Add(sy); pulse.Children.Add(op);
        }

        ServerState? pulseFor;

        public void Refresh()
        {
            var st = c.Server.State;
            string name = c.Server.ClientName ?? "iPhone";
            string pill, title, detail;
            Brush pillBg, pillFg, power;
            switch (st)
            {
                case ServerState.Starting:
                    pill = "กำลังเริ่ม"; title = "กำลังเตรียมตัวรับสัญญาณ…"; detail = "รอสักครู่";
                    pillBg = Theme.B("AccentSoft"); pillFg = Theme.B("Accent"); power = Theme.B("Accent"); break;
                case ServerState.Ready:
                    pill = "รอการเชื่อมต่อ"; title = "พร้อมเชื่อมต่อ";
                    detail = "เปิด “การสะท้อนหน้าจอ” บน iPhone แล้วเลือก “" + c.S.DisplayName + "”";
                    pillBg = Theme.B("AccentSoft"); pillFg = Theme.B("Accent"); power = Theme.B("Accent"); break;
                case ServerState.Connected:
                    if (c.HasNowPlaying)
                    {
                        var tr = c.Server.Track;
                        pill = "กำลังเล่นเพลง"; title = string.IsNullOrEmpty(tr.Title) ? "กำลังเล่นเพลงจาก " + name : tr.Title;
                        detail = (string.IsNullOrEmpty(tr.Artist) ? "" : tr.Artist + " · ") + "เล่นผ่าน AirPlay จาก " + name;
                    }
                    else
                    {
                        pill = "เชื่อมต่อแล้ว"; title = "เชื่อมต่อกับ " + name;
                        detail = "รอภาพจาก iPhone… (ถ้าเปิดเพลงผ่าน AirPlay เสียงจะออกที่คอมนี้)";
                    }
                    pillBg = Theme.B("SuccessSoft"); pillFg = Theme.B("Success"); power = Theme.B("Success"); break;
                case ServerState.Mirroring:
                    pill = c.Rec.IsRecording ? "กำลังบันทึก" : "กำลังแชร์จอ"; title = "กำลังแสดงหน้าจอจาก " + name;
                    detail = (c.Server.ClientModel != null ? "รุ่น " + c.Server.ClientModel + " · " : "") +
                             (c.Server.FpsFresh ? Math.Round(c.Server.ClientFps) + " FPS · " : "") +
                             "กด F11 ที่หน้าต่างภาพเพื่อเต็มจอ";
                    pillBg = c.Rec.IsRecording ? Theme.B("DangerSoft") : Theme.B("SuccessSoft");
                    pillFg = c.Rec.IsRecording ? Theme.B("Danger") : Theme.B("Success"); power = Theme.B("Success"); break;
                case ServerState.Error:
                    pill = "มีปัญหา"; title = "เริ่มตัวรับสัญญาณไม่สำเร็จ";
                    detail = (string.IsNullOrEmpty(c.Server.LastError) ? "" : c.Server.LastError + " · ") + "กดปุ่มเพื่อลองอีกครั้ง หรือดูหน้า “ตรวจสอบและช่วยเหลือ”";
                    pillBg = Theme.B("DangerSoft"); pillFg = Theme.B("Danger"); power = Theme.B("Danger"); break;
                default:
                    pill = "ปิดอยู่"; title = "ปิดรับการเชื่อมต่ออยู่";
                    detail = "กดปุ่มเปิดเพื่อให้ iPhone มองเห็นคอมเครื่องนี้";
                    pillBg = Theme.B("SurfaceAlt"); pillFg = Theme.B("TextMuted"); power = Theme.B("PowerOff"); break;
            }

            E<Border>("StatusPill").Background = pillBg;
            E<Ellipse>("StatusDot").Fill = pillFg;
            var pt = E<TextBlock>("StatusPillText"); pt.Text = pill; pt.Foreground = pillFg;
            E<TextBlock>("StatusTitle").Text = title;
            E<TextBlock>("StatusDetail").Text = detail;
            var pb = E<Button>("PowerButton");
            pb.Background = power;
            pb.ToolTip = c.Server.IsRunning ? "หยุดรับการเชื่อมต่อ" : "เริ่มรับการเชื่อมต่อ";
            E<TextBlock>("ServerNameText").Text = c.S.DisplayName;
            bool pin = c.S.PinEnabled && AppSettings.IsValidPin(c.S.Pin);
            E<Border>("PinChip").Visibility = pin ? Visibility.Visible : Visibility.Collapsed;
            E<TextBlock>("PinText").Text = pin ? c.S.Pin : "";
            E<TextBlock>("QualityText").Text = c.S.QualityLabel;
            E<Button>("NowPlayingBtn").Visibility = c.HasNowPlaying ? Visibility.Visible : Visibility.Collapsed;

            if (pulseFor != st)
            {
                pulseFor = st;
                if (st == ServerState.Ready) pulse.Begin(); else { pulse.Stop(); E<Ellipse>("PulseRing").Opacity = 0; }
            }

            // phone illustration
            var screen = E<Border>("PhoneScreen");
            var glyph = E<TextBlock>("PhoneGlyph");
            var label = E<TextBlock>("PhoneLabel");
            if (st == ServerState.Mirroring || st == ServerState.Connected)
            {
                var acc = ((SolidColorBrush)Theme.B("Accent")).Color;
                screen.Background = st == ServerState.Mirroring
                    ? (Brush)new LinearGradientBrush(acc, Color.FromRgb(0x8B, 0x5C, 0xF6), 60)
                    : Theme.B("AccentSoft");
                glyph.Text = st == ServerState.Mirroring ? "" : "";
                glyph.Foreground = st == ServerState.Mirroring ? Brushes.White : Theme.B("Accent");
                label.Text = name;
                label.Foreground = st == ServerState.Mirroring ? Brushes.White : Theme.B("Accent");
                if (c.HasNowPlaying && c.Server.Track.HasCover)
                {
                    // Show the album art on the phone illustration while music plays.
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        bi.DecodePixelWidth = 220;
                        bi.UriSource = new Uri(NowPlaying.CoverFile);
                        bi.EndInit();
                        screen.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                        glyph.Text = "";
                        label.Text = "";
                    }
                    catch { }
                }
            }
            else
            {
                screen.Background = Theme.B("SurfaceAlt");
                glyph.Text = "";
                glyph.Foreground = Theme.B("TextMuted");
                label.Text = st == ServerState.Ready ? "รอการเชื่อมต่อ" : "";
                label.Foreground = Theme.B("TextMuted");
            }

            // steps
            var ips = LocalAddresses();
            E<TextBlock>("Step1Desc").Text = "ให้ iPhone ต่อ Wi-Fi ของเราเตอร์เดียวกับคอม" +
                (ips.Count > 0 ? " (คอมนี้: " + ips[0] + ")" : "");
            E<TextBlock>("Step3Desc").Text = "แตะ “" + c.S.DisplayName + "” ในรายการ" + (pin ? " แล้วใส่รหัส PIN " + c.S.Pin : "");

            // tools
            bool mirroring = c.IsMirroring;
            bool recording = c.Rec.IsRecording;
            E<Button>("ActShot").IsEnabled = mirroring;
            E<Button>("ActRec").IsEnabled = mirroring || recording;
            E<Button>("ActFull").IsEnabled = mirroring;
            E<Button>("ActPin").IsEnabled = mirroring;
            E<Button>("ActDraw").IsEnabled = mirroring;
            E<TextBlock>("ActDrawText").Text = c.IsDrawing ? "เลิกวาด" : "ปากกาวาด";
            E<TextBlock>("ActRecGlyph").Text = recording ? "" : "";
            E<TextBlock>("ActRecGlyph").Foreground = recording ? Theme.B("Danger") : Theme.B("Accent");
            E<TextBlock>("ActRecText").Text = recording ? "หยุดบันทึก" : "บันทึกวิดีโอ";
            E<TextBlock>("ActFullGlyph").Text = c.VideoFullscreen ? "" : "";
            E<TextBlock>("ActFullText").Text = c.VideoFullscreen ? "ออกจากเต็มจอ" : "เต็มจอ";
            E<TextBlock>("ActPinGlyph").Text = c.VideoPinned ? "" : "";
            E<TextBlock>("ActPinText").Text = c.VideoPinned ? "เลิกปักหมุด" : "ปักหมุดไว้บนสุด";
            E<TextBlock>("ToolsHint").Text = mirroring
                ? "ใช้แถบเครื่องมือข้างหน้าต่างภาพได้เหมือนกัน · คีย์ลัดจากทุกโปรแกรม: Ctrl+Alt+S ถ่ายภาพ · Ctrl+Alt+R อัดวิดีโอ · Ctrl+Alt+D ปากกา · Ctrl+Alt+F เต็มจอ"
                : "ปุ่มเหล่านี้ใช้ได้เมื่อ iPhone กำลังแชร์หน้าจอ ภาพและวิดีโอจะบันทึกไว้ที่ " + c.S.SaveFolder;

            var banner = E<Border>("RecBanner");
            banner.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            if (recording)
            {
                var t = DateTime.Now - c.Rec.Started;
                E<TextBlock>("RecBannerText").Text = "กำลังบันทึกวิดีโอ  " + ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
                E<Ellipse>("RecDot").Opacity = t.Seconds % 2 == 0 ? 1 : 0.35;
            }

            // sidebar
            E<Ellipse>("SideDot").Fill = st == ServerState.Stopped ? Theme.B("TextMuted") : pillFg;
            E<TextBlock>("SideStatus").Text = c.Server.IsRunning ? "ตัวรับสัญญาณเปิดอยู่" : (st == ServerState.Error ? "ตัวรับสัญญาณมีปัญหา" : "ตัวรับสัญญาณปิดอยู่");
            E<TextBlock>("SideDetail").Text = c.StateText();

            E<TextBlock>("AboutText").Text = "UxPlay Mirror " + Paths.AppVersion + " · ใช้ UxPlay " + (c.UxPlayVersion ?? "?") +
                " (โอเพนซอร์ส GPLv3) และ GStreamer · ติดตั้ง UxPlay ไว้ที่ " + Paths.UxPlayExe;
        }

        static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;
                    foreach (var a in props.UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(a.Address.ToString());
                }
            }
            catch { }
            return list;
        }

        // ================= SNACKBAR =================

        public void ShowSnack(string text, string file, bool isError)
        {
            snackFile = file;
            E<TextBlock>("SnackText").Text = text;
            var icon = E<TextBlock>("SnackIcon");
            icon.Text = isError ? "" : "";
            var act = E<Button>("SnackAction");
            act.Visibility = file != null ? Visibility.Visible : Visibility.Collapsed;
            act.Content = file != null && file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "เล่นวิดีโอ" : "เปิดดู";
            var s = E<Border>("Snack");
            s.Visibility = Visibility.Visible;
            s.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            snackTimer.Stop();
            snackTimer.Start();
        }

        void HideSnack()
        {
            var s = E<Border>("Snack");
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            a.Completed += (o, e) => { if (s.Opacity == 0) s.Visibility = Visibility.Collapsed; };
            s.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // ================= MEDIA =================

        void LoadMedia()
        {
            mediaDirty = false;
            var panel = E<WrapPanel>("MediaPanel");
            panel.Children.Clear();
            string dir = c.S.SaveFolder;
            E<TextBlock>("MediaFolderText").Text = dir;
            var files = new List<FileInfo>();
            try
            {
                if (Directory.Exists(dir))
                    files = new DirectoryInfo(dir).GetFiles()
                        .Where(f => { string x = f.Extension.ToLowerInvariant(); return x == ".png" || x == ".jpg" || x == ".mp4"; })
                        .OrderByDescending(f => f.LastWriteTime).Take(120).ToList();
            }
            catch { }
            E<StackPanel>("MediaEmpty").Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var f in files) panel.Children.Add(MediaCard(f));
        }

        UIElement MediaCard(FileInfo f)
        {
            bool video = f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
            var card = new Button { Style = (Style)Application.Current.Resources["MediaCard"], Width = 212, Margin = new Thickness(0, 0, 14, 14) };
            var root = new StackPanel();
            var thumbArea = new Grid { Height = 124, Background = Theme.B("SurfaceAlt") };
            thumbArea.Clip = new RectangleGeometry(new Rect(0, 0, 198, 124), 8, 8);
            var img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            thumbArea.Children.Add(img);
            if (video)
            {
                var play = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
                play.Child = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
                thumbArea.Children.Add(play);
            }
            root.Children.Add(thumbArea);
            var name = new TextBlock { Text = f.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 9, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var meta = new TextBlock
            {
                Text = (video ? "วิดีโอ · " : "ภาพ · ") + f.LastWriteTime.ToString("d MMM yyyy HH:mm") + " · " + SizeText(f.Length),
                FontSize = 11.5, Foreground = Theme.B("TextMuted"), Margin = new Thickness(4, 2, 4, 4), TextTrimming = TextTrimming.CharacterEllipsis
            };
            root.Children.Add(name);
            root.Children.Add(meta);
            card.Content = root;
            card.ToolTip = f.FullName;
            string path = f.FullName;
            card.Click += (o, e) => Controller.OpenFile(path);

            var menu = new ContextMenu();
            menu.Items.Add(MenuItem(video ? "เล่น" : "เปิด", () => Controller.OpenFile(path)));
            menu.Items.Add(MenuItem("แสดงในโฟลเดอร์", () => Controller.RevealFile(path)));
            if (!video) menu.Items.Add(MenuItem("คัดลอกภาพ", () =>
            {
                try { Clipboard.SetImage(new BitmapImage(new Uri(path))); ShowSnack("คัดลอกภาพแล้ว", null, false); } catch { }
            }));
            menu.Items.Add(MenuItem("ย้ายไปถังขยะ", () =>
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    LoadMedia();
                }
                catch (Exception ex) { ShowSnack("ลบไม่สำเร็จ: " + ex.Message, null, true); }
            }));
            card.ContextMenu = menu;

            if (video)
            {
                var ph = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 30, Foreground = Theme.B("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                thumbArea.Children.Insert(0, ph);
            }
            Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { img.Source = LoadThumb(path, video); }));
            return card;
        }

        static ImageSource LoadThumb(string path, bool video)
        {
            if (video) return ShellThumb.Get(path, 400);
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;          // don't keep the file locked
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.DecodePixelHeight = 248;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        static MenuItem MenuItem(string text, Action a)
        {
            var m = new MenuItem { Header = text };
            m.Click += (o, e) => a();
            return m;
        }

        static string SizeText(long b)
        {
            if (b > 1024L * 1024 * 1024) return (b / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
            if (b > 1024 * 1024) return (b / 1024.0 / 1024).ToString("0.0") + " MB";
            return Math.Max(1, b / 1024) + " KB";
        }

        // ================= SETTINGS =================

        TextBox tbName, tbPin;
        CheckBox swNowPlaying, swHotkeys;
        CheckBox swAppendHost, swFull, swTop, swFit, swLowLat, swAudio, swPin, swRecAudio, swCopy, swAutoServe, swStartup, swCloseTray, swSoft, swNoHold;
        RadioButton[] segRes, segFps, segQuality, segTheme, segRenderer;
        TextBlock folderText;
        string pendingFolder;

        void BuildSettingsKeepValues()
        {
            var current = ReadSettings();
            BuildSettings();
            FillSettings(current);
        }

        void BuildSettings()
        {
            var panel = E<StackPanel>("SettingsPanel");
            panel.Children.Clear();
            var title = new TextBlock { Text = "ตั้งค่า", Style = (Style)Application.Current.Resources["H1"], Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(title);

            var sec = Section(panel, "", "การเชื่อมต่อ");
            tbName = Input(200);
            swAppendHost = Switch();
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(tbName);
            Row(sec, "ชื่อที่จะเห็นบน iPhone", "ใช้ได้เฉพาะตัวอักษรภาษาอังกฤษ ตัวเลข เว้นวรรค และ - _ .", nameRow);
            Row(sec, "ต่อท้ายชื่อด้วยชื่อคอม", "เช่น “UxPlay@" + Paths.HostName + "” ช่วยแยกเครื่องได้ถ้ามีหลายเครื่อง", swAppendHost);
            swPin = Switch();
            tbPin = Input(84);
            tbPin.MaxLength = 4;
            tbPin.HorizontalContentAlignment = HorizontalAlignment.Center;
            tbPin.FontWeight = FontWeights.SemiBold;
            var rnd = new Button { Style = (Style)Application.Current.Resources["Btn"], Content = "สุ่มรหัส", Margin = new Thickness(8, 0, 12, 0), Padding = new Thickness(12, 6, 12, 6) };
            rnd.Click += (o, e) => { tbPin.Text = new Random().Next(1001, 9999).ToString(); swPin.IsChecked = true; };
            var pinRow = new StackPanel { Orientation = Orientation.Horizontal };
            pinRow.Children.Add(tbPin); pinRow.Children.Add(rnd); pinRow.Children.Add(swPin);
            Row(sec, "ขอรหัส PIN ก่อนเชื่อมต่อ", "กันคนอื่นในวง Wi-Fi เดียวกันแอบแชร์จอขึ้นคอมคุณ ใส่ตัวเลข 4 หลัก", pinRow);
            swNoHold = Switch();
            Row(sec, "ให้เครื่องใหม่แทนที่การเชื่อมต่อเดิมได้", "ถ้าเปิด เมื่อมีอีกเครื่องเชื่อมต่อเข้ามา เครื่องเดิมจะถูกตัด", swNoHold);

            sec = Section(panel, "", "ภาพ");
            Row(sec, "ความละเอียด", "4K ใช้ได้กับ iPhone รุ่นใหม่ และต้องใช้เน็ตบ้านที่เร็วพอ", Segmented(new[] { "1080p", "1440p", "4K" }, out segRes));
            Row(sec, "อัตราเฟรม", "60 FPS ลื่นกว่า แต่ใช้ Wi-Fi มากขึ้น · 120 FPS เป็นโหมดทดลอง iPhone ส่วนใหญ่ส่งภาพทาง AirPlay ได้สูงสุด 60", Segmented(new[] { "30 FPS", "60 FPS", "120 FPS" }, out segFps));
            swLowLat = Switch();
            Row(sec, "โหมดหน่วงต่ำ", "ภาพตอบสนองไวขึ้น (แนะนำ) ถ้าเสียงกับภาพไม่ตรงกันให้ปิด", swLowLat);
            swFull = Switch();
            Row(sec, "เปิดเต็มจอทันทีเมื่อเริ่มแชร์", "กด Esc หรือ F11 เพื่อออกจากเต็มจอ", swFull);
            swFit = Switch();
            Row(sec, "ปรับขนาดหน้าต่างให้พอดีกับจอ iPhone", "ตัดแถบดำด้านข้างออก และปรับใหม่อัตโนมัติเมื่อหมุนเครื่อง", swFit);
            swTop = Switch();
            Row(sec, "ให้หน้าต่างภาพอยู่บนสุดเสมอ", "หน้าต่างภาพจะไม่ถูกหน้าต่างอื่นบัง", swTop);

            sec = Section(panel, "", "เสียง");
            swAudio = Switch();
            Row(sec, "เล่นเสียงจาก iPhone ที่คอม", "ปิดถ้าอยากให้เสียงออกที่ iPhone อย่างเดียว", swAudio);
            swNowPlaying = Switch();
            Row(sec, "แสดงหน้าจอเพลงเมื่อเปิดเพลงผ่าน AirPlay", "ขึ้นปกอัลบั้ม ชื่อเพลง ศิลปิน และเวลาของเพลง", swNowPlaying);

            sec = Section(panel, "", "คีย์ลัด");
            swHotkeys = Switch();
            Row(sec, "ใช้คีย์ลัดได้จากทุกโปรแกรมระหว่างแชร์จอ",
                "Ctrl+Alt+S ถ่ายภาพ · Ctrl+Alt+R เริ่ม/หยุดอัดวิดีโอ · Ctrl+Alt+D ปากกาวาด · Ctrl+Alt+F เต็มจอ", swHotkeys);

            sec = Section(panel, "", "ภาพหน้าจอและการบันทึก");
            folderText = new TextBlock { Style = (Style)Application.Current.Resources["Muted"], MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            var change = new Button { Style = (Style)Application.Current.Resources["Btn"], Content = "เปลี่ยน…", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
            change.Click += (o, e) =>
            {
                using (var dlg = new WinForms.FolderBrowserDialog())
                {
                    dlg.Description = "เลือกโฟลเดอร์สำหรับเก็บภาพและวิดีโอ";
                    dlg.SelectedPath = pendingFolder;
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK) { pendingFolder = dlg.SelectedPath; folderText.Text = pendingFolder; ApplyInstant(); }
                }
            };
            var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
            folderRow.Children.Add(folderText); folderRow.Children.Add(change);
            Row(sec, "โฟลเดอร์เก็บไฟล์", null, folderRow);
            swCopy = Switch();
            Row(sec, "คัดลอกภาพหน้าจอไปคลิปบอร์ดด้วย", "ถ่ายแล้ววางในแชตหรือเอกสารได้ทันที", swCopy);
            swRecAudio = Switch();
            Row(sec, "บันทึกเสียงจาก iPhone ลงในวิดีโอ", "อัดเฉพาะเสียงของ iPhone ไม่รวมเสียงอื่นในคอม", swRecAudio);
            Row(sec, "คุณภาพวิดีโอ", "“สูง” ภาพคมกว่าแต่ไฟล์ใหญ่ขึ้น (ประมาณ 120 MB ต่อนาทีที่ 1080p)", Segmented(new[] { "มาตรฐาน", "สูง" }, out segQuality));

            sec = Section(panel, "", "โปรแกรม");
            Row(sec, "ธีม", null, Segmented(new[] { "ตามระบบ", "สว่าง", "มืด" }, out segTheme));
            swAutoServe = Switch();
            Row(sec, "เปิดรับการเชื่อมต่อทันทีเมื่อเปิดโปรแกรม", null, swAutoServe);
            swStartup = Switch();
            Row(sec, "เปิดโปรแกรมพร้อม Windows", "เริ่มทำงานเงียบ ๆ ที่ถาดมุมขวาล่าง ไม่เปิดหน้าต่าง", swStartup);
            swCloseTray = Switch();
            Row(sec, "กดปิดหน้าต่างแล้วให้ทำงานต่อที่ถาด", "ถ้าปิดตัวเลือกนี้ กดปิดหน้าต่างจะออกจากโปรแกรมเลย", swCloseTray);

            sec = Section(panel, "", "ขั้นสูง");
            Row(sec, "ตัวแสดงผลภาพ", "ถ้าภาพไม่ขึ้นหรือกระพริบ ลองเปลี่ยนเป็น Direct3D 11", Segmented(new[] { "Direct3D 12", "Direct3D 11" }, out segRenderer));
            swSoft = Switch();
            Row(sec, "ถอดรหัสภาพด้วยซอฟต์แวร์", "ใช้เมื่อภาพเพี้ยนหรือเป็นสีเขียว (ใช้ CPU มากขึ้น)", swSoft);

            // These take effect right away; everything else waits for the Save button.
            foreach (var sw in new[] { swTop, swFit, swHotkeys, swStartup, swCloseTray, swCopy, swRecAudio })
            {
                sw.Checked += (o, e) => ApplyInstant();
                sw.Unchecked += (o, e) => ApplyInstant();
            }
            foreach (var rb in segQuality) rb.Checked += (o, e) => ApplyInstant();
            foreach (var rb in segTheme) rb.Checked += (o, e) => ApplyInstant();
        }

        bool filling;

        /// Saves the settings that don't need the receiver restarted, as soon as they change.
        void ApplyInstant()
        {
            if (filling) return;
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var s = c.S.Clone();
                s.VideoOnTop = swTop.IsChecked == true;
                s.AutoFitWindow = swFit.IsChecked == true;
                s.Hotkeys = swHotkeys.IsChecked == true;
                s.StartWithWindows = swStartup.IsChecked == true;
                s.CloseToTray = swCloseTray.IsChecked == true;
                s.CopyScreenshot = swCopy.IsChecked == true;
                s.RecordAudio = swRecAudio.IsChecked == true;
                s.RecordQuality = Selected(segQuality) == 0 ? "standard" : "high";
                s.Theme = new[] { "system", "light", "dark" }[Selected(segTheme)];
                s.SaveFolder = pendingFolder;
                c.ApplySettings(s, false);
                var note = E<TextBlock>("SettingsNote");
                note.Foreground = Theme.B("Success");
                note.Text = "เปลี่ยนแล้ว มีผลทันที";
            }));
        }

        StackPanel Section(StackPanel parent, string glyph, string title)
        {
            var card = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(22, 16, 22, 6) };
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(new TextBlock { Text = glyph, Style = (Style)Application.Current.Resources["Glyph"], Foreground = Theme.B("Accent") });
            head.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H2"], Margin = new Thickness(10, 0, 0, 0) });
            sp.Children.Add(head);
            card.Child = sp;
            parent.Children.Add(card);
            return sp;
        }

        void Row(StackPanel sec, string title, string desc, UIElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 56 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 20, 10) };
            text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            if (desc != null) text.Children.Add(new TextBlock { Text = desc, Style = (Style)Application.Current.Resources["Muted"], FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(text);
            var fe = (FrameworkElement)control;
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 1);
            g.Children.Add(fe);
            if (sec.Children.Count > 1)
                sec.Children.Add(new Border { Height = 1, Background = Theme.B("Stroke") });
            sec.Children.Add(g);
        }

        static CheckBox Switch() { return new CheckBox { Style = (Style)Application.Current.Resources["Switch"] }; }

        static TextBox Input(double width) { return new TextBox { Style = (Style)Application.Current.Resources["Input"], Width = width }; }

        static Border Segmented(string[] labels, out RadioButton[] items)
        {
            string group = "g" + Guid.NewGuid().ToString("N");
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            items = new RadioButton[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                items[i] = new RadioButton { Content = labels[i], GroupName = group, Style = (Style)Application.Current.Resources["Seg"] };
                sp.Children.Add(items[i]);
            }
            return new Border { Background = Theme.B("SegBg"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Child = sp };
        }

        static void Select(RadioButton[] items, int index) { items[Math.Max(0, Math.Min(items.Length - 1, index))].IsChecked = true; }
        static int Selected(RadioButton[] items) { for (int i = 0; i < items.Length; i++) if (items[i].IsChecked == true) return i; return 0; }

        void FillSettings(AppSettings s)
        {
            filling = true;
            try { FillControls(s); } finally { filling = false; }
        }

        void FillControls(AppSettings s)
        {
            tbName.Text = s.DeviceName;
            swAppendHost.IsChecked = s.AppendHost;
            swPin.IsChecked = s.PinEnabled;
            tbPin.Text = s.Pin;
            swNoHold.IsChecked = s.NoHold;
            Select(segRes, s.Resolution == "1440p" ? 1 : s.Resolution == "4k" ? 2 : 0);
            Select(segFps, s.Fps == 30 ? 0 : s.Fps == 120 ? 2 : 1);
            swLowLat.IsChecked = s.LowLatency;
            swFull.IsChecked = s.FullscreenOnConnect;
            swTop.IsChecked = s.VideoOnTop;
            swFit.IsChecked = s.AutoFitWindow;
            swAudio.IsChecked = s.Audio;
            swNowPlaying.IsChecked = s.ShowNowPlaying;
            swHotkeys.IsChecked = s.Hotkeys;
            pendingFolder = s.SaveFolder;
            folderText.Text = s.SaveFolder;
            swCopy.IsChecked = s.CopyScreenshot;
            swRecAudio.IsChecked = s.RecordAudio;
            Select(segQuality, s.RecordQuality == "standard" ? 0 : 1);
            Select(segTheme, s.Theme == "light" ? 1 : s.Theme == "dark" ? 2 : 0);
            swAutoServe.IsChecked = s.AutoServe;
            swStartup.IsChecked = s.StartWithWindows;
            swCloseTray.IsChecked = s.CloseToTray;
            Select(segRenderer, s.Renderer == "d3d11" ? 1 : 0);
            swSoft.IsChecked = s.SoftwareDecode;
        }

        AppSettings ReadSettings()
        {
            var s = c.S.Clone();
            s.DeviceName = tbName.Text.Trim();
            s.AppendHost = swAppendHost.IsChecked == true;
            s.PinEnabled = swPin.IsChecked == true;
            s.Pin = tbPin.Text.Trim();
            s.NoHold = swNoHold.IsChecked == true;
            s.Resolution = new[] { "1080p", "1440p", "4k" }[Selected(segRes)];
            s.Fps = new[] { 30, 60, 120 }[Selected(segFps)];
            s.LowLatency = swLowLat.IsChecked == true;
            s.FullscreenOnConnect = swFull.IsChecked == true;
            s.VideoOnTop = swTop.IsChecked == true;
            s.AutoFitWindow = swFit.IsChecked == true;
            s.Audio = swAudio.IsChecked == true;
            s.ShowNowPlaying = swNowPlaying.IsChecked == true;
            s.Hotkeys = swHotkeys.IsChecked == true;
            s.SaveFolder = pendingFolder;
            s.CopyScreenshot = swCopy.IsChecked == true;
            s.RecordAudio = swRecAudio.IsChecked == true;
            s.RecordQuality = Selected(segQuality) == 0 ? "standard" : "high";
            s.Theme = new[] { "system", "light", "dark" }[Selected(segTheme)];
            s.AutoServe = swAutoServe.IsChecked == true;
            s.StartWithWindows = swStartup.IsChecked == true;
            s.CloseToTray = swCloseTray.IsChecked == true;
            s.Renderer = Selected(segRenderer) == 1 ? "d3d11" : "d3d12";
            s.SoftwareDecode = swSoft.IsChecked == true;
            return s;
        }

        void SaveSettings()
        {
            var s = ReadSettings();
            var note = E<TextBlock>("SettingsNote");
            note.Foreground = Theme.B("Danger");
            if (!AppSettings.IsValidName(s.DeviceName)) { note.Text = "ชื่อไม่ถูกต้อง: ใช้ภาษาอังกฤษ ตัวเลข เว้นวรรค - _ . ได้ไม่เกิน 32 ตัว"; tbName.Focus(); return; }
            if (s.PinEnabled && !AppSettings.IsValidPin(s.Pin)) { note.Text = "รหัส PIN ต้องเป็นตัวเลข 4 หลัก (ห้ามเป็น 0000)"; tbPin.Focus(); return; }

            bool needRestart = c.Server.IsRunning && s.ServerKey() != c.Server.RunningKey;
            if (needRestart && c.IsMirroring)
            {
                var r = MessageBox.Show(Window, "ต้องเริ่มตัวรับสัญญาณใหม่เพื่อใช้การตั้งค่านี้ iPhone จะหลุดการเชื่อมต่อ แล้วต้องเลือกเชื่อมต่อใหม่\n\nดำเนินการต่อไหม?",
                    "บันทึกการตั้งค่า", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK) return;
            }
            c.ApplySettings(s, needRestart);
            note.Foreground = Theme.B("Success");
            note.Text = needRestart ? "บันทึกแล้ว และเริ่มตัวรับสัญญาณใหม่ให้แล้ว" : "บันทึกการตั้งค่าแล้ว";
        }

        // ================= HELP =================

        class Check
        {
            public int Level; // 0 ok, 1 warning, 2 error
            public string Title, Desc, ActionText;
            public Action Action;
        }

        void RunChecks()
        {
            checksDirty = false;
            var panel = E<StackPanel>("ChecksPanel");
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = "กำลังตรวจ…", Style = (Style)Application.Current.Resources["Muted"] });
            int pid = c.Server.Pid;
            var ports = c.Server.ListeningPorts();
            string ver = c.UxPlayVersion;
            Task.Factory.StartNew(() => CollectChecks(pid, ports, ver)).ContinueWith(t =>
            {
                panel.Children.Clear();
                foreach (var ch in t.Result) panel.Children.Add(CheckRow(ch));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        List<Check> CollectChecks(int pid, List<int> ports, string ver)
        {
            var list = new List<Check>();
            if (File.Exists(Paths.UxPlayExe))
                list.Add(new Check { Level = 0, Title = "ติดตั้ง UxPlay แล้ว", Desc = "เวอร์ชัน " + (ver ?? "?") + " · " + Paths.UxPlayExe });
            else
                list.Add(new Check { Level = 2, Title = "ไม่พบ UxPlay", Desc = "ต้องติดตั้ง UxPlay ไว้ที่ " + Paths.UxPlayExe });

            int sac = SmartAppControl.State;
            Action openSac = () => Controller.OpenFile(SmartAppControl.SettingsUri);
            if (sac == SmartAppControl.On)
                list.Add(new Check
                {
                    Level = 2, Title = "Smart App Control บล็อก UxPlay อยู่",
                    Desc = "Windows ไม่ยอมให้ไฟล์ของ GStreamer ที่ UxPlay ใช้ทำงาน ต้องปิดที่ Windows Security → App & browser control → Smart App Control (ปิดแล้วเปิดกลับไม่ได้)",
                    ActionText = "เปิด Windows Security", Action = openSac
                });
            else if (sac == SmartAppControl.Evaluation)
                list.Add(new Check
                {
                    Level = 1, Title = "Smart App Control อยู่ในโหมดประเมินผล",
                    Desc = "ตอนนี้ยังใช้งานได้ แต่ถ้า Windows เปลี่ยนเป็นเปิดใช้งานเองภายหลัง UxPlay จะถูกบล็อก ถ้าเจอให้กลับมาดูหน้านี้",
                    ActionText = "เปิด Windows Security", Action = openSac
                });

            bool d3d = File.Exists(System.IO.Path.Combine(Paths.MsysRoot, @"ucrt64\lib\gstreamer-1.0\libgstd3d12.dll"));
            list.Add(d3d
                ? new Check { Level = 0, Title = "ตัวแสดงผล GStreamer พร้อม", Desc = "Direct3D 12 / 11 และตัวถอดรหัสด้วยการ์ดจอ" }
                : new Check { Level = 1, Title = "ไม่พบปลั๊กอิน Direct3D 12 ของ GStreamer", Desc = "ลองเปลี่ยนตัวแสดงผลเป็น Direct3D 11 ในหน้าตั้งค่า" });

            var ips = LocalAddresses();
            list.Add(ips.Count > 0
                ? new Check { Level = 0, Title = "เชื่อมต่อเครือข่ายอยู่", Desc = "IP ของคอมนี้: " + string.Join(", ", ips) + " · iPhone ต้องอยู่ในวง Wi-Fi เดียวกัน" }
                : new Check { Level = 2, Title = "คอมไม่ได้เชื่อมต่อเครือข่าย", Desc = "ต่อสาย LAN หรือ Wi-Fi ของเราเตอร์ตัวเดียวกับ iPhone" });

            try
            {
                dynamic fw = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                int cur = fw.CurrentProfileTypes;
                if ((cur & 4) != 0)
                    list.Add(new Check
                    {
                        Level = 1, Title = "เครือข่ายตั้งเป็นแบบสาธารณะ (Public)",
                        Desc = "Windows จะกันไม่ให้ iPhone มองเห็นคอม แนะนำให้เปลี่ยนเป็นแบบส่วนตัว (Private) ที่ Settings → Network & internet",
                        ActionText = "เปิดการตั้งค่าเครือข่าย", Action = () => Controller.OpenFile("ms-settings:network-status")
                    });
                else
                    list.Add(new Check { Level = 0, Title = "เครือข่ายเป็นแบบส่วนตัว (Private)", Desc = "iPhone ในวงเดียวกันค้นหาคอมนี้เจอได้" });

                bool enabled = true;
                try { enabled = fw.FirewallEnabled[(cur & 4) != 0 ? 4 : (cur & 2) != 0 ? 2 : 1]; } catch { }
                bool allowed = false, blocked = false;
                string exe = Paths.UxPlayExe;
                foreach (dynamic r in fw.Rules)
                {
                    try
                    {
                        string app = r.ApplicationName;
                        if (app == null || !string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!(bool)r.Enabled || (int)r.Direction != 1 || ((int)r.Profiles & cur) == 0) continue;
                        if ((int)r.Action == 1) allowed = true; else blocked = true;
                    }
                    catch { }
                }
                Action openFw = () => Controller.OpenFile("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps");
                if (!enabled)
                    list.Add(new Check { Level = 0, Title = "Windows Firewall ปิดอยู่", Desc = "ไม่มีอะไรบล็อกการเชื่อมต่อ" });
                else if (blocked)
                    list.Add(new Check { Level = 2, Title = "Firewall บล็อก UxPlay อยู่", Desc = "มีกฎที่บล็อก uxplay.exe ให้แก้เป็นอนุญาตในหน้าตั้งค่า Firewall", ActionText = "เปิดหน้าตั้งค่า Firewall", Action = openFw });
                else if (allowed)
                    list.Add(new Check { Level = 0, Title = "Firewall อนุญาต UxPlay แล้ว", Desc = "iPhone ส่งภาพเข้ามาที่คอมได้" });
                else
                    list.Add(new Check { Level = 1, Title = "Firewall ยังไม่อนุญาต UxPlay", Desc = "ตอนเปิดรับการเชื่อมต่อ ถ้า Windows ถาม ให้ติ๊ก Private แล้วกด Allow", ActionText = "เปิดหน้าตั้งค่า Firewall", Action = openFw });
            }
            catch (Exception ex)
            {
                list.Add(new Check { Level = 1, Title = "ตรวจ Firewall ไม่ได้", Desc = ex.Message });
            }

            if (pid > 0)
                list.Add(new Check { Level = 0, Title = "ตัวรับสัญญาณทำงานอยู่", Desc = ports.Count > 0 ? "รอรับการเชื่อมต่อที่พอร์ต TCP " + string.Join(", ", ports) : "กำลังเริ่ม" });
            else
                list.Add(new Check { Level = 1, Title = "ตัวรับสัญญาณปิดอยู่", Desc = "iPhone จะมองไม่เห็นคอมจนกว่าจะเปิด", ActionText = "เปิดเลย", Action = () => { c.RestartServer(); checksDirty = true; } });
            return list;
        }

        UIElement CheckRow(Check ch)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string fg = ch.Level == 0 ? "Success" : ch.Level == 1 ? "Warning" : "Danger";
            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Theme.B(fg + "Soft"), VerticalAlignment = VerticalAlignment.Top };
            badge.Child = new TextBlock { Text = ch.Level == 0 ? "" : ch.Level == 1 ? "" : "", Style = (Style)Application.Current.Resources["Glyph"], FontSize = 13, Foreground = Theme.B(fg) };
            g.Children.Add(badge);
            var text = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = ch.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(ch.Desc)) text.Children.Add(new TextBlock { Text = ch.Desc, Style = (Style)Application.Current.Resources["Muted"], FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            if (ch.Action != null)
            {
                var b = new Button { Style = (Style)Application.Current.Resources["Btn"], Content = ch.ActionText, Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
                var act = ch.Action;
                b.Click += (o, e) => act();
                Grid.SetColumn(b, 2);
                g.Children.Add(b);
            }
            return g;
        }

        void BuildTips()
        {
            var tips = new[]
            {
                new[] { "iPhone มองไม่เห็นคอมในรายการ", "ตรวจว่า iPhone ต่อ Wi-Fi ของเราเตอร์ตัวเดียวกับคอม ปิด VPN ชั่วคราว และดูว่าเครือข่ายของคอมเป็นแบบ Private (ดูรายการด้านบน) แล้วลองปิด-เปิดการสะท้อนหน้าจอใหม่" },
                new[] { "เชื่อมต่อได้แต่ภาพไม่ขึ้น ค้าง หรือเป็นสีเขียว", "ในหน้าตั้งค่า ลองเปลี่ยนตัวแสดงผลเป็น Direct3D 11 หรือเปิด “ถอดรหัสภาพด้วยซอฟต์แวร์”" },
                new[] { "ภาพกับเสียงไม่ตรงกัน", "ปิด “โหมดหน่วงต่ำ” ในหน้าตั้งค่า" },
                new[] { "ภาพกระตุกหรือแตก", "ให้ iPhone ใช้ Wi-Fi 5 GHz อยู่ใกล้เราเตอร์ หรือลดเป็น 1080p / 30 FPS" },
                new[] { "แอปดูหนังบางแอปขึ้นจอดำ", "แอปที่มีลิขสิทธิ์ (เช่น Netflix) ไม่อนุญาตให้สะท้อนหน้าจอ เป็นข้อจำกัดของแอปนั้น ไม่ใช่ปัญหาของคอม" },
            };
            var panel = E<StackPanel>("TipsPanel");
            foreach (var t in tips)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = "", Style = (Style)Application.Current.Resources["Glyph"], FontSize = 13, Foreground = Theme.B("Accent") });
                head.Children.Add(new TextBlock { Text = t[0], FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0) });
                sp.Children.Add(head);
                sp.Children.Add(new TextBlock { Text = t[1], Style = (Style)Application.Current.Resources["Muted"], FontSize = 12.5, Margin = new Thickness(23, 3, 0, 0) });
                panel.Children.Add(sp);
            }
        }

        void AppendLog(string line)
        {
            if (E<ScrollViewer>("PageHelp").Visibility != Visibility.Visible) return;
            var box = E<TextBox>("LogBox");
            bool atEnd = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 4;
            box.AppendText((box.Text.Length > 0 ? "\n" : "") + line);
            if (atEnd) box.ScrollToEnd();
        }
    }
}
