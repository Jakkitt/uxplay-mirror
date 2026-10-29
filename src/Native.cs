using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UxPlayMirror
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowText(IntPtr hWnd, string text);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

        [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] public static extern bool FreeConsole();
        [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint group);

        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_MAXIMIZE = 0x01000000;
        public const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
                          SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint WM_SETICON = 0x80;
        public const int SW_RESTORE = 9;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        public static RECT FrameBounds(IntPtr hWnd)
        {
            RECT r;
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0)
                GetWindowRect(hWnd, out r);
            return r;
        }

        public static RECT ClientScreenRect(IntPtr hWnd)
        {
            RECT c; GetClientRect(hWnd, out c);
            POINT p = new POINT();
            ClientToScreen(hWnd, ref p);
            RECT r = new RECT { Left = p.X, Top = p.Y, Right = p.X + c.Width, Bottom = p.Y + c.Height };
            return r;
        }

        public static MONITORINFO Monitor(IntPtr hWnd, out IntPtr hMon)
        {
            hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(hMon, ref mi);
            return mi;
        }

        // ---- TCP table: which connections does a process own ----

        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        public struct TcpRow { public int State; public int LocalPort; public bool RemoteIsLoopback; public bool V6; }

        const int TCP_TABLE_OWNER_PID_ALL = 5, AF_INET = 2, AF_INET6 = 23;
        public const int TCP_LISTEN = 2, TCP_ESTABLISHED = 5;

        public static List<TcpRow> TcpRowsFor(int pid)
        {
            var rows = new List<TcpRow>();
            if (pid <= 0) return rows;
            ReadTable(pid, AF_INET, rows);
            ReadTable(pid, AF_INET6, rows);
            return rows;
        }

        static int Port(int raw) { return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); }

        static void ReadTable(int pid, int af, List<TcpRow> rows)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;
                int n = Marshal.ReadInt32(buf);
                if (af == AF_INET)
                {
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr row = IntPtr.Add(buf, 4 + i * 24);
                        if (Marshal.ReadInt32(row, 20) != pid) continue;
                        uint remote = (uint)Marshal.ReadInt32(row, 12);
                        rows.Add(new TcpRow
                        {
                            State = Marshal.ReadInt32(row, 0),
                            LocalPort = Port(Marshal.ReadInt32(row, 8)),
                            RemoteIsLoopback = (remote & 0xFF) == 127,
                            V6 = false
                        });
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr row = IntPtr.Add(buf, 4 + i * 56);
                        if (Marshal.ReadInt32(row, 52) != pid) continue;
                        bool loop = true;
                        for (int b = 0; b < 15; b++) if (Marshal.ReadByte(row, 24 + b) != 0) { loop = false; break; }
                        loop = loop && Marshal.ReadByte(row, 39) == 1;
                        rows.Add(new TcpRow
                        {
                            State = Marshal.ReadInt32(row, 48),
                            LocalPort = Port(Marshal.ReadInt32(row, 20)),
                            RemoteIsLoopback = loop,
                            V6 = true
                        });
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static int CountRemoteConnections(int pid)
        {
            int n = 0;
            foreach (var r in TcpRowsFor(pid))
                if (r.State == TCP_ESTABLISHED && !r.RemoteIsLoopback) n++;
            return n;
        }

        // ---- Job object: child processes die with us ----

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                         ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static IntPtr job = IntPtr.Zero;

        public static void AddToJob(System.Diagnostics.Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                    SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch (Exception ex) { Log.Write("job: " + ex.Message); }
        }
    }
}
