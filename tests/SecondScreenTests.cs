using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Xml;
using Morror;
using Point=System.Drawing.Point;
using Size=System.Drawing.Size;

class SecondScreenTests
{
    static int checks;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    static ScreenContact Contact(){return new ScreenContact{id=1,x=.5,y=.5};}
    static ScreenTouchFrame Frame(string type,long sequence){return new ScreenTouchFrame{type=type,sequence=sequence,generation=0,points=type=="end"||type=="cancel"?new ScreenContact[0]:new[]{Contact()}};}
    static bool Nal(byte[] bytes,int type){return MonitorVideoReader.HasNal(bytes,type);}
    static async Task Until(Func<bool> condition,string message){var clock=Stopwatch.StartNew();while(!condition()){if(clock.ElapsedMilliseconds>10000)throw new TimeoutException(message);await Task.Delay(30);}}
    sealed class FragmentedStream : MemoryStream
    {
        internal FragmentedStream(byte[] bytes) : base(bytes) { }
        public override Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken cancellation)
        { return base.ReadAsync(bytes,offset,Math.Min(2,count),cancellation); }
    }
    static async Task Protocol()
    {
        var header=MonitorWire.Header(16,5,0x0102030405060708);Check(MonitorWire.Length(header)==14&&header[4]==16&&header[5]==1&&header[12]==8,"Native wire uses network byte order and explicit packet bounds");
        bool invalid=false;try{MonitorWire.Length(new byte[]{255,255,255,255});}catch(IOException){invalid=true;}Check(invalid,"Reject oversized native packets before allocation");
        var state=new ScreenTouchState();Check(!state.Accept(Frame("move",1))&&state.Accept(Frame("start",1))&&state.Accept(Frame("move",2))&&state.Accept(Frame("end",3)),"Native touch requires ordered down/move/up");
        Check(!state.Accept(Frame("start",2)),"Reject replayed touch packets");
        Check(SecondScreenProtocol.MapToScreen(new ScreenContact{x=1,y=1},new Rectangle(-1920,0,1920,1080))==new Point(-1,1079),"Native touch maps negative virtual-monitor coordinates");
        byte[] idr={0,0,0,1,0x67,0x42,0,0,1,0x68,0x12,0,0,1,0x65,0x42,0x42};
        byte[] delta={0,0,1,0x41};
        byte[] sample;
        using(var bytes=new MemoryStream())
        using(var writer=new BinaryWriter(bytes))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(uint.MaxValue);
            writer.Write(Encoding.ASCII.GetBytes("AVI LIST"));writer.Write(uint.MaxValue);
            writer.Write(Encoding.ASCII.GetBytes("movi00dc"));writer.Write(idr.Length);writer.Write(idr);writer.Write((byte)0);
            sample=bytes.ToArray();
        }
        using(var stream=new FragmentedStream(sample))
        {
            var reader=new MonitorVideoReader(stream);
            var frame=await reader.ReadFrame(CancellationToken.None);
            Check(frame.SequenceEqual(idr)&&Nal(frame,5)&&Nal(frame,7)&&Nal(frame,8),"An AVI frame survives fragmented reads and is sent without waiting for the next frame");
        }
        using(var queue=new MonitorVideoQueue())
        using(var cancel=new CancellationTokenSource(2000))
        {
            queue.Add(idr,10);queue.Add(delta,20);queue.Add(delta,30);queue.Add(delta,40);
            queue.Add(delta,50);queue.Add(idr,60);
            var sent=new List<long>();
            try{await queue.Send((frame,pts)=>{sent.Add(pts);cancel.Cancel();return Task.FromResult(0);},cancel.Token);}
            catch(OperationCanceledException){}
            Check(sent.SequenceEqual(new[]{60L}),"A congested video queue discards stale frames and resumes at the next IDR");
        }
        byte[] mono=BitConverter.GetBytes((short)-16384);byte[] stereo=WasapiMonitorAudio.StereoFloat(mono,1,1,16,false,2);
        Check(BitConverter.ToSingle(stereo,0)==-.5f&&BitConverter.ToSingle(stereo,4)==-.5f,"System audio converts PCM16 mono to stereo float");
        var pcm24=new byte[]{0,0,0x80,0xff,0xff,0x7f};stereo=WasapiMonitorAudio.StereoFloat(pcm24,1,2,24,false,6);
        Check(BitConverter.ToSingle(stereo,0)==-1&&BitConverter.ToSingle(stereo,4)>.999f,"System audio preserves signed PCM24 range");
    }
    static void Resolution()
    {
        Check(MonitorResolution.ForDevice(2136,3200,2)==new Size(3200,2136),"Auto preserves Xiaomi Pad 7's panel dimensions");
        Check(MonitorResolution.ForDevice(2136,3200,1)==new Size(1920,1282),"Full HD keeps Xiaomi Pad 7's aspect ratio with even encoder dimensions");
        Check(MonitorResolution.ForDevice(1080,2400,2)==new Size(2400,1080),"Auto uses the full native Pixel 8 panel instead of forced 16:9");
        Check(MonitorResolution.ForDevice(1080,2400,1)==new Size(1920,864),"Full HD budget keeps the phone aspect ratio");
        Check(MonitorResolution.ForDevice(1080,2400,0)==new Size(1280,576),"HD budget keeps the phone aspect ratio");
        Check(MonitorResolution.ForDevice(1600,2560,2)==new Size(2560,1600),"Auto preserves a tablet's 16:10 panel");
        Check(MonitorResolution.ForDevice(2048,1536,2)==new Size(2048,1536),"Auto preserves a 4:3 tablet panel");
        Check(MonitorResolution.ForDevice(1920,1080,1)==new Size(1920,1080),"A 16:9 device retains its existing dimensions");
        var odd=MonitorResolution.ForDevice(1079,2399,2);Check(odd.Width%2==0&&odd.Height%2==0,"Odd Android dimensions remain valid for H.264 YUV420");
        bool invalid=false;try{MonitorResolution.ForDevice(0,2400,2);}catch(ArgumentException){invalid=true;}Check(invalid,"Unknown device size does not silently fall back to the wrong ratio");
        var effective=DeviceCapabilities.Parse("","Physical size: 1080x2400\nOverride size: 900x2000","");
        Check(MonitorResolution.ForDevice(effective.Width,effective.Height,2)==new Size(2000,900),"Effective Android resolution overrides take precedence");
        var xml=new XmlDocument();xml.LoadXml("<vdd_settings><!--keep--><monitors><count>2</count></monitors><gpu><friendlyname>custom</friendlyname></gpu><global><g_refresh_rate>60</g_refresh_rate></global><resolutions><resolution><width>1920</width><height>1080</height><refresh_rate>30</refresh_rate></resolution></resolutions><options><HDRPlus>true</HDRPlus></options></vdd_settings>");
        Check(MonitorResolution.AddMode(xml,2400,1080,60),"Register a native phone mode in driver configuration");
        Check(xml.SelectNodes("/vdd_settings/resolutions/resolution").Count==2&&xml.SelectSingleNode("/vdd_settings/monitors/count").InnerText=="2"&&xml.SelectSingleNode("/vdd_settings/gpu/friendlyname").InnerText=="custom"&&xml.SelectSingleNode("/vdd_settings/options/HDRPlus").InnerText=="true"&&xml.OuterXml.Contains("<!--keep-->"),"Adding a mode preserves existing display counts, modes, GPU, options and comments");
        Check(!MonitorResolution.AddMode(xml,2400,1080,60)&&xml.SelectNodes("/vdd_settings/resolutions/resolution").Count==2,"Repeated connection does not duplicate an already registered mode");
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Resolution-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,"vdd_settings.xml");File.WriteAllText(path,xml.OuterXml);
        Check(MonitorResolution.UpdateConfiguration(path,1280,576,30)&&Directory.GetFiles(directory,"*.bak").Length==1,"Driver mode update is atomic and retains an original backup");
        Check(!MonitorResolution.UpdateConfiguration(path,1280,576,30)&&Directory.GetFiles(directory,"*.bak").Length==1,"Unchanged driver configuration is not rewritten");
    }
    static void PhysicalRefreshRate()
    {
        Check(Marshal.SizeOf(typeof(DisplayTopology.DisplayPath))==72&&Marshal.SizeOf(typeof(DisplayTopology.DisplayMode))==64,"CCD interop structures match the native Windows ABI");
        var physicalPath=new DisplayTopology.DisplayPath{flags=1,source=new DisplayTopology.Source{adapter=new DisplayTopology.Luid{low=1},mode=0},target=new DisplayTopology.Target{mode=1,available=1,refresh=new DisplayTopology.Rational{numerator=239760,denominator=1000}}};
        var virtualPath=new DisplayTopology.DisplayPath{source=new DisplayTopology.Source{adapter=new DisplayTopology.Luid{low=2},mode=uint.MaxValue},target=new DisplayTopology.Target{mode=uint.MaxValue,available=1}};
        var sourceMode=new DisplayTopology.DisplayMode{type=1,adapter=physicalPath.source.adapter,data=new DisplayTopology.ModeData{source=new DisplayTopology.SourceMode{width=2560,height=1440,pixelFormat=4}}};
        var targetMode=new DisplayTopology.DisplayMode{type=2,data=new DisplayTopology.ModeData{timing0=12345678,timing1=239760,timing2=1000}};
        var topology=new DisplayTopology.Snapshot{paths=new[]{physicalPath,virtualPath},modes=new[]{sourceMode,targetMode}};
        var attached=DisplayTopology.CreateAttachment(topology,p=>p.source.adapter.low==2,2400,1080,60,2560,0);
        Check(attached.paths.Length==2&&attached.paths[0].Equals(physicalPath)&&attached.modes[0].Equals(sourceMode)&&attached.modes[1].Equals(targetMode),"Attaching an IDD display preserves exact physical timings, fractional refresh rates and path order");
        var configured=attached.modes[(int)attached.paths[1].source.mode].data.source;
        Check(configured.width==2400&&configured.height==1080&&configured.x==2560&&attached.paths[1].target.mode==uint.MaxValue,"CCD supplies the requested virtual source and lets Windows choose only its missing target timing");
        var changed=DisplayTopology.CreateAttachment(attached,p=>p.source.adapter.low==2,1920,864,60,2560,0);
        var detached=DisplayTopology.CreateDetachment(changed,p=>p.source.adapter.low==2);
        Check(detached.paths.SequenceEqual(new[]{physicalPath})&&detached.modes[1].Equals(targetMode),"Detaching a leftover virtual path preserves the exact physical display path and timings");
        Check(changed.paths.Length==2&&changed.modes.Length==attached.modes.Length&&changed.modes[(int)changed.paths[1].source.mode].data.source.width==1920,"Changing an active virtual mode does not create duplicate source-mode records");
        virtualPath.target.available=0;
        Check(DisplayTopology.CreateAttachment(new DisplayTopology.Snapshot{paths=new[]{physicalPath,virtualPath},modes=topology.modes},p=>p.source.adapter.low==2,2400,1080,60,2560,0)==null,"Unavailable virtual targets are not activated and unrelated displays are not substituted");
        var original=new VirtualMonitorDriver.Mode{x=0,y=0,width=2560,height=1440,bits=32,frequency=240};
        var current=original;int restores=0;
        var saved=new Dictionary<string,VirtualMonitorDriver.Mode>{{"physical-test",original}};
        using(var guard=new VirtualMonitorDriver.PhysicalModes(saved,device=>current,(device,mode)=>{current=mode;restores++;}))
        {current.frequency=60;}
        Check(current.frequency==240&&restores==1,"Virtual display changes restore the physical monitor's 240 Hz refresh rate");
        using(var guard=new VirtualMonitorDriver.PhysicalModes(saved,device=>current,(device,mode)=>{current=mode;restores++;})){}
        Check(restores==1,"Unchanged physical displays do not receive redundant mode changes");
        using(var guard=new VirtualMonitorDriver.PhysicalModes(saved,device=>current,(device,mode)=>{current=mode;}))
        {current.x=-2560;current.width=1920;current.orientation=1;}
        Check(current.x==0&&current.width==2560&&current.orientation==0,"Mode preservation also restores physical display geometry and orientation");
        bool rejected=false;
        try{current.frequency=60;using(var guard=new VirtualMonitorDriver.PhysicalModes(saved,device=>current,(device,mode)=>{})){};}
        catch(IOException){rejected=true;}
        Check(rejected,"Windows failing to restore a physical mode is reported instead of silently leaving it at 60 Hz");
    }
    static void FreshScreenGeometry()
    {
        var cached=System.Windows.Forms.Screen.AllScreens.First();
        var actual=ScreenChoice.Read().Single(s=>s.DeviceName==cached.DeviceName);
        var field=typeof(System.Windows.Forms.Screen).GetField("bounds",BindingFlags.Instance|BindingFlags.NonPublic);
        if(field==null)throw new Exception("Cannot simulate stale WinForms screen bounds.");
        var saved=field.GetValue(cached);
        try
        {
            field.SetValue(cached,new Rectangle(actual.Bounds.X,actual.Bounds.Y,320,200));
            Check(cached.Bounds.Width==320,"Regression fixture reproduces stale WinForms display geometry");
            Check(ScreenChoice.Read().Single(s=>s.DeviceName==cached.DeviceName).Bounds==actual.Bounds,"Display validation reads live Windows pixels despite stale WinForms bounds");
        }
        finally{field.SetValue(cached,saved);}
    }
    static void InstallationErrors()
    {
        Check(AndroidMonitorClient.InstallationError("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]").Contains("другой подписью"),"APK signing conflict explains how to replace the old receiver");
        Check(AndroidMonitorClient.InstallationError("Failure [INSTALL_FAILED_USER_RESTRICTED]").Contains("Xiaomi"),"Restricted APK installation offers Xiaomi-specific guidance");
        Check(AndroidMonitorClient.InstallationError("Failure [INSTALL_FAILED_VERSION_DOWNGRADE]").Contains("Обновите Morror на ПК"),"APK downgrade does not automatically uninstall the newer app");
        Check(AndroidMonitorClient.InstallationError("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]").Contains("свободного места"),"APK storage failure has an actionable explanation");
        Check(AndroidMonitorClient.InstallationError("Failure [INSTALL_FAILED_OLDER_SDK]").Contains("Android 8.0"),"Unsupported Android version is identified");
        Check(AndroidMonitorClient.InstallationError("unexpected adb failure").Contains("unexpected adb failure"),"Unknown APK failures preserve the original Android diagnostic");
    }
    static string FixtureApk { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Android fixture","Morror Монитор.apk"); } }
    static async Task InstallationCommands()
    {
        string fixture=Assembly.GetExecutingAssembly().Location;
        await AndroidMonitorClient.Install(fixture,"install-success",FixtureApk);
        Check(true,"APK install targets the selected device with -r and preserves a Unicode path with spaces");
        int phonePort=await AndroidMonitorClient.OpenDownloadTunnel(fixture,"download-phone",51423);
        Check(phonePort==51424,"APK download asks ADB to allocate a free port on the selected Android");
        await AndroidMonitorClient.CloseDownloadTunnel(fixture,"download-phone",phonePort);
        bool badPort=false;
        try{await AndroidMonitorClient.OpenDownloadTunnel(fixture,"invalid-download-port",51423);}catch(IOException){badPort=true;}
        Check(badPort,"ADB port allocation must return a valid port before publishing a download QR");
        bool rejected=false;try{await AndroidMonitorClient.Install(fixture,"install-restricted",FixtureApk);}catch(IOException error){rejected=error.Message.Contains("Xiaomi")&&error.Message.Contains("INSTALL_FAILED_USER_RESTRICTED");}
        Check(rejected,"A failed ADB installation propagates the Android reason and installation guidance");
        rejected=false;try{await AndroidMonitorClient.Install(fixture,"install-unconfirmed",FixtureApk);}catch(IOException){rejected=true;}
        Check(rejected,"A zero ADB exit code without the Success line does not confirm APK installation");
        string encoder=await NativeMonitorEncoder.Select(fixture,CancellationToken.None,3200,2136,60);
        Check(encoder=="nvenc","Encoder probing uses the tablet's real dimensions and bitrate instead of rejecting NVENC with a 128x128 frame");
    }
    static async Task Send(Stream stream,int type,object value)
    {
        byte[] bytes=value==null?new byte[0]:Encoding.UTF8.GetBytes(SecondScreenProtocol.Json().Serialize(value)),head=MonitorWire.Header(type,bytes.Length,0);
        await stream.WriteAsync(head,0,head.Length);if(bytes.Length>0)await stream.WriteAsync(bytes,0,bytes.Length);
    }
    static async Task Network(string executable,string codec)
    {
        int input=0,released=0;var screen=new ScreenChoice{Name="Synthetic",Bounds=new Rectangle(0,0,640,360)};
        var server=new NativeMonitorServer(executable,codec,screen,640,360,30,false,f=>{input++;return Task.FromResult(0);},()=>{released++;return Task.FromResult(0);},true);
        server.Start();
        using(var bad=new TcpClient()){
            await bad.ConnectAsync(IPAddress.Loopback,server.Port);var s=bad.GetStream();byte[] auth=Encoding.ASCII.GetBytes("MRRMN001"+new string('0',64));await s.WriteAsync(auth,0,auth.Length);
            await Until(()=>server.Status.Contains("ключ"),"Invalid native client was not rejected");
            Check(input==0,"Reject an unauthenticated native Android client");
        }
        await Task.Delay(100);
        using(var client=new TcpClient())using(var timeout=new CancellationTokenSource(15000)){
            await client.ConnectAsync(IPAddress.Loopback,server.Port);var stream=client.GetStream();
            using(timeout.Token.Register(()=>client.Close())){
                byte[] auth=Encoding.ASCII.GetBytes("MRRMN001"+server.Token);await stream.WriteAsync(auth,0,auth.Length);
                int frames=0;var output=new MemoryStream();
                while(frames<8){int length=MonitorWire.Length(await MonitorWire.Read(stream,4,timeout.Token));byte[] packet=await MonitorWire.Read(stream,length,timeout.Token);
                    if(packet[0]==0){var info=SecondScreenProtocol.Json().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(packet,9,packet.Length-9));Check(Convert.ToInt32(info["width"])==640&&Convert.ToInt32(info["height"])==360,"Native client receives actual video dimensions");}
                    else if(packet[0]==1){byte[] frame=packet.Skip(9).ToArray();if(frames==0)Check(Nal(frame,7)&&Nal(frame,8)&&Nal(frame,5),"Native stream starts with SPS/PPS and an independently decodable keyframe");output.Write(frame,0,frame.Length);frames++;}
                }
                Check(frames==8,"Native server sends H.264 frames over an authenticated TCP tunnel");
                await Send(stream,16,Frame("start",1));await Send(stream,16,Frame("move",2));await Send(stream,16,Frame("end",3));await Until(()=>input==3,"Native touch did not reach input sink");Check(input==3,"Native Android touch path delivers down/move/up without mouse packets");
                string sample=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-sample.h264");File.WriteAllBytes(sample,output.ToArray());
                var decoded=await Commands.Run(executable,10000,"-hide_banner","-loglevel","error","-i",sample,"-f","framemd5","-");
                Check(decoded.ExitCode==0&&decoded.Output.Split('\n').Count(line=>line.StartsWith("0,"))>=8,"Independent decoder verifies all native H.264 sample frames");
            }
        }
        Check(await Task.WhenAny(server.Completion,Task.Delay(5000))==server.Completion,"Receiver disconnect completes the owned monitor session without waiting for a reconnect");
        await server.Stop();Check(released>0,"Native session teardown releases touches and stops the encoder");
        bool refused=false;
        using(var probe=new TcpClient())
        {try{await probe.ConnectAsync(IPAddress.Loopback,server.Port);}catch(SocketException){refused=true;}}
        Check(refused,"Stopping native monitor refuses new connections on its listening port");
    }
    static async Task DisconnectCleanup(string executable,string codec)
    {
        var screen=new ScreenChoice{Name="Synthetic",Bounds=new Rectangle(0,0,640,360)};
        var server=new NativeMonitorServer(executable,codec,screen,640,360,30,false,f=>Task.FromResult(0),()=>Task.FromResult(0),true);
        server.Start();
        using(var client=new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback,server.Port);var stream=client.GetStream();
            byte[] auth=Encoding.ASCII.GetBytes("MRRMN001"+server.Token);await stream.WriteAsync(auth,0,auth.Length);
            var drain=Task.Run(async()=>{var buffer=new byte[32768];try{while(await stream.ReadAsync(buffer,0,buffer.Length)>0){}}catch(IOException){}catch(ObjectDisposedException){}});
            Check(await Task.WhenAny(server.Completion,Task.Delay(9000))==server.Completion,"Missing Android heartbeats terminate a half-open USB session within a bounded timeout");
            await drain;
        }
        Window owner;using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))owner=(Window)System.Windows.Markup.XamlReader.Load(source);
        owner.Show();
        var dialog=new SecondScreenDialog(owner,"not-an-adb.exe",null);var type=typeof(SecondScreenDialog);var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var window=(Window)type.GetField("window",flags).GetValue(dialog);
        type.GetField("server",flags).SetValue(dialog,server);
        type.GetField("sessionDisplay",flags).SetValue(dialog,"nonexistent-owned-display-fixture");
        await (Task)type.GetMethod("CheckSession",flags).Invoke(dialog,null);
        Check(type.GetField("server",flags).GetValue(dialog)==null&&type.GetField("sessionDisplay",flags).GetValue(dialog)==null,"Disconnect clears the adopted display even if it was already active at session start");
        Check(((Button)window.FindName("MonitorStart")).IsEnabled&&!((Button)window.FindName("MonitorStop")).IsEnabled,"Disconnect restores start/stop controls after automatic session cleanup");
        type.GetField("allowClose",flags).SetValue(dialog,true);window.Close();owner.Close();
    }
    [StructLayout(LayoutKind.Sequential)]struct NativePoint{public int x,y;}
    [DllImport("user32.dll",EntryPoint="GetPhysicalCursorPos")]static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll")]static extern IntPtr GetMessageExtraInfo();
    static async Task NativeTouch()
    {
        var button=new Button{Content="Morror touch fixture"};var window=new Window{Content=button,Width=220,Height=120,Left=100,Top=100,WindowStyle=WindowStyle.None,ShowInTaskbar=false,Topmost=true};int tapped=0;button.AddHandler(UIElement.TouchDownEvent,new EventHandler<System.Windows.Input.TouchEventArgs>((s,e)=>tapped++),true);button.Click+=(s,e)=>tapped++;
        window.Show();window.UpdateLayout();int nativeDown=0;
        var hwndSource=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        HwndSourceHook observer=delegate(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg==0x246||msg==0x240||(msg==0x201&&(unchecked((uint)GetMessageExtraInfo().ToInt64())&0xffffff80u)==0xff515780u))nativeDown++;return IntPtr.Zero;};
        hwndSource.AddHook(observer);await Task.Delay(100);
        var origin=window.PointToScreen(new System.Windows.Point(0,0));var bounds=new Rectangle((int)origin.X,(int)origin.Y,(int)window.ActualWidth,(int)window.ActualHeight);
        var injection=new ScreenTouchInjection(bounds);NativePoint before,after;GetCursorPos(out before);
        Exception failure=null;try{injection.Apply(Frame("start",1));await Task.Delay(70);injection.Apply(Frame("end",2));await Task.Delay(150);GetCursorPos(out after);Console.WriteLine("Native touch diagnostic: pointer="+nativeDown+" wpf="+tapped+" cursorBefore="+before.x+","+before.y+" after="+after.x+","+after.y+" fixture="+bounds);Check(nativeDown>0||tapped>0,"Native Windows touch reaches the owned fixture window");var expected=injection.LastOtherMouseMove??new Point(before.x,before.y);Check(Math.Abs(expected.X-after.x)<=2&&Math.Abs(expected.Y-after.y)<=2,"Native touch preserves the cursor while allowing concurrent physical mouse movement");}catch(Exception e){failure=e;}
        Console.WriteLine("Suppressed touch moves: "+injection.SuppressedMoves);injection.Dispose();hwndSource.RemoveHook(observer);window.Close();if(failure!=null)throw failure;
    }
    static void Ui()
    {
        Window owner;using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))owner=(Window)System.Windows.Markup.XamlReader.Load(source);owner.Show();
        var dialog=new SecondScreenDialog(owner,"not-an-adb.exe",null);var window=(Window)typeof(SecondScreenDialog).GetField("window",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(dialog);window.Show();window.UpdateLayout();
        Check(window.FindName("ScreenBrowserUrl")==null&&window.FindName("ScreenOpenHost")==null,"Native monitor UI contains no browser or web sender");
        Check(((CheckBox)window.FindName("MonitorTouch")).IsChecked==true&&((Button)window.FindName("MonitorQr"))!=null,"Native monitor defaults to touch and exposes existing QR pairing");
        Check(((ComboBox)window.FindName("MonitorQuality")).SelectedIndex==2,"Ship device-native automatic resolution as the default");
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-monitor.png")))encoder.Save(file);
        typeof(SecondScreenDialog).GetField("allowClose",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(dialog,true);window.Close();owner.Close();
    }
    [STAThread]static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="-hide_banner")
        {
            int input=Array.IndexOf(args,"-i"),codec=Array.IndexOf(args,"-c:v"),rate=Array.IndexOf(args,"-b:v");
            return input>=0&&codec>=0&&rate>=0&&args[input+1]=="color=black:s=3200x2136:r=60"&&args[codec+1]=="h264_nvenc"&&args[rate+1]=="18000k"?0:1;
        }
        // Acts only as a child-process fixture; never invokes ADB or touches Android.
        if(args.Length>0&&args[0]=="-s")
        {
            if(args.Length==6&&args[2]=="reverse"&&args[3]=="--no-rebind"&&args[4]=="tcp:0"&&args[5]=="tcp:51423")
            {
                Console.WriteLine(args[1]=="download-phone"?"51424":"65536");
                return 0;
            }
            if(args.SequenceEqual(new[]{"-s","download-phone","reverse","--remove","tcp:51424"}))return 0;
            if(args.Length!=5||args[2]!="install"||args[3]!="-r"||args[4]!=FixtureApk)return 2;
            if(args[1]=="install-restricted"){Console.Error.WriteLine("Failure [INSTALL_FAILED_USER_RESTRICTED]");return 1;}
            Console.WriteLine(args[1]=="install-success"?"Success":"Success is still pending");return 0;
        }
        int result=1;var application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};application.Startup+=async(s,e)=>{
            try{await Protocol();Resolution();PhysicalRefreshRate();FreshScreenGeometry();InstallationErrors();await InstallationCommands();Ui();VirtualMonitorDriver.VerifyBundle(args[1]);Check(true,"Bundled display driver files match pinned hashes and a trusted Windows catalog signature");string codec=await NativeMonitorEncoder.Select(args[0],CancellationToken.None);Console.WriteLine("Encoder: "+codec);await Network(args[0],codec);await DisconnectCleanup(args[0],codec);await NativeTouch();Console.WriteLine(checks+" native monitor checks passed.");result=0;}catch(Exception error){Console.WriteLine(error);}finally{application.Shutdown();}
        };application.Run();return result;
    }
}
