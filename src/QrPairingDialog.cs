using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

namespace Morror
{
    public sealed class QrPairingDialog
    {
        readonly Window window;
        readonly WirelessPairing pairing;
        CancellationTokenSource cancellation;
        Task worker;
        readonly DispatcherTimer timer;
        DateTime deadline;
        bool closed;
        PairingResult result;
        T Get<T>(string name) where T : class { return window.FindName(name) as T; }

        public QrPairingDialog(Window owner, WirelessPairing pairing)
        {
            this.pairing = pairing;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QrWindow.xaml")) window = (Window)System.Windows.Markup.XamlReader.Load(stream);
            window.Resources = owner.Resources;
            window.Owner = owner;
            window.Icon = owner.Icon;
            var area = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = Math.Min(window.Width, area.Width);
            window.Height = Math.Min(window.Height, area.Height);
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => UpdateCountdown();
            Get<Button>("QrTitleClose").Click += (s, e) => window.Close();
            Get<Button>("QrClose").Click += (s, e) => window.Close();
            Get<Button>("QrRenew").Click += async (s, e) => await Restart();
            window.Loaded += async (s, e) => await Restart();
            window.Closed += (s, e) => { closed = true; timer.Stop(); if (cancellation != null) cancellation.Cancel(); Get<Image>("QrImage").Source = null; };
        }

        public PairingResult Show() { window.ShowDialog(); return result; }

        public static ImageSource Render(string payload)
        {
            using (var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M))
            using (var renderer = new PngByteQRCode(data))
            using (var stream = new MemoryStream(renderer.GetGraphic(8)))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
        }

        async Task Restart()
        {
            Get<Button>("QrRenew").IsEnabled = false;
            if (cancellation != null) cancellation.Cancel();
            if (worker != null) await worker;
            if (closed) return;
            result = null;
            worker = Run();
            Get<Button>("QrRenew").IsEnabled = true;
        }

        async Task Run()
        {
            var source = new CancellationTokenSource(); cancellation = source;
            try
            {
                var session = new QrSession();
                Get<Image>("QrImage").Source = Render(session.Payload);
                Get<Image>("QrImage").Visibility = Visibility.Visible;
                Get<TextBlock>("QrFinished").Visibility = Visibility.Collapsed;
                Get<Button>("QrClose").Content = "Отмена";
                deadline = DateTime.UtcNow.AddMinutes(3); source.CancelAfter(TimeSpan.FromMinutes(3));
                UpdateCountdown(); timer.Start();
                var paired = await pairing.Pair(session, status => { if (!closed) Get<TextBlock>("QrStatus").Text = status; }, source.Token);
                if (closed) return;
                result = paired;
                Get<TextBlock>("QrStatus").Text = paired.Connected ? "Телефон подключён. Нажмите «Готово» и начните трансляцию." : "Сопряжение выполнено. Для подключения введите адрес с экрана беспроводной отладки в основном окне.";
                Get<TextBlock>("QrFinished").Text = "✓";
                Get<TextBlock>("QrCountdown").Text = "Сопряжение сохранено на телефоне.";
                Get<Button>("QrClose").Content = "Готово";
            }
            catch (OperationCanceledException)
            {
                if (!closed) { Get<TextBlock>("QrStatus").Text = "Время ожидания истекло. Создайте новый QR-код или используйте ручное сопряжение."; Get<TextBlock>("QrFinished").Text = "↻"; Get<TextBlock>("QrCountdown").Text = "Ожидание завершено."; }
            }
            catch (Exception)
            {
                if (!closed) { Get<TextBlock>("QrStatus").Text = "Не удалось подключиться. Проверьте общую сеть и сканер беспроводной отладки. Можно повторить или подключиться вручную."; Get<TextBlock>("QrFinished").Text = "↻"; Get<TextBlock>("QrCountdown").Text = "Попробуйте создать новый QR-код."; }
            }
            finally
            {
                timer.Stop();
                if (!closed) { Get<Image>("QrImage").Source = null; Get<Image>("QrImage").Visibility = Visibility.Collapsed; Get<TextBlock>("QrFinished").Visibility = Visibility.Visible; }
                if (cancellation == source) cancellation = null;
                source.Dispose();
            }
        }

        void UpdateCountdown() { Get<TextBlock>("QrCountdown").Text = "Код действует ещё " + Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds) + " с"; }
    }
}
