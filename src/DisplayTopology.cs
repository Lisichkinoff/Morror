using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Morror
{
    // CCD preserves the exact timings of existing paths while enabling one IDD
    // target. Legacy ChangeDisplaySettingsEx can reject a detached IDD source.
    internal static class DisplayTopology
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Luid
        {public uint low;public int high;}
        [StructLayout(LayoutKind.Sequential)] internal struct Rational
        {public uint numerator,denominator;}
        [StructLayout(LayoutKind.Sequential)] internal struct Source
        {public Luid adapter;public uint id,mode,status;}
        [StructLayout(LayoutKind.Sequential)] internal struct Target
        {
            public Luid adapter;public uint id,mode,technology,rotation,scaling;
            public Rational refresh;public uint scanline;public int available;public uint status;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct DisplayPath
        {public Source source;public Target target;public uint flags;}
        [StructLayout(LayoutKind.Sequential)] internal struct SourceMode
        {public uint width,height,pixelFormat;public int x,y;}
        [StructLayout(LayoutKind.Explicit,Size=48)] internal struct ModeData
        {
            [FieldOffset(0)]public SourceMode source;
            [FieldOffset(0)]public ulong timing0;
            [FieldOffset(8)]public ulong timing1;
            [FieldOffset(16)]public ulong timing2;
            [FieldOffset(24)]public ulong timing3;
            [FieldOffset(32)]public ulong timing4;
            [FieldOffset(40)]public ulong timing5;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct DisplayMode
        {public uint type,id;public Luid adapter;public ModeData data;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SourceName
        {
            public uint type,size;public Luid adapter;public uint id;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string name;
        }
        internal sealed class Snapshot
        {
            internal DisplayPath[] paths;
            internal DisplayMode[] modes;
            internal void Restore()
            {
                int result=SetDisplayConfig((uint)paths.Length,paths,(uint)modes.Length,modes,0x80|0x20);
                if(result!=0)throw new Win32Exception(result,"Не удалось восстановить исходную конфигурацию дисплеев Windows.");
            }
        }
        internal static Snapshot Read(bool all=false)
        {
            uint flags=all?1u:2u;
            for(int attempt=0;attempt<5;attempt++)
            {
                uint paths,modes;int result=GetDisplayConfigBufferSizes(flags,out paths,out modes);
                if(result!=0)throw new Win32Exception(result,"Не удалось прочитать конфигурацию дисплеев Windows.");
                if(paths>1024||modes>4096)throw new IOException("Windows вернула слишком большую конфигурацию дисплеев.");
                var p=new DisplayPath[paths];var m=new DisplayMode[modes];
                result=QueryDisplayConfig(flags,ref paths,p,ref modes,m,IntPtr.Zero);
                if(result==122)continue;
                if(result!=0)throw new Win32Exception(result,"Не удалось прочитать пути дисплеев Windows.");
                Array.Resize(ref p,(int)paths);Array.Resize(ref m,(int)modes);
                return new Snapshot{paths=p,modes=m};
            }
            throw new IOException("Конфигурация дисплеев меняется. Повторите подключение Morror.");
        }
        internal static string Name(DisplayPath path)
        {
            var name=new SourceName{type=1,size=(uint)Marshal.SizeOf(typeof(SourceName)),adapter=path.source.adapter,id=path.source.id};
            return DisplayConfigGetDeviceInfo(ref name)==0?name.name:null;
        }
        internal static string Describe()
        {
            return string.Join("\n",Read(true).paths.Select(p=>Name(p)+" active="+((p.flags&1)!=0)+" available="+p.target.available+" source="+p.source.id+" target="+p.target.id+" rate="+p.target.refresh.numerator+"/"+p.target.refresh.denominator));
        }
        internal static int Attach(string device,int width,int height,int fps,int x,int y)
        {
            var configured=CreateAttachment(Read(true),p=>string.Equals(Name(p),device,StringComparison.OrdinalIgnoreCase),width,height,fps,x,y);
            if(configured==null)return 1167; // ERROR_DEVICE_NOT_CONNECTED
            return Apply(configured);
        }
        internal static Snapshot CreateAttachment(Snapshot snapshot,Func<DisplayPath,bool> selected,int width,int height,int fps,int x,int y)
        {
            MonitorResolution.Validate(width,height,fps);
            var candidates=snapshot.paths.Where(p=>p.target.available!=0&&selected(p)).ToArray();
            if(candidates.Length==0)return null;
            var path=candidates.FirstOrDefault(p=>(p.flags&1)!=0);
            if((path.flags&1)==0)path=candidates[0];
            var active=snapshot.paths.Where(p=>(p.flags&1)!=0&&!selected(p)).ToList();
            var modes=snapshot.modes.ToList();
            int sourceIndex=modes.FindIndex(m=>m.type==1&&m.id==path.source.id&&m.adapter.low==path.source.adapter.low&&m.adapter.high==path.source.adapter.high);
            if(sourceIndex<0){sourceIndex=modes.Count;modes.Add(new DisplayMode());}
            path.source.mode=(uint)sourceIndex;
            modes[sourceIndex]=new DisplayMode{type=1,id=path.source.id,adapter=path.source.adapter,
                data=new ModeData{source=new SourceMode{width=(uint)width,height=(uint)height,pixelFormat=4,x=x,y=y}}};
            path.target.mode=uint.MaxValue;path.target.rotation=1;path.target.scaling=1;
            path.target.refresh=new Rational{numerator=(uint)fps,denominator=1};path.target.scanline=1;path.flags|=1;
            active.Add(path);
            return new Snapshot{paths=active.ToArray(),modes=modes.ToArray()};
        }
        static int Apply(Snapshot snapshot)
        {
            int result=SetDisplayConfig((uint)snapshot.paths.Length,snapshot.paths,(uint)snapshot.modes.Length,snapshot.modes,0x40|0x20);
            if(result!=0)return result;
            return SetDisplayConfig((uint)snapshot.paths.Length,snapshot.paths,(uint)snapshot.modes.Length,snapshot.modes,0x80|0x20);
        }
        internal static int Detach(string device)
        {
            var snapshot=Read();
            var paths=snapshot.paths.Where(p=>!string.Equals(Name(p),device,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(paths.Length==snapshot.paths.Length)return 0;
            if(paths.Length==0)throw new IOException("Нельзя отключить единственный активный дисплей Windows.");
            return Apply(new Snapshot{paths=paths,modes=snapshot.modes});
        }
        [DllImport("user32.dll")]static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
        [DllImport("user32.dll")]static extern int QueryDisplayConfig(uint flags,ref uint pathCount,[Out]DisplayPath[] paths,ref uint modeCount,[Out]DisplayMode[] modes,IntPtr topology);
        [DllImport("user32.dll")]static extern int SetDisplayConfig(uint pathCount,[In]DisplayPath[] paths,uint modeCount,[In]DisplayMode[] modes,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
    }
}
