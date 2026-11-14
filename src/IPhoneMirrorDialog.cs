using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Morror
{
    internal sealed class IPhoneMirrorDialog
    {
        readonly Window window;
        readonly IPhoneMirrorEngine engine;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        PreviewHost preview;
        bool busy, listening, closeRequested, allowClose;
        DateTime started;
        T Get<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        bool WiFi { get { return Get<ComboBox>("Transport").SelectedIndex == 1; } }
        void Status(string message) { Get<TextBlock>("Status").Text = message; }

        internal IPhoneMirrorDialog(string directory)
        {
            engine = new IPhoneMirrorEngine(directory);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IPhoneWindow.xaml"))
                window = (Window)XamlReader.Load(stream);
            using (var style = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                window.Resources.MergedDictionaries.Add(((Window)XamlReader.Load(style)).Resources);
            using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Morror.ico"))
                window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(icon,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height);

            Get<ComboBox>("Transport").SelectionChanged += async (s, e) =>
            {
                TransportChanged();
                if (!WiFi) await RefreshUsb();
            };
            Get<ComboBox>("Devices").SelectionChanged += (s, e) => UpdateButtons();
            Get<Button>("Refresh").Click += async (s, e) => await RefreshUsb();
            Get<Button>("Driver").Click += async (s, e) => await ConfigureUsb();
            Get<Button>("Start").Click += async (s, e) => await Start();
            Get<Button>("Stop").Click += async (s, e) => await Stop();
            window.Loaded += async (s, e) => { await RefreshUsb(); if (!closeRequested) timer.Start(); };
            timer.Tick += async (s, e) => await Poll();
            window.Closing += async (s, e) =>
            {
                if (allowClose) return;
                e.Cancel = true;
                closeRequested = true;
                timer.Stop();
                if (busy) { Status("Завершаем текущую операцию…"); return; }
                busy = true;
                UpdateButtons();
                try { await Task.Run(() => engine.Dispose()); }
                catch (Exception error) { Trace.WriteLine(error); }
                RemovePreview();
                await window.Dispatcher.InvokeAsync(new Action(() => { allowClose = true; window.Close(); }));
            };
            TransportChanged();
        }

        internal Window Window { get { return window; } }

        void TransportChanged()
        {
            Get<FrameworkElement>("DeviceRow").Visibility = WiFi ? Visibility.Collapsed : Visibility.Visible;
            Get<Button>("Driver").Visibility = WiFi ? Visibility.Collapsed : Visibility.Visible;
            Get<TextBlock>("Instructions").Text = WiFi
                ? "Подключите ПК и iPhone к одной частной сети. Запустите AirPlay и выберите Morror в разделе «Повтор экрана» на iPhone. Если Windows запросит доступ к сети, разрешите его для частных сетей."
                : "Подключите iPhone кабелем с передачей данных, разблокируйте его и нажмите «Доверять», если появится запрос.";
            Get<Button>("Start").Content = WiFi ? "Запустить AirPlay" : "Начать трансляцию";
            Status(WiFi ? "" : "Выберите подключённый iPhone.");
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool active = listening || engine.HasSession;
            bool idle = !busy && !active && !closeRequested;
            Get<ComboBox>("Transport").IsEnabled = idle;
            Get<ComboBox>("Devices").IsEnabled = idle;
            Get<Button>("Refresh").IsEnabled = idle;
            Get<Button>("Driver").IsEnabled = idle;
            Get<Button>("Start").IsEnabled = idle && (WiFi || Get<ComboBox>("Devices").SelectedItem != null);
            Get<Button>("Stop").IsEnabled = !busy && active && !closeRequested;
        }

        async Task Run(Func<Task> operation)
        {
            if (busy || closeRequested) return;
            busy = true;
            UpdateButtons();
            try { await operation(); }
            catch (Exception error) { Status(error.Message); }
            finally
            {
                busy = false;
                UpdateButtons();
            }
            if (closeRequested) await window.Dispatcher.InvokeAsync(new Action(window.Close));
        }

        Task RefreshUsb()
        {
            return Run(async () =>
            {
                Status("Ищем iPhone по USB…");
                var devices = await Task.Run(() => { engine.Initialize(); return engine.Devices(false, true); });
                var list = Get<ComboBox>("Devices");
                var selected = list.SelectedItem as IPhoneMirrorEngine.Device?;
                int index = selected.HasValue ? Array.FindIndex(devices, device => device.Id == selected.Value.Id) : -1;
                list.ItemsSource = devices;
                list.SelectedIndex = devices.Length == 0 ? -1 : Math.Max(0, index);
                Status(devices.Length == 0 ? "iPhone не найден. Разблокируйте телефон или настройте USB-компонент Windows." : "iPhone найден. Нажмите «Начать трансляцию».");
            });
        }

        async Task ConfigureUsb()
        {
            await Run(async () =>
            {
                if (!File.Exists(engine.DriverTool)) throw new IOException("В этой сборке отсутствует утилита настройки USB.");
                Status("В утилите USB выберите подключённый iPhone и настройте для него драйвер захвата.");
                using (var process = Process.Start(new ProcessStartInfo(engine.DriverTool, "--language en-US")
                { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(engine.DriverTool) }))
                    await Task.Run(() => process.WaitForExit());
            });
            await RefreshUsb();
        }

        Task Start()
        {
            return Run(async () =>
            {
                if (WiFi)
                {
                    Status("Запускаем приёмник AirPlay…");
                    await Task.Run(() => { engine.Initialize(); engine.Listen(); });
                    listening = true;
                    started = DateTime.UtcNow;
                }
                else
                {
                    var device = Get<ComboBox>("Devices").SelectedItem as IPhoneMirrorEngine.Device?;
                    if (device.HasValue) await StartPreview(device.Value, false);
                }
            });
        }

        async Task StartPreview(IPhoneMirrorEngine.Device device, bool wifi)
        {
            Status("Подключаем экран iPhone…");
            Exception startupFailure = null;
            try
            {
                await Task.Run(() => engine.Start(device, wifi));
                preview = new PreviewHost();
                Get<Grid>("Preview").Children.Add(preview);
                preview.UpdateLayout();
                engine.Attach(preview.Handle);
                started = DateTime.UtcNow;
            }
            catch (Exception error) { startupFailure = error; }
            if (startupFailure == null) return;
            // Finish native teardown before destroying the preview handle.
            try { await Task.Run(() => engine.Stop()); }
            finally { RemovePreview(); }
            throw startupFailure;
        }

        Task Poll()
        {
            if (!listening && !engine.HasSession) return Task.FromResult(0);
            return Run(async () =>
            {
                Exception failure = null;
                try
                {
                    if (engine.HasSession)
                    {
                        var state = await Task.Run(() => engine.Status());
                        if (state.State == 6 || state.State == 7 || state.FailureKind != 0)
                        {
                            await StopSession();
                            Status(state.FailureKind == 8 ? "iPhone отключился." :
                                "Трансляция завершилась. Проверьте подключение iPhone и повторите запуск.");
                        }
                        else if (state.State == 4 && state.VideoFrames > 0)
                            Status("iPhone подключён · " + state.Width + " × " + state.Height + " · " + Math.Round(state.Fps) + " FPS");
                        else if (DateTime.UtcNow - started > TimeSpan.FromSeconds(25))
                            throw new IOException("iPhone не передал изображение. Разблокируйте телефон и попробуйте снова.");
                        return;
                    }
                    bool ready = await Task.Run(() => engine.ReceiverReady());
                    if (!ready)
                    {
                        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(15))
                            throw new IOException("AirPlay не запустился. Проверьте доступ к частной сети в брандмауэре Windows.");
                        return;
                    }
                    var devices = await Task.Run(() => engine.Devices(true, false));
                    if (devices.Length == 0) Status("AirPlay готов. Выберите Morror в «Повторе экрана» на iPhone.");
                    else await StartPreview(devices.First(), true);
                }
                catch (Exception error) { failure = error; }
                if (failure == null) return;
                await StopSession();
                throw failure;
            });
        }

        Task Stop()
        {
            return Run(async () =>
            {
                Status("Останавливаем трансляцию…");
                await StopSession();
                Status("Трансляция остановлена.");
            });
        }

        async Task StopSession()
        {
            try
            {
                await Task.Run(() =>
                {
                    try { engine.Stop(); }
                    finally { engine.StopListening(); }
                });
            }
            finally
            {
                listening = false;
                RemovePreview();
            }
        }

        void RemovePreview()
        {
            if (preview == null) return;
            Get<Grid>("Preview").Children.Remove(preview);
            preview.Dispose();
            preview = null;
        }

        sealed class PreviewHost : HwndHost
        {
            protected override HandleRef BuildWindowCore(HandleRef parent)
            {
                IntPtr hwnd = CreateWindowEx(0, "STATIC", null, 0x56000000, 0, 0, 1, 1, parent.Handle,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return new HandleRef(this, hwnd);
            }

            protected override void DestroyWindowCore(HandleRef hwnd) { DestroyWindow(hwnd.Handle); }

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern IntPtr CreateWindowEx(int style, string className, string text, uint windowStyle, int x, int y,
                int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr value);
            [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        }
    }
}
