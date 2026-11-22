using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

namespace Morror
{
    internal sealed class SendFilesDialog
    {
        internal const string LaunchArgument = "--send-files";
        readonly string adb;
        readonly List<FileInfo> pending;
        readonly int skipped;
        readonly Window window;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        CancellationTokenSource sending;
        bool busy, closeAfterSend;

        sealed class Recipient
        {
            internal Device Device;
            public string Label { get { return Device.Label + (Device.Model == Device.Serial ? "" : "  ·  " + Device.Serial); } }
        }

        internal SendFilesDialog(string adb, IEnumerable<string> paths)
        {
            this.adb = adb;
            var requested = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            pending = requested.Where(File.Exists).Select(path => new FileInfo(path)).ToList();
            skipped = requested.Count - pending.Count;
            var assembly = Assembly.GetExecutingAssembly();
            using (var resource = assembly.GetManifestResourceStream("SendFilesWindow.xaml"))
                window = (Window)XamlReader.Load(resource);
            using (var resource = assembly.GetManifestResourceStream("MainWindow.xaml"))
            {
                var theme = (Window)XamlReader.Load(resource);
                window.Resources.MergedDictionaries.Add(theme.Resources);
                theme.Close();
            }
            using (var resource = assembly.GetManifestResourceStream("Morror.ico"))
                window.Icon = new IconBitmapDecoder(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                    .Frames.OrderByDescending(frame => frame.PixelWidth).First();
            var area = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = Math.Min(window.Width, area.Width);
            window.Height = Math.Min(window.Height, area.Height);
            WindowFrame.Bind(window, Get<Border>("WindowFrame"), Get<Button>("SendMinimize"),
                Get<Button>("SendMaximize"), Get<Button>("SendTitleClose"));
            window.Loaded += async (s, e) => await Refresh();
            Get<Button>("SendRefresh").Click += async (s, e) => await Refresh();
            Get<Button>("SendStart").Click += async (s, e) => await Send();
            Get<Button>("SendClose").Click += (s, e) => window.Close();
            Get<Button>("SendCancel").Click += (s, e) => CancelSend();
            Get<ComboBox>("SendDevice").SelectionChanged += (s, e) =>
            {
                UpdateButtons();
                if (!busy && pending.Count != 0) ShowRecipientHint();
            };
            window.Closing += (s, e) =>
            {
                if (sending == null) return;
                e.Cancel = true;
                closeAfterSend = true;
                CancelSend();
            };
            window.Closed += (s, e) => lifetime.Cancel();
            UpdatePending();
            UpdateButtons();
        }

        internal static void Run(IEnumerable<string> paths)
        {
            using (var guard = new Mutex(false, @"Local\Morror.SendFiles"))
            {
                try
                {
                    if (AppUpdate.IsInstalling())
                    {
                        MessageBox.Show("Morror обновляется. Повторите отправку после установки.", "Morror");
                        return;
                    }
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var dialog = new SendFilesDialog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "adb.exe"), paths);
                    application.MainWindow = dialog.window;
                    dialog.window.Closed += (s, e) => application.Shutdown();
                    application.Run(dialog.window);
                }
                catch (Exception error)
                {
                    MessageBox.Show("Не удалось открыть отправку файлов.\n" + error.Message, "Morror");
                    Environment.ExitCode = 1;
                }
            }
        }

        T Get<T>(string name) where T : class { return (T)window.FindName(name); }
        void Status(string message) { Get<TextBlock>("SendStatus").Text = message; }

        void UpdatePending()
        {
            Get<ListBox>("SendList").ItemsSource = pending.ToArray();
            Get<TextBlock>("SendCount").Text = "К отправке: " + pending.Count + (skipped == 0 ? "" : " · Пропущено папок и недоступных файлов: " + skipped);
        }

        void UpdateButtons()
        {
            var selector = Get<ComboBox>("SendDevice");
            selector.IsEnabled = !busy && pending.Count != 0;
            Get<Button>("SendRefresh").IsEnabled = !busy && pending.Count != 0;
            Get<Button>("SendStart").IsEnabled = !busy && pending.Count != 0 && selector.SelectedItem is Recipient;
        }

        void ShowRecipientHint()
        {
            var selector = Get<ComboBox>("SendDevice");
            Status(selector.Items.Count == 0
                ? "Подключите Android к Morror и разрешите отладку. Затем нажмите «Обновить»."
                : selector.SelectedItem == null ? "Выберите телефон, на который нужно отправить файлы."
                : "Фото сохранятся в Pictures/Morror, остальные файлы — в Download/Morror.");
        }

        async Task Refresh()
        {
            if (busy) return;
            if (pending.Count == 0) { Status("Выберите один или несколько доступных файлов в Проводнике."); return; }
            busy = true; UpdateButtons(); Status("Ищем Android-устройства…");
            var selector = Get<ComboBox>("SendDevice");
            var previous = selector.SelectedItem as Recipient;
            try
            {
                var devices = await Commands.ListDevices(adb, lifetime.Token);
                var recipients = devices.Where(device => device.Ready).Select(device => new Recipient { Device = device }).ToList();
                selector.ItemsSource = recipients;
                selector.SelectedItem = recipients.FirstOrDefault(item => previous != null && item.Device.SameConnection(previous.Device))
                    ?? (recipients.Count == 1 ? recipients[0] : null);
                ShowRecipientHint();
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                selector.ItemsSource = null;
                Status(error.Message);
            }
            finally { busy = false; UpdateButtons(); }
        }

        async Task Send()
        {
            var recipient = Get<ComboBox>("SendDevice").SelectedItem as Recipient;
            if (busy || recipient == null || pending.Count == 0) return;
            var progress = Get<ProgressBar>("SendProgress");
            var cancel = Get<Button>("SendCancel");
            var close = Get<Button>("SendClose");
            int total = pending.Count, sent = 0;
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                sending = cancellation;
                busy = true; UpdateButtons();
                progress.Maximum = total;
                progress.Value = 0;
                progress.Visibility = cancel.Visibility = Visibility.Visible;
                cancel.IsEnabled = true;
                close.Visibility = Visibility.Collapsed;
                Status("Проверяем подключение к " + recipient.Device.Model + "…");
                try
                {
                    var devices = await Commands.ListDevices(adb, cancellation.Token);
                    if (!devices.Any(device => device.Ready && device.SameConnection(recipient.Device)))
                        throw new IOException("Получатель недоступен. Переподключите его и нажмите «Обновить».");
                    var transfer = new FileTransfer(adb, recipient.Device.Serial);
                    foreach (var file in pending.ToArray())
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        Status("Отправлено " + sent + " из " + total + " · " + file.Name);
                        await transfer.Push(file.FullName, cancellation.Token);
                        pending.Remove(file);
                        progress.Value = ++sent;
                        UpdatePending();
                    }
                    Status("Отправлено " + sent + " из " + total + " на " + recipient.Device.Model + ". Папки: Pictures/Morror и Download/Morror.");
                }
                catch (OperationCanceledException)
                {
                    Status("Отправка отменена. Отправлено " + sent + " из " + total + ".");
                }
                catch (Exception error)
                {
                    Status("Отправлено " + sent + " из " + total + ". " + error.Message);
                }
                finally
                {
                    sending = null;
                    busy = false;
                    cancel.Visibility = progress.Visibility = Visibility.Collapsed;
                    close.Visibility = Visibility.Visible;
                    UpdateButtons();
                    if (closeAfterSend) window.Close();
                }
            }
        }

        void CancelSend()
        {
            if (sending == null) return;
            Get<Button>("SendCancel").IsEnabled = false;
            sending.Cancel();
        }
    }
}
