using System;
using System.Reflection;
using System.Windows.Forms;
using Morror;

class TrayTests
{
    static int count;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }

    [STAThread]
    static int Main()
    {
        try
        {
            int opens = 0, stops = 0, exits = 0;
            using (var tray = new TrayController(() => opens++, () => stops++, () => exits++))
            {
                var notification = Field<NotifyIcon>(tray, "notification");
                var menu = Field<ContextMenuStrip>(tray, "menu");
                var stop = (ToolStripMenuItem)menu.Items[1];
                Check(notification.Visible && notification.Icon != null, "Native tray icon remains available for restoring settings");
                Check(!stop.Enabled, "Stop is disabled without a stream");
                menu.Items[0].PerformClick();
                Check(opens == 1, "Open action is routed to the controller");
                tray.SetRunning(true);
                Check(stop.Enabled && notification.Text.Contains("активна"), "Running state updates tray status and actions");
                stop.PerformClick();
                Check(stops == 1, "Stop action is routed to the owned stream");
                menu.Items["exit"].PerformClick();
                Check(exits == 1, "Exit remains available while streaming");
                tray.SetRunning(false);
                Check(!stop.Enabled && notification.Text.Contains("настройки"), "Session end resets tray state");
                tray.Dispose(); tray.Dispose();
                Check(!notification.Visible, "Shutdown removes the tray icon and repeated cleanup is safe");
            }
            Console.WriteLine(count + " tray checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
