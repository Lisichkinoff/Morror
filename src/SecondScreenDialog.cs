using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
            Get<Button>("MonitorInstallDriver").Click+=async(s,e)=>await InstallDriver();
            Get<ComboBox>("MonitorTransport").SelectionChanged+=async(s,e)=>{UpdateHint();if(!busy)await Refresh();};
            window.Loaded+=async(s,e)=>await Refresh();
            timer.Tick+=(s,e)=>{if(server!=null&&!busy)Status(server.Status);};
            window.Closing+=async(s,e)=>
            {
                if(allowClose)return;e.Cancel=true;if(busy){Status("Дождитесь завершения установки или подключения.");return;}
                await Stop();await window.Dispatcher.InvokeAsync(new Action(()=>{allowClose=true;window.Close();}));
            };
            if(device!=null)Get<ComboBox>("MonitorTransport").SelectedIndex=device.Wireless?1:0;
            UpdateHint();UpdateDriver();
        }
        public void Show(){window.ShowDialog();}
        void UpdateHint()
        {
            bool wifi=Get<ComboBox>("MonitorTransport").SelectedIndex==1;
            Get<TextBlock>("MonitorConnectionHint").Text=wifi?"Android 11+: отладка по Wi-Fi → сопряжение по QR-коду. Используется тот же способ, что для трансляции телефона.":"Подключите USB, разблокируйте телефон и разрешите отладку. Изображение, звук и касания идут по кабелю.";
            Get<Button>("MonitorQr").Visibility=wifi?Visibility.Visible:Visibility.Collapsed;
        }
        void UpdateDriver(){Get<TextBlock>("MonitorDriverStatus").Text=VirtualMonitorDriver.Installed?"Драйвер найден. При запуске Morror подключит виртуальный монитор к рабочему столу Windows.":"Один раз установите компонент, чтобы Windows могла создать настоящий дополнительный монитор.";}
        void Buttons()
        {
            bool edit=!busy&&server==null;
            foreach(string name in new[]{"MonitorTransport","MonitorDevices","MonitorRefresh","MonitorQr","MonitorInstallDriver","MonitorQuality","MonitorFps","MonitorTouch","MonitorAudio","MonitorStart"})Get<UIElement>(name).IsEnabled=edit;
            Get<Button>("MonitorStop").IsEnabled=!busy&&server!=null;
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
        }
        async Task InstallDriver()
        {
            if(busy||server!=null)return;busy=true;Buttons();
            try
            {
                Status("Подтвердите установку подписанного компонента в запросе Windows.");
                bool alreadyInstalled=VirtualMonitorDriver.Installed;
                using(var install=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--install-monitor-driver"){UseShellExecute=true,Verb="runas"}))
                {await Task.Run(()=>install.WaitForExit());if(install.ExitCode!=0)throw new IOException("Установка компонента не завершена. Проверьте сообщение Windows.");}
                for(int i=0;i<30&&!VirtualMonitorDriver.Installed;i++)await Task.Delay(300);
                if(!alreadyInstalled)foreach(var screen in VirtualMonitorDriver.Screens())VirtualMonitorDriver.Detach(screen.DeviceName);
                UpdateDriver();Status("Компонент установлен. Теперь можно включить дополнительный монитор.");
            }
            catch(System.ComponentModel.Win32Exception){Status("Установка отменена или Windows не предоставила права администратора.");}
            catch(Exception e){Status(e.Message);}
            finally{busy=false;Buttons();}
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
                string apk=Path.Combine(runtime,"MorrorMonitor.apk"),ffmpeg=Path.Combine(runtime,"ffmpeg.exe");
                if(!File.Exists(apk)||!File.Exists(ffmpeg))throw new IOException("В сборке нет нативного Android-клиента или кодировщика. Распакуйте всю новую сборку Morror.");
                if(!VirtualMonitorDriver.Installed)throw new IOException("Сначала нажмите «Установить компонент монитора». Браузер и ручная трансляция экрана не используются.");
                Status("Определяем размер экрана телефона…");
                var info=await DeviceCapabilities.Read(adb,activeDevice.Serial,CancellationToken.None);
                var resolution=MonitorResolution.ForDevice(info.Width,info.Height,Get<ComboBox>("MonitorQuality").SelectedIndex);
                int fps=Get<ComboBox>("MonitorFps").SelectedIndex==0?30:60;
                displayActivated=VirtualMonitorDriver.Screens().Length==0;
                await EnsureResolution(resolution.Width,resolution.Height,fps);
                if(!VirtualMonitorDriver.Attach(resolution.Width,resolution.Height,fps))throw new IOException("Windows не подключила виртуальный экран. Проверьте настройки дисплеев или перезагрузите ПК после установки компонента.");
                ScreenChoice screen=null;
                for(int i=0;i<30;i++){screen=VirtualMonitorDriver.Screens().FirstOrDefault();if(screen!=null)break;await Task.Delay(200);}
                if(screen==null)throw new IOException("Не найден виртуальный монитор. Физические экраны передаваться не будут.");
                sessionDisplay=screen.DeviceName;
                VirtualMonitorDriver.SetMode(screen.DeviceName,resolution.Width,resolution.Height,fps);
                for(int i=0;i<30;i++)
                {
                    await Task.Delay(100);screen=VirtualMonitorDriver.Screens().FirstOrDefault(s=>s.DeviceName==sessionDisplay);
                    if(screen!=null&&screen.Bounds.Width==resolution.Width&&screen.Bounds.Height==resolution.Height)break;
                }
                if(screen==null||screen.Bounds.Width!=resolution.Width||screen.Bounds.Height!=resolution.Height)throw new IOException("Windows не подтвердила выбранный размер монитора. Попробуйте перезапустить сеанс.");
                Status("Проверяем H.264-кодировщик…");string codec=await NativeMonitorEncoder.Select(ffmpeg,CancellationToken.None);
                bool touch=Get<CheckBox>("MonitorTouch").IsChecked==true;if(touch)injection=new ScreenTouchInjection(screen.Bounds);
                server=new NativeMonitorServer(ffmpeg,codec,screen,resolution.Width,resolution.Height,fps,Get<CheckBox>("MonitorAudio").IsChecked==true,async frame=>
                {
                    if(!VirtualMonitorDriver.Screens().Any(s=>s.DeviceName==screen.DeviceName&&s.Bounds==screen.Bounds))throw new IOException("Расположение экрана изменилось. Перезапустите монитор.");
                    if(injection!=null)await window.Dispatcher.InvokeAsync(()=>injection.Apply(frame));
                },async()=>{if(injection!=null)await window.Dispatcher.InvokeAsync(()=>injection.Cancel());});
                server.Start();
                Status("Устанавливаем нативный клиент Morror на телефон…");var install=await Commands.Run(adb,60000,"-s",activeDevice.Serial,"install","-r",apk);
                if(install.ExitCode!=0||!install.Combined.Contains("Success"))throw new IOException("Android не установил Morror Монитор. Разрешите установку через USB на телефоне. "+install.Combined);
                var reverse=await Commands.Run(adb,8000,"-s",activeDevice.Serial,"reverse","--no-rebind","tcp:"+server.Port,"tcp:"+server.Port);
                if(reverse.ExitCode!=0)throw new IOException("Не удалось создать туннель монитора. "+reverse.Combined);reversePort=server.Port;
                var launch=await Commands.Run(adb,10000,"-s",activeDevice.Serial,"shell","am","start","-n","ru.morror.monitor/.MonitorActivity","--ei","port",server.Port.ToString(),"--es","token",server.Token);
                if(launch.ExitCode!=0||launch.Combined.Contains("Error"))throw new IOException("Не удалось открыть Morror Монитор на телефоне.");
                timer.Start();Status("Монитор включён. Перетащите нужное окно за край основного экрана Windows.");
            }
            catch(Exception e){failure=e;}
            if(failure!=null){await Cleanup();Status(failure.Message);}
            busy=false;Buttons();
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
            timer.Stop();if(server!=null){await server.Stop();server=null;}
            if(injection!=null){injection.Cancel();await Task.Delay(80);injection.Dispose();injection=null;}
            if(reversePort!=0&&activeDevice!=null){int port=reversePort;reversePort=0;try{await Commands.Run(adb,5000,"-s",activeDevice.Serial,"reverse","--remove","tcp:"+port);}catch(Exception){}}
            if(displayActivated&&sessionDisplay!=null)VirtualMonitorDriver.Detach(sessionDisplay);displayActivated=false;sessionDisplay=null;
        }
        async Task Stop(){if(busy)return;busy=true;Buttons();try{await Cleanup();Status("Монитор остановлен. Туннель и сенсорное управление отключены.");}finally{busy=false;Buttons();}}
    }
}
