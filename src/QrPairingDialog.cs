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
        bool manualBusy;
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
            Get<Button>("QrConnect").Click += async (s, e) => await ConnectManually(false);
            Get<Button>("QrPairManual").Click += async (s, e) => await ConnectManually(true);
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
            if (manualBusy) return;
            Get<Button>("QrRenew").IsEnabled = false;
            if (cancellation != null) cancellation.Cancel();
            if (worker != null) await worker;
            if (closed || manualBusy) return;
            result = null;
            worker = Run();
            Get<Button>("QrRenew").IsEnabled = true;
        }

        async Task ConnectManually(bool pair)
        {
            if (manualBusy) return;
            manualBusy = true;
            Get<Button>("QrRenew").IsEnabled = false;
            Get<Button>("QrConnect").IsEnabled = Get<Button>("QrPairManual").IsEnabled = false;
            if (cancellation != null) cancellation.Cancel();
            if (worker != null) await worker;
            if (closed) return;
            result = null;
            Get<Button>("QrClose").Content = "Закрыть";
            Get<TextBlock>("QrFinished").Text = "↻";
            Get<TextBlock>("QrCountdown").Text = "";
            var source = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            cancellation = source;
            try
            {
                string address = Get<TextBox>("QrConnectAddress").Text;
                Get<TextBlock>("QrStatus").Text = pair ? "Выполняем сопряжение…" : "Подключаем телефон по IP и порту…";
                PairingResult connection;
                if (pair)
                    connection = await pairing.PairByCode(Get<TextBox>("QrPairAddress").Text, Get<PasswordBox>("QrPairCode").Password, address, source.Token);
                else
                {
                    address = Protocol.Endpoint(address);
                    connection = new PairingResult { Connected = await pairing.Connect(address, source.Token), Address = address };
                }
                if (closed) return;
                if (connection.Connected)
                {
                    result = connection;
                    window.Close();
                }
                else Get<TextBlock>("QrStatus").Text = pair
                    ? "Сопряжение выполнено. Укажите IP:порт с главной страницы беспроводной отладки и нажмите «Подключить по IP:порту»."
                    : "Подключение не подтверждено. Проверьте адрес на главной странице беспроводной отладки и сопряжение с ПК.";
            }
            catch (Exception error) { if (!closed) Get<TextBlock>("QrStatus").Text = error is OperationCanceledException ? "Время подключения истекло." : error.Message; }
            finally
            {
                cancellation = null;
                source.Dispose();
                manualBusy = false;
                if (!closed)
                {
                    Get<PasswordBox>("QrPairCode").Clear();
                    Get<Button>("QrRenew").IsEnabled = Get<Button>("QrConnect").IsEnabled = Get<Button>("QrPairManual").IsEnabled = true;
                }
            }
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
                Get<TextBlock>("QrStatus").Text = paired.Connected ? "Телефон подключён. Нажмите «Готово»." : "Сопряжение выполнено. Введите IP:порт с главной страницы беспроводной отладки в разделе ниже.";
                if (!paired.Connected) ShowManual();
                Get<TextBlock>("QrFinished").Text = "✓";
                Get<TextBlock>("QrCountdown").Text = "Сопряжение сохранено на телефоне.";
                Get<Button>("QrClose").Content = "Готово";
            }
            catch (OperationCanceledException)
            {
                if (!closed && !manualBusy) { Get<TextBlock>("QrStatus").Text = "Время ожидания истекло. Создайте новый QR-код или подключитесь по коду."; Get<TextBlock>("QrFinished").Text = "↻"; Get<TextBlock>("QrCountdown").Text = "Ожидание завершено."; ShowManual(); }
            }
            catch (Exception)
            {
                if (!closed && !manualBusy) { Get<TextBlock>("QrStatus").Text = "Не удалось подключиться. Проверьте общую сеть и сканер беспроводной отладки. Можно подключиться по коду или IP:порту."; Get<TextBlock>("QrFinished").Text = "↻"; Get<TextBlock>("QrCountdown").Text = "Попробуйте создать новый QR-код."; ShowManual(); }
            }
            finally
            {
                timer.Stop();
                if (!closed) { Get<Image>("QrImage").Source = null; Get<Image>("QrImage").Visibility = Visibility.Collapsed; Get<TextBlock>("QrFinished").Visibility = Visibility.Visible; }
                if (cancellation == source) cancellation = null;
                source.Dispose();
            }
        }

        void ShowManual()
        {
            var manual = Get<Expander>("QrManual");
            manual.IsExpanded = true;
            manual.BringIntoView();
        }

        void UpdateCountdown() { Get<TextBlock>("QrCountdown").Text = "Код действует ещё " + Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds) + " с"; }
    }
}
