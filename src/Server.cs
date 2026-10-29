using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace UxPlayMirror
{
    enum ServerState { Stopped, Starting, Ready, Connected, Mirroring, Error }

    /// Runs uxplay.exe in the background and turns its output into a simple state.
    class UxPlayServer
    {
        readonly Dispatcher disp;
        Process proc;
        AppSettings cfg;
        bool userStop;
        DateTime startTime;
        DateTime lastRemoteSeen;
        readonly List<DateTime> crashes = new List<DateTime>();
        StreamWriter fileLog;

        public ServerState State { get; private set; }
        public string ClientName { get; private set; }
        public string ClientModel { get; private set; }
        public string LastError { get; private set; }
        /// Width / height of the phone's video stream, as reported by uxplay (0 until mirroring starts).
        public double StreamAspect { get; private set; }
        public string RunningKey { get; private set; }
        public readonly NowPlaying Track = new NowPlaying();
        /// Latest frame rate reported by the phone, or -1.
        public double ClientFps { get; private set; }
        DateTime fpsAt;
        public bool FpsFresh { get { return ClientFps >= 0 && (DateTime.Now - fpsAt).TotalSeconds < 3; } }
        readonly StringBuilder plist = new StringBuilder();
        bool inPlist;

        public event Action<string> Line;
        public event Action Changed;
        public event Action ClientRequested;
        public event Action FpsUpdated;
        public event Action TrackChanged;

        public UxPlayServer(Dispatcher d) { disp = d; LastError = ""; ClientFps = -1; }

        /// Test mode only: pretend a phone is connected.
        public bool DemoConnected;

        /// Test mode only: pretend a phone stream of this shape is playing.
        public void SimulateStream(double aspect) { StreamAspect = aspect; }

        public int Pid
        {
            get
            {
                var p = proc;
                try { return p != null && !p.HasExited ? p.Id : 0; } catch { return 0; }
            }
        }

        public bool IsRunning { get { return proc != null; } }

        public void Start(AppSettings s)
        {
            Stop();
            cfg = s.Clone();
            userStop = false;
            LastError = "";
            StreamAspect = 0;
            ClientFps = -1;
            inPlist = false;
            Track.Clear();
            ClientName = null;
            ClientModel = null;
            if (!File.Exists(Paths.UxPlayExe))
            {
                SetState(ServerState.Error, "ไม่พบ uxplay.exe ที่ " + Paths.UxPlayExe);
                return;
            }
            KillStrays();
            OpenFileLog();

            var psi = new ProcessStartInfo(Paths.UxPlayExe, cfg.BuildArgs());
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = Paths.DataDir;

            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (o, e) => { if (e.Data != null) Post(e.Data); };
            p.ErrorDataReceived += (o, e) => { if (e.Data != null) Post(e.Data); };
            p.Exited += (o, e) => disp.BeginInvoke(new Action(() => OnExited(p)));

            Emit("▶ uxplay " + psi.Arguments);
            try { p.Start(); }
            catch (Exception ex)
            {
                SetState(ServerState.Error, "เปิด uxplay ไม่ได้: " + ex.Message);
                return;
            }
            Native.AddToJob(p);
            proc = p;
            RunningKey = cfg.ServerKey();
            startTime = DateTime.Now;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            SetState(ServerState.Starting, null);
        }

        public void Stop()
        {
            var p = proc;
            if (p == null) return;
            userStop = true;
            proc = null;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { }
            ClientName = null;
            ClientModel = null;
            Track.Clear();
            if (TrackChanged != null) TrackChanged();
            Emit("■ หยุดตัวรับสัญญาณ");
            SetState(ServerState.Stopped, null);
        }

        /// Called a few times per second with whether uxplay currently shows a video window.
        public void UpdateLive(bool hasVideo)
        {
            if (State != ServerState.Ready && State != ServerState.Connected && State != ServerState.Mirroring) return;
            int remote = DemoConnected ? 1 : Native.CountRemoteConnections(Pid);
            if (remote > 0) lastRemoteSeen = DateTime.Now;
            bool recentlyConnected = remote > 0 || (DateTime.Now - lastRemoteSeen).TotalSeconds < 3;

            ServerState next;
            if (hasVideo) next = ServerState.Mirroring;
            else if (recentlyConnected && ClientName != null) next = ServerState.Connected;
            else next = ServerState.Ready;

            if (next == ServerState.Ready && !recentlyConnected) { ClientName = null; ClientModel = null; }
            if (next != State) SetState(next, null);
        }

        public List<int> ListeningPorts()
        {
            var ports = new List<int>();
            foreach (var r in Native.TcpRowsFor(Pid))
                if (r.State == Native.TCP_LISTEN && !ports.Contains(r.LocalPort)) ports.Add(r.LocalPort);
            ports.Sort();
            return ports;
        }

        void Post(string line) { disp.BeginInvoke(new Action(() => HandleLine(line))); }

        /// Test mode only: process a line as if uxplay had printed it.
        public void SimulateLine(string l) { HandleLine(l); }

        void HandleLine(string l)
        {
            // Video statistics plist (one every second with -FPSdata): parse it, keep it out of the log.
            if (inPlist || l.StartsWith("<?xml"))
            {
                if (!inPlist) { inPlist = true; plist.Clear(); }
                plist.AppendLine(l);
                if (l.Contains("</plist>") || plist.Length > 200000)
                {
                    inPlist = false;
                    double fps = FpsReport.Parse(plist.ToString());
                    if (fps >= 0)
                    {
                        ClientFps = fps;
                        fpsAt = DateTime.Now;
                        if (FpsUpdated != null) FpsUpdated();
                    }
                }
                return;
            }

            var kind = Track.Feed(l);
            if (kind == NowPlaying.Parse.Progress) { if (TrackChanged != null) TrackChanged(); return; }  // every second: not logged
            if (kind != NowPlaying.Parse.None && TrackChanged != null) TrackChanged();
            if (l.StartsWith("Connection closed on socket") && Track.Active)
            {
                Track.Clear();
                if (TrackChanged != null) TrackChanged();
            }

            Emit(l);
            if (l.Contains("Initialized server socket") && State == ServerState.Starting)
                SetState(ServerState.Ready, null);

            var m = Regex.Match(l, @"connection request from (.*) \((.*)\) with deviceID");
            if (m.Success)
            {
                ClientName = m.Groups[1].Value.Trim();
                ClientModel = m.Groups[2].Value.Trim();
                lastRemoteSeen = DateTime.Now;
                if (ClientRequested != null) ClientRequested();
                if (State == ServerState.Ready) SetState(ServerState.Connected, null);
                else Fire();
            }
            var g = Regex.Match(l, @"geometry is \(w,h\)=\((\d+),(\d+)\)");
            if (g.Success)
            {
                double w = double.Parse(g.Groups[1].Value), h = double.Parse(g.Groups[2].Value);
                if (w > 0 && h > 0) StreamAspect = w / h;
            }
            if (l.StartsWith("*** ERROR") || l.StartsWith("invalid") || l.StartsWith("unknown option"))
                LastError = l.Replace("*** ERROR:", "").Trim();
        }

        void OnExited(Process p)
        {
            if (p != proc) return;
            int code = 0;
            try { code = p.ExitCode; } catch { }
            proc = null;
            if (userStop) return;
            Emit("uxplay หยุดทำงาน (exit " + code + ")");

            if (code == SmartAppControl.BlockedExitCode)
            {
                // Restarting won't help: Windows refuses to load the GStreamer DLLs.
                SetState(ServerState.Error, "Windows (Smart App Control) บล็อก UxPlay ไม่ให้ทำงาน ดูวิธีแก้ที่หน้า “ตรวจสอบและช่วยเหลือ”");
                return;
            }

            crashes.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 2);
            crashes.Add(DateTime.Now);
            if ((DateTime.Now - startTime).TotalSeconds > 15 && crashes.Count <= 3)
            {
                Emit("กำลังเริ่มตัวรับสัญญาณใหม่อัตโนมัติ…");
                Start(cfg);
                return;
            }
            string msg = string.IsNullOrEmpty(LastError) ? "ตัวรับสัญญาณหยุดทำงานกะทันหัน (รหัส " + code + ")" : LastError;
            SetState(ServerState.Error, msg);
        }

        void SetState(ServerState s, string error)
        {
            State = s;
            if (error != null) { LastError = error; Emit("ข้อผิดพลาด: " + error); }
            Fire();
        }

        void Fire() { if (Changed != null) Changed(); }

        void Emit(string line)
        {
            try { if (fileLog != null) fileLog.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + line); } catch { }
            if (Line != null) Line(line);
        }

        void OpenFileLog()
        {
            try
            {
                if (fileLog != null) fileLog.Dispose();
                string f = Path.Combine(Paths.LogDir, "uxplay.log");
                string old = Path.Combine(Paths.LogDir, "uxplay.previous.log");
                if (File.Exists(f)) { if (File.Exists(old)) File.Delete(old); File.Move(f, old); }
                fileLog = new StreamWriter(f, false, new UTF8Encoding(false));
                fileLog.AutoFlush = true;
            }
            catch { fileLog = null; }
        }

        static void KillStrays()
        {
            foreach (var p in Process.GetProcessesByName("uxplay"))
            {
                try { Log.Write("killing stray uxplay " + p.Id); p.Kill(); p.WaitForExit(2000); } catch { }
            }
        }
    }
}
