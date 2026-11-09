using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Morror
{
    public sealed class TrayController : IDisposable
    {
        readonly NotifyIcon notification;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem stopItem;
        readonly ToolStripMenuItem pinItem;
        readonly ToolStripMenuItem transferItem;
        readonly Icon icon;
        bool disposed;

        public TrayController(Action open, Action stop, Action exit, Action<bool> setPinned = null, Action transfer = null)
        {
            icon = CreateIcon();
            menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Открыть Morror", null, (s, e) => open()) { Name = "open" });
            stopItem = new ToolStripMenuItem("Остановить трансляцию", null, (s, e) => stop()) { Name = "stop", Enabled = false };
            menu.Items.Add(stopItem);
            pinItem = new ToolStripMenuItem("Поверх остальных окон") { Name = "pin", CheckOnClick = true, Enabled = false };
            pinItem.Click += (s, e) => { if (setPinned != null) setPinned(pinItem.Checked); };
            menu.Items.Add(pinItem);
            transferItem = new ToolStripMenuItem("Фото и буфер обмена…", null, (s, e) => { if (transfer != null) transfer(); }) { Name = "transfer", Enabled = false };
            menu.Items.Add(transferItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выйти из Morror", null, (s, e) => exit()) { Name = "exit" });
            notification = new NotifyIcon { Icon = icon, Text = "Morror — настройки трансляции", ContextMenuStrip = menu, Visible = true };
            notification.DoubleClick += (s, e) => open();
            notification.BalloonTipClicked += (s, e) => open();
        }

        public void SetRunning(bool running)
        {
            if (disposed) return;
            stopItem.Enabled = running;
            pinItem.Enabled = running;
            notification.Text = running ? "Morror — трансляция активна" : "Morror — настройки трансляции";
        }

        public void SetPinned(bool pinned) { if (!disposed) pinItem.Checked = pinned; }
        public void SetTransferAvailable(bool available) { if (!disposed) transferItem.Enabled = available; }

        public void ExplainHidden()
        {
            if (disposed) return;
            notification.ShowBalloonTip(3500, "Morror в трее", "Настройки скрыты. Дважды нажмите на значок Morror рядом с часами, чтобы открыть их.", ToolTipIcon.Info);
        }

        public void FileTransferNotice(string message, bool failed)
        {
            if (!disposed) notification.ShowBalloonTip(6000, "Morror — передача файла", message, failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            notification.Visible = false;
            notification.Dispose();
            menu.Dispose();
            icon.Dispose();
        }

        static Icon CreateIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Morror.ico"))
            using (var source = new Icon(stream, 32, 32)) return (Icon)source.Clone();
        }
    }
}
