using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Morror;

class NativeAndroidSmoke
{
    static int Main(string[] args){try{Run(args).GetAwaiter().GetResult();return 0;}catch(Exception e){Console.WriteLine(e.Message);return 1;}}
    static async Task Run(string[] args)
    {
        string adb=args[0],serial=args[1],apk=args[2],ffmpeg=args[3];
        string codec=await NativeMonitorEncoder.Select(ffmpeg,CancellationToken.None);
        var info=await DeviceCapabilities.Read(adb,serial,CancellationToken.None);
        var size=MonitorResolution.ForDevice(info.Width,info.Height,2);
        int touchFrames=0;
        var server=new NativeMonitorServer(ffmpeg,codec,new ScreenChoice{Name="Synthetic only",Bounds=new Rectangle(0,0,size.Width,size.Height)},size.Width,size.Height,30,false,f=>{Interlocked.Increment(ref touchFrames);return Task.FromResult(0);},()=>Task.FromResult(0),true);
        int reverse=0;Exception failure=null;
        try
        {
            server.Start();var result=await Commands.Run(adb,60000,"-s",serial,"install","-r",apk);
            if(result.ExitCode!=0 || !result.Combined.Contains("Success"))throw new IOException("APK install failed: "+result.Combined);
            result=await Commands.Run(adb,8000,"-s",serial,"reverse","--no-rebind","tcp:"+server.Port,"tcp:"+server.Port);
            if(result.ExitCode!=0)throw new IOException("Native reverse tunnel failed.");reverse=server.Port;
            result=await Commands.Run(adb,10000,"-s",serial,"shell","am","start","-n","ru.morror.monitor/.MonitorActivity","--ei","port",server.Port.ToString(),"--es","token",server.Token);
            if(result.ExitCode!=0||result.Combined.Contains("Error"))throw new IOException("Native app did not start.");
            for(int i=0;i<150&&server.DecodedFrames<60;i++)await Task.Delay(200);
            if(server.DecodedFrames<60)throw new IOException("Android did not acknowledge decoded frames. "+server.Status);
            Console.WriteLine("PASS Android decoder acknowledged "+server.DecodedFrames+" H.264 frames at the device-native "+size.Width+"x"+size.Height+" through ADB reverse.");
            await Commands.Run(adb,5000,"-s",serial,"shell","input","touchscreen","tap",(size.Width/2).ToString(),(size.Height/2).ToString());
            for(int i=0;i<30&&touchFrames<2;i++)await Task.Delay(100);
            if(touchFrames<2)throw new IOException("Phone touch was not received by the native host.");
            Console.WriteLine("PASS Real Android touchscreen event returned to the native host through the same tunnel.");
            using(var capture=new System.Diagnostics.Process{StartInfo=Commands.StartInfo(adb,new[]{"-s",serial,"exec-out","screencap","-p"})})
            {
                capture.Start();var errors=capture.StandardError.ReadToEndAsync();
                using(var image=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-phone.png")))await capture.StandardOutput.BaseStream.CopyToAsync(image);
                await Task.Run(()=>capture.WaitForExit(5000));await errors;
            }
            using(var image=new Bitmap(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-phone.png")))
            {
                if(image.Width!=size.Width||image.Height!=size.Height)throw new IOException("Screenshot does not match the device-native dimensions.");
                if(image.GetPixel(4,image.Height/2).GetBrightness()<.05 || image.GetPixel(image.Width-5,image.Height/2).GetBrightness()<.05)throw new IOException("Black side bars remain on the native device screen.");
                Console.WriteLine("PASS Device-native video fills both screen edges without black side bars.");
            }
        }
        catch(Exception e){failure=e;}
        await server.Stop();
        if(reverse!=0)await Commands.Run(adb,5000,"-s",serial,"reverse","--remove","tcp:"+reverse);
        if(failure!=null)throw failure;
    }
}
