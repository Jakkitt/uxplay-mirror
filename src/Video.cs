using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UxPlayMirror
{
    /// Helpers for the video window that uxplay (GStreamer) opens.
    static class VideoWin
    {
        public static IntPtr Find(int pid)
        {
            if (pid <= 0) return IntPtr.Zero;
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            Native.EnumWindows((h, l) =>
            {
                uint wp;
                Native.GetWindowThreadProcessId(h, out wp);
                if (wp != pid || !Native.IsWindowVisible(h)) return true;
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                long area = (long)r.Width * r.Height;
                if (r.Width > 80 && r.Height > 80 && area > bestArea) { best = h; bestArea = area; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        public static void Decorate(IntPtr h, string title, IntPtr bigIcon, IntPtr smallIcon)
        {
            Native.SetWindowText(h, title);
            if (bigIcon != IntPtr.Zero) Native.SendMessage(h, Native.WM_SETICON, new IntPtr(1), bigIcon);
            if (smallIcon != IntPtr.Zero) Native.SendMessage(h, Native.WM_SETICON, IntPtr.Zero, smallIcon);
        }

        /// GStreamer opens the window at the stream's native size, which can be taller than the screen.
        public static void FitToScreen(IntPtr h)
        {
            IntPtr mon;
            var mi = Native.Monitor(h, out mon);
            var work = mi.rcWork;
            Native.RECT r;
            Native.GetWindowRect(h, out r);
            if (r.Width <= work.Width * 0.92 && r.Height <= work.Height * 0.92 &&
                r.Left >= work.Left && r.Top >= work.Top && r.Right <= work.Right && r.Bottom <= work.Bottom) return;
            double scale = Math.Min(work.Width * 0.82 / r.Width, work.Height * 0.85 / r.Height);
            if (scale > 1) scale = 1;
            int w = (int)(r.Width * scale), hh = (int)(r.Height * scale);
            int x = work.Left + (work.Width - w) / 2, y = work.Top + (work.Height - hh) / 2;
            Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hh, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public static void SetTopmost(IntPtr h, bool on)
        {
            Native.SetWindowPos(h, on ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public class FullscreenState { public IntPtr Style; public Native.RECT Rect; }

        public static FullscreenState EnterFullscreen(IntPtr h)
        {
            var st = new FullscreenState();
            st.Style = Native.GetWindowLongPtr(h, Native.GWL_STYLE);
            Native.GetWindowRect(h, out st.Rect);
            IntPtr mon;
            var mi = Native.Monitor(h, out mon);
            long style = st.Style.ToInt64() & ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_MAXIMIZE);
            Native.SetWindowLongPtr(h, Native.GWL_STYLE, new IntPtr(style));
            var m = mi.rcMonitor;
            Native.SetWindowPos(h, Native.HWND_TOP, m.Left, m.Top, m.Width, m.Height, Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
            Native.SetForegroundWindow(h);
            return st;
        }

        public static void ExitFullscreen(IntPtr h, FullscreenState st, bool topmost)
        {
            if (st == null) return;
            Native.SetWindowLongPtr(h, Native.GWL_STYLE, st.Style);
            var r = st.Rect;
            Native.SetWindowPos(h, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, r.Left, r.Top, r.Width, r.Height,
                Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
        }

        /// Grabs the window's client area without bringing it forward. Returns null if nothing was drawn.
        public static Bitmap CaptureRaw(IntPtr h)
        {
            Native.RECT c;
            Native.GetClientRect(h, out c);
            if (c.Width < 2 || c.Height < 2) return null;
            var bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                ok = Native.PrintWindow(h, hdc, 3); // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
            }
            if (ok && !IsBlank(bmp)) return bmp;
            bmp.Dispose();
            return null;
        }

        /// Screenshot of the mirrored screen with the black letterbox bars trimmed off.
        /// `decorate` can paint on the full-size frame first (pen strokes).
        public static Bitmap Capture(IntPtr h, Action<Bitmap> decorate)
        {
            var bmp = CaptureRaw(h);
            if (bmp == null)
            {
                // Fallback: copy the pixels from the screen after bringing the window forward.
                Native.SetForegroundWindow(h);
                Thread.Sleep(150);
                var r = Native.ClientScreenRect(h);
                if (r.Width < 2 || r.Height < 2) return null;
                bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(r.Width, r.Height));
            }
            if (decorate != null) decorate(bmp);
            var cb = ContentBounds(bmp);
            if (cb.Width >= 16 && cb.Height >= 16 && (cb.Width < bmp.Width || cb.Height < bmp.Height))
            {
                var cropped = bmp.Clone(new Rectangle(cb.Left, cb.Top, cb.Width, cb.Height), PixelFormat.Format32bppRgb);
                bmp.Dispose();
                return cropped;
            }
            return bmp;
        }

        /// The part of the frame that is not pure black bars (letterbox / pillarbox).
        public static Native.RECT ContentBounds(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                int stride = data.Stride;
                byte[] px = new byte[stride * h];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                Func<int, bool> colDark = x =>
                {
                    for (int y = 0; y < h; y += 2) { int i = y * stride + x * 4; if (px[i] > 18 || px[i + 1] > 18 || px[i + 2] > 18) return false; }
                    return true;
                };
                Func<int, bool> rowDark = y =>
                {
                    int o = y * stride;
                    for (int x = 0; x < w; x += 2) { int i = o + x * 4; if (px[i] > 18 || px[i + 1] > 18 || px[i + 2] > 18) return false; }
                    return true;
                };
                int left = 0, right = w, top = 0, bottom = h;
                while (left < right && colDark(left)) left++;
                while (right > left && colDark(right - 1)) right--;
                while (top < bottom && rowDark(top)) top++;
                while (bottom > top && rowDark(bottom - 1)) bottom--;
                return new Native.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
            }
            finally { b.UnlockBits(data); }
        }

        /// Resize the window so its client area matches the phone picture's aspect ratio, staying on screen.
        public static void FitClientToAspect(IntPtr h, double aspect, bool keepHeight)
        {
            Native.RECT wr, cr;
            Native.GetWindowRect(h, out wr);
            Native.GetClientRect(h, out cr);
            int extraW = wr.Width - cr.Width, extraH = wr.Height - cr.Height;
            double cw = cr.Width, ch = cr.Height;
            if (keepHeight) cw = ch * aspect; else ch = cw / aspect;

            IntPtr mon;
            var work = Native.Monitor(h, out mon).rcWork;
            double maxW = work.Width - extraW, maxH = work.Height - extraH;

            // Never shrink to a size you can't use: a portrait phone at least ~45% of the screen tall,
            // a landscape one at least ~35% of the screen wide.
            if (aspect < 1) { double minH = maxH * 0.45; if (ch < minH) { ch = minH; cw = ch * aspect; } }
            else { double minW = maxW * 0.35; if (cw < minW) { cw = minW; ch = cw / aspect; } }

            double scale = Math.Min(1, Math.Min(maxW / cw, maxH / ch));
            cw *= scale; ch *= scale;

            int nw = (int)Math.Round(cw) + extraW, nh = (int)Math.Round(ch) + extraH;
            int cx = wr.Left + wr.Width / 2, cy = wr.Top + wr.Height / 2;
            int x = Math.Max(work.Left, Math.Min(cx - nw / 2, work.Right - nw));
            int y = Math.Max(work.Top, Math.Min(cy - nh / 2, work.Bottom - nh));
            Native.SetWindowPos(h, IntPtr.Zero, x, y, nw, nh, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        static bool IsBlank(Bitmap b)
        {
            int nonBlack = 0;
            for (int y = 1; y < 10; y++)
                for (int x = 1; x < 10; x++)
                {
                    var c = b.GetPixel(b.Width * x / 10, b.Height * y / 10);
                    if (c.R + c.G + c.B > 24) nonBlack++;
                }
            return nonBlack == 0;
        }
    }

    /// Records the mirrored window (screen capture + uxplay's audio) to MP4 with gst-launch.
    class Recorder
    {
        static readonly object consoleLock = new object();
        static readonly string[] Encoders = { "nvd3d11h264enc", "mfh264enc", "openh264enc" };
        static readonly Dictionary<string, bool> available = new Dictionary<string, bool>();

        readonly Dispatcher disp;
        Process gst;
        string tmpFile, saveFolder;
        bool stopping;
        int encoderIndex;
        StringBuilder errors = new StringBuilder();
        Func<string> pipelineFor;

        public bool IsRecording { get { return gst != null; } }
        public DateTime Started { get; private set; }
        public string EncoderName { get; private set; }
        public event Action Changed;
        public event Action<string, string> Finished; // saved file (or null), error message (or null)

        public Recorder(Dispatcher d) { disp = d; }

        public static void ProbeEncoders()
        {
            foreach (var e in Encoders)
            {
                bool ok = false;
                try
                {
                    var psi = new ProcessStartInfo(Paths.GstInspect, e);
                    psi.UseShellExecute = false; psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                    using (var p = Process.Start(psi))
                    {
                        p.StandardOutput.ReadToEnd();
                        p.WaitForExit(15000);
                        ok = p.ExitCode == 0;
                    }
                }
                catch { }
                lock (available) available[e] = ok;
            }
            Log.Write("encoders: " + string.Join(", ", Encoders) + " -> " + string.Join(", ", new List<bool>(available.Values)));
        }

        static bool IsAvailable(string enc)
        {
            lock (available) { bool v; return !available.TryGetValue(enc, out v) || v; }
        }

        public bool Start(IntPtr hwnd, int audioPid, AppSettings s, out string error)
        {
            error = null;
            if (gst != null) return true;
            if (!File.Exists(Paths.GstLaunch)) { error = "ไม่พบ gst-launch-1.0.exe"; return false; }

            IntPtr hmon;
            var mi = Native.Monitor(hwnd, out hmon);
            var mon = mi.rcMonitor;
            var r = Native.ClientScreenRect(hwnd);
            int x = Math.Max(r.Left, mon.Left), y = Math.Max(r.Top, mon.Top);
            int right = Math.Min(r.Right, mon.Right), bottom = Math.Min(r.Bottom, mon.Bottom);
            int w = (right - x) & ~1, h = (bottom - y) & ~1;
            if (w < 64 || h < 64) { error = "หน้าต่างภาพเล็กเกินไปหรืออยู่นอกจอ"; return false; }

            saveFolder = s.SaveFolder;
            tmpFile = Path.Combine(Paths.TempDir, "rec_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");
            int fps = Math.Min(s.Fps, 60); // the phone's AirPlay stream itself tops out at 60

            int kbps = s.RecordQuality == "standard" ? 8000 : 16000;
            if (w * h > 2560 * 1440) kbps = kbps * 2;
            bool audio = s.RecordAudio && audioPid > 0;
            string monHandle = hmon.ToInt64().ToString();
            int cropX = x - mon.Left, cropY = y - mon.Top;

            pipelineFor = () =>
            {
                string enc = Encoders[encoderIndex];
                var a = new List<string>();
                a.Add("-e");
                a.Add(string.Format("d3d11screencapturesrc monitor-handle={0} crop-x={1} crop-y={2} crop-width={3} crop-height={4} show-cursor=false",
                    monHandle, cropX, cropY, w, h));
                a.Add("! video/x-raw(memory:D3D11Memory),framerate=" + fps + "/1 ! queue max-size-buffers=8 leaky=downstream");
                if (enc == "nvd3d11h264enc")
                    a.Add("! d3d11convert ! video/x-raw(memory:D3D11Memory),format=NV12 ! nvd3d11h264enc bitrate=" + kbps + " gop-size=" + (fps * 2) + " bframes=0");
                else if (enc == "mfh264enc")
                    a.Add("! d3d11download ! videoconvert ! video/x-raw,format=NV12 ! mfh264enc bitrate=" + kbps + " gop-size=" + (fps * 2));
                else
                    a.Add("! d3d11download ! videoconvert ! video/x-raw,format=I420 ! openh264enc bitrate=" + (kbps * 1000));
                a.Add("! h264parse ! queue ! mux.");
                if (audio)
                {
                    const string caps = "audio/x-raw,format=F32LE,rate=48000,channels=2,layout=interleaved";
                    a.Add("audiotestsrc wave=silence is-live=true ! audioconvert ! " + caps + " ! amix.");
                    a.Add("wasapi2src loopback=true loopback-mode=include-process-tree loopback-target-pid=" + audioPid +
                          " ! queue ! audioconvert ! audioresample ! " + caps + " ! amix.");
                    a.Add("audiomixer name=amix ! audioconvert ! avenc_aac bitrate=192000 ! queue ! mux.");
                }
                a.Add("mp4mux name=mux ! filesink");
                a.Add("\"location=" + tmpFile.Replace('\\', '/') + "\"");
                return string.Join(" ", a);
            };

            encoderIndex = 0;
            while (encoderIndex < Encoders.Length && !IsAvailable(Encoders[encoderIndex])) encoderIndex++;
            if (encoderIndex >= Encoders.Length) { error = "ไม่พบตัวเข้ารหัสวิดีโอที่ใช้ได้"; return false; }
            Launch();
            return true;
        }

        void Launch()
        {
            errors = new StringBuilder();
            stopping = false;
            EncoderName = Encoders[encoderIndex];
            var psi = new ProcessStartInfo(Paths.GstLaunch, pipelineFor());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Log.Write("record: gst-launch-1.0 " + psi.Arguments);
            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (o, e) => { if (e.Data != null && (e.Data.Contains("ERROR") || e.Data.Contains("WARNING"))) lock (errors) errors.AppendLine(e.Data); };
            p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            p.Exited += (o, e) => disp.BeginInvoke(new Action(() => OnExited(p)));
            p.Start();
            Native.AddToJob(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            gst = p;
            Started = DateTime.Now;
            if (Changed != null) Changed();
        }

        public void Stop()
        {
            var p = gst;
            if (p == null || stopping) return;
            stopping = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (consoleLock)
                    {
                        Native.FreeConsole();
                        if (Native.AttachConsole((uint)p.Id))
                        {
                            Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
                            Native.GenerateConsoleCtrlEvent(0, 0);
                            Thread.Sleep(100);
                            Native.FreeConsole();
                        }
                    }
                    if (!p.WaitForExit(10000)) p.Kill();
                }
                catch (Exception ex) { Log.Write("record stop: " + ex.Message); try { p.Kill(); } catch { } }
            });
            if (Changed != null) Changed();
        }

        /// Blocks until the current recording is finalized (used when the app exits).
        public void StopAndWait()
        {
            var p = gst;
            if (p == null) return;
            Stop();
            try { p.WaitForExit(12000); } catch { }
        }

        void OnExited(Process p)
        {
            if (p != gst) return;
            gst = null;
            string err;
            lock (errors) err = errors.ToString().Trim();
            bool quickFail = !stopping && (DateTime.Now - Started).TotalSeconds < 4;
            if (quickFail)
            {
                Log.Write("record: " + EncoderName + " failed: " + err);
                lock (available) available[EncoderName] = false;
                encoderIndex++;
                while (encoderIndex < Encoders.Length && !IsAvailable(Encoders[encoderIndex])) encoderIndex++;
                if (encoderIndex < Encoders.Length) { Launch(); return; }
            }

            string saved = null, message = null;
            try
            {
                if (File.Exists(tmpFile) && new FileInfo(tmpFile).Length > 4096)
                {
                    Directory.CreateDirectory(saveFolder);
                    saved = UniquePath(Path.Combine(saveFolder, "UxPlay_" + Started.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4"));
                    File.Move(tmpFile, saved);
                }
                else
                {
                    message = "บันทึกวิดีโอไม่สำเร็จ" + (err.Length > 0 ? ": " + LastLine(err) : "");
                    if (File.Exists(tmpFile)) File.Delete(tmpFile);
                }
            }
            catch (Exception ex) { message = "บันทึกไฟล์ไม่สำเร็จ: " + ex.Message; }
            if (message != null) Log.Write("record: " + message + "\n" + err);
            stopping = false;
            if (Changed != null) Changed();
            if (Finished != null) Finished(saved, message);
        }

        static string LastLine(string s)
        {
            var lines = s.Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
        }
    }

    /// Windows Explorer thumbnails (works for both images and videos).
    static class ShellThumb
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        /// Real thumbnail only (no generic file-type icon); null when Windows has none.
        public static BitmapSource Get(string path, int size)
        {
            try
            {
                IShellItemImageFactory f;
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                IntPtr hbm;
                int hr = f.GetImage(new SIZE { cx = size, cy = size }, 0x8 | 0x1, out hbm); // THUMBNAILONLY | BIGGERSIZEOK
                Marshal.ReleaseComObject(f);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DeleteObject(hbm); }
            }
            catch { return null; }
        }
    }

    /// System-wide shortcuts (Ctrl+Alt+…), registered only while the phone is mirroring.
    class HotKeys : IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const uint Alt = 1, Ctrl = 2, Shift = 4;
        readonly HwndSource src;
        readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        int nextId = 0x5100;

        public HotKeys()
        {
            var p = new HwndSourceParameters("UxPlayMirrorHotKeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
            src = new HwndSource(p);
            src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                Action a;
                if (msg == 0x312 && actions.TryGetValue(w.ToInt32(), out a)) { handled = true; a(); }
                return IntPtr.Zero;
            });
        }

        /// Returns false when another program already owns this key combination.
        public bool Add(uint mods, System.Windows.Forms.Keys key, Action a)
        {
            int id = nextId++;
            if (!RegisterHotKey(src.Handle, id, mods | 0x4000 /* MOD_NOREPEAT */, (uint)key)) return false;
            actions[id] = a;
            return true;
        }

        public void Dispose()
        {
            foreach (var id in actions.Keys) UnregisterHotKey(src.Handle, id);
            actions.Clear();
            src.Dispose();
        }
    }

    /// F11 / Esc for the video window. The hook is only installed while a video window exists.
    class KeyHook : IDisposable
    {
        IntPtr hook;
        readonly Native.LowLevelKeyboardProc proc;
        readonly Func<int, bool> handler;

        public KeyHook(Func<int, bool> onKeyDown)
        {
            handler = onKeyDown;
            proc = Callback;
            hook = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle(null), 0);
        }

        IntPtr Callback(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0 && (w.ToInt32() == 0x100 || w.ToInt32() == 0x104))
            {
                int vk = Marshal.ReadInt32(l);
                try { if (handler(vk)) return new IntPtr(1); } catch { }
            }
            return Native.CallNextHookEx(hook, nCode, w, l);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }
}
