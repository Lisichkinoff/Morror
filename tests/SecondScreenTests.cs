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
    static bool Nal(byte[] bytes,int type){for(int i=0;i+4<bytes.Length;i++){int p=H264AccessUnits.Prefix(bytes,i);if(p>0&&(bytes[i+p]&31)==type)return true;}return false;}
    static async Task Until(Func<bool> condition,string message){var clock=Stopwatch.StartNew();while(!condition()){if(clock.ElapsedMilliseconds>10000)throw new TimeoutException(message);await Task.Delay(30);}}
    static void Protocol()
    {
        var header=MonitorWire.Header(16,5,0x0102030405060708);Check(MonitorWire.Length(header)==14&&header[4]==16&&header[5]==1&&header[12]==8,"Native wire uses network byte order and explicit packet bounds");
        bool invalid=false;try{MonitorWire.Length(new byte[]{255,255,255,255});}catch(IOException){invalid=true;}Check(invalid,"Reject oversized native packets before allocation");
        var state=new ScreenTouchState();Check(!state.Accept(Frame("move",1))&&state.Accept(Frame("start",1))&&state.Accept(Frame("move",2))&&state.Accept(Frame("end",3)),"Native touch requires ordered down/move/up");
        Check(!state.Accept(Frame("start",2)),"Reject replayed touch packets");
        Check(SecondScreenProtocol.MapToScreen(new ScreenContact{x=1,y=1},new Rectangle(-1920,0,1920,1080))==new Point(-1,1079),"Native touch maps negative virtual-monitor coordinates");
        byte[] sample={0,0,0,1,0x67,0x42,0x10,0,0,1,0x68,0x12,0,0,0,1,9,0xf0,0,0,1,0x65,0xaa,0xbb,0,0,0,1,9,0xf0,0,0,1,0x41,0xcc,0xdd};
        for(int split=1;split<=sample.Length;split++){
            var parser=new H264AccessUnits();var frames=new List<byte[]>();
            for(int i=0;i<sample.Length;i+=split){byte[] chunk=sample.Skip(i).Take(split).ToArray();frames.AddRange(parser.Push(chunk,chunk.Length));}frames.AddRange(parser.Push(new byte[0],0,true));
            Check(frames.Count==2&&Nal(frames[0],7)&&Nal(frames[0],8)&&Nal(frames[0],5)&&Nal(frames[1],1),"H.264 access units survive fragment size "+split);
        }
        byte[] mono=BitConverter.GetBytes((short)-16384);byte[] stereo=WasapiMonitorAudio.StereoFloat(mono,1,1,16,false,2);
        Check(BitConverter.ToSingle(stereo,0)==-.5f&&BitConverter.ToSingle(stereo,4)==-.5f,"System audio converts PCM16 mono to stereo float");
        var pcm24=new byte[]{0,0,0x80,0xff,0xff,0x7f};stereo=WasapiMonitorAudio.StereoFloat(pcm24,1,2,24,false,6);
        Check(BitConverter.ToSingle(stereo,0)==-1&&BitConverter.ToSingle(stereo,4)>.999f,"System audio preserves signed PCM24 range");
    }
    static void Resolution()
    {
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
        await server.Stop();Check(released>0,"Native session teardown releases touches and stops the encoder");
        Check(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==server.Port&&IPAddress.IsLoopback(p.Address)),"Stopping native monitor closes its listening port");
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
        int result=1;var application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};application.Startup+=async(s,e)=>{
            try{Protocol();Resolution();Ui();VirtualMonitorDriver.VerifyBundle(args[1]);Check(true,"Bundled display driver files match pinned hashes and a trusted Windows catalog signature");await NativeTouch();string codec=await NativeMonitorEncoder.Select(args[0],CancellationToken.None);Console.WriteLine("Encoder: "+codec);await Network(args[0],codec);Console.WriteLine(checks+" native monitor checks passed.");result=0;}catch(Exception error){Console.WriteLine(error);}finally{application.Shutdown();}
        };application.Run();return result;
    }
}
