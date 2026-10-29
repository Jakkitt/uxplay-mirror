using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("UxPlay Mirror")]
[assembly: AssemblyProduct("UxPlay Mirror")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace UxPlayMirror
{
    static class Paths
    {
        public const string AppVersion = "1.0";
        public const string MsysRoot = @"C:\msys64";
        public static readonly string BinDir = Path.Combine(MsysRoot, @"ucrt64\bin");
        public static readonly string UxPlayExe = Path.Combine(BinDir, "uxplay.exe");
        public static readonly string GstLaunch = Path.Combine(BinDir, "gst-launch-1.0.exe");
        public static readonly string GstInspect = Path.Combine(BinDir, "gst-inspect-1.0.exe");
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string IconFile = Path.Combine(AppDir, "app.ico");
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UxPlay Mirror");
        public static readonly string LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UxPlay Mirror");
        public static readonly string LogDir = Path.Combine(LocalDir, "logs");
        public static readonly string TempDir = Path.Combine(LocalDir, "tmp");

        static string host;
        public static string HostName
        {
            get
            {
                if (host == null) { try { host = Dns.GetHostName(); } catch { host = Environment.MachineName; } }
                return host;
            }
        }

        static Paths()
        {
            try
            {
                Directory.CreateDirectory(DataDir); Directory.CreateDirectory(LogDir); Directory.CreateDirectory(TempDir);
                Directory.CreateDirectory(Path.Combine(LocalDir, "nowplaying"));
            }
            catch { }
        }
    }

    /// Windows 11 Smart App Control blocks the unsigned GStreamer DLLs that uxplay.exe loads.
    static class SmartAppControl
    {
        public const int Off = 0, On = 1, Evaluation = 2;
        /// Exit code of a process killed by the policy (STATUS_SYSTEM_INTEGRITY_POLICY_VIOLATION).
        public const int BlockedExitCode = unchecked((int)0xC0E90002);
        public const string SettingsUri = "windowsdefender://appbrowser";

        public static int State
        {
            get
            {
                int fake;   // test hook: UXM_FAKE_SAC=1 shows the "on" warnings on a machine where it's already off
                if (int.TryParse(Environment.GetEnvironmentVariable("UXM_FAKE_SAC"), out fake)) return fake;
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy"))
                    {
                        object v = k == null ? null : k.GetValue("VerifiedAndReputablePolicyState");
                        return v is int ? (int)v : Off;
                    }
                }
                catch { return Off; }
            }
        }
    }

    static class AppIcon
    {
        /// The biggest picture inside an .ico file. WPF otherwise takes the first (16 px) one and blows it up,
        /// which makes the logo blurry wherever it's shown larger than a small icon.
        public static System.Windows.Media.Imaging.BitmapSource Largest(Stream ico)
        {
            var d = new System.Windows.Media.Imaging.IconBitmapDecoder(ico,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            System.Windows.Media.Imaging.BitmapFrame best = null;
            foreach (var f in d.Frames) if (best == null || f.PixelWidth > best.PixelWidth) best = f;
            if (best != null) best.Freeze();
            return best;
        }

        public static System.Windows.Media.Imaging.BitmapSource Largest(string path)
        {
            using (var s = File.OpenRead(path)) return Largest(s);
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        static readonly string file = Path.Combine(Paths.LogDir, "app.log");

        public static void Write(string msg)
        {
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(file);
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    class AppSettings
    {
        public string DeviceName = "UxPlay";
        public bool AppendHost = true;
        public string Resolution = "1080p";
        public int Fps = 60;
        public bool FullscreenOnConnect = false;
        public bool VideoOnTop = false;
        public bool AutoFitWindow = true;
        public bool Hotkeys = true;
        public bool ShowNowPlaying = true;
        public bool LowLatency = true;
        public bool Audio = true;
        public bool PinEnabled = false;
        public string Pin = "";
        public string SaveFolder = "";
        public bool RecordAudio = true;
        public bool CopyScreenshot = true;
        public string RecordQuality = "high";
        public bool AutoServe = true;
        public bool StartWithWindows = false;
        public bool CloseToTray = true;
        public bool TrayHintShown = false;
        public string Renderer = "d3d12";
        public bool SoftwareDecode = false;
        public bool NoHold = false;
        public string Theme = "system";

        static string FilePath { get { return Path.Combine(Paths.DataDir, "settings.ini"); } }

        public static string DefaultSaveFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "UxPlay Mirror"); }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        var f = typeof(AppSettings).GetField(line.Substring(0, i).Trim());
                        if (f == null) continue;
                        string v = line.Substring(i + 1).Trim();
                        if (f.FieldType == typeof(bool)) f.SetValue(s, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                        else if (f.FieldType == typeof(int)) { int n; if (int.TryParse(v, out n)) f.SetValue(s, n); }
                        else f.SetValue(s, v);
                    }
                }
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
            if (string.IsNullOrEmpty(s.SaveFolder)) s.SaveFolder = DefaultSaveFolder;
            if (s.Fps != 30 && s.Fps != 60 && s.Fps != 120) s.Fps = 60;
            return s;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var f in typeof(AppSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = f.GetValue(this);
                sb.Append(f.Name).Append('=');
                if (v is bool) sb.Append((bool)v ? "1" : "0"); else sb.Append(Convert.ToString(v));
                sb.AppendLine();
            }
            try { File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8); }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }

        public AppSettings Clone() { return (AppSettings)MemberwiseClone(); }

        /// Settings that only take effect when uxplay is restarted.
        public string ServerKey()
        {
            return string.Join("|", new object[] { DeviceName, AppendHost, Resolution, Fps, LowLatency, Audio, PinEnabled, Pin, Renderer, SoftwareDecode, NoHold });
        }

        public string DisplayName { get { return AppendHost ? DeviceName + "@" + Paths.HostName : DeviceName; } }

        public static bool IsValidPin(string p) { return p != null && Regex.IsMatch(p, "^[0-9]{4}$") && p != "0000"; }
        public static bool IsValidName(string n) { return n != null && Regex.IsMatch(n, @"^[A-Za-z0-9][A-Za-z0-9 ._'\-]{0,31}$"); }

        public int[] VideoSize()
        {
            if (Resolution == "1440p") return new[] { 2560, 1440 };
            if (Resolution == "4k") return new[] { 3840, 2160 };
            return new[] { 1920, 1080 };
        }

        public string QualityLabel
        {
            get
            {
                string r = Resolution == "4k" ? "4K" : Resolution;
                return r + " · " + Fps + " FPS" + (Audio ? "" : " · ไม่มีเสียง");
            }
        }

        public string BuildArgs()
        {
            var a = new List<string>();
            a.Add("-n"); a.Add(Quote(DeviceName));
            if (!AppendHost) a.Add("-nh");
            int[] sz = VideoSize();
            a.Add("-s"); a.Add(sz[0] + "x" + sz[1] + "@" + Fps);
            a.Add("-fps"); a.Add(Fps.ToString());
            if (Resolution == "4k") a.Add("-h265");
            if (LowLatency) { a.Add("-vsync"); a.Add("no"); }
            a.Add("-vs"); a.Add(Renderer == "d3d11" ? "d3d11videosink" : "d3d12videosink");
            if (!Audio) { a.Add("-as"); a.Add("0"); }
            if (PinEnabled && IsValidPin(Pin)) { a.Add("-pin"); a.Add(Pin); }
            if (SoftwareDecode) a.Add("-avdec");
            if (NoHold) a.Add("-nohold");
            a.Add("-FPSdata");                                   // frame-rate reports from the phone
            a.Add("-ca"); a.Add(Quote(NowPlaying.CoverFile));   // album art for the Now Playing screen
            return string.Join(" ", a);
        }

        static string Quote(string s) { return "\"" + s.Replace("\"", "") + "\""; }
    }

    static class Theme
    {
        public static bool IsDark { get; private set; }

        public static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        static void Set(ResourceDictionary r, string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            r[key] = b;
        }

        public static void Apply(string mode)
        {
            bool dark = mode == "dark" || (mode == "system" && SystemIsDark());
            IsDark = dark;
            var r = Application.Current.Resources;
            if (!dark)
            {
                Set(r, "Bg", "#F4F6FA"); Set(r, "NavBg", "#FBFCFE"); Set(r, "Surface", "#FFFFFF"); Set(r, "SurfaceAlt", "#F1F4F9");
                Set(r, "Stroke", "#E3E8EF"); Set(r, "Hover", "#EEF2F8"); Set(r, "Text", "#0F172A"); Set(r, "TextMuted", "#64748B");
                Set(r, "Accent", "#2F6BFF"); Set(r, "AccentHover", "#2459D9"); Set(r, "AccentSoft", "#E9F0FF"); Set(r, "OnAccent", "#FFFFFF");
                Set(r, "Success", "#16A34A"); Set(r, "SuccessSoft", "#E6F6EC"); Set(r, "Warning", "#D97706"); Set(r, "WarningSoft", "#FDF3E3");
                Set(r, "Danger", "#DC2626"); Set(r, "DangerSoft", "#FDECEC"); Set(r, "SwitchOff", "#FFFFFF"); Set(r, "SwitchBorder", "#8A97AB");
                Set(r, "ScrollThumb", "#CBD5E1"); Set(r, "SegBg", "#EEF1F6"); Set(r, "PhoneFrame", "#1E2633"); Set(r, "SnackBg", "#1E2633");
                Set(r, "SnackFg", "#FFFFFF"); Set(r, "SnackLink", "#8DB0FF"); Set(r, "PowerOff", "#94A3B8");
            }
            else
            {
                Set(r, "Bg", "#0F1115"); Set(r, "NavBg", "#14171C"); Set(r, "Surface", "#1A1E25"); Set(r, "SurfaceAlt", "#222731");
                Set(r, "Stroke", "#2A303B"); Set(r, "Hover", "#262C37"); Set(r, "Text", "#F1F5F9"); Set(r, "TextMuted", "#94A3B8");
                Set(r, "Accent", "#4F86FF"); Set(r, "AccentHover", "#6B9AFF"); Set(r, "AccentSoft", "#1C2A4A"); Set(r, "OnAccent", "#FFFFFF");
                Set(r, "Success", "#22C55E"); Set(r, "SuccessSoft", "#12301F"); Set(r, "Warning", "#F59E0B"); Set(r, "WarningSoft", "#33260E");
                Set(r, "Danger", "#F87171"); Set(r, "DangerSoft", "#3A1A1A"); Set(r, "SwitchOff", "#1A1E25"); Set(r, "SwitchBorder", "#7C8799");
                Set(r, "ScrollThumb", "#3A4250"); Set(r, "SegBg", "#222731"); Set(r, "PhoneFrame", "#3A4250"); Set(r, "SnackBg", "#E8ECF2");
                Set(r, "SnackFg", "#0F172A"); Set(r, "SnackLink", "#2F6BFF"); Set(r, "PowerOff", "#4B5563");
            }
        }

        public static Brush B(string key) { return (Brush)Application.Current.Resources[key]; }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "UxPlay Mirror";

        public static void Set(bool enabled)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled)
                        k.SetValue(Name, "\"" + Path.Combine(Paths.AppDir, "UxPlayMirror.exe") + "\" --tray");
                    else if (k.GetValue(Name) != null)
                        k.DeleteValue(Name, false);
                }
            }
            catch (Exception ex) { Log.Write("startup: " + ex.Message); }
        }
    }
}
