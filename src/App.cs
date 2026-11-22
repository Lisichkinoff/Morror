using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
        MirrorToolbar mirrorToolbar;
        Process iPhoneWindow;
        Device activeDevice, interruptedDevice;
        bool streamStarted;
        Dictionary<string, object> interruptedSettings;
        string preferredUsbSerial;
        Device usbMigrationDevice;
        int? actualFps;
        static readonly Regex ScrcpyFps = new Regex(@"^(?:INFO:\s*)?([0-9]+) fps(?: \(\+[0-9]+ frames skipped\))?$", RegexOptions.CultureInvariant);
        DispatcherTimer timer;
        TrayController tray;
        EventWaitHandle restoreEvent;
        RegisteredWaitHandle restoreWait;
        WindowState lastWindowState = WindowState.Normal;
        readonly string restoreSignalName;
        readonly Dictionary<string, DeviceCapabilities> capabilities = new Dictionary<string, DeviceCapabilities>();
        readonly HashSet<string> pendingProfiles = new HashSet<string>();
        readonly CancellationTokenSource profileLifetime = new CancellationTokenSource();
        readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        readonly bool checkForUpdates;
        readonly string settingsPath;
        static readonly string[] SelectionSettings = { "Quality", "FrameRate", "KeyboardMode" };
        const int CustomPreset = 3;
        static readonly int[] PresetQuality = { 1, 0, 2, -1, 0 };
        static readonly int[] PresetFrameRate = { 2, 1, 2, -1, 2 };
        static readonly string[] ToggleSettings = { "ScreenOff", "Control", "ClipboardSync", "AlwaysOnTop", "AutoTray", "Audio" };
        AppUpdate availableUpdate;
        public App() : this("Local\\Morror.ShowSettings", true, StreamSettings.UserPath) { }
        internal App(string restoreSignalName, bool checkForUpdates = false, string settingsPath = null)
        {
            this.restoreSignalName = restoreSignalName;
            this.checkForUpdates = checkForUpdates;
            this.settingsPath = settingsPath;
        }
        T Get<T>(string name) where T : class { return window.FindName(name) as T; }
        string Adb { get { return Path.Combine(runtime, "adb.exe"); } }
        bool Running { get { return mirror != null; } }

        [STAThread]
        public static void Main()
        {
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Length > 0 && args[0] == AppUpdate.ApplyArgument)
            {
                AppUpdate.Apply(args);
                return;
            }
            if (args.Length == 4 && args[0] == "--allow-monitor-apk")
            {
                try
                {
                    Guid id;
                    System.Net.IPAddress address;
                    int port;
                    if (!Guid.TryParseExact(args[1], "N", out id) || !System.Net.IPAddress.TryParse(args[2], out address) ||
                        !int.TryParse(args[3], out port))
                        throw new ArgumentException("Недопустимые параметры скачивания APK.");
                    MonitorApkFirewall.Allow(id.ToString("N"), address, port);
                }
                catch (Exception error) { Trace.WriteLine(error); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length == 4 && args[0] == "--monitor-driver-session")
            {
                try
                {
                    int parent;
                    long started;
                    Guid signal;
                    if (!int.TryParse(args[1], out parent) || !long.TryParse(args[2], out started) || !Guid.TryParseExact(args[3], "N", out signal))
                        throw new ArgumentException("Недопустимые параметры сеанса монитора.");
                    VirtualMonitorDriver.RunSessionWorker(parent, started, signal.ToString("N"));
                }
                catch (Exception error) { Trace.WriteLine(error); Environment.ExitCode = 1; }
                return;
            }
            if (args.SequenceEqual(new[] { "--hide-idle-monitor" }))
            {
                try { VirtualMonitorDriver.SetEnabled(false); }
                catch (Exception error) { MessageBox.Show(error.Message, "Morror"); Environment.ExitCode = 1; }
                return;
            }
            if(args.Length>0 && args[0]=="--configure-monitor-mode")
            {
                try
                {
                    int width,height,fps;
                    if(args.Length!=4||!int.TryParse(args[1],out width)||!int.TryParse(args[2],out height)||!int.TryParse(args[3],out fps))throw new ArgumentException("Недопустимые параметры разрешения.");
                    VirtualMonitorDriver.ConfigureMode(width,height,fps);
                }
                catch(Exception error){MessageBox.Show("Не удалось добавить разрешение монитора.\n"+error.Message,"Morror");Environment.ExitCode=1;}
                return;
            }
            if (args.SequenceEqual(new[] { "--install-monitor-driver" }))
            {
                try { VirtualMonitorDriver.InstallBundled(); }
                catch (Exception error) { MessageBox.Show("Не удалось установить компонент монитора.\n" + error.Message, "Morror"); Environment.ExitCode = 1; }
                return;
            }
            if (AppUpdate.IsInstalling())
            {
                MessageBox.Show("Morror обновляется и откроется снова после установки.", "Обновление Morror");
                return;
            }
            if (args.Length > 0 && args[0] == SendFilesDialog.LaunchArgument)
            {
                SendFilesDialog.Run(args.Skip(1));
                return;
            }
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
                    if (AppUpdate.IsInstalling()) return;
                    var application = new Application();
                    application.DispatcherUnhandledException += (s, e) => { MessageBox.Show(e.Exception.Message, "Morror"); e.Handled = true; };
                    var app = new App("Local\\Morror.ShowSettings", !args.Contains(AppUpdate.SkipCheckArgument), StreamSettings.UserPath);
                    application.SessionEnding += (s, e) => { app.exitRequested = true; };
                    app.Initialize();
                    try { VirtualMonitorDriver.DetachIdleScreens(); }
                    catch (Exception error)
                    {
                        app.Notice("Не удалось отключить оставшийся виртуальный монитор. Подробности в журнале.");
                        app.AddLog(error.Message);
                    }
                    try { application.Run(app.window); }
                    finally
                    {
                        try { VirtualMonitorDriver.DetachIdleScreens(); }
                        catch (Exception error) { Trace.WriteLine(error); }
                    }
                }
                catch (Exception e) { MessageBox.Show("Не удалось открыть Morror.\n" + e.Message, "Morror"); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        void Initialize()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            var area = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = Math.Min(window.Width, area.Width);
            window.Height = Math.Min(window.Height, area.Height);
            window.SizeChanged += (s, e) =>
            {
                UpdateLayout();
            };
            WindowFrame.Bind(window, Get<Border>("WindowFrame"), Get<Button>("MinimizeButton"), Get<Button>("MaximizeButton"), Get<Button>("CloseButton"));
            Get<Button>("OverviewNav").Click += (s, e) => ShowPage("Dashboard");
            Get<Button>("AndroidTask").Click += async (s, e) => { ShowAndroidSetup(true); await Refresh(); };
            Get<Button>("IPhoneTask").Click += async (s, e) => { if (!Running && !starting) ShowAndroidSetup(false); await OpenIPhone(); };
            Get<Button>("MonitorTask").Click += (s, e) => { if (!Running && !starting) ShowAndroidSetup(false); OpenSecondScreen(); };
            Get<Button>("SettingsNav").Click += (s, e) => ShowPage("Settings");
            Get<Button>("OpenSettingsButton").Click += (s, e) => ShowPage("Settings");
            Get<Button>("LogNav").Click += (s, e) => ShowPage("Log");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Morror.ico"))
            {
                var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                window.Icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
                Get<Image>("BrandIcon").Source = window.Icon;
            }
            Get<Button>("DownloadUpdate").Click += async (s, e) => await DownloadUpdateAsync();
            Get<Button>("LaterUpdate").Click += (s, e) => Get<Border>("UpdateNotice").Visibility = Visibility.Collapsed;
            Get<TextBlock>("SidebarVersion").Text = "Morror " + AppVersion.Number;
            LoadStreamSettings();
            Get<ComboBox>("FrameRate").SelectionChanged += (s, e) => UpdateFrameRateSummary();
            Get<ComboBox>("Quality").SelectionChanged += (s, e) => VideoSettingChanged();
            Get<ComboBox>("FrameRate").SelectionChanged += (s, e) => VideoSettingChanged();
            Get<ComboBox>("StreamPreset").SelectionChanged += (s, e) =>
            {
                if (applyingPreset) return;
                int preset = SelectedPreset();
                if (HasFixedPreset(preset))
                {
                    applyingPreset = true;
                    Get<ComboBox>("Quality").SelectedIndex = PresetQuality[preset];
                    Get<ComboBox>("FrameRate").SelectedIndex = PresetFrameRate[preset];
                    applyingPreset = false;
                }
                SaveStreamSettings();
            };
            Get<Button>("UsbWifiButton").Click += async (s, e) => await UsbToWifi();
            Get<Button>("KeyboardSettings").Click += async (s, e) =>
            {
                if (!Running || activeDevice == null) return;
                try
                {
                    var result = await Commands.Run(Adb, 8000, "-s", activeDevice.Serial, "shell", "am", "start", "-a", "android.settings.HARD_KEYBOARD_SETTINGS");
                    Notice(result.ExitCode == 0 && !result.Combined.Contains("Error") ? "Выберите клавиатуру scrcpy и добавьте русский и английский. Переключение обычно Ctrl+Пробел." : "Откройте на телефоне: Настройки → Язык и ввод → Физическая клавиатура.");
                }
                catch (Exception error) { Notice(error.Message); }
            };
            Get<Button>("DevicePresetButton").Click += (s, e) =>
            {
                applyingPreset = true;
                Get<ComboBox>("Quality").SelectedIndex = Protocol.NativeQuality;
                Get<ComboBox>("FrameRate").SelectedIndex = 0;
                SelectPreset(CustomPreset);
                var selected = Get<ComboBox>("Devices").SelectedItem as Device;
                DeviceCapabilities info;
                if (selected != null && capabilities.TryGetValue(selected.Serial, out info) && info.ApiLevel > 0) Get<CheckBox>("Audio").IsChecked = info.ApiLevel >= 30;
                applyingPreset = false;
                SaveStreamSettings();
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
            Get<Button>("ResumeButton").Click += async (s, e) => await Resume();
            Get<Button>("StopButton").Click += (s, e) => Stop();
            Get<Button>("TransferButton").Click += (s, e) => OpenTransfer();
            Get<Button>("SecondScreenButton").Click += (s, e) => OpenSecondScreen();
            Get<Button>("IPhoneButton").Click += async (s, e) => await OpenIPhone();
            Get<CheckBox>("Control").Checked += (s, e) => UpdateButtons();
            Get<CheckBox>("Control").Unchecked += (s, e) => UpdateButtons();
            Get<CheckBox>("AlwaysOnTop").Checked += (s, e) => ApplyPin();
            Get<CheckBox>("AlwaysOnTop").Unchecked += (s, e) => ApplyPin();
            Get<ComboBox>("Devices").SelectionChanged += async (s, e) => { UpdateDeviceSelection(); UpdateButtons(); await UpdateDeviceProfile(); };
            window.StateChanged += (s, e) =>
            {
                if (closing) return;
                if (window.WindowState == WindowState.Minimized && tray != null) HideToTray();
                else if (window.WindowState != WindowState.Minimized) lastWindowState = window.WindowState;
            };
            window.Closing += (s, e) =>
            {
                if (!exitRequested && (Running || iPhoneWindow != null) && tray != null) { e.Cancel = true; HideToTray(); return; }
                if (iPhoneWindow != null && !iPhoneWindow.HasExited)
                {
                    e.Cancel = true;
                    iPhoneWindow.Refresh();
                    if (!iPhoneWindow.CloseMainWindow())
                        Notice("Закройте окно iPhone перед выходом из Morror.");
                    return;
                }
                closing = true; updateLifetime.Cancel(); profileLifetime.Cancel(); if (timer != null) timer.Stop(); Stop();
            };
            window.Closed += (s, e) =>
            {
                if (restoreWait != null) restoreWait.Unregister(null);
                if (restoreEvent != null) restoreEvent.Dispose();
                if (tray != null) tray.Dispose();
            };
            InitializeTray();
            ApplyPin();
            UpdateButtons();
            Get<ComboBox>("KeyboardMode").SelectionChanged += (s, e) => SaveStreamSettings();
            foreach (string name in ToggleSettings)
            {
                Get<CheckBox>(name).Checked += (s, e) => SaveStreamSettings();
                Get<CheckBox>(name).Unchecked += (s, e) => SaveStreamSettings();
            }
            if (checkForUpdates) window.Loaded += async (s, e) => await CheckForUpdatesAsync();
            window.Loaded += async (s, e) =>
            {
                if (!File.Exists(Adb) || !File.Exists(Path.Combine(runtime, "scrcpy.exe")) || !File.Exists(Path.Combine(runtime, "scrcpy-server")))
                { Notice("Не найдены компоненты трансляции. Распакуйте всю папку Morror из архива рядом с Morror.exe."); return; }
                await Refresh();
                if (closing) return;
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += async (ts, te) => { if (!Running) await Refresh(); };
                timer.Start();
            };
        }

        int SelectedPreset()
        {
            int preset;
            return int.TryParse(Get<ComboBox>("StreamPreset").SelectedValue as string, out preset)
                ? preset : CustomPreset;
        }

        void SelectPreset(int preset)
        {
            Get<ComboBox>("StreamPreset").SelectedValue = preset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static bool HasFixedPreset(int preset)
        {
            return preset >= 0 && preset < PresetQuality.Length && preset != CustomPreset;
        }

        void VideoSettingChanged()
        {
            if (applyingPreset) return;
            if (SelectedPreset() != CustomPreset) SelectPreset(CustomPreset);
            else SaveStreamSettings();
        }

        void LoadStreamSettings()
        {
            if (settingsPath == null) return;
            try { ApplyStreamSettings(StreamSettings.Load(settingsPath)); }
            catch (Exception error) { AddLog("Чтение настроек: " + error.Message); }
        }

        void ApplyStreamSettings(Dictionary<string, object> values)
        {
            applyingPreset = true;
            try
            {
                foreach (string name in SelectionSettings)
                {
                    var control = Get<ComboBox>(name);
                    object value;
                    if (values.TryGetValue(name, out value) && value is int && (int)value >= 0 && (int)value < control.Items.Count)
                        control.SelectedIndex = (int)value;
                }
                foreach (string name in ToggleSettings)
                {
                    object value;
                    if (values.TryGetValue(name, out value) && value is bool) Get<CheckBox>(name).IsChecked = (bool)value;
                }
                object savedPreset;
                if (values.TryGetValue("StreamPreset", out savedPreset) && savedPreset is int
                    && ((int)savedPreset == CustomPreset || HasFixedPreset((int)savedPreset)))
                    SelectPreset((int)savedPreset);
                int preset = SelectedPreset();
                if (HasFixedPreset(preset) && (Get<ComboBox>("Quality").SelectedIndex != PresetQuality[preset] ||
                    Get<ComboBox>("FrameRate").SelectedIndex != PresetFrameRate[preset]))
                    SelectPreset(CustomPreset);
            }
            finally { applyingPreset = false; }
        }

        Dictionary<string, object> CaptureStreamSettings()
        {
            var values = new Dictionary<string, object>();
            foreach (string name in SelectionSettings) values[name] = Get<ComboBox>(name).SelectedIndex;
            values["StreamPreset"] = SelectedPreset();
            foreach (string name in ToggleSettings) values[name] = Get<CheckBox>(name).IsChecked == true;
            return values;
        }

        void SaveStreamSettings()
        {
            if (settingsPath == null || applyingPreset || closing) return;
            var status = Get<TextBlock>("SettingsSaveStatus");
            try
            {
                StreamSettings.Save(settingsPath, CaptureStreamSettings());
                status.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                status.Visibility = Visibility.Visible;
                AddLog("Сохранение настроек: " + error.Message);
            }
        }

        async Task CheckForUpdatesAsync()
        {
            try
            {
                await Task.Run(() => AppUpdate.CleanOldDownloads());
                var update = await AppUpdate.CheckAsync(updateLifetime.Token);
                if (!closing && update != null) ShowUpdate(update);
            }
            catch (Exception error)
            {
                AddLog("Проверка обновлений Morror: " + error.Message);
            }
        }

        void ShowUpdate(AppUpdate update)
        {
            availableUpdate = update;
            Get<TextBlock>("UpdateTitle").Text = "Доступна новая версия Morror " + update.Version;
            Get<Border>("UpdateNotice").Visibility = Visibility.Visible;
        }

        async Task DownloadUpdateAsync()
        {
            var download = Get<Button>("DownloadUpdate");
            if (availableUpdate == null || !download.IsEnabled) return;
            var later = Get<Button>("LaterUpdate");
            var status = Get<TextBlock>("UpdateStatus");
            download.IsEnabled = later.IsEnabled = false;
            status.Text = "Подключение к GitHub…";
            string folder = null;
            try
            {
                var progress = new Progress<int>(percent =>
                {
                    status.Text = percent == 100 ? "Проверка установщика…" : "Скачивание… " + percent + "%";
                });
                folder = await availableUpdate.DownloadAsync(progress, updateLifetime.Token);
                status.Text = "Установщик проверен.";
                if (window.OwnedWindows.Cast<Window>().Any(owned => owned.IsVisible) || iPhoneWindow != null || starting || networkBusy)
                    status.Text = "Обновление начнётся после завершения текущей операции.";
                while (!closing && (window.OwnedWindows.Cast<Window>().Any(owned => owned.IsVisible) || iPhoneWindow != null || starting || networkBusy))
                    await Task.Delay(300, updateLifetime.Token);
                if (closing) return;
                status.Text = "Установка обновления. Morror откроется снова.";
                window.IsEnabled = false;
                try
                {
                    await availableUpdate.StartInstallerAsync(folder, updateLifetime.Token);
                    folder = null;
                    if (closing) return;
                    exitRequested = true;
                    window.Close();
                }
                finally
                {
                    if (!closing) window.IsEnabled = true;
                }
            }
            catch (Exception error)
            {
                if (!closing)
                {
                    status.Text = "Не удалось скачать или запустить обновление. Повторите попытку.";
                    AddLog("Обновление Morror: " + error.Message);
                }
            }
            finally
            {
                AppUpdate.DeleteStaging(folder);
                if (!closing) download.IsEnabled = later.IsEnabled = true;
            }
        }

        bool iPhoneOpening;
        async Task OpenIPhone()
        {
            if (iPhoneOpening) return;
            iPhoneOpening = true;
            try
            {
                if (iPhoneWindow != null && !iPhoneWindow.HasExited)
                {
                    await ActivateIPhone(iPhoneWindow);
                    return;
                }
                string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "iphone", "Morror.iPhone.exe");
                if (!File.Exists(executable)) throw new IOException("Установите полный пакет Morror с поддержкой iPhone.");
                int parent;
                using (var current = Process.GetCurrentProcess()) parent = current.Id;
                var process = new Process { StartInfo = new ProcessStartInfo(executable, parent.ToString())
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) }, EnableRaisingEvents = true };
                process.Exited += (s, e) =>
                {
                    if (window.Dispatcher.HasShutdownStarted) { process.Dispose(); return; }
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (iPhoneWindow != process) { process.Dispose(); return; }
                        iPhoneWindow = null;
                        process.Dispose();
                        if (exitRequested && !closing) window.Close();
                    }));
                };
                try { process.Start(); iPhoneWindow = process; }
                catch { process.Dispose(); throw; }
                await ActivateIPhone(process);
            }
            catch (Exception error) { Notice(error.Message); AddLog(error.Message); }
            finally { iPhoneOpening = false; }
        }

        async Task ActivateIPhone(Process process)
        {
            // MainWindowHandle is zero during WPF startup. A click must not be lost.
            for (int attempt = 0; attempt < 100 && !closing; attempt++)
            {
                if (iPhoneWindow != process || process.HasExited) return;
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero) { ShowWindow(handle, 9); SetForegroundWindow(handle); return; }
                await Task.Delay(100);
            }
            if (!closing) Notice("Окно iPhone ещё запускается. Повторите нажатие через несколько секунд.");
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

        void UpdateLayout()
        {
            bool compact = window.ActualWidth <= 800;
            Get<ColumnDefinition>("SidebarColumn").Width = new GridLength(compact ? 66 : 166);
            foreach (string name in new[] { "BrandLabel", "OverviewLabel", "TransferLabel", "SecondScreenLabel", "IPhoneLabel", "SettingsLabel", "LogLabel", "SidebarVersion" })
                Get<FrameworkElement>(name).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            bool singleColumn = window.ActualWidth < 900;
            Get<StackPanel>("ProfilePanel").Visibility = singleColumn ? Visibility.Collapsed : Visibility.Visible;
            Get<ColumnDefinition>("ProfileColumn").Width = singleColumn ? new GridLength(0) : new GridLength(0.43, GridUnitType.Star);
            Get<ColumnDefinition>("ProfileGap").Width = new GridLength(singleColumn ? 0 : 12);
        }

        void ShowPage(string page)
        {
            foreach (string name in new[] { "Dashboard", "Settings", "Log" })
                Get<StackPanel>(name + "Page").Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
            Get<TextBlock>("PageTitle").Text = page == "Settings" ? "Настройки трансляции" : page == "Log" ? "Журнал подключения" : "Что хотите сделать?";
            string selectedNav = page == "Dashboard" ? "OverviewNav" : page + "Nav";
            foreach (string name in new[] { "OverviewNav", "SettingsNav", "LogNav" })
                Get<Button>(name).Tag = name == selectedNav ? "Selected" : null;
            Get<ScrollViewer>("MainScroll").ScrollToTop();
        }

        void ShowAndroidSetup(bool show)
        {
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Get<Grid>("AndroidSetup").Visibility = visibility;
            Get<Grid>("StreamActions").Visibility = visibility;
            Get<Button>("AndroidTask").Tag = show ? "Selected" : null;
        }

        void OpenSecondScreen()
        {
            new SecondScreenDialog(window, Adb, Get<ComboBox>("Devices").SelectedItem as Device).Show();
        }

        void InitializeTray()
        {
            try
            {
                tray = new TrayController(RestoreFromTray, () => { Stop(); RestoreFromTray(); }, () => { exitRequested = true; window.Close(); }, pinned => { Get<CheckBox>("AlwaysOnTop").IsChecked = pinned; }, () => { RestoreFromTray(); OpenTransfer(); });
            }
            catch (Exception e)
            {
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
                if (mirrorToolbar != null) mirrorToolbar.SetPinned(pinned);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception e) { Notice("Не удалось изменить закрепление окна. Повторите переключение."); AddLog(e.Message); }
        }

        void OpenTransfer()
        {
            var device = Get<ComboBox>("Devices").SelectedItem as Device;
            if (Running) device = activeDevice;
            if (closing || starting || device == null || !device.Ready) return;
            var dialog = new TransferDialog(window, new FileTransfer(Adb, device.Serial), device.Model);
            dialog.Show();
        }

        void SetMode(bool wireless)
        {
            bool changed = wifi != wireless;
            wifi = wireless;
            if (changed) Get<ComboBox>("Devices").ItemsSource = null;
            Get<StackPanel>("WifiPanel").Visibility = wifi ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("UsbButton").Tag = wifi ? null : "Selected";
            Get<Button>("WifiButton").Tag = wifi ? "Selected" : null;
            Get<TextBlock>("ConnectionHint").Text = wifi
                ? "Подключите телефон и ПК к одной сети. На Android 11+ включите беспроводную отладку и выберите QR-код или код сопряжения."
                : "Включите USB-отладку и подключите телефон кабелем с передачей данных.";
            UpdateDeviceSelection();
            UpdateButtons();
        }

        void UpdateDeviceSelection()
        {
            var selector = Get<ComboBox>("Devices");
            var device = selector.SelectedItem as Device;
            bool ready = device != null && device.Ready;
            if (device != null && !device.Wireless) preferredUsbSerial = device.Serial;
            Get<TextBlock>("ConnectionHint").Visibility = device == null ? Visibility.Visible : Visibility.Collapsed;
            Get<TextBlock>("DeviceHint").Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            Get<TextBlock>("DeviceHint").Text = device == null
                ? (wifi ? "Выберите способ подключения по Wi-Fi." : "Ожидаем телефон по USB.")
                : device.State == "unauthorized" ? "Разблокируйте телефон и подтвердите «Разрешить отладку»."
                : !device.Ready ? "Устройство недоступно. Переподключите его и проверьте разрешение отладки."
                : "Готов к трансляции";
            selector.ToolTip = device == null ? null : device.Serial;
            if (!Running && !starting) Badge(ready ? "●  Устройство готово" : "●  Ожидание", ready);
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
            Get<TextBlock>("ProfileFps").Text = fps == 0 ? "Без лимита" : fps + " FPS";
            Get<TextBlock>("FpsHint").Text = fps == 0 ? "Реальная частота зависит от контента и захвата телефона." : "До " + fps + " FPS, если экран и захват телефона поддерживают эту частоту.";
        }

        void RenderStreamFps()
        {
            Get<Grid>("ProfileActualRow").Visibility = Running ? Visibility.Visible : Visibility.Collapsed;
            string measured = actualFps.HasValue ? actualFps.Value + " FPS" : "—";
            Get<TextBlock>("ProfileActualFps").Text = measured;
            if (mirrorToolbar != null) mirrorToolbar.SetFps(actualFps);
            if (!Running) return;
            var limit = Get<TextBlock>("ProfileFps").Text;
            Get<TextBlock>("SessionHint").Text = activeDevice.Model + " · " + (SelectedFrameRate() == 0 ? limit : "Лимит " + limit)
                + " · Фактически " + measured + " · Alt+F — полный экран · Alt+K — языки клавиатуры";
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

        async Task Refresh(string preferredSerial = null)
        {
            if (refreshing || networkBusy || starting || closing || Running && hiddenToTray || !File.Exists(Adb)) return;
            refreshing = true; Get<Button>("RefreshButton").IsEnabled = false; UpdateButtons();
            try
            {
                var all = await Commands.ListDevices(Adb, profileLifetime.Token);
                if (closing) return;
                SelectDevices(all, preferredSerial);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                usbMigrationDevice = null;
                Get<ComboBox>("Devices").ItemsSource = null;
                Get<TextBlock>("DeviceHint").Text = "Не удалось получить список устройств. Нажмите «Обновить».";
                AddLog(e.Message);
            }
            finally { refreshing = false; Get<Button>("RefreshButton").IsEnabled = true; UpdateButtons(); }
            await UpdateDeviceProfile();
        }

        void SelectDevices(List<Device> all, string preferredSerial)
        {
            var selector = Get<ComboBox>("Devices");
            var previous = selector.SelectedItem as Device;
            var preferredUsb = all.FirstOrDefault(d => !d.Wireless && d.Serial == preferredUsbSerial);
            var usbReady = all.Where(d => !d.Wireless && d.Ready).ToList();
            usbMigrationDevice = preferredUsb != null
                ? (preferredUsb.Ready ? preferredUsb : null)
                : (usbReady.Count == 1 ? usbReady[0] : null);
            var devices = all.Where(d => d.Wireless == wifi).ToList();
            var serial = preferredSerial ?? (previous != null ? previous.Serial : wifi ? null : preferredUsbSerial);
            selector.ItemsSource = devices;
            selector.SelectedItem = devices.FirstOrDefault(d => d.Serial == serial)
                ?? devices.FirstOrDefault(d => d.Ready) ?? devices.FirstOrDefault();
            UpdateDeviceSelection();
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
            var device = usbMigrationDevice;
            if (device == null) { Notice("На вкладке USB выберите нужный телефон, разрешите отладку и вернитесь на вкладку Wi-Fi."); return; }
            string connectedSerial = null;
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
                connectedSerial = endpoint;
                Get<TextBox>("ConnectAddress").Text = endpoint;
                Get<ComboBox>("Devices").SelectedItem = null;
                SetMode(true);
                Notice("Wi-Fi подключён. USB можно отключить. ADB на порту 5555 включён до перезагрузки телефона; используйте доверенную сеть.");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Notice(error.Message); }
            finally { networkBusy = false; UpdateButtons(); }
            await Refresh(connectedSerial);
        }

        Task Start() { return StartMirror(false); }
        Task Resume() { return StartMirror(true); }

        void AttachMirrorToolbar(IntPtr handle, Device device)
        {
            var pin = Get<CheckBox>("AlwaysOnTop");
            try
            {
                mirrorToolbar = new MirrorToolbar(window, handle, pinned => pin.IsChecked = pinned,
                    Stop, token => CaptureMirrorScreen(device, token), AddLog);
                mirrorToolbar.SetPinned(pin.IsChecked == true);
            }
            catch (Exception error)
            {
                CloseMirrorToolbar();
                AddLog("Не удалось открыть панель трансляции. " + error.Message);
            }
        }

        void CloseMirrorToolbar()
        {
            if (mirrorToolbar == null) return;
            mirrorToolbar.Dispose();
            mirrorToolbar = null;
        }

        async Task<string> CaptureMirrorScreen(Device device, CancellationToken token)
        {
            var transfer = new FileTransfer(Adb, device.Serial);
            string path = await transfer.Screenshot(FileTransfer.ReceiveFolder(), token);
            try
            {
                await ClipboardTransfer.CopyFile(path, token);
                return "Снимок сохранён и скопирован в буфер.";
            }
            catch (IOException error)
            {
                AddLog(error.Message);
                return "Снимок сохранён. Буфер Windows занят.";
            }
        }

        async Task StartMirror(bool resume)
        {
            if (closing || Running || starting || networkBusy || refreshing) return;
            var device = resume ? interruptedDevice : Get<ComboBox>("Devices").SelectedItem as Device;
            if (device == null || !device.Ready || !resume && device.Wireless != wifi) return;
            ShowAndroidSetup(true);
            starting = true; stopRequested = false; streamStarted = false; UpdateButtons(); Badge("●  Запуск…");
            Process launched = null;
            try
            {
                if (resume)
                {
                    var devices = await Commands.ListDevices(Adb, profileLifetime.Token);
                    if (closing) return;
                    var available = devices.FirstOrDefault(d => d.Ready && d.SameConnection(device));
                    if (available == null)
                    {
                        Badge("●  Ожидание");
                        Notice("Подключите " + device.Model + " снова по " + (device.Wireless ? "Wi-Fi" : "USB") + " и нажмите «Возобновить».");
                        return;
                    }
                    device = available;
                    SetMode(device.Wireless);
                    SelectDevices(devices, device.Serial);
                    ApplyStreamSettings(interruptedSettings);
                    SaveStreamSettings();
                    Get<TextBlock>("Notice").Visibility = Visibility.Collapsed;
                }
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
                int quality = Get<ComboBox>("Quality").SelectedIndex;
                var args = Protocol.MirrorArguments(device.Serial, quality, fps, audio, Get<CheckBox>("Control").IsChecked == true, Get<CheckBox>("AlwaysOnTop").IsChecked == true, Get<CheckBox>("ClipboardSync").IsChecked == true, Get<ComboBox>("KeyboardMode").SelectedIndex == 0, Get<CheckBox>("ScreenOff").IsChecked == true, device.Wireless);
                launched = new Process { StartInfo = Commands.StartInfo(Path.Combine(runtime, "scrcpy.exe"), args), EnableRaisingEvents = true };
                launched.StartInfo.EnvironmentVariables["ADB"] = Adb;
                launched.StartInfo.EnvironmentVariables["SCRCPY_ICON_DIR"] = runtime;
                launched.OutputDataReceived += OnOutput; launched.ErrorDataReceived += OnOutput;
                var owned = launched;
                launched.Exited += (s, e) => { if (!closing && !window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(new Action(() => Ended(owned))); };
                AddLog("Запуск трансляции: " + device.Label + (fps == 0 ? ", FPS устройства без ограничения Morror." : ", до " + fps + " FPS."));
                launched.Start(); mirror = launched; activeDevice = device;
                actualFps = null;
                launched.BeginOutputReadLine(); launched.BeginErrorReadLine();
                UpdateButtons();
                for (int i = 0; i < 60; i++)
                {
                    if (closing || mirror != launched || launched.HasExited) return;
                    launched.Refresh();
                    IntPtr handle = launched.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        ApplyPin();
                        AttachMirrorToolbar(handle, device);
                        streamStarted = true;
                        interruptedDevice = null;
                        interruptedSettings = null;
                        Badge("●  Трансляция", true); RenderStreamFps();
                        if (Get<CheckBox>("AutoTray").IsChecked == true) HideToTray();
                        return;
                    }
                    await Task.Delay(250);
                }
                throw new TimeoutException("Окно трансляции не появилось. Проверьте телефон и журнал подключения.");
            }
            catch (OperationCanceledException) { }
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
                if (closing || sender != mirror) return;
                AddLog(e.Data);
                var fps = ScrcpyFps.Match(e.Data.Trim());
                int measured;
                if (fps.Success && int.TryParse(fps.Groups[1].Value, out measured))
                {
                    actualFps = measured;
                    RenderStreamFps();
                }
                else if (e.Data.Trim() == "INFO: FPS counter stopped")
                {
                    actualFps = null;
                    RenderStreamFps();
                }
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
            var device = activeDevice;
            CloseMirrorToolbar();
            mirror = null; activeDevice = null; owned.Dispose();
            if (closing) return;
            actualFps = null;
            RenderStreamFps();
            Badge("●  Ожидание");
            Get<TextBlock>("SessionHint").Text = "Трансляция завершена. Можно снова подключить телефон.";
            if (!stopRequested && exitCode != 0)
            {
                if (streamStarted)
                {
                    interruptedDevice = device;
                    interruptedSettings = CaptureStreamSettings();
                }
                Notice(interruptedDevice == null
                    ? "Не удалось запустить трансляцию. Причина указана в журнале."
                    : "Трансляция прервана. Подключите телефон снова и нажмите «Возобновить».");
            }
            AddLog("Трансляция завершена, код " + exitCode + "."); UpdateButtons();
            if (hiddenToTray) RestoreFromTray();
        }

        void Stop()
        {
            stopRequested = true;
            if (closing) CloseMirrorToolbar();
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
            Get<Button>("TransferButton").ToolTip = canTransfer ? "Фото и буфер обмена" : "Фото и буфер обмена · подключите телефон";
            if (tray != null) tray.SetTransferAvailable(canTransfer);
            Get<CheckBox>("ClipboardSync").IsEnabled = idle && !networkBusy && Get<CheckBox>("Control").IsChecked == true;
            Get<Button>("StartButton").IsEnabled = idle && !networkBusy && !refreshing && device != null && device.Ready && device.Wireless == wifi;
            var resume = Get<Button>("ResumeButton");
            resume.Visibility = interruptedDevice != null && !Running ? Visibility.Visible : Visibility.Collapsed;
            resume.IsEnabled = idle && !networkBusy && !refreshing;
            Get<Button>("StopButton").IsEnabled = Running;
            foreach (string name in new[] { "PairButton", "ConnectButton", "QrButton", "UsbWifiButton" }) Get<Button>(name).IsEnabled = idle && !networkBusy;
            foreach (string name in new[] { "Devices", "Quality", "FrameRate", "StreamPreset", "KeyboardMode", "Audio", "Control", "UsbButton", "WifiButton" }) Get<Control>(name).IsEnabled = idle && !networkBusy;
            Get<ComboBox>("KeyboardMode").IsEnabled = idle && !networkBusy && Get<CheckBox>("Control").IsChecked == true;
            Get<CheckBox>("ScreenOff").IsEnabled = idle && !networkBusy && Get<CheckBox>("Control").IsChecked == true;
            Get<Button>("KeyboardSettings").IsEnabled = Running && Get<CheckBox>("Control").IsChecked == true && Get<ComboBox>("KeyboardMode").SelectedIndex == 0;
            Get<Button>("DevicePresetButton").IsEnabled = idle && !networkBusy && device != null && device.Ready;
        }
        void Notice(string value) { if (!closing) { Get<TextBlock>("Notice").Text = value; Get<TextBlock>("Notice").Visibility = Visibility.Visible; } }
        void Badge(string value, bool ready = false)
        {
            if (closing) return;
            Get<TextBlock>("StatusBadge").Text = value;
            Get<Border>("StatusPill").Tag = ready ? "Ready" : null;
        }
        void AddLog(string line)
        {
            if (closing) return;
            var log = Get<TextBox>("Log");
            if (log.Text.Length > 40000) log.Text = log.Text.Substring(log.Text.Length - 20000);
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); log.ScrollToEnd();
        }
    }
}

