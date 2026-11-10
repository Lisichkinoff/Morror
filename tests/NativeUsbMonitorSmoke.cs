using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Morror;
using Brushes=System.Windows.Media.Brushes;

// Explicit hardware test. Captures only its own full-screen RGB fixture on the
// virtual display, and restores the original Windows topology in finally.
class NativeUsbMonitorSmoke
{
    [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [STAThread]static int Main(string[] args)
    {
        int result=1;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(s,e)=>{try{await Run(args);result=0;}catch(Exception error){Console.WriteLine(error);}finally{app.Shutdown();}};
        app.Run();return result;
    }
    static async Task Run(string[] args)
    {
        string adb=args[0],serial=args[1],apk=args[2],ffmpeg=args[3];
        var topology=DisplayTopology.Read();NativeMonitorServer server=null;Window fixture=null;int port=0,touches=0;string display=null;Exception failure=null;
        try
        {
            var device=await DeviceCapabilities.Read(adb,serial,CancellationToken.None);
            var size=MonitorResolution.ForDevice(device.Width,device.Height,1);
            if(!VirtualMonitorDriver.SupportsMode(size.Width,size.Height,60))throw new Exception("Full HD mode is not registered: "+size);
            VirtualMonitorDriver.Attach(size.Width,size.Height,60);
            var screen=VirtualMonitorDriver.Screens().First();display=screen.DeviceName;
            VirtualMonitorDriver.SetMode(display,size.Width,size.Height,60);
            screen=VirtualMonitorDriver.Screens().Single(s=>s.DeviceName==display);
            if(screen.Bounds.Width!=size.Width||screen.Bounds.Height!=size.Height)throw new Exception("Virtual display size mismatch.");
            NativeResolutionSmoke.VerifyPhysical(topology);
            Console.WriteLine("PASS Real Windows virtual display attached at "+size.Width+"x"+size.Height+".");
            var grid=new Grid();foreach(var brush in new[]{Brushes.Red,Brushes.Lime,Brushes.Blue})
            {int column=grid.ColumnDefinitions.Count;grid.ColumnDefinitions.Add(new ColumnDefinition());var panel=new Border{Background=brush};Grid.SetColumn(panel,column);grid.Children.Add(panel);}
            fixture=new Window{Content=grid,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,WindowStartupLocation=WindowStartupLocation.Manual,Left=screen.Bounds.X,Top=screen.Bounds.Y,Width=size.Width,Height=size.Height};
            fixture.Show();fixture.UpdateLayout();
            if(!SetWindowPos(new WindowInteropHelper(fixture).Handle,IntPtr.Zero,screen.Bounds.X,screen.Bounds.Y,size.Width,size.Height,0x10|0x40))throw new System.ComponentModel.Win32Exception();
            await Task.Delay(300);
            string codec=await NativeMonitorEncoder.Select(ffmpeg,CancellationToken.None,size.Width,size.Height,60);
            server=new NativeMonitorServer(ffmpeg,codec,screen,size.Width,size.Height,60,false,f=>{Interlocked.Increment(ref touches);return Task.FromResult(0);},()=>Task.FromResult(0));
            await AndroidMonitorClient.Install(adb,serial,apk);server.Start();
            var reverse=await Commands.Run(adb,8000,"-s",serial,"reverse","--no-rebind","tcp:"+server.Port,"tcp:"+server.Port);
            if(reverse.ExitCode!=0)throw new IOException(reverse.Combined);port=server.Port;
            var launch=await Commands.Run(adb,10000,"-s",serial,"shell","am","start","-n","ru.morror.monitor/.MonitorActivity","--ei","port",port.ToString(),"--es","token",server.Token);
            if(launch.ExitCode!=0||launch.Combined.Contains("Error"))throw new IOException(launch.Combined);
            for(int i=0;i<150&&server.DecodedFrames<60&&!server.Completion.IsCompleted;i++)await Task.Delay(200);
            if(server.DecodedFrames<60)throw new IOException("Real desktop video did not reach Android. "+server.Status);
            Console.WriteLine("PASS Android acknowledged "+server.DecodedFrames+" rendered frames from real virtual-desktop capture over USB. "+server.Status);
            string png=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"usb-virtual-desktop.png");
            using(var capture=new System.Diagnostics.Process{StartInfo=Commands.StartInfo(adb,new[]{"-s",serial,"exec-out","screencap","-p"})})
            {
                capture.Start();var errors=capture.StandardError.ReadToEndAsync();
                using(var image=File.Create(png))await capture.StandardOutput.BaseStream.CopyToAsync(image);
                await Task.Run(()=>capture.WaitForExit(5000));await errors;
                if(capture.ExitCode!=0)throw new IOException("Android screenshot failed.");
            }
            using(var image=new Bitmap(png))
            {
                var left=image.GetPixel(image.Width/6,image.Height/2);var middle=image.GetPixel(image.Width/2,image.Height/2);var right=image.GetPixel(image.Width*5/6,image.Height/2);
                if(left.R<150||left.G>100||middle.G<150||middle.R>100||right.B<150||right.G>100)throw new IOException("Android did not display the owned RGB desktop fixture.");
                Console.WriteLine("PASS Phone screenshot confirms the real virtual Windows desktop RGB fixture fills the native Android display.");
            }
            await Commands.Run(adb,5000,"-s",serial,"shell","input","touchscreen","tap",(Math.Max(device.Width,device.Height)/2).ToString(),(Math.Min(device.Width,device.Height)/2).ToString());
            for(int i=0;i<30&&touches<2;i++)await Task.Delay(100);
            if(touches<2)throw new IOException("Android touch did not return through the USB tunnel.");
            Console.WriteLine("PASS Android touch returns through the same live USB monitor tunnel.");
        }
        catch(Exception error){failure=error;}
        try
        {
                if(server!=null)await server.Stop();
                if(port!=0)await Commands.Run(adb,5000,"-s",serial,"reverse","--remove","tcp:"+port);
                if(fixture!=null)fixture.Close();
                if(display!=null){VirtualMonitorDriver.Detach(display);if(VirtualMonitorDriver.Screens().Any(s=>s.DeviceName==display))throw new Exception("Virtual display did not detach.");}
                NativeResolutionSmoke.VerifyPhysical(topology);
                Console.WriteLine("PASS USB session cleanup detached the virtual monitor.");
        }
        finally{topology.Restore();Console.WriteLine("Original Windows desktop restored.");}
        if(failure!=null)throw failure;
    }
}
