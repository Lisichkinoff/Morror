using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Morror
{
    public sealed class App
    {
        Window window;
        string runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime");
        bool wifi, refreshing, networkBusy, starting, closing, stopRequested, exitRequested, hiddenToTray, trayHintShown, applyingPreset;
        Process mirror;
        string activeSerial, activeModel;
        DispatcherTimer timer;
        TrayController tray;
        EventWaitHandle restoreEvent;
        RegisteredWaitHandle restoreWait;
        WindowState lastWindowState = WindowState.Normal;
        readonly string restoreSignalName;
        readonly Dictionary<string, DeviceCapabilities> capabilities = new Dictionary<string, DeviceCapabilities>();
        readonly HashSet<string> pendingProfiles = new HashSet<string>();
        readonly CancellationTokenSource profileLifetime = new CancellationTokenSource();
        public App() : this("Local\\Morror.ShowSettings") { }
        internal App(string restoreSignalName) { this.restoreSignalName = restoreSignalName; }
        T Get<T>(string name) where T : class { return window.FindName(name) as T; }
        string Adb { get { return Path.Combine(runtime, "adb.exe"); } }
        bool Running { get { return mirror != null; } }

        [STAThread]
        public static void Main()
        {
            using (var mutex = new Mutex(false, "Local\\Morror.Desktop"))
            {
                if (!mutex.WaitOne(0))
                {
                    try { using (var signal = EventWaitHandle.OpenExisting("Local\\Morror.ShowSettings")) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("Morror уже запущен. Откройте его через значок рядом с часами или на панели задач.", "Morror"); }
                    return;
                }
                try
                {
                    var application = new Application();
                    application.DispatcherUnhandledException += (s, e) => { MessageBox.Show(e.Exception.Message, "Morror"); e.Handled = true; };
                    var app = new App();
                    application.SessionEnding += (s, e) => { app.exitRequested = true; };
                    app.Initialize(); application.Run(app.window);
                }
                catch (Exception e) { MessageBox.Show("Не удалось открыть Morror.\n" + e.Message, "Morror"); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        void Initialize()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            var area = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(760, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = Math.Min(window.Width, area.Width);
            window.Height = Math.Min(window.Height, area.Height);
            window.SizeChanged += (s, e) =>
            {
                bool compact = window.ActualWidth < 900;
                Get<StackPanel>("ProfilePanel").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                Get<ColumnDefinition>("ProfileColumn").Width = new GridLength(compact ? 0 : 220);
                Get<ColumnDefinition>("ProfileGap").Width = new GridLength(compact ? 0 : 18);
            };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Morror.ico"))
            {
                var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                window.Icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
            }
            Get<Button>("MinimizeButton").Click += (s, e) => SystemCommands.MinimizeWindow(window);
            Get<Button>("MaximizeButton").Click += (s, e) => { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); };
            Get<Button>("CloseButton").Click += (s, e) => window.Close();
            Get<ComboBox>("FrameRate").SelectionChanged += (s, e) => UpdateFrameRateSummary();
            Get<ComboBox>("Quality").SelectionChanged += (s, e) => { if (!applyingPreset) Get<ComboBox>("StreamPreset").SelectedIndex = 3; };
            Get<ComboBox>("FrameRate").SelectionChanged += (s, e) => { if (!applyingPreset) Get<ComboBox>("StreamPreset").SelectedIndex = 3; };
            Get<ComboBox>("StreamPreset").SelectionChanged += (s, e) =>
            {
                int preset = Get<ComboBox>("StreamPreset").SelectedIndex;
                if (preset < 0 || preset > 2) return;
                applyingPreset = true;
                Get<ComboBox>("Quality").SelectedIndex = preset == 1 ? 0 : preset == 2 ? 2 : 1;
                Get<ComboBox>("FrameRate").SelectedIndex = preset == 1 ? 1 : 2;
                applyingPreset = false;
            };
            Get<Button>("UsbWifiButton").Click += async (s, e) => await UsbToWifi();
            Get<Button>("KeyboardSettings").Click += async (s, e) =>
            {
                if (!Running || string.IsNullOrEmpty(activeSerial)) return;
                try
                {
                    var result = await Commands.Run(Adb, 8000, "-s", activeSerial, "shell", "am", "start", "-a", "android.settings.HARD_KEYBOARD_SETTINGS");
                    Notice(result.ExitCode == 0 && !result.Combined.Contains("Error") ? "Выберите клавиатуру scrcpy и добавьте русский и английский. Переключение обычно Ctrl+Пробел." : "Откройте на телефоне: Настройки → Язык и ввод → Физическая клавиатура.");
                }
                catch (Exception error) { Notice(error.Message); }
            };
            Get<Button>("DevicePresetButton").Click += (s, e) =>
            {
                Get<ComboBox>("Quality").SelectedIndex = Protocol.NativeQuality;
                Get<ComboBox>("FrameRate").SelectedIndex = 0;
                var selected = Get<ComboBox>("Devices").SelectedItem as Device;
                DeviceCapabilities info;
                if (selected != null && capabilities.TryGetValue(selected.Serial, out info) && info.ApiLevel > 0) Get<CheckBox>("Audio").IsChecked = info.ApiLevel >= 30;
                UpdateFrameRateSummary();
                Notice("Исходное качество без ограничения FPS: нагрузка и нагрев могут вырасти.");
            };
            UpdateFrameRateSummary();
            Get<Button>("UsbButton").Click += async (s, e) => { SetMode(false); await Refresh(); };
            Get<Button>("WifiButton").Click += async (s, e) => { SetMode(true); await Refresh(); };
            Get<Button>("RefreshButton").Click += async (s, e) => { capabilities.Clear(); await Refresh(); };
            Get<Button>("PairButton").Click += async (s, e) => await Network(true);
            Get<Button>("ConnectButton").Click += async (s, e) => await Network(false);
            Get<Button>("QrButton").Click += async (s, e) => await ConnectWithQr();
            Get<Button>("StartButton").Click += async (s, e) => await Start();
            Get<Button>("StopButton").Click += (s, e) => Stop();
            Get<Button>("TransferButton").Click += (s, e) => OpenTransfer();
            Get<CheckBox>("Control").Checked += (s, e) => UpdateButtons();
            Get<CheckBox>("Control").Unchecked += (s, e) => UpdateButtons();
            Get<CheckBox>("AlwaysOnTop").Checked += (s, e) => ApplyPin();
            Get<CheckBox>("AlwaysOnTop").Unchecked += (s, e) => ApplyPin();
            Get<ComboBox>("Devices").SelectionChanged += async (s, e) => { UpdateButtons(); await UpdateDeviceProfile(); };
            window.StateChanged += (s, e) =>
            {
                if (closing) return;
                if (window.WindowState == WindowState.Minimized && tray != null) HideToTray();
                else if (window.WindowState != WindowState.Minimized) lastWindowState = window.WindowState;
            };
            window.Closing += (s, e) =>
            {
                if (!exitRequested && Running && tray != null) { e.Cancel = true; HideToTray(); return; }
                closing = true; profileLifetime.Cancel(); if (timer != null) timer.Stop(); Stop();
            };
            window.Closed += (s, e) =>
            {
                if (restoreWait != null) restoreWait.Unregister(null);
                if (restoreEvent != null) restoreEvent.Dispose();
                if (tray != null) tray.Dispose();
            };
            InitializeTray();
            window.Loaded += async (s, e) =>
            {
                if (!File.Exists(Adb) || !File.Exists(Path.Combine(runtime, "scrcpy.exe")) || !File.Exists(Path.Combine(runtime, "scrcpy-server")))
                { Notice("Не найдены компоненты трансляции. Распакуйте всю папку Morror из архива рядом с Morror.exe."); return; }
                await Refresh();
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += async (ts, te) => await Refresh(); timer.Start();
            };
        }

        void InitializeTray()
        {
            try
            {
                tray = new TrayController(RestoreFromTray, () => { Stop(); RestoreFromTray(); }, () => { exitRequested = true; window.Close(); }, pinned => { Get<CheckBox>("AlwaysOnTop").IsChecked = pinned; }, () => { RestoreFromTray(); OpenTransfer(); });
            }
            catch (Exception e)
            {
                Get<CheckBox>("AutoTray").IsChecked = false;
                Get<CheckBox>("AutoTray").IsEnabled = false;
                Notice("Не удалось создать значок трея. Окно настроек останется доступным на панели задач.");
                AddLog(e.Message);
            }
            restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, restoreSignalName);
            restoreWait = ThreadPool.RegisterWaitForSingleObject(restoreEvent, (s, timedOut) =>
            {
                if (!closing && !window.Dispatcher.HasShutdownStarted)
                    window.Dispatcher.BeginInvoke(new Action(RestoreFromTray));
            }, null, Timeout.Infinite, false);
        }

        void HideToTray()
        {
            if (closing || tray == null) return;
            tray.SetRunning(Running);
            hiddenToTray = true;
            window.Hide();
            if (!trayHintShown) { trayHintShown = true; tray.ExplainHidden(); }
        }

        void RestoreFromTray()
        {
            if (closing) return;
            hiddenToTray = false;
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = lastWindowState;
            window.Activate();
        }

        void ApplyPin()
        {
            if (closing) return;
            bool pinned = Get<CheckBox>("AlwaysOnTop").IsChecked == true;
            try
            {
                if (Running && !MirrorWindow.SetPinned(mirror, pinned)) return;
                if (tray != null) tray.SetPinned(pinned);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception e) { Notice("Не удалось изменить закрепление окна. Повторите переключение."); AddLog(e.Message); }
        }

        void OpenTransfer()
        {
            var device = Get<ComboBox>("Devices").SelectedItem as Device;
            string serial = Running ? activeSerial : device != null && device.Ready ? device.Serial : null;
            string model = Running ? activeModel : device != null ? device.Model : "Android";
            if (closing || starting || string.IsNullOrEmpty(serial)) return;
            var dialog = new TransferDialog(window, new FileTransfer(Adb, serial), model);
            dialog.Show();
        }

        void SetMode(bool wireless)
        {
            if (wifi != wireless) Get<ComboBox>("Devices").SelectedItem = null;
            wifi = wireless;
            Get<StackPanel>("WifiPanel").Visibility = wifi ? Visibility.Visible : Visibility.Collapsed;
            var converter = new System.Windows.Media.BrushConverter();
            var selected = (System.Windows.Media.Brush)converter.ConvertFromString("#BAF3CF");
            var normal = (System.Windows.Media.Brush)converter.ConvertFromString("#233039");
            var dark = (System.Windows.Media.Brush)converter.ConvertFromString("#163329");
            var light = (System.Windows.Media.Brush)converter.ConvertFromString("#E5EEE9");
            var border = (System.Windows.Media.Brush)converter.ConvertFromString("#34444B");
            foreach (string name in new[] { "UsbButton", "WifiButton" })
            {
                bool active = (name == "WifiButton") == wifi;
                var button = Get<Button>(name);
                button.Background = active ? selected : normal;
                button.Foreground = active ? dark : light;
                button.BorderBrush = active ? selected : border;
            }
            Get<TextBlock>("ConnectionHint").Text = wifi ? "Одна сеть с ПК. Android 11+: QR или код; остальные: сначала USB." : "Подключите кабель и разрешите отладку на телефоне.";
        }

        int SelectedFrameRate()
        {
            var item = Get<ComboBox>("FrameRate").SelectedItem as ComboBoxItem;
            if (item == null) throw new InvalidOperationException("Выберите частоту кадров.");
            return Convert.ToInt32(item.Tag, System.Globalization.CultureInfo.InvariantCulture);
        }

        void UpdateFrameRateSummary()
        {
            int fps = SelectedFrameRate();
            Get<TextBlock>("ProfileFps").Text = fps == 0 ? "Авто" : fps + " FPS";
            Get<TextBlock>("FpsHint").Text = fps == 0 ? "Как на устройстве: Morror не ограничивает FPS. Реальная частота зависит от контента и захвата телефона." : "До " + fps + " FPS, если экран и захват телефона поддерживают эту частоту. Фактический FPS — в журнале.";
        }

        void RenderDeviceProfile(Device device, DeviceCapabilities info)
        {
            bool ready = device != null && device.Ready;
            Get<TextBlock>("DeviceModel").Text = ready ? (info != null && !string.IsNullOrEmpty(info.Model) ? info.Model : device.Model) : "Телефон не выбран";
            Get<TextBlock>("DeviceDetails").Text = ready ? (info != null && !string.IsNullOrEmpty(info.Manufacturer) ? info.Manufacturer + " · " : "") + (device.Wireless ? "Wi-Fi" : "USB") : "Подключите совместимое Android-устройство";
            Get<TextBlock>("DeviceResolution").Text = ready && info != null ? info.ResolutionLabel : "—";
            Get<TextBlock>("DeviceRefresh").Text = ready && info != null ? info.RefreshLabel : "—";
            Get<TextBlock>("DeviceAndroid").Text = ready && info != null && !string.IsNullOrEmpty(info.AndroidVersion) ? "Android " + info.AndroidVersion : "—";
        }

        async Task UpdateDeviceProfile()
        {
            if (closing) return;
            var device = Get<ComboBox>("Devices").SelectedItem as Device;
            if (device == null || !device.Ready) { if (!refreshing) RenderDeviceProfile(null, null); return; }
            DeviceCapabilities cached;
            if (capabilities.TryGetValue(device.Serial, out cached)) { RenderDeviceProfile(device, cached); return; }
            RenderDeviceProfile(device, null);
            if (!pendingProfiles.Add(device.Serial)) return;
            try
            {
                var info = await DeviceCapabilities.Read(Adb, device.Serial, profileLifetime.Token);
                if (closing) return;
                capabilities[device.Serial] = info;
                var current = Get<ComboBox>("Devices").SelectedItem as Device;
                if (current != null && current.Serial == device.Serial && current.Ready) RenderDeviceProfile(current, info);
            }
            catch (OperationCanceledException) { }
            finally { pendingProfiles.Remove(device.Serial); }
        }

        async Task Refresh()
        {
            if (refreshing || networkBusy || closing || Running && hiddenToTray || !File.Exists(Adb)) return;
            refreshing = true; Get<Button>("RefreshButton").IsEnabled = false;
            try
            {
                var result = await Commands.Run(Adb, 10000, "devices", "-l");
                if (closing) return;
                if (result.ExitCode != 0) throw new Exception(result.Combined);
                var previous = Get<ComboBox>("Devices").SelectedItem as Device;
                var devices = Protocol.ParseDevices(result.Output).OrderByDescending(d => d.Wireless == wifi).ToList();
                Get<ComboBox>("Devices").ItemsSource = devices;
                Get<ComboBox>("Devices").SelectedItem = devices.FirstOrDefault(d => previous != null && d.Serial == previous.Serial) ?? devices.FirstOrDefault(d => d.Ready) ?? devices.FirstOrDefault();
                var device = Get<ComboBox>("Devices").SelectedItem as Device;
                Get<TextBlock>("ConnectionHint").Visibility = device != null && device.Ready ? Visibility.Collapsed : Visibility.Visible;
                Get<TextBlock>("DeviceHint").Text = device == null ? (wifi ? "Выполните сопряжение по QR-коду, коду или сначала подключите USB." : "Телефон не найден. Проверьте кабель, отладку и USB-драйвер.") : device.State == "unauthorized" ? "Разблокируйте телефон и подтвердите доступ для этого ПК." : !device.Ready ? "Устройство недоступно. Проверьте разрешение отладки." : "Готов к трансляции";
                Get<ComboBox>("Devices").ToolTip = device == null ? null : device.Serial;
            }
            catch (Exception e) { Get<TextBlock>("DeviceHint").Text = "Не удалось получить список устройств. Нажмите «Обновить»."; AddLog(e.Message); Get<ComboBox>("Devices").ItemsSource = null; }
            finally { refreshing = false; Get<Button>("RefreshButton").IsEnabled = true; UpdateButtons(); }
            await UpdateDeviceProfile();
        }

        async Task Network(bool pairing)
        {
            if (networkBusy || Running) return;
            try
            {
                string address = Get<TextBox>(pairing ? "PairAddress" : "ConnectAddress").Text;
                if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException(pairing ? "На телефоне откройте «Беспроводная отладка → Подключить устройство с помощью кода сопряжения». Введите IP:порт из открывшегося окна." : "Введите IP:порт с главной страницы «Беспроводная отладка» на телефоне. Код для этого шага не нужен.");
                string endpoint = Protocol.Endpoint(address);
                if (pairing && string.IsNullOrWhiteSpace(Get<PasswordBox>("PairCode").Password)) throw new ArgumentException("Шестизначный код появится на телефоне: «Беспроводная отладка → Подключить устройство с помощью кода сопряжения». Оставьте это окно открытым и введите код в Morror.");
                string code = pairing ? Protocol.PairingCode(Get<PasswordBox>("PairCode").Password) : null;
                networkBusy = true; UpdateButtons(); Notice(pairing ? "Ожидаем сопряжение с телефоном…" : "Подключаемся к телефону по Wi-Fi…");
                var result = pairing ? await Commands.Run(Adb, 25000, "pair", endpoint, code) : await Commands.Run(Adb, 20000, "connect", endpoint);
                if (closing) return;
                AddLog(result.Combined);
                bool success = result.ExitCode == 0 && (pairing ? result.Combined.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0 : result.Combined.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0);
                if (success && pairing)
                {
                    Notice("Сопряжение выполнено. Ищем порт подключения…");
                    PairingResult connection = null;
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(profileLifetime.Token))
                        { timeout.CancelAfter(25000); connection = await new WirelessPairing(Adb).ConnectHost(endpoint, timeout.Token); }
                    }
                    catch (InvalidOperationException) { }
                    catch (TimeoutException) { }
                    catch (OperationCanceledException) { if (closing) return; }
                    if (connection != null && connection.Connected) Get<TextBox>("ConnectAddress").Text = connection.Address;
                    else Get<Expander>("ManualWifi").IsExpanded = true;
                    Notice(connection != null && connection.Connected ? "Телефон подключён. Можно начать трансляцию." : "Сопряжение сохранено. Введите IP:порт с главной страницы беспроводной отладки — порт отличается от окна с кодом.");
                }
                else if (success)
                {
                    var state = await Commands.Run(Adb, 8000, "-s", endpoint, "get-state");
                    Notice(state.ExitCode == 0 && state.Output.Trim() == "device" ? "Телефон подключён. Можно начать трансляцию." : "Телефон ещё не готов. Разблокируйте его и проверьте разрешение отладки.");
                }
                else Notice("Не удалось подключиться. Проверьте общую сеть, IP и порт. Обычной USB-отладки для беспроводного сопряжения недостаточно.");
            }
            catch (Exception e) { Notice(e.Message); }
            finally { Get<PasswordBox>("PairCode").Clear(); networkBusy = false; UpdateButtons(); }
            await Refresh();
        }

        async Task ConnectWithQr()
        {
            if (networkBusy || Running || starting || closing) return;
            networkBusy = true; UpdateButtons();
            try
            {
                var dialog = new QrPairingDialog(window, new WirelessPairing(Adb));
                var paired = dialog.Show();
                if (closing || paired == null) return;
                if (paired.Connected)
                {
                    Get<TextBox>("ConnectAddress").Text = paired.Address;
                    Notice("Телефон подключён по QR-коду. Выберите его в списке и начните трансляцию.");
                    AddLog("Сопряжение и подключение по QR-коду выполнены.");
                }
                else
                {
                    Get<Expander>("ManualWifi").IsExpanded = true;
                    Notice("Сопряжение по QR-коду выполнено. Введите IP:порт с главной страницы беспроводной отладки в поле подключения. Повторный код не нужен.");
                }
            }
            catch (Exception e) { Notice("Не удалось открыть подключение по QR-коду. Используйте ручное сопряжение."); AddLog(e.GetType().Name); }
            finally { networkBusy = false; UpdateButtons(); }
            await Refresh();
        }

        async Task UsbToWifi()
        {
            if (networkBusy || Running || starting || closing) return;
            var device = Get<ComboBox>("Devices").SelectedItem as Device;
            if (device == null || !device.Ready || device.Wireless) { Notice("Выберите подключённый по USB телефон. Разблокируйте его и разрешите отладку."); return; }
            networkBusy = true; UpdateButtons();
            try
            {
                Notice("Определяем Wi-Fi-адрес телефона…");
                var address = await Commands.Run(Adb, 8000, "-s", device.Serial, "shell", "ip -o -4 addr show");
                if (address.ExitCode != 0) throw new InvalidOperationException("Не удалось прочитать адрес Wi-Fi. Используйте QR-код или код сопряжения.");
                string endpoint = WirelessPairing.WifiAddress(address.Output);
                var enabled = await Commands.Run(Adb, 10000, "-s", device.Serial, "tcpip", "5555");
                if (enabled.ExitCode != 0) throw new InvalidOperationException("Телефон не разрешил ADB по Wi-Fi. Используйте беспроводную отладку в настройках Android.");
                bool connected = false;
                for (int i = 0; i < 5 && !connected; i++)
                {
                    await Task.Delay(1000, profileLifetime.Token);
                    try { connected = await new WirelessPairing(Adb).Connect(endpoint, profileLifetime.Token); }
                    catch (TimeoutException) { }
                }
                if (!connected) throw new InvalidOperationException("Телефон включил порт 5555, но не отвечает. Проверьте общую сеть и изоляцию Wi-Fi. USB пока оставьте подключённым.");
                Get<TextBox>("ConnectAddress").Text = endpoint;
                Get<ComboBox>("Devices").SelectedItem = null;
                SetMode(true);
                Notice("Wi-Fi подключён. USB можно отключить. ADB на порту 5555 включён до перезагрузки телефона; используйте доверенную сеть.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Notice(error.Message); }
            finally { networkBusy = false; UpdateButtons(); }
            await Refresh();
        }

        async Task Start()
        {
            var device = Get<ComboBox>("Devices").SelectedItem as Device;
            if (device == null || !device.Ready || Running || starting) return;
            starting = true; stopRequested = false; UpdateButtons(); Badge("●  Запуск…");
            Process launched = null;
            try
            {
                bool audio = Get<CheckBox>("Audio").IsChecked == true;
                if (audio)
                {
                    var sdk = await Commands.Run(Adb, 8000, "-s", device.Serial, "shell", "getprop", "ro.build.version.sdk");
                    int level;
                    if (sdk.ExitCode != 0 || !int.TryParse(sdk.Output.Trim(), out level)) throw new Exception("Не удалось определить версию Android. Проверьте разрешение отладки и повторите подключение.");
                    if (level < 30) { audio = false; Notice("На этом телефоне Android ниже 11: будет передаваться экран без звука."); }
                    else if (level == 30) Notice("Android 11: оставьте телефон разблокированным при запуске, чтобы включилась передача звука.");
                }
                if (closing) return;
                int fps = SelectedFrameRate();
                var args = Protocol.MirrorArguments(device.Serial, Get<ComboBox>("Quality").SelectedIndex, fps, audio, Get<CheckBox>("Control").IsChecked == true, Get<CheckBox>("AlwaysOnTop").IsChecked == true, Get<CheckBox>("ClipboardSync").IsChecked == true, Get<ComboBox>("KeyboardMode").SelectedIndex == 0, Get<CheckBox>("ScreenOff").IsChecked == true);
                launched = new Process { StartInfo = Commands.StartInfo(Path.Combine(runtime, "scrcpy.exe"), args), EnableRaisingEvents = true };
                launched.StartInfo.EnvironmentVariables["ADB"] = Adb;
                launched.OutputDataReceived += OnOutput; launched.ErrorDataReceived += OnOutput;
                var owned = launched;
                launched.Exited += (s, e) => { if (!closing && !window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(new Action(() => Ended(owned))); };
                AddLog("Запуск трансляции: " + device.Label + (fps == 0 ? ", FPS устройства без ограничения Morror." : ", до " + fps + " FPS."));
                launched.Start(); mirror = launched; activeSerial = device.Serial; activeModel = device.Model;
                launched.BeginOutputReadLine(); launched.BeginErrorReadLine();
                UpdateButtons();
                for (int i = 0; i < 60; i++)
                {
                    if (closing || mirror != launched || launched.HasExited) return;
                    launched.Refresh();
                    if (launched.MainWindowHandle != IntPtr.Zero)
                    {
                        ApplyPin();
                        Badge("●  Трансляция"); Get<TextBlock>("SessionHint").Text = device.Model + (fps == 0 ? " · без лимита FPS" : " · до " + fps + " FPS") + " · Alt+F — полный экран · Alt+K — языки клавиатуры";
                        if (Get<CheckBox>("AutoTray").IsChecked == true) HideToTray();
                        return;
                    }
                    await Task.Delay(250);
                }
                throw new TimeoutException("Окно трансляции не появилось. Проверьте телефон и журнал подключения.");
            }
            catch (Exception e)
            {
                AddLog(e.Message); Notice(e.Message); Stop(); Badge("●  Ошибка запуска");
                if (launched != null && mirror != launched) launched.Dispose();
            }
            finally { starting = false; UpdateButtons(); }
        }

        void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data) || closing) return;
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closing) return;
                AddLog(e.Data);
                if (e.Data.IndexOf("uhid", StringComparison.OrdinalIgnoreCase) >= 0 && e.Data.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0)
                    Notice("Телефон не поддерживает UHID. Остановите трансляцию и выберите Настройки → Клавиатура → Совместимость. Для русского текста используйте вставку из буфера.");
                string pushed = Protocol.FilePushNotice(e.Data);
                if (pushed != null)
                {
                    Notice(pushed);
                    if (!window.IsVisible && tray != null) tray.FileTransferNotice(pushed, e.Data.Contains("Failed to push "));
                }
                if (e.Data.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0 && (e.Data.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || e.Data.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0))
                    Notice("Передача звука недоступна. Проверьте версию Android и сообщения в журнале; экран может продолжать работать.");
            }));
        }

        void Ended(Process owned)
        {
            if (mirror != owned) return;
            int exitCode = owned.ExitCode;
            mirror = null; activeSerial = null; activeModel = null; owned.Dispose();
            if (closing) return;
            Badge("●  Ожидание");
            Get<TextBlock>("SessionHint").Text = "Трансляция завершена. Можно снова подключить телефон.";
            if (!stopRequested && exitCode != 0) Notice("Трансляция прервана. Проверьте подключение телефона. Причина указана в журнале.");
            AddLog("Трансляция завершена, код " + exitCode + "."); UpdateButtons();
            if (hiddenToTray) RestoreFromTray();
        }

        void Stop()
        {
            stopRequested = true;
            if (mirror == null) return;
            try { if (!mirror.HasExited && !mirror.CloseMainWindow()) mirror.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception e) { if (!closing) AddLog(e.Message); }
            if (closing) { try { if (!mirror.WaitForExit(1200)) mirror.Kill(); } catch (InvalidOperationException) { } }
        }

        void UpdateButtons()
        {
            if (closing) return;
            var device = Get<ComboBox>("Devices").SelectedItem as Device;
            bool idle = !Running && !starting;
            if (tray != null) tray.SetRunning(Running);
            bool canTransfer = !starting && !networkBusy && (Running || device != null && device.Ready);
            Get<Button>("TransferButton").IsEnabled = canTransfer;
            if (tray != null) tray.SetTransferAvailable(canTransfer);
            Get<CheckBox>("ClipboardSync").IsEnabled = idle && !networkBusy && Get<CheckBox>("Control").IsChecked == true;
            Get<Button>("StartButton").IsEnabled = idle && !networkBusy && !refreshing && device != null && device.Ready;
            Get<Button>("StopButton").IsEnabled = Running;
            foreach (string name in new[] { "PairButton", "ConnectButton", "QrButton", "UsbWifiButton" }) Get<Button>(name).IsEnabled = idle && !networkBusy;
            foreach (string name in new[] { "Devices", "Quality", "FrameRate", "StreamPreset", "KeyboardMode", "Audio", "Control", "UsbButton", "WifiButton" }) Get<Control>(name).IsEnabled = idle && !networkBusy;
            Get<ComboBox>("KeyboardMode").IsEnabled = idle && !networkBusy && Get<CheckBox>("Control").IsChecked == true;
            Get<CheckBox>("ScreenOff").IsEnabled = idle && !networkBusy && Get<CheckBox>("Control").IsChecked == true;
            Get<Button>("KeyboardSettings").IsEnabled = Running && Get<CheckBox>("Control").IsChecked == true && Get<ComboBox>("KeyboardMode").SelectedIndex == 0;
            Get<Button>("DevicePresetButton").IsEnabled = idle && !networkBusy && device != null && device.Ready;
        }
        void Notice(string value) { if (!closing) { Get<TextBlock>("Notice").Text = value; Get<TextBlock>("Notice").Visibility = Visibility.Visible; } }
        void Badge(string value) { if (!closing) Get<TextBlock>("StatusBadge").Text = value; }
        void AddLog(string line)
        {
            if (closing) return;
            var log = Get<TextBox>("Log");
            if (log.Text.Length > 40000) log.Text = log.Text.Substring(log.Text.Length - 20000);
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); log.ScrollToEnd();
        }
    }
}

