using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Morror
{
    public static class MonitorWire
    {
        public const int MaximumPacket = 4*1024*1024;
        public static byte[] Header(int type,int size,long pts)
        {
            if(size<0 || size>MaximumPacket-9) throw new IOException("Слишком большой пакет монитора.");
            byte[] b=new byte[13];uint length=(uint)(size+9);
            for(int i=0;i<4;i++) b[i]=(byte)(length>>(24-8*i));b[4]=(byte)type;
            for(int i=0;i<8;i++) b[i+5]=(byte)((ulong)pts>>(56-8*i));return b;
        }
        public static async Task<byte[]> Read(Stream stream,int count,CancellationToken cancellation)
        {
            if(count<0 || count>MaximumPacket) throw new IOException("Недопустимый размер пакета.");
            byte[] bytes=new byte[count];for(int offset=0;offset<count;) { int n=await stream.ReadAsync(bytes,offset,count-offset,cancellation).ConfigureAwait(false);if(n==0)throw new EndOfStreamException();offset+=n; }return bytes;
        }
        public static int Length(byte[] b) { long n=((long)b[0]<<24)|((long)b[1]<<16)|((long)b[2]<<8)|b[3];if(n<9 || n>MaximumPacket)throw new IOException("Недопустимый размер пакета.");return (int)n; }
    }
    // Annex B NAL and AUD boundaries can cross arbitrary TCP/pipe read boundaries.
    public sealed class H264AccessUnits
    {
        readonly List<byte> pending=new List<byte>();
        readonly MemoryStream unit=new MemoryStream(); bool hasPicture;
        int scan,nalStart=-1;
        public static int Prefix(IList<byte> b,int i)
        { if(i+3>b.Count || b[i]!=0 || b[i+1]!=0)return 0;if(b[i+2]==1)return 3;return i+4<=b.Count && b[i+2]==0 && b[i+3]==1?4:0; }
        public List<byte[]> Push(byte[] data,int count,bool end=false)
        {
            for(int i=0;i<count;i++)pending.Add(data[i]);if(pending.Count>MonitorWire.MaximumPacket)throw new IOException("Поток H.264 повреждён.");
            var frames=new List<byte[]>();
            while(scan+4<=pending.Count)
            {
                int prefix=Prefix(pending,scan);if(prefix==0){scan++;continue;}
                if(nalStart>=0) AddNal(pending.GetRange(nalStart,scan-nalStart).ToArray(),frames);
                nalStart=scan;scan+=prefix;
            }
            if(nalStart>0){pending.RemoveRange(0,nalStart);scan-=nalStart;nalStart=0;}
            if(end)
            {
                if(pending.Count>0)AddNal(pending.ToArray(),frames);
                pending.Clear();scan=0;nalStart=-1;
                if(hasPicture){frames.Add(unit.ToArray());unit.SetLength(0);hasPicture=false;}
            }
            return frames;
        }
        void AddNal(byte[] nal,List<byte[]> frames)
        {
            int prefix=Prefix(nal,0);if(prefix==0 || nal.Length<=prefix)return;int type=nal[prefix]&31;
            if(type==9 && hasPicture){frames.Add(unit.ToArray());unit.SetLength(0);hasPicture=false;}
            unit.Write(nal,0,nal.Length);if(unit.Length>MonitorWire.MaximumPacket-9)throw new IOException("Кадр H.264 слишком большой.");
            if(type==1 || type==5)hasPicture=true;
        }
    }
    public sealed class NativeMonitorEncoder : IDisposable
    {
        Process process;
        public string LastError {get;private set;}
        public static string[] EncoderArguments(string codec)
        {
            if(codec=="nvenc")return new[]{"-c:v","h264_nvenc","-preset","p1","-tune","ull","-zerolatency","1","-delay","0","-rc-lookahead","0","-rc","cbr"};
            if(codec=="mf-hw")return new[]{"-c:v","h264_mf","-hw_encoding","1","-scenario","display_remoting","-rate_control","cbr"};
            if(codec=="mf-sw")return new[]{"-c:v","h264_mf","-hw_encoding","0","-scenario","display_remoting","-rate_control","cbr"};
            throw new ArgumentException("Неизвестный кодировщик.");
        }
        public static async Task<string> Select(string executable,CancellationToken cancellation,int width=1920,int height=1080,int fps=60)
        {
            MonitorResolution.Validate(width,height,fps);
            foreach(string codec in new[]{"nvenc","mf-hw","mf-sw"})
            {
                // NVENC rejects 128x128 even on RTX 4070. Probe the session's real
                // dimensions, pixel format and bitrate before considering CPU fallback.
                var args=new List<string>{"-hide_banner","-loglevel","error","-f","lavfi","-i","color=black:s="+width+"x"+height+":r="+fps,"-frames:v","3","-vf","format=nv12"};
                args.AddRange(EncoderArguments(codec));args.AddRange(new[]{"-b:v","10M","-g",fps.ToString(),"-bf","0","-f","null","-"});
                CommandResult result=null;
                try {result=await Commands.RunCancellable(executable,8000,cancellation,args.ToArray()).ConfigureAwait(false);}catch(TimeoutException){}
                if(result!=null && result.ExitCode==0)return codec;
            }
            throw new IOException("Не найден рабочий H.264-кодировщик NVIDIA/Media Foundation. Обновите драйвер видеокарты и проверьте компоненты мультимедиа Windows.");
        }
        public Stream Start(string executable,string codec,Rectangle bounds,int width,int height,int fps,bool synthetic=false)
        {
            var args=new List<string>{"-hide_banner","-loglevel","warning","-nostdin"};
            if(synthetic)args.AddRange(new[]{"-re","-f","lavfi","-i","testsrc2=s="+width+"x"+height+":r="+fps});
            else args.AddRange(new[]{"-thread_queue_size","2","-rtbufsize","128M","-probesize","32","-analyzeduration","0","-f","gdigrab","-framerate",fps.ToString(),"-draw_mouse","1","-offset_x",bounds.X.ToString(),"-offset_y",bounds.Y.ToString(),"-video_size",bounds.Width+"x"+bounds.Height,"-i","desktop"});
            args.AddRange(new[]{"-an","-vf",(bounds.Width==width&&bounds.Height==height?"":"scale="+width+":"+height+":flags=fast_bilinear,")+"format=nv12"});args.AddRange(EncoderArguments(codec));
            args.AddRange(new[]{"-b:v","10M","-g",fps.ToString(),"-bf","0","-bsf:v","h264_metadata=aud=insert","-flush_packets","1","-fps_mode","passthrough","-f","h264","pipe:1"});
            process=new Process{StartInfo=Commands.StartInfo(executable,args)};
            process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)LastError=e.Data.Length>1024?e.Data.Substring(0,1024):e.Data;};
            process.Start();process.BeginErrorReadLine();return process.StandardOutput.BaseStream;
        }
        public void Dispose()
        {
            if(process==null)return;
            try{if(!process.HasExited)process.Kill();process.WaitForExit(2000);}
            catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            process.Dispose();process=null;
        }
    }
    public sealed class NativeMonitorServer : IDisposable
    {
        readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly Func<ScreenTouchFrame,Task> touch;
        readonly Func<Task> release;
        readonly string executable,codec;
        readonly ScreenChoice screen;
        readonly int width,height,fps;
        readonly bool audioEnabled,synthetic;
        TcpClient client;
        Task runner;
        readonly TaskCompletionSource<string> completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        volatile bool authenticated;
        readonly object gate=new object();
        string status="Ожидаем Android-клиент…";
        public readonly string Token=SecondScreenProtocol.Token();
        public int Port {get;private set;}
        public int DecodedFrames {get;private set;}
        public Task<string> Completion {get{return completion.Task;}}
        public bool HasClient {get{return authenticated;}}
        public string Status {get{lock(gate)return status;}}
        public NativeMonitorServer(string executable,string codec,ScreenChoice screen,int width,int height,int fps,bool audioEnabled,Func<ScreenTouchFrame,Task> touch,Func<Task> release,bool synthetic=false)
        {this.executable=executable;this.codec=codec;this.screen=screen;this.width=width;this.height=height;this.fps=fps;this.audioEnabled=audioEnabled;this.touch=touch;this.release=release;this.synthetic=synthetic;}
        static void PreventHandleInheritance(Socket socket)
        {
            if(!SetHandleInformation(socket.Handle,1,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
        public void Start()
        {
            listener.Start(2);
            try{PreventHandleInheritance(listener.Server);Port=((IPEndPoint)listener.LocalEndpoint).Port;runner=Accept();}
            catch{listener.Stop();throw;}
        }
        public async Task Stop(){Dispose();if(runner!=null)await runner.ConfigureAwait(false);}
        async Task Accept()
        {
            while(!lifetime.IsCancellationRequested)
            {
                TcpClient incoming=null;
                try
                {
                    incoming=await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    PreventHandleInheritance(incoming.Client);
                    lock(gate){if(client!=null){incoming.Close();continue;}client=incoming;}
                    await Session(incoming).ConfigureAwait(false);
                }
                catch(ObjectDisposedException){break;}
                catch(SocketException){if(lifetime.IsCancellationRequested)break;}
                catch(Exception e){lock(gate)status="Передача прервана: "+e.Message;}
                finally{if(incoming!=null)incoming.Close();lock(gate)client=null;}
                // A disconnected authenticated receiver ends the owned display session.
                // Invalid handshakes may be retried until the legitimate client connects.
                if(authenticated)break;
            }
            listener.Stop();completion.TrySetResult(Status);
        }
        async Task Session(TcpClient connection)
        {
            using(connection)
            using(var session=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            using(session.Token.Register(()=>connection.Close()))
            using(var encoder=new NativeMonitorEncoder())
            using(var writer=new SemaphoreSlim(1,1))
            {
                connection.NoDelay=true;connection.SendTimeout=5000;connection.ReceiveTimeout=15000;
                var stream=connection.GetStream();
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    timeout.CancelAfter(5000);
                    using(timeout.Token.Register(()=>connection.Close()))
                    {
                        byte[] magic=await MonitorWire.Read(stream,8,timeout.Token).ConfigureAwait(false);
                        string token=Encoding.ASCII.GetString(await MonitorWire.Read(stream,64,timeout.Token).ConfigureAwait(false));
                        if(Encoding.ASCII.GetString(magic)!="MRRMN001" || !SecondScreenProtocol.EqualToken(Token,token))throw new IOException("Недействительный ключ Android-клиента.");
                        authenticated=true;
                    }
                }
                WasapiMonitorAudio audio=null;
                if(audioEnabled) {try{audio=await Task.Run(()=>new WasapiMonitorAudio()).ConfigureAwait(false);}catch(Exception){lock(gate)status="Видео подключено; системный звук недоступен.";}}
                Func<int,byte[],long,Task> send=async(type,body,pts)=>
                {
                    await writer.WaitAsync(session.Token).ConfigureAwait(false);
                    try
                    {
                        byte[] header=MonitorWire.Header(type,body.Length,pts);
                        Task write=stream.WriteAsync(header,0,header.Length,session.Token);
                        if(await Task.WhenAny(write,Task.Delay(5000,session.Token)).ConfigureAwait(false)!=write){session.Cancel();throw new IOException("Телефон перестал принимать данные.");}await write.ConfigureAwait(false);
                        write=stream.WriteAsync(body,0,body.Length,session.Token);
                        if(await Task.WhenAny(write,Task.Delay(5000,session.Token)).ConfigureAwait(false)!=write){session.Cancel();throw new IOException("Телефон перестал принимать видео.");}await write.ConfigureAwait(false);
                    }
                    finally{writer.Release();}
                };
                byte[] hello=Encoding.UTF8.GetBytes(SecondScreenProtocol.Json().Serialize(new{width=width,height=height,fps=fps,sampleRate=audio==null?48000:audio.SampleRate,channels=2,floatAudio=true}));
                await send(0,hello,0).ConfigureAwait(false);
                var stopwatch=Stopwatch.StartNew();
                Task controls=Controls(stream,session.Token);
                Task audioTask=audio==null?Task.FromResult(0):audio.Run((bytes)=>send(2,bytes,stopwatch.ElapsedTicks*1000000/Stopwatch.Frequency),session.Token);
                Exception failure=null;
                try
                {
                    Stream video=encoder.Start(executable,codec,screen.Bounds,width,height,fps,synthetic);
                    lock(gate)status="Монитор подключён · "+width+" × "+height+" · "+fps+" FPS · "+(codec=="mf-sw"?"программное кодирование":"аппаратное кодирование");
                    var parser=new H264AccessUnits();var buffer=new byte[32768];
                    while(!session.IsCancellationRequested)
                    {
                        Task<int> read=video.ReadAsync(buffer,0,buffer.Length,session.Token);
                        Task completed=await Task.WhenAny(read,controls,Task.Delay(12000,session.Token)).ConfigureAwait(false);
                        if(completed==controls){await controls.ConfigureAwait(false);throw new EndOfStreamException("Телефон отключён.");}
                        if(completed!=read)throw new IOException("Кодировщик не выдаёт кадры. "+encoder.LastError);
                        int count=await read.ConfigureAwait(false);if(count==0)throw new IOException("Кодировщик остановлен. "+encoder.LastError);
                        foreach(byte[] frame in parser.Push(buffer,count))await send(1,frame,stopwatch.ElapsedTicks*1000000/Stopwatch.Frequency).ConfigureAwait(false);
                    }
                }
                catch(Exception e){failure=e;}
                session.Cancel();encoder.Dispose();
                try{await controls.ConfigureAwait(false);}catch(Exception){}
                try{await audioTask.ConfigureAwait(false);}catch(Exception){}
                if(audio!=null)audio.Dispose();
                await release().ConfigureAwait(false);
                lock(gate)status=failure==null?"Телефон отключён. Ожидаем подключение…":"Передача остановлена: "+failure.Message;
            }
        }
        async Task Controls(Stream stream,CancellationToken cancellation)
        {
            var state=new ScreenTouchState();
            var statsClock=Stopwatch.StartNew();int lastFrames=0;long lastStats=0;
            while(!cancellation.IsCancellationRequested)
            {
                byte[] packet;
                using(var heartbeat=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    heartbeat.CancelAfter(6000);
                    // NetworkStream.ReadAsync on .NET Framework does not reliably abort
                    // an already pending read from cancellation alone.
                    using(heartbeat.Token.Register(()=>stream.Close()))
                    {
                        byte[] size=await MonitorWire.Read(stream,4,heartbeat.Token).ConfigureAwait(false);int length=MonitorWire.Length(size);
                        if(length>16384)throw new IOException("Слишком большой пакет управления.");
                        packet=await MonitorWire.Read(stream,length,heartbeat.Token).ConfigureAwait(false);
                    }
                }
                if(packet[0]==17)continue;
                if(packet[0]==18)
                {
                    var stats=SecondScreenProtocol.Json().Deserialize<Dictionary<string,int>>(Encoding.UTF8.GetString(packet,9,packet.Length-9));int frames;
                    if(stats.TryGetValue("frames",out frames)&&frames>=DecodedFrames)
                    {
                        DecodedFrames=frames;long now=statsClock.ElapsedMilliseconds;
                        if(now-lastStats>=500)
                        {
                            double measured=(frames-lastFrames)*1000.0/(now-lastStats);
                            lock(gate)status="Монитор · "+width+" × "+height+" · "+measured.ToString("0.0")+" FPS на Android · "+(codec=="mf-sw"?"кодирование на CPU":"аппаратное кодирование");
                            lastFrames=frames;lastStats=now;
                        }
                    }
                    continue;
                }
                if(packet[0]!=16)throw new IOException("Неизвестный пакет управления.");
                var frame=SecondScreenProtocol.Json().Deserialize<ScreenTouchFrame>(Encoding.UTF8.GetString(packet,9,packet.Length-9));
                if(!state.Accept(frame))throw new IOException("Нарушен порядок касаний.");
                await touch(frame).ConfigureAwait(false);
            }
        }
        public void Dispose(){lifetime.Cancel();listener.Stop();lock(gate){if(client!=null)client.Close();}}
    }
}
