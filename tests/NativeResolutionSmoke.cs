using System;
using System.Linq;
using System.Threading;
using Morror;

// Explicit OS smoke: applies only a mode already registered for the virtual adapter.
class NativeResolutionSmoke
{
    static int Main(string[] args)
    {
        try
        {
            int width=int.Parse(args[0]),height=int.Parse(args[1]),fps=int.Parse(args[2]);
            var physical=ScreenChoice.Read().Where(s=>!VirtualMonitorDriver.IsVirtualScreen(s.DeviceName)).ToArray();
            if(!VirtualMonitorDriver.SupportsMode(width,height,fps))throw new Exception("Driver does not advertise the requested native mode.");
            Console.WriteLine("PASS Driver advertises "+width+"x"+height+" @ "+fps+" Hz.");
            if(!VirtualMonitorDriver.Attach(width,height,fps))throw new Exception("Virtual monitor could not attach.");
            var monitor=VirtualMonitorDriver.Screens().First();VirtualMonitorDriver.SetMode(monitor.DeviceName,width,height,fps);
            for(int i=0;i<40;i++){Thread.Sleep(100);monitor=VirtualMonitorDriver.Screens().First();if(monitor.Bounds.Width==width&&monitor.Bounds.Height==height)break;}
            if(monitor.Bounds.Width!=width||monitor.Bounds.Height!=height)throw new Exception("Windows did not apply the native dimensions: "+monitor.Bounds);
            Console.WriteLine("PASS Windows virtual monitor uses "+monitor.Bounds.Width+"x"+monitor.Bounds.Height+" pixels.");
            var after=ScreenChoice.Read();
            if(physical.Any(before=>!after.Any(s=>s.DeviceName==before.DeviceName&&s.Bounds==before.Bounds&&s.Primary==before.Primary)))throw new Exception("Physical monitor layout changed.");
            Console.WriteLine("PASS Existing physical monitor dimensions, positions and primary selection are preserved.");return 0;
        }
        catch(Exception e){Console.WriteLine(e);return 1;}
    }
}
