using System;
using System.Linq;
using System.Threading;
using Morror;

// Explicit OS smoke: applies only a mode already registered for the virtual adapter.
class NativeResolutionSmoke
{
    internal static void VerifyPhysical(DisplayTopology.Snapshot before)
    {
        var after=DisplayTopology.Read();
        foreach(var path in before.paths)
        {
            string name=DisplayTopology.Name(path);
            if(VirtualMonitorDriver.IsVirtualScreen(name))continue;
            var matches=after.paths.Where(p=>DisplayTopology.Name(p)==name).ToArray();
            if(matches.Length!=1)throw new Exception("Physical display path missing: "+name);
            var actual=matches[0];var original=path;
            if(!before.modes[(int)path.source.mode].Equals(after.modes[(int)actual.source.mode])||!before.modes[(int)path.target.mode].Equals(after.modes[(int)actual.target.mode]))throw new Exception("Physical timing or geometry changed: "+name);
            actual.source.mode=original.source.mode=0;actual.target.mode=original.target.mode=0;
            if(!actual.Equals(original))throw new Exception("Physical path parameters changed: "+name);
        }
        Console.WriteLine("PASS Exact physical geometry, fractional refresh timings and primary layout preserved.");
    }
    static int Main(string[] args)
    {
        DisplayTopology.Snapshot topology=null;
        try
        {
            if(args.Length==0||args.Length%3!=0)throw new ArgumentException("Pass successive width height fps triplets.");
            topology=DisplayTopology.Read();string display=null;
            for(int index=0;index<args.Length;index+=3)
            {
            int width=int.Parse(args[index]),height=int.Parse(args[index+1]),fps=int.Parse(args[index+2]);
            if(!VirtualMonitorDriver.SupportsMode(width,height,fps))throw new Exception("Driver does not advertise the requested native mode.");
            Console.WriteLine("PASS Driver advertises "+width+"x"+height+" @ "+fps+" Hz.");
            if(!VirtualMonitorDriver.Attach(width,height,fps))throw new Exception("Virtual monitor could not attach.");
            var monitor=VirtualMonitorDriver.Screens().First();VirtualMonitorDriver.SetMode(monitor.DeviceName,width,height,fps);
            display=monitor.DeviceName;
            for(int i=0;i<40;i++){Thread.Sleep(100);monitor=VirtualMonitorDriver.Screens().First();if(monitor.Bounds.Width==width&&monitor.Bounds.Height==height)break;}
            if(monitor.Bounds.Width!=width||monitor.Bounds.Height!=height)throw new Exception("Windows did not apply the native dimensions: "+monitor.Bounds);
            Console.WriteLine("PASS Windows virtual monitor uses "+monitor.Bounds.Width+"x"+monitor.Bounds.Height+" pixels.");
            VerifyPhysical(topology);
            }
            VirtualMonitorDriver.Detach(display);
            if(VirtualMonitorDriver.Screens().Any(s=>s.DeviceName==display))throw new Exception("Virtual display remained attached after stop.");
            Console.WriteLine("PASS Virtual monitor detached from the real Windows desktop.");VerifyPhysical(topology);return 0;
        }
        catch(Exception e){Console.WriteLine(e);return 1;}
        finally{if(topology!=null){topology.Restore();Console.WriteLine("Original Windows topology restored.");}}
    }
}
