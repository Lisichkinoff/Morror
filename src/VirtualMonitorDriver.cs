using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Morror
{
    public static class VirtualMonitorDriver
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string description;
            public int flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string key;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Mode
        {
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string name;
            public ushort spec,version,size,extra; public uint fields;
            public int x,y; public uint orientation,fixedOutput;
            public short color,duplex,yRes,tt,collate;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string form;
            public ushort logPixels; public uint bits,width,height,flags,frequency,icmMethod,icmIntent,media,dither,reserved1,reserved2,panningWidth,panningHeight;
        }
        static DisplayDevice[] Adapters()
        {
            var list = new System.Collections.Generic.List<DisplayDevice>();
            for(uint i=0;i<64;i++) { var d=new DisplayDevice { cb=Marshal.SizeOf(typeof(DisplayDevice)) }; if(!EnumDisplayDevices(null,i,ref d,0)) break; list.Add(d); }
            return list.ToArray();
        }
        static bool IsDriver(DisplayDevice d) { return d.description != null && d.description.IndexOf("Virtual Display Driver",StringComparison.OrdinalIgnoreCase)>=0; }
        public static bool Installed { get { return Adapters().Any(IsDriver); } }
        public static bool IsVirtualScreen(string deviceName) { return Adapters().Any(d=>d.name==deviceName && IsDriver(d)); }
        public static ScreenChoice[] Screens() { return ScreenChoice.Read().Where(s=>!s.Primary && IsVirtualScreen(s.DeviceName)).ToArray(); }
        public static bool Attach(int width=1920,int height=1080,int fps=60)
        {
            MonitorResolution.Validate(width,height,fps);
            if(Screens().Length>0) return true;
            var adapter=Adapters().FirstOrDefault(IsDriver); if(adapter.name==null) return false;
            var mode=new Mode { size=(ushort)Marshal.SizeOf(typeof(Mode)) };
            EnumDisplaySettings(adapter.name,-2,ref mode);
            mode.size=(ushort)Marshal.SizeOf(typeof(Mode)); mode.fields=0x20|0x40000|0x80000|0x100000|0x400000;
            mode.x=System.Windows.Forms.Screen.AllScreens.Max(s=>s.Bounds.Right);mode.y=0;
            mode.width=(uint)width;mode.height=(uint)height;mode.bits=32;mode.frequency=(uint)fps;
            return ChangeDisplaySettingsEx(adapter.name,ref mode,IntPtr.Zero,0,IntPtr.Zero)==0;
        }
        static string AdapterName(){var adapter=Adapters().FirstOrDefault(IsDriver);if(adapter.name==null)throw new IOException("Не найден драйвер виртуального дисплея.");return adapter.name;}
        public static bool SupportsMode(int width,int height,int fps)
        {
            MonitorResolution.Validate(width,height,fps);string adapter=AdapterName();
            for(int i=0;i<4096;i++)
            {
                var mode=new Mode{size=(ushort)Marshal.SizeOf(typeof(Mode))};if(!EnumDisplaySettings(adapter,i,ref mode))break;
                if(mode.width==width&&mode.height==height&&mode.frequency==fps)return true;
            }
            return false;
        }
        public static void SetMode(string deviceName,int width,int height,int fps)
        {
            MonitorResolution.Validate(width,height,fps);
            if(!Screens().Any(s=>s.DeviceName==deviceName))throw new IOException("Виртуальный экран не подключён.");
            var mode=new Mode{size=(ushort)Marshal.SizeOf(typeof(Mode))};
            if(!EnumDisplaySettings(deviceName,-1,ref mode))throw new IOException("Не удалось прочитать режим виртуального экрана.");
            mode.fields=0x40000|0x80000|0x100000|0x400000;mode.width=(uint)width;mode.height=(uint)height;mode.bits=32;mode.frequency=(uint)fps;
            int result=ChangeDisplaySettingsEx(deviceName,ref mode,IntPtr.Zero,0,IntPtr.Zero);
            if(result!=0)throw new IOException("Windows не применила размер виртуального монитора "+width+" × "+height+" (код "+result+").");
        }
        public static string ConfigurationPath
        {
            get
            {
                using(var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver"))
                {
                    string folder=key==null?null:key.GetValue("VDDPATH") as string;
                    if(string.IsNullOrWhiteSpace(folder))folder=@"C:\VirtualDisplayDriver";
                    return Path.Combine(folder,"vdd_settings.xml");
                }
            }
        }
        // Runs elevated only when the installed driver does not yet advertise the requested size.
        public static void ConfigureMode(int width,int height,int fps)
        {
            MonitorResolution.Validate(width,height,fps);
            MonitorResolution.UpdateConfiguration(ConfigurationPath,width,height,fps);
            RestartVirtualAdapters();
        }
        static void RestartVirtualAdapters()
        {
            Guid displayClass=new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
            IntPtr set=SetupDiGetClassDevs(ref displayClass,null,IntPtr.Zero,2);if(set==new IntPtr(-1))throw new Win32Exception();int restarted=0;
            try
            {
                for(uint index=0;index<128;index++)
                {
                    var device=new DeviceInfo{size=Marshal.SizeOf(typeof(DeviceInfo))};if(!SetupDiEnumDeviceInfo(set,index,ref device))break;
                    uint type,required;var bytes=new byte[4096];
                    if(!SetupDiGetDeviceRegistryProperty(set,ref device,1,out type,bytes,(uint)bytes.Length,out required))continue;
                    string[] ids=Encoding.Unicode.GetString(bytes,0,(int)Math.Min(required,(uint)bytes.Length)).Split('\0');
                    if(!ids.Any(id=>string.Equals(id,@"Root\MttVDD",StringComparison.OrdinalIgnoreCase)||string.Equals(id,"MttVDD",StringComparison.OrdinalIgnoreCase)))continue;
                    var change=new PropertyChange{headerSize=8,function=0x12,stateChange=3,scope=2,profile=0};
                    if(!SetupDiSetClassInstallParams(set,ref device,ref change,Marshal.SizeOf(typeof(PropertyChange)))||!SetupDiCallClassInstaller(0x12,set,ref device))throw new Win32Exception();
                    restarted++;
                }
            }
            finally{SetupDiDestroyDeviceInfoList(set);}
            if(restarted==0)throw new IOException("Не найдено устройство MttVDD для обновления списка разрешений.");
        }
        public static void Detach(string deviceName)
        {
            if(!Screens().Any(s=>s.DeviceName==deviceName))return;
            var mode=new Mode{size=(ushort)Marshal.SizeOf(typeof(Mode)),fields=0x20|0x80000|0x100000};
            ChangeDisplaySettingsEx(deviceName,ref mode,IntPtr.Zero,0,IntPtr.Zero);
        }
        public static void InstallBundled()
        {
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"runtime","monitor-driver");
            VerifyBundle(folder);
            if(Installed) return;
            Install(folder);
        }
        public static void VerifyBundle(string folder)
        {
            VerifyFile(Path.Combine(folder,"MttVDD.inf"),"550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D");
            VerifyFile(Path.Combine(folder,"MttVDD.dll"),"C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05");
            VerifyFile(Path.Combine(folder,"mttvdd.cat"),"08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6");
            VerifySignature(Path.Combine(folder,"mttvdd.cat"));
        }
        static void Install(string folder)
        {
            string configuration=@"C:\VirtualDisplayDriver\vdd_settings.xml";
            if(!File.Exists(configuration)) { Directory.CreateDirectory(Path.GetDirectoryName(configuration));File.Copy(Path.Combine(folder,"vdd_settings.xml"),configuration); }
            Guid displayClass=new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
            IntPtr set=SetupDiCreateDeviceInfoList(ref displayClass,IntPtr.Zero);
            if(set==new IntPtr(-1)) throw new Win32Exception();
            var device=new DeviceInfo { size=Marshal.SizeOf(typeof(DeviceInfo)) }; bool registered=false;
            try
            {
                if(!SetupDiCreateDeviceInfo(set,"Display",ref displayClass,"Morror virtual display (MttVDD)",IntPtr.Zero,1,ref device)) throw new Win32Exception();
                byte[] id=Encoding.Unicode.GetBytes("Root\\MttVDD\0\0");
                if(!SetupDiSetDeviceRegistryProperty(set,ref device,1,id,(uint)id.Length)) throw new Win32Exception();
                if(!SetupDiCallClassInstaller(0x19,set,ref device)) throw new Win32Exception(); registered=true;
                bool reboot;
                if(!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero,"Root\\MttVDD",Path.Combine(folder,"MttVDD.inf"),1,out reboot)) throw new Win32Exception();
                registered=false; // Successfully installed: retain the device node.
            }
            finally { if(registered) SetupDiCallClassInstaller(5,set,ref device); SetupDiDestroyDeviceInfoList(set); }
        }
        static void VerifyFile(string path,string expected)
        { using(var sha=SHA256.Create()) using(var stream=File.OpenRead(path)) if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!=expected) throw new IOException("Компонент драйвера повреждён: "+Path.GetFileName(path)); }
        static void VerifySignature(string path)
        {
            IntPtr filePointer=IntPtr.Zero;
            var file=new TrustFile { size=(uint)Marshal.SizeOf(typeof(TrustFile)),path=path };
            try
            {
                filePointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));Marshal.StructureToPtr(file,filePointer,false);
                var data=new TrustData { size=(uint)Marshal.SizeOf(typeof(TrustData)),ui=2,choice=1,file=filePointer,providerFlags=0x1000 };
                Guid action=new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
                if(WinVerifyTrust(IntPtr.Zero,ref action,ref data)!=0) throw new IOException("Windows не подтвердила подпись каталога драйвера. Установка отменена.");
            }
            finally { if(filePointer!=IntPtr.Zero) { Marshal.DestroyStructure(filePointer,typeof(TrustFile));Marshal.FreeHGlobal(filePointer); } }
        }
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfo { public int size; public Guid classGuid; public uint devInst; public IntPtr reserved; }
        [StructLayout(LayoutKind.Sequential)] struct PropertyChange{public uint headerSize,function,stateChange,scope,profile;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct TrustFile { public uint size; [MarshalAs(UnmanagedType.LPWStr)] public string path; public IntPtr handle,subject; }
        [StructLayout(LayoutKind.Sequential)] struct TrustData { public uint size;public IntPtr policy,sip;public uint ui,revocation,choice;public IntPtr file;public uint stateAction;public IntPtr state,url;public uint providerFlags,context; }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string device,uint index,ref DisplayDevice info,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplaySettings(string device,int index,ref Mode mode);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string name,ref Mode mode,IntPtr hwnd,uint flags,IntPtr data);
        [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid,IntPtr parent);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiCreateDeviceInfo(IntPtr set,string name,ref Guid classGuid,string description,IntPtr parent,uint flags,ref DeviceInfo device);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set,ref DeviceInfo device,uint property,byte[] buffer,uint length);
        [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(uint function,IntPtr set,ref DeviceInfo device);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid,string enumerator,IntPtr parent,uint flags);
        [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref DeviceInfo info);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set,ref DeviceInfo info,uint property,out uint type,byte[] data,uint size,out uint required);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiSetClassInstallParams(IntPtr set,ref DeviceInfo info,ref PropertyChange parameters,int size);
        [DllImport("newdev.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd,string id,string inf,uint flags,out bool reboot);
        [DllImport("wintrust.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int WinVerifyTrust(IntPtr hwnd,ref Guid action,ref TrustData data);
    }
}
