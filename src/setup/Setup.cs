using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace UxPlayMirror.Setup
{
    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Load("Theme.xaml"));
            Theme.Apply("system");
            var w = new SetupWin(args);
            app.Run(w.Window);
            if (w.DeleteSelfOnExit != null) ScheduleDelete(w.DeleteSelfOnExit);
            return 0;
        }

        /// The uninstaller lives in the program folder, so it removes itself (and the folder,
        /// only if nothing else is left in it) from a helper process after it has exited.
        static void ScheduleDelete(string exe)
        {
            string dir = Path.GetDirectoryName(exe);
            string cmd = "/c for /l %i in (1,1,30) do (del /f /q \"" + exe + "\" 2>nul & if not exist \"" + exe + "\" (rmdir \"" + dir +
                         "\" 2>nul & exit) & ping -n 2 127.0.0.1 >nul)";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
        }

        public static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup." + name);
        }

        public static object Load(string name)
        {
            using (var s = Resource(name)) return XamlReader.Load(s);
        }
    }

    /// Installs MSYS2 + GStreamer, builds UxPlay, and installs UxPlay Mirror (or uninstalls it with --uninstall).
    class SetupWin
    {
        public readonly Window Window;
        public string DeleteSelfOnExit { get; private set; }
        const string Root = @"C:\msys64";
        const string MsysInstallerUrl = "https://github.com/msys2/msys2-installer/releases/latest/download/msys2-x86_64-latest.exe";
        const string Packages = "git mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-ninja " +
                                "mingw-w64-ucrt-x86_64-libplist mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base " +
                                "mingw-w64-ucrt-x86_64-gst-libav mingw-w64-ucrt-x86_64-gst-plugins-good mingw-w64-ucrt-x86_64-gst-plugins-bad";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\UxPlayMirror";

        /// The only files the installer puts in the program folder; uninstall removes exactly these.
        static readonly string[] InstalledFiles = { "UxPlayMirror.exe", "app.ico", "UxPlayMirrorSetup.exe" };

        static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\UxPlay Mirror");
        static readonly string DataDirs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UxPlay Mirror");
        static readonly string LocalData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UxPlay Mirror");

        readonly bool uninstall, noUpdate;
        bool running, finished, success;
        readonly List<Step> steps = new List<Step>();
        StreamWriter logFile;
        Storyboard spin;

        class Step
        {
            public string Title;
            public Func<bool> Run;
            public Border Badge;
            public TextBlock Glyph, Detail;
        }

        T E<T>(string n) where T : class { return (T)Window.FindName(n); }
        static string G(int c) { return ((char)c).ToString(); }

        public SetupWin(string[] args)
        {
            uninstall = Array.IndexOf(args, "--uninstall") >= 0;
            noUpdate = Array.IndexOf(args, "--no-update") >= 0;   // skip the full MSYS2 upgrade (for re-installs)
            Window = (Window)SetupProgram.Load("Setup.xaml");
            try
            {
                using (var s = SetupProgram.Resource("app.ico"))
                    Window.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);  // title bar / taskbar pick their own size
                using (var s = SetupProgram.Resource("app.ico"))
                    E<Image>("Logo").Source = AppIcon.Largest(s);
            }
            catch { }
            Window.SourceInitialized += (o, e) =>
            {
                int round = 2;
                DwmSetWindowAttribute(new WindowInteropHelper(Window).Handle, 33, ref round, 4);
            };
            E<TextBlock>("VersionText").Text = "เวอร์ชัน " + Paths.AppVersion + "\nใช้ UxPlay (GPLv3), MSYS2 และ GStreamer";
            E<Button>("BtnMin").Click += (o, e) => Window.WindowState = WindowState.Minimized;
            E<Button>("BtnClose").Click += (o, e) => Window.Close();
            Window.Closing += (o, e) =>
            {
                if (running && MessageBox.Show(Window, "กำลังติดตั้งอยู่ ต้องการยกเลิกจริงไหม?\nติดตั้งใหม่ภายหลังได้ ตัวติดตั้งจะทำต่อจากที่ค้างไว้",
                        "ยกเลิกการติดตั้ง", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    e.Cancel = true;
            };
            E<Button>("BtnPrimary").Click += (o, e) => Primary();
            E<Button>("BtnSecondary").Click += (o, e) => Window.Close();

            if (uninstall) PrepareUninstall(); else PrepareInstall();
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

        // ================= pages =================

        void PrepareInstall()
        {
            Window.Title = "ติดตั้ง UxPlay Mirror";
            E<TextBlock>("WelcomeTitle").Text = "ติดตั้ง UxPlay Mirror";
            var list = E<StackPanel>("WelcomeList");
            bool hasMsys = File.Exists(Path.Combine(Root, @"usr\bin\bash.exe"));
            AddBullet(list, G(0xE896), "MSYS2", hasMsys ? "มีอยู่แล้วที่ " + Root + (noUpdate ? "" : " จะอัปเดตให้") : "ระบบที่ใช้ build UxPlay จะติดตั้งที่ " + Root);
            AddBullet(list, G(0xE7F4), "GStreamer", "ตัวถอดรหัสและแสดงผลภาพ ใช้การ์ดจอช่วย");
            AddBullet(list, G(0xE8EA), "UxPlay", "ตัวรับ AirPlay โอเพนซอร์ส ดาวน์โหลดเวอร์ชันล่าสุดจาก GitHub แล้ว build ในเครื่อง");
            AddBullet(list, G(0xE80F), "UxPlay Mirror", "โปรแกรมหลัก พร้อม shortcut บน Desktop และ Start Menu");
            if (SmartAppControl.State == SmartAppControl.On)
            {
                list.Children.Insert(0, SmartAppControlWarning());
                foreach (var c in list.Children) if (c is Grid) ((Grid)c).Margin = new Thickness(0, 0, 0, 6);   // keep everything on one page
            }

            steps.Add(new Step { Title = "ปิด UxPlay Mirror ที่เปิดอยู่", Run = StepCloseApp });
            steps.Add(new Step { Title = "ติดตั้ง MSYS2", Run = StepMsys });
            if (!noUpdate) steps.Add(new Step { Title = "อัปเดต MSYS2", Run = StepUpdate });
            steps.Add(new Step { Title = "ติดตั้งเครื่องมือ build และ GStreamer", Run = StepPackages });
            steps.Add(new Step { Title = "ดาวน์โหลดและ build UxPlay", Run = StepUxPlay });
            steps.Add(new Step { Title = "ติดตั้ง UxPlay Mirror", Run = StepApp });
            steps.Add(new Step { Title = "ตั้งค่า Firewall", Run = StepFirewall });
            BuildStepList();
        }

        void PrepareUninstall()
        {
            Window.Title = "ถอนการติดตั้ง UxPlay Mirror";
            E<TextBlock>("WelcomeTitle").Text = "ถอนการติดตั้ง UxPlay Mirror";
            E<TextBlock>("WelcomeText").Text = "จะลบโปรแกรม UxPlay Mirror, shortcut และการตั้งค่าเปิดพร้อม Windows ออก";
            var list = E<StackPanel>("WelcomeList");
            AddBullet(list, G(0xE946), "MSYS2 และ UxPlay จะยังอยู่", "ถ้าไม่ใช้แล้ว ลบ MSYS2 ได้จาก Settings → Apps → MSYS2");
            AddBullet(list, G(0xE8B9), "ภาพและวิดีโอที่บันทึกไว้จะไม่ถูกลบ", "อยู่ในโฟลเดอร์ รูปภาพ\\UxPlay Mirror");
            var chk = E<CheckBox>("ChkFirewall");
            chk.Content = new TextBlock { Text = "ลบการตั้งค่าและ log ของโปรแกรมด้วย", TextWrapping = TextWrapping.Wrap };
            chk.IsChecked = false;
            E<Button>("BtnPrimary").Content = "ถอนการติดตั้ง";
            steps.Add(new Step { Title = "ปิด UxPlay Mirror ที่เปิดอยู่", Run = StepCloseApp });
            steps.Add(new Step { Title = "ลบโปรแกรมและ shortcut", Run = StepRemove });
            BuildStepList();
        }

        /// Smart App Control (on) blocks UxPlay's unsigned GStreamer DLLs, so say so before installing.
        UIElement SmartAppControlWarning()
        {
            var box = new Border { Background = Theme.B("WarningSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14) };
            var row = new DockPanel();
            var btn = new Button { Style = (Style)Application.Current.Resources["Btn"], Content = "เปิด Windows Security", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            btn.Click += (o, e) => { try { Process.Start(SmartAppControl.SettingsUri); } catch { } };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = "Smart App Control ของ Windows เปิดอยู่", FontWeight = FontWeights.SemiBold, Foreground = Theme.B("Warning") });
            text.Children.Add(new TextBlock
            {
                Text = "จะบล็อกไม่ให้ UxPlay ทำงาน ต้องปิดที่ App & browser control → Smart App Control (ปิดแล้วเปิดกลับไม่ได้)",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.B("Text"), Margin = new Thickness(0, 2, 0, 0)
            });
            row.Children.Add(text);
            box.Child = row;
            return box;
        }

        void AddBullet(StackPanel p, string glyph, string title, string desc)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var b = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Theme.B("AccentSoft"), VerticalAlignment = VerticalAlignment.Top };
            b.Child = new TextBlock { Text = glyph, Style = (Style)Application.Current.Resources["Glyph"], FontSize = 14, Foreground = Theme.B("Accent") };
            g.Children.Add(b);
            var t = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            t.Children.Add(new TextBlock { Text = desc, Style = (Style)Application.Current.Resources["Muted"], FontSize = 12 });
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            p.Children.Add(g);
        }

        void BuildStepList()
        {
            var panel = E<StackPanel>("StepsPanel");
            foreach (var s in steps)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                s.Badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Theme.B("SurfaceAlt") };
                s.Glyph = new TextBlock { Style = (Style)Application.Current.Resources["Glyph"], FontSize = 10, Foreground = Theme.B("TextMuted"), RenderTransformOrigin = new Point(0.5, 0.5) };
                s.Glyph.RenderTransform = new RotateTransform();
                s.Badge.Child = s.Glyph;
                g.Children.Add(s.Badge);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new TextBlock { Text = s.Title });
                s.Detail = new TextBlock { Style = (Style)Application.Current.Resources["Muted"], FontSize = 12, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.NoWrap };
                row.Children.Add(s.Detail);
                Grid.SetColumn(row, 1);
                g.Children.Add(row);
                panel.Children.Add(g);
            }
        }

        void SetStep(Step s, int state, string detail)
        {
            // 0 = running, 1 = done, 2 = failed, 3 = skipped/warning
            Window.Dispatcher.Invoke(new Action(() =>
            {
                if (spin != null) { spin.Stop(); spin = null; }
                ((RotateTransform)s.Glyph.RenderTransform).Angle = 0;
                if (detail != null) s.Detail.Text = detail;
                switch (state)
                {
                    case 0:
                        s.Badge.Background = Theme.B("AccentSoft"); s.Glyph.Foreground = Theme.B("Accent"); s.Glyph.Text = G(0xE72C);
                        var a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };
                        spin = new Storyboard();
                        Storyboard.SetTarget(a, s.Glyph);
                        Storyboard.SetTargetProperty(a, new PropertyPath("RenderTransform.Angle"));
                        spin.Children.Add(a);
                        spin.Begin();
                        E<TextBlock>("ProgressText").Text = s.Title + "…";
                        break;
                    case 1: s.Badge.Background = Theme.B("SuccessSoft"); s.Glyph.Foreground = Theme.B("Success"); s.Glyph.Text = G(0xE73E); break;
                    case 2: s.Badge.Background = Theme.B("DangerSoft"); s.Glyph.Foreground = Theme.B("Danger"); s.Glyph.Text = G(0xE711); break;
                    default: s.Badge.Background = Theme.B("WarningSoft"); s.Glyph.Foreground = Theme.B("Warning"); s.Glyph.Text = G(0xE7BA); break;
                }
            }));
        }

        void Progress(double percent, string text)
        {
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var bar = E<ProgressBar>("Bar");
                bar.IsIndeterminate = percent < 0;
                if (percent >= 0) bar.Value = percent;
                if (text != null) E<TextBlock>("ProgressText").Text = text;
            }));
        }

        void Log(string line)
        {
            if (line == null) return;
            line = line.TrimEnd('\r');
            try { if (logFile != null) logFile.WriteLine(line); } catch { }
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var box = E<TextBox>("LogBox");
                if (box.Text.Length > 60000) box.Text = box.Text.Substring(30000);
                box.AppendText((box.Text.Length > 0 ? "\n" : "") + line);
                box.ScrollToEnd();
            }));
        }

        void Primary()
        {
            if (finished)
            {
                if (success && !uninstall && E<CheckBox>("ChkLaunch").IsChecked == true)
                    try { Process.Start(Path.Combine(AppDir, "UxPlayMirror.exe")); } catch { }
                Window.Close();
                return;
            }
            if (running) return;
            running = true;
            E<StackPanel>("PageWelcome").Visibility = Visibility.Collapsed;
            E<Grid>("PageProgress").Visibility = Visibility.Visible;
            E<TextBlock>("ProgressTitle").Text = uninstall ? "กำลังถอนการติดตั้ง…" : "กำลังติดตั้ง…";
            E<Button>("BtnPrimary").IsEnabled = false;
            E<Button>("BtnSecondary").Content = "ยกเลิก";
            optCheckbox = E<CheckBox>("ChkFirewall").IsChecked == true;
            try
            {
                Directory.CreateDirectory(Path.Combine(LocalData, "logs"));
                logFile = new StreamWriter(Path.Combine(LocalData, @"logs\setup.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch { }
            Task.Factory.StartNew(RunAll);
        }

        bool optCheckbox;   // install: add firewall rule · uninstall: also remove settings and logs
        readonly List<string> warnings = new List<string>();

        void RunAll()
        {
            bool ok = true;
            for (int i = 0; i < steps.Count && ok; i++)
            {
                var s = steps[i];
                SetStep(s, 0, "");
                Progress(-1, null);
                Log("== " + s.Title);
                try { ok = s.Run(); }
                catch (Exception ex) { Log("ERROR: " + ex); ok = false; SetStep(s, 2, ex.Message); }
                if (ok && Window.Dispatcher.Invoke(() => s.Glyph.Text) == G(0xE72C)) SetStep(s, 1, null);
                if (!ok) SetStep(s, 2, null);
            }
            Window.Dispatcher.Invoke(new Action(() => Finish(ok)));
        }

        void Finish(bool ok)
        {
            running = false;
            finished = true;
            success = ok;
            if (spin != null) spin.Stop();
            var bar = E<ProgressBar>("Bar");
            bar.IsIndeterminate = false;
            bar.Value = 100;
            E<Button>("BtnPrimary").IsEnabled = true;
            E<Button>("BtnSecondary").Visibility = Visibility.Collapsed;
            if (!ok)
            {
                E<TextBlock>("ProgressTitle").Text = uninstall ? "ถอนการติดตั้งไม่สำเร็จ" : "ติดตั้งไม่สำเร็จ";
                E<TextBlock>("ProgressText").Text = "ดูรายละเอียดด้านล่าง แก้ปัญหาแล้วเปิดตัวติดตั้งอีกครั้งได้ จะทำต่อจากที่ค้างไว้ · log: " + Path.Combine(LocalData, @"logs\setup.log");
                E<Button>("BtnPrimary").Content = "ปิด";
                return;
            }
            E<Grid>("PageProgress").Visibility = Visibility.Collapsed;
            E<StackPanel>("PageDone").Visibility = Visibility.Visible;
            E<Button>("BtnPrimary").Content = "เสร็จสิ้น";
            if (uninstall)
            {
                E<TextBlock>("DoneTitle").Text = "ถอนการติดตั้งเรียบร้อย";
                E<TextBlock>("DoneText").Text = "ลบ UxPlay Mirror ออกจากเครื่องแล้ว";
                E<CheckBox>("ChkLaunch").Visibility = Visibility.Collapsed;
                return;
            }
            E<TextBlock>("DoneTitle").Text = "ติดตั้งเสร็จแล้ว";
            E<TextBlock>("DoneText").Text = "เปิด UxPlay Mirror แล้วบน iPhone เปิด “การสะท้อนหน้าจอ” และเลือก “UxPlay@" + Dns.GetHostName() + "”";
            var checks = E<StackPanel>("DoneChecks");
            if (SmartAppControl.State == SmartAppControl.On) checks.Children.Add(SmartAppControlWarning());
            if (IsPublicNetwork())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var btn = new Button { Style = (Style)Application.Current.Resources["Btn"], Content = "เปิดการตั้งค่าเครือข่าย", Padding = new Thickness(12, 6, 12, 6) };
                btn.Click += (o, e) => { try { Process.Start("ms-settings:network-status"); } catch { } };
                DockPanel.SetDock(btn, Dock.Right);
                row.Children.Add(btn);
                row.Children.Add(new TextBlock
                {
                    Text = "เครือข่ายตอนนี้เป็นแบบสาธารณะ (Public) แนะนำให้เปลี่ยนเป็นแบบส่วนตัว (Private) ไม่อย่างนั้น iPhone อาจมองไม่เห็นคอม",
                    TextWrapping = TextWrapping.Wrap, Foreground = Theme.B("Warning"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
                });
                checks.Children.Add(row);
            }
            foreach (var w in warnings)
                checks.Children.Add(new TextBlock { Text = "• " + w, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B("Warning"), Margin = new Thickness(0, 0, 0, 6) });
        }

        // ================= steps =================

        bool StepCloseApp()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (var name in new[] { "UxPlayMirror", "uxplay" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (p.Id == self) continue;
                    try { Log("closing " + name + " (" + p.Id + ")"); p.Kill(); p.WaitForExit(5000); } catch { }
                }
            return true;
        }

        bool StepMsys()
        {
            var s = steps.Find(x => x.Run == StepMsys);
            if (File.Exists(Path.Combine(Root, @"usr\bin\bash.exe")))
            {
                SetStep(s, 1, "มีอยู่แล้ว");
                return true;
            }
            string exe = Path.Combine(Path.GetTempPath(), "msys2-x86_64-latest.exe");
            Log("downloading " + MsysInstallerUrl);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                var done = new ManualResetEvent(false);
                Exception error = null;
                wc.DownloadProgressChanged += (o, e) =>
                {
                    double mb = e.BytesReceived / 1048576.0, total = e.TotalBytesToReceive / 1048576.0;
                    Progress(e.ProgressPercentage, string.Format("กำลังดาวน์โหลด MSYS2… {0:0} / {1:0} MB", mb, total));
                };
                wc.DownloadFileCompleted += (o, e) => { error = e.Error; done.Set(); };
                wc.DownloadFileAsync(new Uri(MsysInstallerUrl), exe);
                done.WaitOne();
                if (error != null) { Log("download failed: " + error.Message); SetStep(s, 2, "ดาวน์โหลดไม่สำเร็จ ตรวจอินเทอร์เน็ต"); return false; }
            }
            Progress(-1, "กำลังติดตั้ง MSYS2 ที่ " + Root + "…");
            int code = Run(exe, "in --confirm-command --accept-messages --root " + Root.Replace('\\', '/'), null);
            try { File.Delete(exe); } catch { }
            if (code != 0 || !File.Exists(Path.Combine(Root, @"usr\bin\bash.exe")))
            {
                SetStep(s, 2, "ติดตั้งไม่สำเร็จ (รหัส " + code + ")");
                return false;
            }
            Bash("echo first start");   // runs MSYS2's first-time setup (keyring etc.)
            return true;
        }

        bool StepUpdate()
        {
            // The first upgrade may replace pacman itself and stop; running it twice finishes the job.
            Bash("pacman -Syu --noconfirm");
            int code = Bash("pacman -Syu --noconfirm");
            if (code != 0) { warnings.Add("อัปเดต MSYS2 ไม่ครบ (ดู log) แต่ติดตั้งต่อได้"); SetStep(steps.Find(x => x.Run == StepUpdate), 3, "ไม่ครบ"); }
            return true;
        }

        bool StepPackages()
        {
            int code = Bash("pacman -S --needed --noconfirm " + Packages);
            if (code != 0) { SetStep(steps.Find(x => x.Run == StepPackages), 2, "pacman ผิดพลาด (รหัส " + code + ")"); return false; }
            return true;
        }

        bool StepUxPlay()
        {
            int code = Bash("if [ -d ~/UxPlay/.git ]; then cd ~/UxPlay && git pull --ff-only; else git clone https://github.com/FDH2/UxPlay ~/UxPlay; fi");
            if (code != 0) { SetStep(steps.Find(x => x.Run == StepUxPlay), 2, "ดาวน์โหลดซอร์สไม่สำเร็จ"); return false; }
            code = Bash("mkdir -p ~/UxPlay/build && cd ~/UxPlay/build && (test -f build.ninja || cmake -G Ninja ..) && cmake .. && ninja && cmake --install . --prefix $HOME/../../ucrt64");
            if (code != 0 || !File.Exists(Path.Combine(Root, @"ucrt64\bin\uxplay.exe")))
            {
                SetStep(steps.Find(x => x.Run == StepUxPlay), 2, "build ไม่สำเร็จ (รหัส " + code + ")");
                return false;
            }
            return true;
        }

        bool StepApp()
        {
            Directory.CreateDirectory(AppDir);
            Extract("UxPlayMirror.exe", Path.Combine(AppDir, "UxPlayMirror.exe"));
            Extract("app.ico", Path.Combine(AppDir, "app.ico"));
            string self = Assembly.GetExecutingAssembly().Location;
            string setupCopy = Path.Combine(AppDir, "UxPlayMirrorSetup.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, setupCopy, true);

            string exe = Path.Combine(AppDir, "UxPlayMirror.exe");
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
                Shortcut(Path.Combine(dir, "UxPlay Mirror.lnk"), exe);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "UxPlay Mirror");
                k.SetValue("DisplayVersion", Paths.AppVersion);
                k.SetValue("Publisher", "UxPlay Mirror");
                k.SetValue("DisplayIcon", Path.Combine(AppDir, "app.ico"));
                k.SetValue("InstallLocation", AppDir);
                k.SetValue("UninstallString", "\"" + setupCopy + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long size = 0;
                foreach (var name in InstalledFiles)
                {
                    var f = new FileInfo(Path.Combine(AppDir, name));
                    if (f.Exists) size += f.Length;
                }
                k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            }
            Log("installed to " + AppDir);
            return true;
        }

        bool StepFirewall()
        {
            var s = steps.Find(x => x.Run == StepFirewall);
            string uxplay = Path.Combine(Root, @"ucrt64\bin\uxplay.exe");
            if (HasFirewallRule(uxplay)) { SetStep(s, 1, "อนุญาตไว้แล้ว"); return true; }
            if (!optCheckbox)
            {
                SetStep(s, 3, "ข้าม (Windows จะถามตอนเปิดใช้ครั้งแรก)");
                return true;
            }
            Log("asking for administrator permission to add a firewall rule");
            try
            {
                var psi = new ProcessStartInfo("netsh.exe",
                    "advfirewall firewall add rule name=\"UxPlay\" dir=in action=allow program=\"" + uxplay + "\" enable=yes profile=private,domain")
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(60000);
            }
            catch (Exception ex)
            {
                Log("firewall: " + ex.Message);
            }
            if (HasFirewallRule(uxplay)) return true;
            SetStep(s, 3, "ไม่ได้ตั้งค่า (Windows จะถามตอนเปิดใช้ครั้งแรก)");
            warnings.Add("ยังไม่ได้อนุญาต UxPlay ใน Firewall: ตอนเปิดใช้ครั้งแรก ถ้า Windows ถาม ให้ติ๊ก Private แล้วกด Allow");
            return true;
        }

        bool StepRemove()
        {
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            {
                string lnk = Path.Combine(dir, "UxPlay Mirror.lnk");
                if (File.Exists(lnk)) { File.Delete(lnk); Log("removed " + lnk); }
            }
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (run != null && run.GetValue("UxPlay Mirror") != null) run.DeleteValue("UxPlay Mirror");
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            // Remove only the files the installer put there; anything else in the folder is left alone.
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            foreach (var name in InstalledFiles)
            {
                string f = Path.Combine(AppDir, name);
                if (!File.Exists(f)) continue;
                if (string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) { DeleteSelfOnExit = f; continue; }
                File.Delete(f);
                Log("removed " + f);
            }
            if (DeleteSelfOnExit == null)
                try { Directory.Delete(AppDir, false); Log("removed " + AppDir); } catch { /* not empty: keep it */ }

            if (optCheckbox)
            {
                if (logFile != null) { logFile.Dispose(); logFile = null; }
                foreach (var d in new[] { DataDirs, LocalData })
                    try { if (Directory.Exists(d)) { Directory.Delete(d, true); Log("removed " + d); } } catch (Exception ex) { Log(ex.Message); }
            }
            return true;
        }

        // ================= helpers =================

        int Bash(string cmd)
        {
            Log("$ " + cmd);
            var psi = new ProcessStartInfo(Path.Combine(Root, @"usr\bin\bash.exe"), "-lc \"" + cmd.Replace("\"", "\\\"") + "\"");
            psi.EnvironmentVariables["MSYSTEM"] = "UCRT64";
            psi.EnvironmentVariables["CHERE_INVOKING"] = "1";
            return Run(psi.FileName, psi.Arguments, psi);
        }

        int Run(string file, string args, ProcessStartInfo template)
        {
            var psi = template ?? new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (o, e) => Log(e.Data);
                p.ErrorDataReceived += (o, e) => Log(e.Data);
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                Log("(exit " + p.ExitCode + ")");
                return p.ExitCode;
            }
        }

        static void Extract(string resource, string target)
        {
            using (var s = SetupProgram.Resource(resource))
            using (var f = File.Create(target))
                s.CopyTo(f);
        }

        static void Shortcut(string lnk, string target)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "แชร์จอ iPhone ขึ้นคอมผ่าน AirPlay";
            sc.Save();
        }

        static bool HasFirewallRule(string exe)
        {
            try
            {
                dynamic fw = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                foreach (dynamic r in fw.Rules)
                {
                    try
                    {
                        string app = r.ApplicationName;
                        if (app != null && string.Equals(app, exe, StringComparison.OrdinalIgnoreCase) && (bool)r.Enabled && (int)r.Action == 1 && (int)r.Direction == 1)
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        static bool IsPublicNetwork()
        {
            try
            {
                dynamic fw = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                int cur = fw.CurrentProfileTypes;
                return (cur & 4) != 0;
            }
            catch { return false; }
        }
    }
}
