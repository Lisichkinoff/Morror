using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace Morror
{
    internal static class IPhoneApp
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == IPhoneNetwork.ConfigureSwitch)
            {
                try { IPhoneNetwork.AllowReceiver(AppDomain.CurrentDomain.BaseDirectory); return 0; }
                catch (Exception error) { Trace.WriteLine(error); return 1; }
            }
            using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Morror.iPhone.Activate"))
            using (var mutex = new Mutex(false, @"Local\Morror.iPhone"))
            {
                try { if (!mutex.WaitOne(0)) { activate.Set(); return 0; } }
                catch (AbandonedMutexException) { }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var dialog = new IPhoneMirrorDialog(System.AppDomain.CurrentDomain.BaseDirectory);
                    app.MainWindow = dialog.Window;
                    var activationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    activationTimer.Tick += (s, e) =>
                    {
                        if (!activate.WaitOne(0)) return;
                        if (dialog.Window.WindowState == WindowState.Minimized) dialog.Window.WindowState = WindowState.Normal;
                        dialog.Window.Show();
                        dialog.Window.Activate();
                    };
                    dialog.Window.Closed += (s, e) => activationTimer.Stop();
                    activationTimer.Start();
                    int parentId;
                    if (args.Length == 1 && int.TryParse(args[0], out parentId))
                    {
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
                        timer.Tick += (s, e) =>
                        {
                            try
                            {
                                using (var parent = System.Diagnostics.Process.GetProcessById(parentId))
                                    if (!parent.HasExited) return;
                            }
                            catch (System.ArgumentException) { }
                            dialog.Window.Close();
                        };
                        dialog.Window.Closed += (s, e) => timer.Stop();
                        timer.Start();
                    }
                    app.Run(dialog.Window);
                }
                catch (Exception error) { MessageBox.Show(error.Message, "Morror — iPhone"); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }
}
