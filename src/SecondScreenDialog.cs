using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Morror
{
    public sealed class SecondScreenDialog
    {
        readonly Window window;
        readonly string adb;
        readonly Device initialDevice;
        readonly string runtime=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"runtime");
        readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        NativeMonitorServer server;
        ScreenTouchInjection injection;
        Device activeDevice;
        bool busy,allowClose;
        int reversePort;
        string sessionDisplay;
        bool displayActivated;
        Process driverWorker;
        EventWaitHandle driverStop;
        MonitorApkDownload apkDownload;
        Task apkTransfer = Task.FromResult(0);
        bool loadingApkNetworks, installingApk;
        int apkSelection;
        EventWaitHandle apkFirewallStop;
        string MonitorApkPath { get { return Path.Combine(runtime, "MorrorMonitor.apk"); } }
        DateTime clientDeadline;
        T Get<T>(string name) where T:class{return window.FindName(name) as T;}
        void Status(string text){Get<TextBlock>("MonitorStatus").Text=text;}
        public SecondScreenDialog(Window owner,string adb,Device device)
        {
            this.adb=adb;initialDevice=device;
            using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("SecondScreenWindow.xaml"))window=(Window)System.Windows.Markup.XamlReader.Load(source);
            window.Resources=owner.Resources;window.Owner=owner;window.Icon=owner.Icon;
            var area=SystemParameters.WorkArea;window.Width=Math.Min(window.Width,area.Width);window.Height=Math.Min(window.Height,area.Height);window.MinWidth=Math.Min(window.MinWidth,area.Width);window.MinHeight=Math.Min(window.MinHeight,area.Height);
            Get<Button>("MonitorStart").Click+=async(s,e)=>await Start();
            Get<Button>("MonitorStop").Click+=async(s,e)=>await Stop();
            Get<Button>("MonitorClose").Click+=(s,e)=>window.Close();Get<Button>("ScreenTitleClose").Click+=(s,e)=>window.Close();
            Get<Button>("MonitorRefresh").Click+=async(s,e)=>await Refresh();
            Get<Button>("MonitorQr").Click+=async(s,e)=>await Pair();
            Get<Expander>("MonitorApk").Expanded += async (s,e) => await ShowApkNetworks();
            Get<Expander>("MonitorApk").Collapsed += (s,e) => StopApkDownload();
            Get<Button>("MonitorApkRefresh").Click += async (s,e) => await ShowApkNetworks();
            Get<Button>("MonitorApkInstall").Click += async (s,e) => await InstallMonitorApk();
            Get<Button>("MonitorApkFirewall").Click += async (s,e) => await AllowApkDownload();
            Get<ComboBox>("MonitorApkNetwork").SelectionChanged += async (s,e) =>
            {
                if (!loadingApkNetworks) await SelectApkDownload();
            };
            Get<Button>("MonitorInstallDriver").Click+=async(s,e)=>await InstallDriver();
            Get<Button>("MonitorHideDriver").Click+=async(s,e)=>await HideIdleDriver();
            Get<ComboBox>("MonitorTransport").SelectionChanged+=async(s,e)=>{UpdateHint();if(!busy)await Refresh();};
            window.Loaded+=async(s,e)=>await Refresh();
            timer.Tick+=async(s,e)=>await CheckSession();
            window.Closed += (s,e) => StopApkDownload();
            window.Closing+=async(s,e)=>
            {
                if(allowClose)return;e.Cancel=true;if(busy || installingApk){Status("Дождитесь завершения установки или подключения.");return;}
                await Stop();if(sessionDisplay!=null||displayActivated)return;
                StopApkDownload();
                await apkTransfer;
                await window.Dispatcher.InvokeAsync(new Action(()=>{allowClose=true;window.Close();}));
            };
            if(device!=null)Get<ComboBox>("MonitorTransport").SelectedIndex=device.Wireless?1:0;
            UpdateHint();UpdateDriver();
        }
        public void Show(){window.ShowDialog();}

        sealed class ApkTarget
        {
            internal Device Phone;
            internal ScreenNetwork Network;
            public string Label { get { return Phone != null ? Phone.Model + (Phone.Wireless ? " · через беспроводную отладку" : " · через USB") : "По локальной сети · " + Network; } }
        }

        ApkTarget SelectedApkTarget { get { return Get<ComboBox>("MonitorApkNetwork").SelectedItem as ApkTarget; } }

        async Task ShowApkNetworks()
        {
            int selection = StopApkDownload();
            var status = Get<TextBlock>("MonitorApkStatus");
            Get<Button>("MonitorApkInstall").Visibility = Visibility.Collapsed;
            status.Text = "Ищем подключённый Android и адреса ПК…";
            try
            {
                Device[] phones = new Device[0];
                try
                {
                    var result = await Commands.Run(adb, 5000, "devices", "-l");
                    if (result.ExitCode == 0)
                        phones = Protocol.ParseDevices(result.Output).Where(d => d.Ready &&
                            !d.Serial.StartsWith("emulator-", StringComparison.Ordinal)).OrderBy(d => d.Wireless).ToArray();
                }
                catch (Exception error) { Trace.WriteLine(error); }
                if (selection != apkSelection) return;
                var targets = phones.Select(phone => new ApkTarget { Phone = phone })
                    .Concat(ScreenNetwork.Read().Select(network => new ApkTarget { Network = network })).ToArray();
                var list = Get<ComboBox>("MonitorApkNetwork");
                loadingApkNetworks = true;
                try
                {
                    list.ItemsSource = targets;
                    list.SelectedIndex = targets.Length == 0 ? -1 : 0;
                }
                finally { loadingApkNetworks = false; }
                await SelectApkDownload();
            }
            catch (Exception error) { if (Get<Expander>("MonitorApk").IsExpanded) status.Text = error.Message; }
        }

        async Task SelectApkDownload()
        {
            int selection = StopApkDownload();
            var previous = apkTransfer;
            apkTransfer = ShowApkDownload(previous, selection, SelectedApkTarget);
            await apkTransfer;
        }

        async Task ShowApkDownload(Task previous, int selection, ApkTarget target)
        {
            await previous;
            if (selection != apkSelection) return;
            var status = Get<TextBlock>("MonitorApkStatus");
            Get<Button>("MonitorApkInstall").Visibility = target != null && target.Phone != null ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("MonitorApkFirewall").Visibility = Visibility.Collapsed;
            if (target == null)
            {
                status.Text = "Подключите Android с разрешённой отладкой по USB или Wi-Fi либо подключите ПК к локальной сети. Затем нажмите «Обновить».";
                return;
            }
            MonitorApkDownload download = null;
            int phonePort = 0;
            try
            {
                bool direct = target.Phone != null;
                download = new MonitorApkDownload(direct ? IPAddress.Loopback : target.Network.Address, MonitorApkPath);
                apkDownload = download;
                var url = download.Url;
                if (direct)
                {
                    status.Text = "Открываем передачу APK через отладку Android…";
                    phonePort = await AndroidMonitorClient.OpenDownloadTunnel(adb, target.Phone.Serial, url.Port);
                    url = download.PhoneUrl(phonePort);
                }
                if (selection == apkSelection)
                {
                    Get<Image>("MonitorApkQrImage").Source = QrPairingDialog.Render(url.AbsoluteUri);
                    Get<Border>("MonitorApkCode").Visibility = Visibility.Visible;
                    Get<Button>("MonitorApkFirewall").Visibility = direct ? Visibility.Collapsed : Visibility.Visible;
                    status.Text = direct
                        ? "Отсканируйте код на выбранном телефоне или установите APK кнопкой ниже. Код действует 5 минут."
                        : "Код действует 5 минут. Если телефон не открывает ссылку, нажмите «Разрешить скачивание в Windows».";
                    await download.Completion;
                    if (selection == apkSelection)
                    {
                        StopApkDownload();
                        if (direct) Get<Button>("MonitorApkInstall").Visibility = Visibility.Visible;
                        status.Text = "Код истёк. Нажмите «Обновить», чтобы получить новый.";
                    }
                }
            }
            catch (Exception error)
            {
                if (selection == apkSelection)
                {
                    StopApkDownload();
                    Get<Button>("MonitorApkInstall").Visibility = target.Phone != null ? Visibility.Visible : Visibility.Collapsed;
                    status.Text = "Не удалось открыть скачивание APK. " + error.Message;
                }
            }
            if (phonePort != 0) await AndroidMonitorClient.CloseDownloadTunnel(adb, target.Phone.Serial, phonePort);
        }

        int StopApkDownload()
        {
            if (apkFirewallStop != null) { apkFirewallStop.Set(); apkFirewallStop.Dispose(); apkFirewallStop = null; }
            var download = apkDownload;
            apkDownload = null;
            if (download != null) download.Dispose();
            Get<Image>("MonitorApkQrImage").Source = null;
            Get<Border>("MonitorApkCode").Visibility = Visibility.Collapsed;
            Get<Button>("MonitorApkInstall").Visibility = Visibility.Collapsed;
            Get<Button>("MonitorApkFirewall").Visibility = Visibility.Collapsed;
            return ++apkSelection;
        }

        async Task InstallMonitorApk()
        {
            var target = SelectedApkTarget;
            if (target == null || target.Phone == null || installingApk || busy) return;
            int selection = apkSelection;
            var button = Get<Button>("MonitorApkInstall");
            var status = Get<TextBlock>("MonitorApkStatus");
            installingApk = true;
            button.IsEnabled = false;
            Buttons();
            try
            {
                status.Text = "Устанавливаем Morror Монитор. Если Android спросит разрешение, подтвердите его.";
                await AndroidMonitorClient.Install(adb, target.Phone.Serial, MonitorApkPath);
                if (selection == apkSelection)
                {
                    StopApkDownload();
                    status.Text = "Morror Монитор установлен на " + target.Phone.Model + ".";
                }
            }
            catch (Exception error) { if (selection == apkSelection) status.Text = error.Message; }
            finally { installingApk = false; Buttons(); }
        }

        async Task AllowApkDownload()
        {
            var download = apkDownload;
            if (download == null || apkFirewallStop != null) return;
            var button = Get<Button>("MonitorApkFirewall");
            var status = Get<TextBlock>("MonitorApkStatus");
            string id = Guid.NewGuid().ToString("N");
            button.IsEnabled = false;
            try
            {
                using (var ready = CreateDriverEvent(MonitorApkFirewall.ReadyPrefix + id, EventWaitHandleRights.Modify))
                {
                    apkFirewallStop = CreateDriverEvent(MonitorApkFirewall.StopPrefix + id, EventWaitHandleRights.Synchronize);
                    string arguments = Protocol.Arguments(new[] { "--allow-monitor-apk", id, download.Url.Host, download.Url.Port.ToString() });
                    using (var worker = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments)
                    { UseShellExecute = true, Verb = "runas" }))
                    {
                        bool allowed = await Task.Run(() =>
                        {
                            for (int i = 0; i < 100 && !worker.HasExited; i++)
                                if (ready.WaitOne(100)) return true;
                            return false;
                        });
                        if (apkDownload != download) return;
                        if (!allowed) throw new IOException("Windows не добавила временное разрешение.");
                        status.Text = "Доступ в локальной сети разрешён на время действия кода. Отсканируйте его снова.";
                    }
                }
            }
            catch (Exception error)
            {
                if (apkDownload != download) return;
                if (apkFirewallStop != null) { apkFirewallStop.Set(); apkFirewallStop.Dispose(); apkFirewallStop = null; }
                status.Text = error.Message + " Если Android уже подключён к Morror, выберите его в списке и нажмите «Установить».";
            }
            finally { button.IsEnabled = true; }
        }

        void UpdateHint()
        {
            bool wifi=Get<ComboBox>("MonitorTransport").SelectedIndex==1;
            Get<TextBlock>("MonitorConnectionHint").Text=wifi?"Если Android уже есть в списке, выберите его и включите монитор. Для нового телефона откройте «Беспроводная отладка» в настройках Android.":"Подключите USB, разблокируйте телефон и разрешите отладку. Изображение, звук и касания идут по кабелю.";
            Get<Button>("MonitorQr").Visibility=wifi?Visibility.Visible:Visibility.Collapsed;
        }
        void UpdateDriver()
        {
            Get<TextBlock>("MonitorDriverStatus").Text = VirtualMonitorDriver.Installed
                ? "Драйвер установлен. При запуске второго экрана Windows запросит разрешение включить его."
                : "Установите компонент, чтобы Windows могла создать дополнительный монитор.";
            Get<Button>("MonitorHideDriver").Visibility = VirtualMonitorDriver.Enabled && driverWorker == null && server == null
                ? Visibility.Visible : Visibility.Collapsed;
        }
        void Buttons()
        {
            bool edit=!busy&&!installingApk&&server==null&&sessionDisplay==null&&!displayActivated;
            foreach(string name in new[]{"MonitorTransport","MonitorDevices","MonitorRefresh","MonitorQr","MonitorInstallDriver","MonitorQuality","MonitorFps","MonitorTouch","MonitorAudio","MonitorStart"})Get<UIElement>(name).IsEnabled=edit;
            Get<Button>("MonitorStop").IsEnabled=!busy&&(server!=null||sessionDisplay!=null||displayActivated);
            Get<Button>("MonitorHideDriver").IsEnabled = !busy && server == null && driverWorker == null;
            Get<Button>("MonitorApkInstall").IsEnabled = !installingApk && !busy;
        }
        async Task Refresh(string preferred=null)
        {
            if(busy||server!=null)return;busy=true;Buttons();
            try
            {
                var selected=Get<ComboBox>("MonitorDevices").SelectedItem as Device;
                var result=await Commands.Run(adb,8000,"devices","-l");
                var all=Protocol.ParseDevices(result.Output).Where(d=>d.Ready).ToArray();bool wifi=Get<ComboBox>("MonitorTransport").SelectedIndex==1;
                Get<ComboBox>("MonitorDevices").ItemsSource=all;
                string serial=preferred??(selected==null?(initialDevice==null?null:initialDevice.Serial):selected.Serial);
                Get<ComboBox>("MonitorDevices").SelectedItem=all.FirstOrDefault(d=>d.Serial==serial&&d.Wireless==wifi)??all.FirstOrDefault(d=>d.Wireless==wifi);
                UpdateDriver();
            }
            catch(Exception e){Status(e.Message);}
            finally{busy=false;Buttons();}
        }
        async Task Pair()
        {
            if(busy||server!=null)return;PairingResult result=null;busy=true;Buttons();
            try{result=new QrPairingDialog(window,new WirelessPairing(adb)).Show();}
            catch(Exception e){Status(e.Message);}
            finally{busy=false;Buttons();}
            await Refresh(result!=null&&result.Connected?result.Address:null);
            if (result != null && result.Connected) Status("Телефон подключён. Нажмите «Включить дополнительный монитор».");
        }
        async Task InstallDriver()
        {
            if(busy||server!=null)return;busy=true;Buttons();
            try
            {
                Status("Подтвердите установку подписанного компонента в запросе Windows.");
                using(var preserve=VirtualMonitorDriver.PreservePhysicalModes())
                {
                using(var install=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--install-monitor-driver"){UseShellExecute=true,Verb="runas"}))
                {await Task.Run(()=>install.WaitForExit());if(install.ExitCode!=0)throw new IOException("Установка компонента не завершена. Проверьте сообщение Windows.");}
                for(int i=0;i<30&&!VirtualMonitorDriver.Installed;i++)await Task.Delay(300);
                }
                UpdateDriver();Status("Компонент установлен. Теперь можно включить дополнительный монитор.");
            }
            catch(System.ComponentModel.Win32Exception){Status("Установка отменена или Windows не предоставила права администратора.");}
            catch(Exception e){Status(e.Message);}
            finally{busy=false;Buttons();}
        }
        async Task HideIdleDriver()
        {
            if (busy || server != null || driverWorker != null) return;
            busy = true; Buttons();
            try
            {
                using (var worker = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--hide-idle-monitor")
                { UseShellExecute = true, Verb = "runas" }))
                {
                    await Task.Run(() => worker.WaitForExit());
                    if (worker.ExitCode != 0) throw new IOException("Не удалось скрыть виртуальный монитор.");
                }
                displayActivated = false;
                sessionDisplay = null;
                Status("Виртуальный монитор скрыт из параметров дисплея Windows.");
            }
            catch (Exception error) { Status(error.Message); }
            finally { busy = false; UpdateDriver(); Buttons(); }
        }

        async Task EnableSessionDriver()
        {
            string signal = Guid.NewGuid().ToString("N");
            using (var ready = CreateDriverEvent(@"Local\Morror.MonitorReady." + signal, EventWaitHandleRights.Modify))
            using (var parent = Process.GetCurrentProcess())
            {
                driverStop = CreateDriverEvent(@"Local\Morror.MonitorStopped." + signal, EventWaitHandleRights.Synchronize);
                string arguments = Protocol.Arguments(new[] { "--monitor-driver-session", parent.Id.ToString(),
                    parent.StartTime.ToUniversalTime().Ticks.ToString(), signal });
                driverWorker = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments)
                { UseShellExecute = true, Verb = "runas" });
                bool enabled = await Task.Run(() =>
                {
                    var timer = Stopwatch.StartNew();
                    while (!driverWorker.HasExited && timer.ElapsedMilliseconds < 12000)
                        if (ready.WaitOne(100)) return true;
                    return false;
                });
                if (!enabled) throw new IOException("Windows не включила драйвер виртуального монитора.");
            }
        }

        static EventWaitHandle CreateDriverEvent(string name, EventWaitHandleRights workerRights)
        {
            var security = new EventWaitHandleSecurity();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.AddAccessRule(new EventWaitHandleAccessRule(admins, workerRights, AccessControlType.Allow));
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new EventWaitHandleAccessRule(identity.User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            bool created;
            return new EventWaitHandle(false, EventResetMode.ManualReset, name, out created, security);
        }

        async Task DisableSessionDriver()
        {
            if (driverStop == null) return;
            driverStop.Set();
            try
            {
                if (driverWorker == null) return;
                var worker = driverWorker;
                if (!await Task.Run(() => worker.WaitForExit(12000)) || worker.ExitCode != 0 && VirtualMonitorDriver.Enabled)
                    throw new IOException("Драйвер виртуального монитора остался включён. Нажмите «Скрыть виртуальный монитор».");
            }
            finally
            {
                if (driverWorker != null) driverWorker.Dispose();
                driverWorker = null;
                driverStop.Dispose();
                driverStop = null;
            }
        }
        async Task Start()
        {
            if(busy||server!=null)return;busy=true;Buttons();Exception failure=null;
            try
            {
                activeDevice=Get<ComboBox>("MonitorDevices").SelectedItem as Device;
                if(activeDevice==null)throw new IOException("Подключите телефон по USB или QR-коду и выберите его в списке.");
                bool wifi=Get<ComboBox>("MonitorTransport").SelectedIndex==1;
                if(activeDevice.Wireless!=wifi)throw new IOException("Выберите телефон с соответствующим подключением USB или Wi-Fi.");
                string apk=MonitorApkPath,ffmpeg=Path.Combine(runtime,"ffmpeg.exe");
                if(!File.Exists(apk)||!File.Exists(ffmpeg))throw new IOException("В сборке нет нативного Android-клиента или кодировщика. Распакуйте всю новую сборку Morror.");
                if(!VirtualMonitorDriver.Installed)throw new IOException("Сначала нажмите «Установить компонент монитора». Браузер и ручная трансляция экрана не используются.");
                Status("Определяем размер экрана телефона…");
                var info=await DeviceCapabilities.Read(adb,activeDevice.Serial,CancellationToken.None);
                var resolution=MonitorResolution.ForDevice(info.Width,info.Height,Get<ComboBox>("MonitorQuality").SelectedIndex);
                int fps=Get<ComboBox>("MonitorFps").SelectedIndex==0?30:60;
                if(info.ApiLevel>0&&info.ApiLevel<26)throw new IOException("Morror Монитор требует Android 8.0 или новее. На устройстве Android "+info.AndroidVersion+".");
                Status("Устанавливаем Morror Монитор на Android. Разблокируйте устройство и подтвердите установку, если появится запрос…");
                await AndroidMonitorClient.Install(adb,activeDevice.Serial,apk);
                Status("Подтвердите включение виртуального монитора в запросе Windows.");
                await EnableSessionDriver();
                Status("Настраиваем виртуальный монитор Windows: "+resolution.Width+" × "+resolution.Height+"…");
                displayActivated=true;
                ScreenChoice screen=null;
                using(var preserve=VirtualMonitorDriver.PreservePhysicalModes())
                {
                await EnsureResolution(resolution.Width,resolution.Height,fps);
                VirtualMonitorDriver.Attach(resolution.Width,resolution.Height,fps);
                for(int i=0;i<30;i++){screen=VirtualMonitorDriver.Screens().FirstOrDefault();if(screen!=null)break;await Task.Delay(200);}
                if(screen==null)throw new IOException("Не найден виртуальный монитор. Физические экраны передаваться не будут.");
                sessionDisplay=screen.DeviceName;
                VirtualMonitorDriver.SetMode(screen.DeviceName,resolution.Width,resolution.Height,fps);
                for(int i=0;i<30;i++)
                {
                    await Task.Delay(100);screen=VirtualMonitorDriver.Screens().FirstOrDefault(s=>s.DeviceName==sessionDisplay);
                    if(screen!=null&&screen.Bounds.Width==resolution.Width&&screen.Bounds.Height==resolution.Height)break;
                }
                if(screen==null||screen.Bounds.Width!=resolution.Width||screen.Bounds.Height!=resolution.Height)
                    throw new IOException("Windows не подтвердила размер "+resolution.Width+" × "+resolution.Height+". Текущий размер: "+(screen==null?"монитор отключён":screen.Bounds.Width+" × "+screen.Bounds.Height)+". Morror Монитор уже установлен на Android. Попробуйте включить монитор повторно.");
                }
                Status("Проверяем H.264-кодировщик…");string codec=await NativeMonitorEncoder.Select(ffmpeg,CancellationToken.None,resolution.Width,resolution.Height,fps);
                var capture=await NativeMonitorEncoder.SelectCapture(ffmpeg,codec,screen,resolution.Width,resolution.Height,fps);
                bool touch=Get<CheckBox>("MonitorTouch").IsChecked==true;if(touch)injection=new ScreenTouchInjection(screen.Bounds);
                server=new NativeMonitorServer(ffmpeg,codec,screen,resolution.Width,resolution.Height,fps,Get<CheckBox>("MonitorAudio").IsChecked==true,async frame=>
                {
                    if(injection!=null)await window.Dispatcher.InvokeAsync(()=>
                    {
                        if (!VirtualMonitorDriver.HasBounds(screen)) throw new IOException("Расположение экрана изменилось. Перезапустите монитор.");
                        if (injection != null) injection.Apply(frame);
                    });
                },async()=>{if(injection!=null)await window.Dispatcher.InvokeAsync(()=>injection.Cancel());},false,capture);
                server.Start();
                var reverse=await Commands.Run(adb,8000,"-s",activeDevice.Serial,"reverse","--no-rebind","tcp:"+server.Port,"tcp:"+server.Port);
                if(reverse.ExitCode!=0)throw new IOException("Не удалось создать туннель монитора. "+reverse.Combined);reversePort=server.Port;
                var launch=await Commands.Run(adb,10000,"-s",activeDevice.Serial,"shell","am","start","-n","ru.morror.monitor/.MonitorActivity","--ei","port",server.Port.ToString(),"--es","token",server.Token);
                if(launch.ExitCode!=0||launch.Combined.Contains("Error"))throw new IOException("Не удалось открыть Morror Монитор на телефоне.");
                clientDeadline=DateTime.UtcNow.AddSeconds(20);timer.Start();Status("Монитор включён. Перетащите нужное окно за край основного экрана Windows.");
            }
            catch(Exception e){failure=e;}
            if(failure!=null){try{await Cleanup();}catch(Exception cleanup){failure=new IOException(failure.Message+"\n"+cleanup.Message);}Status(failure.Message);}
            busy=false;Buttons();
            if (server != null) await EndOnDisconnect(server);
        }
        async Task EndOnDisconnect(NativeMonitorServer session)
        {
            await session.Completion;
            if (server == session) await CheckSession();
        }
        async Task CheckSession()
        {
            if(server==null||busy)return;
            bool ended=server.Completion.IsCompleted;
            if(!ended&&(server.HasClient||DateTime.UtcNow<clientDeadline)){Status(server.Status);return;}
            string message=ended?"Устройство отключено. "+server.Status:"Android-клиент не подключился за 20 секунд. Разблокируйте устройство и повторите запуск.";
            busy=true;Buttons();
            try{await Cleanup();Status(message+" Виртуальный монитор отключён.");}
            catch(Exception error){Status(message+"\n"+error.Message);}
            finally{busy=false;Buttons();}
        }
        async Task EnsureResolution(int width,int height,int fps)
        {
            if(VirtualMonitorDriver.SupportsMode(width,height,fps))return;
            Status("Добавляем разрешение "+width+" × "+height+" для этого устройства. Подтвердите запрос Windows; для уже добавленных размеров он больше не нужен.");
            try
            {
                using(var configure=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,Protocol.Arguments(new[]{"--configure-monitor-mode",width.ToString(),height.ToString(),fps.ToString()})){UseShellExecute=true,Verb="runas"}))
                {await Task.Run(()=>configure.WaitForExit());if(configure.ExitCode!=0)throw new IOException("Windows не добавила разрешение устройства. Проверьте сообщение компонента монитора.");}
            }
            catch(System.ComponentModel.Win32Exception){throw new IOException("Добавление разрешения отменено. Разрешите изменение компонента монитора для работы на весь экран устройства.");}
            for(int i=0;i<60;i++)
            {
                try{if(VirtualMonitorDriver.SupportsMode(width,height,fps))return;}catch(IOException){}
                await Task.Delay(200);
            }
            throw new IOException("Драйвер пока не сообщил новый размер экрана. Перезагрузите ПК и повторите запуск.");
        }
        async Task Cleanup()
        {
            timer.Stop();Exception failure=null;
            var ending=server;server=null;
            try{if(ending!=null)await ending.Stop();}catch(Exception error){failure=error;}
            try{if(injection!=null){injection.Cancel();await Task.Delay(80);injection.Dispose();}}catch(Exception error){failure=error;}finally{injection=null;}
            // A failed attach can leave an active path before sessionDisplay is known.
            string display=sessionDisplay;
            try
            {
                if(display!=null||displayActivated)
                {
                    for(int attempt=0;attempt<3;attempt++)
                    {
                        if(display==null)VirtualMonitorDriver.DetachIdleScreens();
                        else VirtualMonitorDriver.Detach(display);
                        if(!VirtualMonitorDriver.Screens().Any(s=>display==null||s.DeviceName==display))break;
                        if(attempt==2)throw new IOException("Windows оставила виртуальный монитор активным. Нажмите «Стоп», чтобы повторить отключение.");
                        await Task.Delay(200);
                    }
                }
                displayActivated=false;sessionDisplay=null;
            }
            catch(Exception error){sessionDisplay=display;failure=error;}
            try
            {
                await DisableSessionDriver();
                if (!VirtualMonitorDriver.Enabled)
                {
                    displayActivated = false;
                    sessionDisplay = null;
                }
            }
            catch (Exception error) { failure = error; }
            UpdateDriver();
            if(reversePort!=0&&activeDevice!=null){int port=reversePort;reversePort=0;try{await Commands.Run(adb,5000,"-s",activeDevice.Serial,"reverse","--remove","tcp:"+port);}catch(Exception){}}
            if(failure!=null)throw failure;
        }
        async Task Stop(){if(busy)return;busy=true;Buttons();try{await Cleanup();Status("Монитор остановлен. Туннель и сенсорное управление отключены.");}catch(Exception error){Status(error.Message);}finally{busy=false;Buttons();}}
    }
}
