using System;
using System.Threading;
using System.Windows;

namespace Morror
{
    internal static class IPhoneApp
    {
        [STAThread]
        internal static void Main(string[] args)
        {
            using (var mutex = new Mutex(false, @"Local\Morror.iPhone"))
            {
                try { if (!mutex.WaitOne(0)) return; }
                catch (AbandonedMutexException) { }
                try
                {
                    var app = new Application();
                    var dialog = new IPhoneMirrorDialog(System.AppDomain.CurrentDomain.BaseDirectory);
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
                catch (Exception error) { MessageBox.Show(error.Message, "Morror — iPhone"); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
