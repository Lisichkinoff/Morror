using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Morror
{
    // Captures the default Windows output mix. It never changes the default endpoint or volume.
    public sealed class WasapiMonitorAudio : IDisposable
    {
        IAudioClient client; IAudioCaptureClient capture;
        int sourceChannels,bits,blockAlign;bool floating;
        public int SampleRate {get;private set;}
        public WasapiMonitorAudio()
        {
            var enumerator=(IMMDeviceEnumerator)new DeviceEnumerator();IMMDevice device;Check(enumerator.GetDefaultAudioEndpoint(0,0,out device));
            Guid iid=typeof(IAudioClient).GUID;object result;Check(device.Activate(ref iid,23,IntPtr.Zero,out result));client=(IAudioClient)result;
            IntPtr format;Check(client.GetMixFormat(out format));
            try
            {
                sourceChannels=(ushort)Marshal.ReadInt16(format,2);SampleRate=Marshal.ReadInt32(format,4);blockAlign=(ushort)Marshal.ReadInt16(format,12);bits=(ushort)Marshal.ReadInt16(format,14);
                int tag=(ushort)Marshal.ReadInt16(format,0);floating=tag==3 || (tag==0xfffe && Marshal.ReadInt32(format,24)==3);
                if(sourceChannels<1 || sourceChannels>16 || SampleRate<8000 || SampleRate>192000 || (bits!=16 && bits!=24 && bits!=32))throw new InvalidOperationException("Формат системного звука не поддерживается.");
                Check(client.Initialize(0,0x20000,1000000,0,format,IntPtr.Zero));
                iid=typeof(IAudioCaptureClient).GUID;Check(client.GetService(ref iid,out result));capture=(IAudioCaptureClient)result;
            }
            finally{Marshal.FreeCoTaskMem(format);Marshal.ReleaseComObject(device);Marshal.ReleaseComObject(enumerator);}
        }
        public Task Run(Func<byte[],Task> send,CancellationToken cancellation)
        {
            return Task.Run(async()=>
            {
                using(var queue=new BlockingCollection<byte[]>(4))
                {
                    Task producer=Task.Run(()=>
                    {
                        try
                        {
                            Check(client.Start());
                            while(!cancellation.IsCancellationRequested)
                            {
                                uint count;Check(capture.GetNextPacketSize(out count));
                                if(count==0){cancellation.WaitHandle.WaitOne(8);continue;}
                                IntPtr pointer;uint flags;ulong pos,time;Check(capture.GetBuffer(out pointer,out count,out flags,out pos,out time));
                                byte[] converted;
                                try
                                {
                                    if(count>SampleRate)throw new InvalidOperationException("Недопустимый аудиобуфер.");
                                    var raw=new byte[count*blockAlign];if((flags&2)==0)Marshal.Copy(pointer,raw,0,raw.Length);
                                    converted=StereoFloat(raw,(int)count,sourceChannels,bits,floating,blockAlign);
                                }
                                finally{Check(capture.ReleaseBuffer(count));}
                                if(!queue.TryAdd(converted)){byte[] old;queue.TryTake(out old);queue.TryAdd(converted);}
                            }
                        }
                        finally{client.Stop();queue.CompleteAdding();}
                    });
                    Exception failure=null;
                    try {foreach(byte[] bytes in queue.GetConsumingEnumerable(cancellation))await send(bytes).ConfigureAwait(false);}catch(Exception e){failure=e;}
                    try{await producer.ConfigureAwait(false);}catch(Exception){}
                    if(failure!=null && !cancellation.IsCancellationRequested)throw failure;
                }
            });
        }
        public static byte[] StereoFloat(byte[] raw,int frames,int channels,int bits,bool floating,int stride)
        {
            var output=new float[frames*2];int bytes=bits/8;
            for(int frame=0;frame<frames;frame++)for(int channel=0;channel<2;channel++)
            {
                int offset=frame*stride+Math.Min(channel,channels-1)*bytes;float value;
                if(floating && bits==32)value=BitConverter.ToSingle(raw,offset);
                else if(bits==16)value=BitConverter.ToInt16(raw,offset)/32768f;
                else if(bits==24){int n=raw[offset]|raw[offset+1]<<8|raw[offset+2]<<16;if((n&0x800000)!=0)n|=unchecked((int)0xff000000);value=n/8388608f;}
                else value=BitConverter.ToInt32(raw,offset)/2147483648f;
                output[frame*2+channel]=float.IsNaN(value)?0:Math.Max(-1,Math.Min(1,value));
            }
            byte[] result=new byte[output.Length*4];Buffer.BlockCopy(output,0,result,0,result.Length);return result;
        }
        static void Check(int error){Marshal.ThrowExceptionForHR(error);}
        public void Dispose(){if(capture!=null){Marshal.ReleaseComObject(capture);capture=null;}if(client!=null){Marshal.ReleaseComObject(client);client=null;}}
        [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator{}
        [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceEnumerator
        {
            [PreserveSig]int EnumAudioEndpoints(int flow,uint mask,out IntPtr collection);
            [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device);
        }
        [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDevice
        { [PreserveSig]int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)]out object result); }
        [ComImport,Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioClient
        {
            [PreserveSig]int Initialize(int mode,uint flags,long duration,long period,IntPtr format,IntPtr session);
            [PreserveSig]int GetBufferSize(out uint frames);[PreserveSig]int GetStreamLatency(out long latency);[PreserveSig]int GetCurrentPadding(out uint frames);
            [PreserveSig]int IsFormatSupported(int mode,IntPtr format,out IntPtr closest);[PreserveSig]int GetMixFormat(out IntPtr format);[PreserveSig]int GetDevicePeriod(out long normal,out long minimum);
            [PreserveSig]int Start();[PreserveSig]int Stop();[PreserveSig]int Reset();[PreserveSig]int SetEventHandle(IntPtr handle);
            [PreserveSig]int GetService(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)]out object result);
        }
        [ComImport,Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioCaptureClient
        {
            [PreserveSig]int GetBuffer(out IntPtr data,out uint frames,out uint flags,out ulong position,out ulong timestamp);
            [PreserveSig]int ReleaseBuffer(uint frames);[PreserveSig]int GetNextPacketSize(out uint frames);
        }
    }
}
