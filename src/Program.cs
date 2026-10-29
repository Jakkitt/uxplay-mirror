using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace UxPlayMirror
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool created;
            var mutex = new Mutex(true, @"Local\UxPlayMirror_Instance", out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\UxPlayMirror_Show");
            if (!created)
            {
                // Already running: bring the existing window forward instead.
                showEvent.Set();
                return 0;
            }

            Log.Write("start " + string.Join(" ", args));
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += (o, e) =>
            {
                Log.Write("UI error: " + e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (o, e) => Log.Write("fatal: " + e.ExceptionObject);

            app.Resources.MergedDictionaries.Add((ResourceDictionary)Xaml.Load("Theme.xaml"));
            var controller = new Controller(app);

            var listener = new Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    app.Dispatcher.BeginInvoke(new Action(controller.ShowMain));
                }
            });
            listener.IsBackground = true;
            listener.Start();

            bool tray = Array.IndexOf(args, "--tray") >= 0;
            bool demo = Array.IndexOf(args, "--demo") >= 0;
            bool demoMusic = Array.IndexOf(args, "--demo-music") >= 0;
            app.Dispatcher.BeginInvoke(new Action(() => controller.Run(tray, demo, demoMusic)));
            app.Run();
            Log.Write("exit");
            GC.KeepAlive(mutex);
            return 0;
        }
    }
}
