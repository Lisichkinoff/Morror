using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Morror
{
    // Windows x64 ABI from iPhoneMirror v1.8.3, CoreApi.h (version 18).
    internal sealed class IPhoneMirrorEngine : IDisposable
    {
        const string Core = "iPhoneMirror.Core.dll";
        const uint Api = 18;
        internal const string ReceiverName = "Morror";
        IntPtr module;
        ulong session;
        bool initialized, listening;
        readonly string directory;

        internal IPhoneMirrorEngine(string directory) { this.directory = directory; }
        internal bool HasSession { get { return session != 0; } }
        internal string WirelessHost { get { return Path.Combine(directory, "Wireless", "iPhoneMirror.WirelessHost.exe"); } }
        internal string DriverTool { get { return Path.Combine(directory, "iPhoneMirror.Driver.exe"); } }

        internal void Initialize()
        {
            if (initialized) return;
            string path = Path.Combine(directory, Core);
            if (!File.Exists(path)) throw new IOException("В сборке отсутствует компонент iPhone. Установите полный пакет Morror.");
            // Resolve native dependencies from the bundled core directory and System32.
            module = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось загрузить компонент iPhone.");
            try
            {
                if (im_api_version() != Api) throw new IOException("Версия компонента iPhone не соответствует этой сборке Morror.");
                Check(im_initialize(), "Не удалось запустить компонент iPhone");
                initialized = true;
            }
            catch
            {
                FreeLibrary(module);
                module = IntPtr.Zero;
                throw;
            }
        }

        internal Device[] Devices(bool wiFi, bool refreshMetadata)
        {
            uint count = 0;
            Check(wiFi ? im_refresh_wireless_devices(null, ref count) : im_refresh_devices_ex(null, ref count, refreshMetadata ? 1 : 0),
                "Не удалось обновить список iPhone");
            for (int attempt = 0; attempt < 3 && count != 0; attempt++)
            {
                if (count > 64) throw new IOException("Компонент iPhone вернул слишком много устройств.");
                var devices = new Device[count];
                int result = wiFi ? im_refresh_wireless_devices(devices, ref count) : im_refresh_devices_ex(devices, ref count, 0);
                if (result == -3) continue;
                Check(result, "Не удалось получить список iPhone");
                return devices.Take((int)count).Where(d => !string.IsNullOrEmpty(d.Id) && (wiFi || d.UsbConnected != 0)).ToArray();
            }
            return new Device[0];
        }

        internal void Listen()
        {
            if (!File.Exists(WirelessHost)) throw new IOException("В сборке отсутствует приёмник AirPlay.");
            Check(im_wireless_receiver_start_ex(ReceiverName, WirelessHost, 1920, 1080, 60), "Не удалось запустить AirPlay");
            listening = true;
        }

        internal bool ReceiverReady()
        {
            int running, ready;
            Check(im_wireless_receiver_get_status(out running, out ready), "Не удалось проверить состояние AirPlay");
            if (running == 0) throw new IOException("Приёмник AirPlay завершился. Проверьте журнал и доступ к частной сети в брандмауэре Windows.");
            return ready != 0;
        }

        internal void Start(Device device, bool wiFi)
        {
            var options = new CaptureOptions { Size = (uint)Marshal.SizeOf(typeof(CaptureOptions)), ApiVersion = Api, PlayAudio = 1, Volume = 1 };
            if (!wiFi)
            {
                int available;
                Check(im_is_libusb0_device_available(device.Id, out available), "Не удалось проверить USB");
                if (available == 0)
                    throw new IOException("USB-компонент для выбранного iPhone не готов. Нажмите «Настроить USB в Windows».");
            }
            Check(wiFi ? im_wireless_session_create(device.Id, ref options, out session) : im_session_create(device.Id, ref options, out session),
                "Не удалось начать трансляцию");
        }

        internal void Attach(IntPtr window)
        {
            if (window == IntPtr.Zero) throw new IOException("Не удалось создать окно трансляции iPhone.");
            Check(im_session_attach_preview(session, window), "Не удалось открыть изображение iPhone");
        }

        internal CaptureStatus Status()
        {
            var status = new CaptureStatus { Size = (uint)Marshal.SizeOf(typeof(CaptureStatus)), ApiVersion = Api };
            Check(im_session_get_status(session, ref status), "Не удалось проверить трансляцию");
            return status;
        }

        internal void Stop()
        {
            if (session == 0) return;
            ulong handle = session;
            session = 0;
            try { Check(im_session_stop(handle), "Windows не подтвердила завершение трансляции"); }
            finally { im_session_destroy(handle); }
        }

        internal void StopListening()
        {
            if (!listening) return;
            listening = false;
            im_wireless_receiver_stop();
        }

        public void Dispose()
        {
            if (module == IntPtr.Zero) return;
            try
            {
                try { Stop(); }
                finally
                {
                    try { StopListening(); }
                    finally { if (initialized) im_shutdown(); }
                }
            }
            finally
            {
                initialized = false;
                FreeLibrary(module);
                module = IntPtr.Zero;
            }
        }

        static void Check(int code, string message)
        {
            if (code == 0) return;
            string detail = Marshal.PtrToStringUni(im_last_error());
            Trace.WriteLine("iPhoneMirror: " + code + " " + detail);
            throw new IOException(message + " (код " + code + ").");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Device
        {
            public uint Size, ApiVersion, DeviceId, MuxPort;
            public int State, UsbConnected, PairRecordPresent, LockdownAccessible;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ProductType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string OsVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ConnectionType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 192)] public string Description;
            public string Label { get { return string.IsNullOrWhiteSpace(Name) ? "iPhone" : Name; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CaptureOptions
        {
            public uint Size, ApiVersion, Width, Height, Fps;
            public int PlayAudio;
            public float Volume;
            public uint UsbWidth, UsbHeight, UsbMode, Decoder, Color;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CaptureStatus
        {
            public uint Size, ApiVersion;
            public int State;
            public uint Width, Height;
            public double Fps, Latency;
            public ulong VideoFrames, AudioPackets;
            public uint SampleRate, Channels;
            public int FailureKind, FailureStage, ErrorCode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 192)] public string Message;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr handle);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern uint im_api_version();
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern int im_initialize();
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern void im_shutdown();
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr im_last_error();
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        static extern int im_refresh_devices_ex([Out] Device[] devices, ref uint count, int refreshMetadata);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        static extern int im_refresh_wireless_devices([Out] Device[] devices, ref uint count);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int im_wireless_receiver_start_ex(string name, string host, uint width, uint height, uint fps);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        static extern int im_wireless_receiver_get_status(out int running, out int ready);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
        static extern void im_wireless_receiver_stop();
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int im_is_libusb0_device_available(string id, out int available);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int im_session_create(string id, ref CaptureOptions options, out ulong handle);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        static extern int im_wireless_session_create(string id, ref CaptureOptions options, out ulong handle);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern int im_session_get_status(ulong handle, ref CaptureStatus status);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern int im_session_attach_preview(ulong handle, IntPtr window);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern int im_session_stop(ulong handle);
        [DllImport(Core, CallingConvention = CallingConvention.Cdecl)] static extern void im_session_destroy(ulong handle);
    }
}
