using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace UxPlayMirror
{
    /// Owns the server, the video window helpers, the recorder and the tray icon.
    class Controller
    {
        public static Controller I { get; private set; }

        public AppSettings S;
        public readonly UxPlayServer Server;
        public readonly Recorder Rec;
        public readonly Dispatcher Disp;
        public readonly List<string> LogLines = new List<string>();
        public event Action Changed;
        public event Action<string> LogLine;
        public event Action MediaChanged;

        public IntPtr VideoHwnd { get; private set; }
        public bool VideoFullscreen { get { return fsState != null; } }
        public bool VideoPinned { get; private set; }
        public string UxPlayVersion { get; private set; }
        public bool IsMirroring { get { return VideoHwnd != IntPtr.Zero; } }
        public bool IsDrawing { get { return drawing != null; } }

        readonly Application app;
        MainWin main;
        ToolbarWin toolbar;
        WinForms.NotifyIcon tray;
        WinForms.ToolStripMenuItem miServe, miShot, miRec;
        Icon trayBase, trayLive, trayRec, trayOff;
        Icon appIconBig, appIconSmall;
        VideoWin.FullscreenState fsState;
        bool pinnedForRecording;
        KeyHook keys;
        HotKeys hotkeys;
        DrawOverlay drawing;
        NowPlayingWin nowPlaying;
        string lastTrackKey;
        readonly DispatcherTimer poll, track, fit;
        int fitVotes;
        double fitAspect;
        Action balloonAction;
        int lastRecSecond = -1;
        bool exiting;

        public Controller(Application app)
        {
            I = this;
            this.app = app;
            Disp = app.Dispatcher;
            S = AppSettings.Load();
            Theme.Apply(S.Theme);
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (o, e) =>
            {
                if (S.Theme == "system") Disp.BeginInvoke(new Action(() => { Theme.Apply("system"); if (main != null) main.ThemeChanged(); }));
            };

            Server = new UxPlayServer(Disp);
            Server.Changed += () => { UpdateTray(); Fire(); };
            Server.Line += AddLog;
            Server.ClientRequested += () =>
            {
                if (main == null || !main.IsVisible)
                    Balloon("มีอุปกรณ์กำลังเชื่อมต่อ", (Server.ClientName ?? "iPhone") + " กำลังเชื่อมต่อกับคอมเครื่องนี้", null);
            };
            Server.FpsUpdated += () => { if (toolbar != null) toolbar.RefreshFps(); Fire(); };
            Server.TrackChanged += OnTrackChanged;

            Rec = new Recorder(Disp);
            Rec.Changed += () => { RefreshPinForRecording(); UpdateTray(); Fire(); };
            Rec.Finished += OnRecordingFinished;

            poll = new DispatcherTimer(DispatcherPriority.Background, Disp) { Interval = TimeSpan.FromMilliseconds(400) };
            poll.Tick += (o, e) => Poll();
            track = new DispatcherTimer(DispatcherPriority.Render, Disp) { Interval = TimeSpan.FromMilliseconds(30) };
            track.Tick += (o, e) =>
            {
                if (toolbar != null) toolbar.Follow(VideoHwnd, VideoFullscreen);
                if (drawing != null) drawing.Follow(VideoHwnd);
            };
            fit = new DispatcherTimer(DispatcherPriority.Background, Disp) { Interval = TimeSpan.FromMilliseconds(800) };
            fit.Tick += (o, e) => AutoFit();

            LoadIcons();
            CreateTray();
            Task.Factory.StartNew(() =>
            {
                Recorder.ProbeEncoders();
                string v = ReadUxPlayVersion();
                Disp.BeginInvoke(new Action(() => { UxPlayVersion = v; Fire(); }));
            });
        }

        public void Run(bool startInTray, bool demo, bool demoMusic)
        {
            if (!startInTray) ShowMain();
            if (S.AutoServe) Server.Start(S);
            if (demo) StartDemo();
            if (demoMusic) StartDemoMusic();
            poll.Start();
        }

        /// Developer test mode: feeds the same lines uxplay prints while a phone streams music.
        void StartDemoMusic()
        {
            // uxplay resets the cover file when it starts, so draw ours a moment later
            Action makeCover = () =>
            {
                using (var bmp = new Bitmap(600, 600))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 600, 600), Color.FromArgb(255, 94, 58), Color.FromArgb(120, 40, 200), 45f))
                    {
                        g.FillRectangle(br, 0, 0, 600, 600);
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (var w = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) { g.FillEllipse(w, 150, 150, 300, 300); }
                        using (var b = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) { g.FillEllipse(b, 270, 270, 60, 60); }
                    }
                    bmp.Save(NowPlaying.CoverFile, ImageFormat.Jpeg);
                }
            };
            Server.DemoConnected = true;
            int pos = 42;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            int step = 0;
            t.Tick += (o, e) =>
            {
                step++;
                if (step == 2)
                {
                    makeCover();
                    Server.SimulateLine("connection request from iPhone ของฉัน (iPhone15,2) with deviceID = 00:11:22:33:44:55");
                    Server.SimulateLine("====================Audio Metadata==================");
                    Server.SimulateLine("Title: ทะเลยามเย็น");
                    Server.SimulateLine("Artist: ศิลปินตัวอย่าง");
                    Server.SimulateLine("Album: อัลบั้มทดสอบ");
                    Server.SimulateLine("coverart size 48211 written to " + NowPlaying.CoverFile);
                }
                if (step >= 2 && (step < 12 || step > 17)) // a short pause in the middle
                {
                    pos++;
                    Server.SimulateLine(string.Format("audio progress (min:sec): {0,3}:{1:00}; remaining: {2,3}:{3:00}; track length 4:21",
                        pos / 60, pos % 60, (261 - pos) / 60, (261 - pos) % 60));
                }
            };
            t.Start();
        }

        /// Developer test mode: a GStreamer test pattern stands in for the iPhone's video window.
        Process demo;

        void StartDemo()
        {
            bool landscape = Environment.GetEnvironmentVariable("UXM_DEMO_LANDSCAPE") == "1";
            bool pillar = Environment.GetEnvironmentVariable("UXM_DEMO_PILLAR") == "1";  // 16:9 video on a 19.5:9 phone, like YouTube
            string image = Environment.GetEnvironmentVariable("UXM_DEMO_IMAGE");                // still picture as the phone screen
            string source = !string.IsNullOrEmpty(image)
                ? "filesrc \"location=" + image.Replace('\\', '/') + "\" ! pngdec ! imagefreeze is-live=true ! videoconvert ! video/x-raw,framerate=60/1"
                : pillar
                ? "videotestsrc pattern=smpte is-live=true ! video/x-raw,width=1574,height=886,framerate=60/1 ! videobox left=-173 right=-173 fill=black"
                : "videotestsrc pattern=smpte is-live=true ! video/x-raw,width=" + (landscape ? "1920,height=886" : "886,height=1920") + ",framerate=60/1";
            if (string.IsNullOrEmpty(image)) source += " ! timeoverlay font-desc=\"Sans 60\"";
            var psi = new ProcessStartInfo(Paths.GstLaunch,
                source + " ! d3d12videosink " +
                "audiotestsrc wave=sine freq=440 volume=0.02 is-live=true ! autoaudiosink");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            demo = Process.Start(psi);
            Native.AddToJob(demo);
            Server.SimulateStream(1920.0 / 886); // same stream shape an iPhone 15 Pro sends in landscape
            var fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            var rnd = new Random();
            fpsTimer.Tick += (o, e) =>
            {
                Server.SimulateLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                Server.SimulateLine("<plist version=\"1.0\"><dict><key>fps</key><real>" + (55 + rnd.Next(6)) + ".0</real><key>bitrate</key><integer>8000</integer></dict>");
                Server.SimulateLine("</plist>");
            };
            fpsTimer.Start();
        }

        int VideoSourcePid
        {
            get
            {
                try { if (demo != null && !demo.HasExited) return demo.Id; } catch { }
                return Server.Pid;
            }
        }

        // ---------------- window / tray ----------------

        public void ShowMain()
        {
            if (main == null)
            {
                main = new MainWin(this);
                main.Window.Closed += (o, e) => main = null;
            }
            main.Show();
        }

        public void MainClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (exiting) return;
            if (S.CloseToTray)
            {
                e.Cancel = true;
                main.Window.Hide();
                if (!S.TrayHintShown)
                {
                    S.TrayHintShown = true;
                    S.Save();
                    Balloon("UxPlay Mirror ยังทำงานอยู่", "iPhone ยังเชื่อมต่อได้ตามปกติ คลิกไอคอนที่ถาดมุมขวาล่างเพื่อเปิดหน้าต่างอีกครั้ง", null);
                }
            }
            else
            {
                e.Cancel = true;
                Disp.BeginInvoke(new Action(Exit));
            }
        }

        public void Exit()
        {
            if (exiting) return;
            exiting = true;
            poll.Stop();
            track.Stop();
            if (Rec.IsRecording) Rec.StopAndWait();
            StopDrawing();
            CloseToolbar();
            if (hotkeys != null) hotkeys.Dispose();
            if (nowPlaying != null) nowPlaying.Close();
            Server.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (main != null) main.Window.Close();
            app.Shutdown();
        }

        void LoadIcons()
        {
            try
            {
                appIconBig = new Icon(Paths.IconFile, 32, 32);
                appIconSmall = new Icon(Paths.IconFile, 16, 16);
                trayBase = new Icon(Paths.IconFile, WinForms.SystemInformation.SmallIconSize);
            }
            catch
            {
                trayBase = SystemIcons.Application;
            }
            trayOff = Overlay(trayBase, Color.FromArgb(148, 163, 184));
            trayLive = Overlay(trayBase, Color.FromArgb(34, 197, 94));
            trayRec = Overlay(trayBase, Color.FromArgb(239, 68, 68));
        }

        /// Tray icon with a status dot, drawn at exactly the size the taskbar shows (no rescaling = sharp).
        static Icon Overlay(Icon baseIcon, Color dot)
        {
            int size = baseIcon.Width;
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var art = baseIcon.ToBitmap())
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawImageUnscaled(art, 0, 0);
                    float ring = size * 0.5f, inset = Math.Max(1.5f, size * 0.07f);
                    using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, size - ring, size - ring, ring, ring);
                    using (var b = new SolidBrush(dot)) g.FillEllipse(b, size - ring + inset, size - ring + inset, ring - 2 * inset, ring - 2 * inset);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void CreateTray()
        {
            tray = new WinForms.NotifyIcon();
            tray.Icon = trayOff;
            tray.Text = "UxPlay Mirror";
            var menu = new WinForms.ContextMenuStrip();
            menu.Font = new Font("Segoe UI", 9.5f);
            var open = new WinForms.ToolStripMenuItem("เปิด UxPlay Mirror", null, (o, e) => ShowMain());
            open.Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            miServe = new WinForms.ToolStripMenuItem("หยุดรับการเชื่อมต่อ", null, (o, e) => ToggleServer());
            miShot = new WinForms.ToolStripMenuItem("ถ่ายภาพหน้าจอ", null, (o, e) => TakeScreenshot());
            miRec = new WinForms.ToolStripMenuItem("เริ่มบันทึกวิดีโอ", null, (o, e) => ToggleRecording());
            menu.Items.Add(miServe);
            menu.Items.Add(miShot);
            menu.Items.Add(miRec);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("เปิดโฟลเดอร์ภาพและวิดีโอ", null, (o, e) => OpenSaveFolder()));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("ออก", null, (o, e) => Exit()));
            menu.Opening += (o, e) => UpdateTray();
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (o, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowMain(); };
            tray.BalloonTipClicked += (o, e) => { var a = balloonAction; balloonAction = null; if (a != null) a(); else ShowMain(); };
            tray.Visible = true;
        }

        void UpdateTray()
        {
            if (tray == null) return;
            bool running = Server.IsRunning;
            miServe.Text = running ? "หยุดรับการเชื่อมต่อ" : "เริ่มรับการเชื่อมต่อ";
            miShot.Enabled = IsMirroring;
            miRec.Enabled = IsMirroring || Rec.IsRecording;
            miRec.Text = Rec.IsRecording ? "หยุดบันทึกวิดีโอ" : "เริ่มบันทึกวิดีโอ";
            tray.Icon = Rec.IsRecording ? trayRec : (Server.State == ServerState.Mirroring || Server.State == ServerState.Connected) ? trayLive :
                        (Server.State == ServerState.Ready ? trayBase : trayOff);
            string t = "UxPlay Mirror – " + StateText();
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        public string StateText()
        {
            switch (Server.State)
            {
                case ServerState.Starting: return "กำลังเริ่ม…";
                case ServerState.Ready: return "รอการเชื่อมต่อ";
                case ServerState.Connected: return "เชื่อมต่อกับ " + (Server.ClientName ?? "iPhone");
                case ServerState.Mirroring: return Rec.IsRecording ? "กำลังบันทึกวิดีโอ" : "กำลังแชร์จอจาก " + (Server.ClientName ?? "iPhone");
                case ServerState.Error: return "เกิดข้อผิดพลาด";
                default: return "ปิดรับการเชื่อมต่อ";
            }
        }

        public void Balloon(string title, string text, Action onClick)
        {
            if (tray == null) return;
            balloonAction = onClick;
            tray.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.None);
        }

        /// Shows feedback in the main window if it is in front, otherwise as a Windows notification.
        public void Notify(string text, string file, bool isError)
        {
            Log.Write((isError ? "notify(error): " : "notify: ") + text + (file != null ? " [" + file + "]" : ""));
            if (main != null && main.IsVisible && main.Window.IsActive)
                main.ShowSnack(text, file, isError);
            else
                Balloon(isError ? "UxPlay Mirror – มีปัญหา" : "UxPlay Mirror", text, file == null ? (Action)null : () => OpenFile(file));
        }

        void Fire() { if (Changed != null) Changed(); }

        void AddLog(string line)
        {
            LogLines.Add(line);
            if (LogLines.Count > 1500) LogLines.RemoveRange(0, 300);
            if (LogLine != null) LogLine(line);
        }

        // ---------------- server ----------------

        public void ToggleServer()
        {
            if (Server.IsRunning) { if (Rec.IsRecording) Rec.Stop(); Server.Stop(); }
            else Server.Start(S);
        }

        public void RestartServer() { Server.Start(S); }

        public void ApplySettings(AppSettings next, bool restart)
        {
            bool themeChanged = next.Theme != S.Theme;
            bool startupChanged = next.StartWithWindows != S.StartWithWindows;
            bool topChanged = next.VideoOnTop != S.VideoOnTop;
            bool keysChanged = next.Hotkeys != S.Hotkeys;
            S = next;
            S.Save();
            if (keysChanged) RegisterHotKeys();
            if (themeChanged) { Theme.Apply(S.Theme); if (main != null) main.ThemeChanged(); }
            if (startupChanged) Startup.Set(S.StartWithWindows);
            if (topChanged && IsMirroring) SetPinned(S.VideoOnTop);
            if (restart && Server.IsRunning) Server.Start(S);
            if (MediaChanged != null) MediaChanged();
            Fire();
        }

        // ---------------- video window ----------------

        void Poll()
        {
            int pid = VideoSourcePid;
            IntPtr h = pid > 0 ? VideoWin.Find(pid) : IntPtr.Zero;
            if (h != VideoHwnd)
            {
                if (VideoHwnd != IntPtr.Zero) OnVideoClosed();
                if (h != IntPtr.Zero) OnVideoOpened(h);
            }
            else if (h != IntPtr.Zero && toolbar != null)
            {
                toolbar.SetVisible(!Native.IsIconic(h));
            }
            Server.UpdateLive(VideoHwnd != IntPtr.Zero);
            if (Rec.IsRecording)
            {
                int sec = (int)(DateTime.Now - Rec.Started).TotalSeconds;
                if (sec != lastRecSecond) { lastRecSecond = sec; Fire(); if (toolbar != null) toolbar.Refresh(); }
            }
        }

        void OnVideoOpened(IntPtr h)
        {
            VideoHwnd = h;
            fsState = null;
            VideoPinned = false;
            VideoWin.Decorate(h, "UxPlay Mirror – " + (Server.ClientName ?? "iPhone"),
                appIconBig == null ? IntPtr.Zero : appIconBig.Handle, appIconSmall == null ? IntPtr.Zero : appIconSmall.Handle);
            VideoWin.FitToScreen(h);
            if (S.VideoOnTop) SetPinned(true);
            if (S.FullscreenOnConnect) ToggleFullscreen();

            toolbar = new ToolbarWin(this);
            toolbar.Attach(h);
            track.Start();
            fitVotes = 0;
            lastFitTarget = 0;
            fit.Start();
            if (keys == null) keys = new KeyHook(OnKey);
            RegisterHotKeys();

            if (main == null || !main.IsVisible)
                Balloon("กำลังแชร์จอ", "กำลังแสดงหน้าจอจาก " + (Server.ClientName ?? "iPhone"), null);
            OnTrackChanged();
            UpdateTray();
            Fire();
        }

        void OnVideoClosed()
        {
            if (Rec.IsRecording) Rec.Stop();
            StopDrawing();
            CloseToolbar();
            if (keys != null) { keys.Dispose(); keys = null; }
            if (hotkeys != null) { hotkeys.Dispose(); hotkeys = null; }
            VideoHwnd = IntPtr.Zero;
            fsState = null;
            VideoPinned = false;
            pinnedForRecording = false;
            OnTrackChanged();
            UpdateTray();
            Fire();
        }

        /// Ctrl+Alt+S / R / F / D work from any app while the phone is mirroring.
        void RegisterHotKeys()
        {
            if (hotkeys != null) { hotkeys.Dispose(); hotkeys = null; }
            if (!S.Hotkeys || VideoHwnd == IntPtr.Zero) return;
            hotkeys = new HotKeys();
            var failed = new List<string>();
            const uint mods = HotKeys.Ctrl | HotKeys.Alt;
            if (!hotkeys.Add(mods, WinForms.Keys.S, TakeScreenshot)) failed.Add("Ctrl+Alt+S");
            if (!hotkeys.Add(mods, WinForms.Keys.R, ToggleRecording)) failed.Add("Ctrl+Alt+R");
            if (!hotkeys.Add(mods, WinForms.Keys.F, ToggleFullscreen)) failed.Add("Ctrl+Alt+F");
            if (!hotkeys.Add(mods, WinForms.Keys.D, ToggleDrawing)) failed.Add("Ctrl+Alt+D");
            if (failed.Count > 0)
            {
                Log.Write("hotkeys in use elsewhere: " + string.Join(", ", failed));
                Notify("คีย์ลัด " + string.Join(", ", failed) + " ถูกโปรแกรมอื่นใช้อยู่ จึงใช้ไม่ได้ตอนนี้", null, true);
            }
        }

        // ---------------- pen ----------------

        public void ToggleDrawing()
        {
            if (drawing != null) { StopDrawing(); }
            else if (VideoHwnd != IntPtr.Zero)
            {
                drawing = new DrawOverlay(this);
                drawing.Open(VideoHwnd);
                track.Start();
            }
            if (toolbar != null) toolbar.Refresh();
            Fire();
        }

        void StopDrawing()
        {
            if (drawing == null) return;
            drawing.Close();
            drawing = null;
            if (VideoHwnd != IntPtr.Zero) Native.SetForegroundWindow(VideoHwnd);
        }

        // ---------------- now playing ----------------

        void OnTrackChanged()
        {
            var t = Server.Track;
            bool show = t.Active && t.HasTrack && !IsMirroring;
            string key = t.HasTrack ? (t.Title + "|" + t.Artist) : null;
            bool newSong = key != null && key != lastTrackKey;
            if (key != null || !t.Active) { if (key != lastTrackKey) Fire(); lastTrackKey = key; }

            if (!show)
            {
                if (nowPlaying != null && (!t.Active || IsMirroring)) { nowPlaying.Close(); nowPlaying = null; }
                return;
            }
            if (nowPlaying == null)
            {
                nowPlaying = new NowPlayingWin(this);
                nowPlaying.Window.Closed += (o, e) => nowPlaying = null;
            }
            nowPlaying.Update();
            if (newSong)
            {
                if (S.ShowNowPlaying && !nowPlaying.UserClosed) nowPlaying.Show();
                else Balloon("กำลังเล่น", t.Title + (string.IsNullOrEmpty(t.Artist) ? "" : " – " + t.Artist), ShowNowPlaying);
            }
        }

        public bool HasNowPlaying { get { return Server.Track.Active && Server.Track.HasTrack && !IsMirroring; } }

        public void ShowNowPlaying()
        {
            if (!HasNowPlaying) return;
            if (nowPlaying == null) { OnTrackChanged(); if (nowPlaying == null) return; }
            nowPlaying.Show();
            nowPlaying.Window.Activate();
        }

        /// Keep the window's shape equal to the phone picture so there are no black bars,
        /// and re-fit when the phone is rotated. Requires two matching samples in a row.
        void AutoFit()
        {
            if (VideoHwnd == IntPtr.Zero || fsState != null || !S.AutoFitWindow ||
                Native.IsIconic(VideoHwnd) || Native.IsZoomed(VideoHwnd)) return;
            using (var b = VideoWin.CaptureRaw(VideoHwnd))
            {
                if (b == null) return;
                var r = VideoWin.ContentBounds(b);
                double cw = b.Width, ch = b.Height;
                double known = Server.StreamAspect;
                // With the stream shape known, a small picture is fine (landscape video in a tall portrait window);
                // without it, insist on a big picture so a dark frame can't fool us.
                bool tooSmall = known > 0 ? (r.Width < 40 || r.Height < 40) : (r.Width < cw * 0.2 || r.Height < ch * 0.2);
                if (tooSmall) { fitVotes = 0; return; }
                double content = (double)r.Width / r.Height;
                double target;
                if (known > 0)
                {
                    // The phone screen is either landscape (known) or portrait (1/known). For each, work out where
                    // the phone picture would sit in this window, and pick the one the visible content fills best.
                    // The content may be smaller than the phone screen (a 16:9 YouTube video on a 19.5:9 iPhone has
                    // its own black bars), so it only has to fit inside; dark game scenes rarely fill either well.
                    target = 0;
                    double best = 0;
                    foreach (double t in new[] { known, 1 / known })
                    {
                        double score = FillScore(r, cw, ch, t);
                        if (score > best) { best = score; target = t; }
                    }
                    if (target == 0 || best < 0.5) { fitVotes = 0; return; }
                }
                else
                {
                    double left = r.Left / cw, right = (cw - r.Right) / cw, top = r.Top / ch, bottom = (ch - r.Bottom) / ch;
                    bool pillar = left > 0.06 && right > 0.06 && Math.Abs(left - right) < 0.03 && top < 0.01 && bottom < 0.01;
                    bool letter = top > 0.06 && bottom > 0.06 && Math.Abs(top - bottom) < 0.03 && left < 0.01 && right < 0.01;
                    if (!pillar && !letter) { fitVotes = 0; return; }
                    target = content;
                }

                // Window is too wide (bars left/right) or too tall (bars top/bottom)?
                double mismatch = (cw / ch) / target - 1;
                if (Math.Abs(mismatch) * Math.Max(cw, ch) < 3) { fitVotes = 0; return; }  // under ~3px of bars: already fits
                if (fitVotes > 0 && Math.Abs(target - fitAspect) > 0.01) fitVotes = 0;
                fitAspect = target;
                if (++fitVotes < 2) return;
                fitVotes = 0;

                // Already tried this exact fix and Windows kept the window at this size (e.g. its minimum width):
                // don't retry forever, or the window creeps across the screen.
                if (Math.Abs(target - lastFitTarget) < 0.01 && b.Width == lastFitW && b.Height == lastFitH) return;

                // Phone rotated (window and picture have different orientation): keep the height so the window
                // doesn't collapse. Otherwise trim whichever side has the bars.
                bool rotated = (cw >= ch) != (target >= 1);
                bool keepHeight = rotated || mismatch > 0;
                Log.Write(string.Format("auto-fit: client {0}x{1} content {2}x{3} -> aspect {4:0.000}{5}", b.Width, b.Height, r.Width, r.Height, target, rotated ? " (rotated)" : ""));
                VideoWin.FitClientToAspect(VideoHwnd, target, keepHeight);
                Native.RECT after;
                Native.GetClientRect(VideoHwnd, out after);
                lastFitTarget = target; lastFitW = after.Width; lastFitH = after.Height;
            }
        }

        double lastFitTarget;
        int lastFitW, lastFitH;

        /// How much of the area where a phone screen of shape `aspect` would be drawn (centered, letterboxed)
        /// is covered by the visible content; 0 when the content spills outside that area.
        static double FillScore(Native.RECT r, double cw, double ch, double aspect)
        {
            double pw, ph;
            if (aspect >= cw / ch) { pw = cw; ph = cw / aspect; } else { ph = ch; pw = ch * aspect; }
            double px = (cw - pw) / 2, py = (ch - ph) / 2;
            double tol = 3 + 0.02 * Math.Max(pw, ph);
            if (r.Left < px - tol || r.Right > px + pw + tol || r.Top < py - tol || r.Bottom > py + ph + tol) return 0;
            return Math.Min(1, (double)r.Width * r.Height / (pw * ph));
        }

        void CloseToolbar()
        {
            fit.Stop();
            track.Stop();
            if (toolbar != null) { toolbar.Close(); toolbar = null; }
        }

        bool OnKey(int vk)
        {
            IntPtr fg = Native.GetForegroundWindow();
            bool ours = fg == VideoHwnd || (toolbar != null && fg == toolbar.Handle) || (drawing != null && fg == drawing.Handle);
            if (VideoHwnd == IntPtr.Zero || !ours) return false;
            if (vk == 0x1B && drawing != null) { Disp.BeginInvoke(new Action(ToggleDrawing)); return true; }    // Esc leaves the pen first
            if (vk == 0x7A) { Disp.BeginInvoke(new Action(ToggleFullscreen)); return true; }                 // F11
            if (vk == 0x1B && VideoFullscreen) { Disp.BeginInvoke(new Action(ToggleFullscreen)); return true; } // Esc
            return false;
        }

        public void ToggleFullscreen()
        {
            if (VideoHwnd == IntPtr.Zero) return;
            if (fsState == null) fsState = VideoWin.EnterFullscreen(VideoHwnd);
            else { VideoWin.ExitFullscreen(VideoHwnd, fsState, VideoPinned); fsState = null; }
            if (toolbar != null) toolbar.Refresh();
            Fire();
        }

        public void TogglePinned() { SetPinned(!VideoPinned); }

        void SetPinned(bool on)
        {
            if (VideoHwnd == IntPtr.Zero) return;
            VideoPinned = on;
            pinnedForRecording = false;
            VideoWin.SetTopmost(VideoHwnd, on);
            if (toolbar != null) toolbar.Refresh();
            Fire();
        }

        /// While recording, keep the video window above others so nothing covers the captured area.
        void RefreshPinForRecording()
        {
            if (VideoHwnd == IntPtr.Zero) return;
            if (Rec.IsRecording && !VideoPinned) { pinnedForRecording = true; VideoWin.SetTopmost(VideoHwnd, true); }
            else if (!Rec.IsRecording && pinnedForRecording) { pinnedForRecording = false; VideoWin.SetTopmost(VideoHwnd, VideoPinned); }
            if (toolbar != null) toolbar.Refresh();
        }

        // ---------------- capture ----------------

        public void TakeScreenshot()
        {
            if (VideoHwnd == IntPtr.Zero) { Notify("ยังไม่มีภาพจาก iPhone ให้ถ่าย", null, true); return; }
            try
            {
                var pen = drawing;
                using (var bmp = VideoWin.Capture(VideoHwnd, pen != null ? (Action<Bitmap>)pen.PaintInto : null))
                {
                    if (bmp == null) { Notify("ถ่ายภาพหน้าจอไม่สำเร็จ", null, true); return; }
                    Directory.CreateDirectory(S.SaveFolder);
                    string f = Recorder.UniquePath(Path.Combine(S.SaveFolder, "UxPlay_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png"));
                    bmp.Save(f, ImageFormat.Png);
                    if (S.CopyScreenshot) { try { WinForms.Clipboard.SetImage(bmp); } catch { } }
                    if (toolbar != null) toolbar.Flash();
                    Notify(S.CopyScreenshot ? "บันทึกภาพแล้ว และคัดลอกไว้ในคลิปบอร์ด" : "บันทึกภาพหน้าจอแล้ว", f, false);
                    if (MediaChanged != null) MediaChanged();
                }
            }
            catch (Exception ex)
            {
                Log.Write("screenshot: " + ex);
                Notify("ถ่ายภาพหน้าจอไม่สำเร็จ: " + ex.Message, null, true);
            }
        }

        public void ToggleRecording()
        {
            if (Rec.IsRecording) { Rec.Stop(); return; }
            if (VideoHwnd == IntPtr.Zero) { Notify("เริ่มบันทึกได้เมื่อ iPhone กำลังแชร์จอ", null, true); return; }
            string err;
            if (!Rec.Start(VideoHwnd, S.Audio ? VideoSourcePid : 0, S, out err)) Notify(err, null, true);
        }

        void OnRecordingFinished(string file, string error)
        {
            if (file != null)
            {
                Notify("บันทึกวิดีโอแล้ว", file, false);
                if (MediaChanged != null) MediaChanged();
            }
            else if (error != null) Notify(error, null, true);
        }

        public void OpenSaveFolder()
        {
            try { Directory.CreateDirectory(S.SaveFolder); Process.Start("explorer.exe", "\"" + S.SaveFolder + "\""); } catch { }
        }

        public static void OpenFile(string f)
        {
            try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch { }
        }

        public static void OpenFile(string f, string args)
        {
            try { Process.Start(new ProcessStartInfo(f, args) { UseShellExecute = true }); } catch { }
        }

        public static void RevealFile(string f)
        {
            try { Process.Start("explorer.exe", "/select,\"" + f + "\""); } catch { }
        }

        // ---------------- maintenance ----------------

        static string ReadUxPlayVersion()
        {
            try
            {
                var psi = new ProcessStartInfo(Paths.UxPlayExe, "-v");
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    var m = System.Text.RegularExpressions.Regex.Match(o, @"version\s+([0-9.]+)");
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch { return null; }
        }

        /// Pull the latest UxPlay source and rebuild it in a visible MSYS2 terminal.
        public void UpdateUxPlay()
        {
            bool wasRunning = Server.IsRunning;
            if (Rec.IsRecording) Rec.StopAndWait();
            Server.Stop();
            string shell = Path.Combine(Paths.MsysRoot, "msys2_shell.cmd");
            const string script = "cd ~/UxPlay && git pull && cd build && cmake .. && ninja && cmake --install . --prefix $HOME/../../ucrt64; " +
                                  "echo; echo '--- done: close this window to return to UxPlay Mirror ---'; read -t 600 -p 'Press Enter to close'";
            try
            {
                var psi = new ProcessStartInfo(shell, "-ucrt64 -no-start -c \"" + script + "\"");
                psi.UseShellExecute = false;
                var p = Process.Start(psi);
                Task.Factory.StartNew(() =>
                {
                    try { p.WaitForExit(); } catch { }
                    Thread.Sleep(1000);
                    string v = ReadUxPlayVersion();
                    Disp.BeginInvoke(new Action(() =>
                    {
                        UxPlayVersion = v;
                        if (wasRunning) Server.Start(S);
                        Notify("อัปเดต UxPlay เสร็จแล้ว" + (v != null ? " (เวอร์ชัน " + v + ")" : ""), null, false);
                        Fire();
                    }));
                });
            }
            catch (Exception ex)
            {
                Notify("เปิดหน้าต่างอัปเดตไม่ได้: " + ex.Message, null, true);
                if (wasRunning) Server.Start(S);
            }
        }
    }
}
