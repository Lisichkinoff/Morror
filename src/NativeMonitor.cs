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
    // FFmpeg writes one encoded H.264 access unit per AVI video chunk.
    // The length lets us send the frame immediately, without reading the next frame.
    internal sealed class MonitorVideoReader
    {
        readonly Stream stream;
        bool started;
        internal MonitorVideoReader(Stream stream) { this.stream = stream; }

        internal async Task<byte[]> ReadFrame(CancellationToken cancellation)
        {
            if (!started)
            {
                byte[] riff = await MonitorWire.Read(stream, 12, cancellation).ConfigureAwait(false);
                if (Tag(riff, 0) != "RIFF" || Tag(riff, 8) != "AVI ")
                    throw new IOException("Кодировщик вернул неизвестный формат видео.");

                while (true)
                {
                    byte[] header = await MonitorWire.Read(stream, 8, cancellation).ConfigureAwait(false);
                    uint length = BitConverter.ToUInt32(header, 4);
                    if (Tag(header, 0) == "LIST" && length >= 4)
                    {
                        byte[] kind = await MonitorWire.Read(stream, 4, cancellation).ConfigureAwait(false);
                        if (Tag(kind, 0) == "movi") break;
                        length -= 4;
                    }
                    if (length > 65536) throw new IOException("Заголовок видео слишком большой.");
                    await MonitorWire.Read(stream, (int)(length + (length & 1)), cancellation).ConfigureAwait(false);
                }
                started = true;
            }
            while (true)
            {
                byte[] header = await MonitorWire.Read(stream, 8, cancellation).ConfigureAwait(false);
                uint length = BitConverter.ToUInt32(header, 4);
                if (Tag(header, 0) != "00dc" || length > MonitorWire.MaximumPacket - 9)
                    throw new IOException("Кодировщик вернул недопустимый видеокадр.");
                if (length == 0) continue;
                byte[] frame = await MonitorWire.Read(stream, (int)length, cancellation).ConfigureAwait(false);
                if ((length & 1) != 0) await MonitorWire.Read(stream, 1, cancellation).ConfigureAwait(false);
                return frame;
            }
        }

        static string Tag(byte[] bytes, int offset) { return Encoding.ASCII.GetString(bytes, offset, 4); }
        internal static bool HasNal(byte[] frame, int type)
        {
            for (int i = 2; i + 1 < frame.Length; i++)
                if (frame[i] == 1 && frame[i - 1] == 0 && frame[i - 2] == 0 && (frame[i + 1] & 31) == type)
                    return true;
            return false;
        }
    }

    internal sealed class MonitorVideoQueue : IDisposable
    {
        const int MaxFrames = 3;
        readonly Queue<Tuple<byte[], long>> pending = new Queue<Tuple<byte[], long>>();
        readonly SemaphoreSlim ready = new SemaphoreSlim(0, 1);
        bool needsKeyframe = true;

        internal void Add(byte[] frame, long timestamp)
        {
            lock (pending)
            {
                if (pending.Count >= MaxFrames)
                {
                    pending.Clear();
                    needsKeyframe = true;
                }
                // A delta picture may reference any of the discarded pictures.
                if (needsKeyframe && !MonitorVideoReader.HasNal(frame, 5)) return;
                needsKeyframe = false;
                pending.Enqueue(Tuple.Create(frame, timestamp));
                if (ready.CurrentCount == 0) ready.Release();
            }
        }

        internal async Task Send(Func<byte[], long, Task> send, CancellationToken cancellation)
        {
            while (true)
            {
                await ready.WaitAsync(cancellation).ConfigureAwait(false);
                while (!cancellation.IsCancellationRequested)
                {
                    Tuple<byte[], long> frame;
                    lock (pending)
                    {
                        if (pending.Count == 0) break;
                        frame = pending.Dequeue();
                    }
                    await send(frame.Item1, frame.Item2).ConfigureAwait(false);
                }
            }
        }

        public void Dispose() { ready.Dispose(); }
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
                args.AddRange(EncoderArguments(codec));args.AddRange(VideoArguments(width,height,fps));args.AddRange(new[]{"-f","null","-"});
                CommandResult result=null;
                try {result=await Commands.RunCancellable(executable,8000,cancellation,args.ToArray()).ConfigureAwait(false);}catch(TimeoutException){}
                if(result!=null && result.ExitCode==0)return codec;
            }
            throw new IOException("Не найден рабочий H.264-кодировщик NVIDIA/Media Foundation. Обновите драйвер видеокарты и проверьте компоненты мультимедиа Windows.");
        }
        static string[] CaptureArguments(MonitorCapture capture, int fps)
        {
            return new[]{"-init_hw_device","d3d11va=monitor:"+capture.Adapter,"-filter_hw_device","monitor",
                "-filter_complex","ddagrab=output_idx="+capture.Output+":framerate="+fps};
        }
        internal static async Task<MonitorCapture> SelectCapture(string executable,string codec,ScreenChoice screen,int width,int height,int fps)
        {
            if (codec != "nvenc" || screen.Bounds.Width != width || screen.Bounds.Height != height) return null;
            var capture = MonitorCapture.Find(screen);
            if (capture == null) return null;
            var args = new List<string>{"-hide_banner","-loglevel","error","-nostdin"};
            args.AddRange(CaptureArguments(capture,fps));
            args.AddRange(EncoderArguments(codec));
            args.AddRange(VideoArguments(width,height,fps));
            args.AddRange(new[]{"-frames:v","5","-f","null","-"});
            try
            {
                var result = await Commands.Run(executable,2500,args.ToArray()).ConfigureAwait(false);
                return result.ExitCode == 0 ? capture : null;
            }
            catch (TimeoutException) { return null; }
        }
        internal Stream Start(string executable,string codec,Rectangle bounds,int width,int height,int fps,bool synthetic,MonitorCapture capture)
        {
            var args=new List<string>{"-hide_banner","-loglevel","warning","-nostdin"};
            if(capture!=null)args.AddRange(CaptureArguments(capture,fps));
            else
            {
                if(synthetic)args.AddRange(new[]{"-re","-f","lavfi","-i","testsrc2=s="+width+"x"+height+":r="+fps});
                else args.AddRange(new[]{"-thread_queue_size","1","-probesize","32","-analyzeduration","0","-f","gdigrab","-framerate",fps.ToString(),"-draw_mouse","1","-offset_x",bounds.X.ToString(),"-offset_y",bounds.Y.ToString(),"-video_size",bounds.Width+"x"+bounds.Height,"-i","desktop"});
                args.AddRange(new[]{"-vf",(bounds.Width==width&&bounds.Height==height?"":"scale="+width+":"+height+":flags=fast_bilinear,")+"format=nv12"});
            }
            args.Add("-an");args.AddRange(EncoderArguments(codec));
            args.AddRange(VideoArguments(width,height,fps));
            args.AddRange(new[]{"-bsf:v","dump_extra","-flush_packets","1","-fps_mode","passthrough","-f","avi","pipe:1"});
            process=new Process{StartInfo=Commands.StartInfo(executable,args)};
            process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)LastError=e.Data.Length>1024?e.Data.Substring(0,1024):e.Data;};
            process.Start();process.BeginErrorReadLine();return process.StandardOutput.BaseStream;
        }
        static string[] VideoArguments(int width,int height,int fps)
        {
            int bitrate = (int)Math.Min(18000, Math.Max(2000, (long)width * height * fps / 20000));
            return new[]{"-flags","+low_delay","-b:v",bitrate+"k","-bufsize",Math.Max(200,bitrate/8)+"k","-g",(fps/2).ToString(),"-bf","0"};
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
        readonly MonitorCapture capture;
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
            : this(executable,codec,screen,width,height,fps,audioEnabled,touch,release,synthetic,null) { }
        internal NativeMonitorServer(string executable,string codec,ScreenChoice screen,int width,int height,int fps,bool audioEnabled,Func<ScreenTouchFrame,Task> touch,Func<Task> release,bool synthetic,MonitorCapture capture)
        {this.executable=executable;this.codec=codec;this.screen=screen;this.width=width;this.height=height;this.fps=fps;this.audioEnabled=audioEnabled;this.touch=touch;this.release=release;this.synthetic=synthetic;this.capture=capture;}
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
                connection.NoDelay = true;
                connection.SendBufferSize = 64 * 1024;
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
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                        {
                            deadline.CancelAfter(2500);
                            using (deadline.Token.Register(() => connection.Close()))
                            {
                                byte[] header=MonitorWire.Header(type,body.Length,pts);
                                await stream.WriteAsync(header, 0, header.Length, deadline.Token).ConfigureAwait(false);
                                await stream.WriteAsync(body, 0, body.Length, deadline.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    finally{writer.Release();}
                };
                byte[] hello=Encoding.UTF8.GetBytes(SecondScreenProtocol.Json().Serialize(new{width=width,height=height,fps=fps,sampleRate=audio==null?48000:audio.SampleRate,channels=2,floatAudio=true}));
                try { await send(0,hello,0).ConfigureAwait(false); }
                catch { if (audio != null) audio.Dispose(); throw; }
                var stopwatch=Stopwatch.StartNew();
                Task controls=Controls(stream,session.Token);
                Task audioTask=audio==null?Task.FromResult(0):audio.Run((bytes)=>send(2,bytes,stopwatch.ElapsedTicks*1000000/Stopwatch.Frequency),session.Token);
                using (var queue = new MonitorVideoQueue())
                {
                    Task sendVideo = queue.Send((frame, pts) => send(1, frame, pts), session.Token);
                    Exception failure = null;
                    try
                    {
                        Stream video = encoder.Start(executable, codec, screen.Bounds, width, height, fps, synthetic, capture);
                        lock (gate) status = "Монитор подключён · " + width + " × " + height + " · " + fps + " FPS · " + (codec == "mf-sw" ? "программное кодирование" : "аппаратное кодирование");
                        await ReadVideo(encoder, video, queue, controls, sendVideo, stopwatch, session.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) { failure = error; }
                    session.Cancel();
                    encoder.Dispose();
                    try { await Task.WhenAll(sendVideo, controls, audioTask).ConfigureAwait(false); } catch (Exception) { }
                    if (audio != null) audio.Dispose();
                    await release().ConfigureAwait(false);
                    lock (gate) status = failure == null ? "Телефон отключён." : "Передача остановлена: " + failure.Message;
                }
            }
        }

        static async Task ReadVideo(NativeMonitorEncoder encoder, Stream video, MonitorVideoQueue queue, Task controls,
            Task sendVideo, Stopwatch clock, CancellationToken cancellation)
        {
            using (var buffered = new BufferedStream(video, 65536))
            {
                var reader = new MonitorVideoReader(buffered);
                while (!cancellation.IsCancellationRequested)
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        Task<byte[]> read = reader.ReadFrame(cancellation);
                        Task completed = await Task.WhenAny(read, controls, sendVideo, Task.Delay(5000, deadline.Token)).ConfigureAwait(false);
                        deadline.Cancel();
                        if (completed == read)
                        {
                            byte[] frame;
                            try { frame = await read.ConfigureAwait(false); }
                            catch (EndOfStreamException error) { throw new IOException("Кодировщик остановлен. " + encoder.LastError, error); }
                            queue.Add(frame, clock.ElapsedTicks * 1000000 / Stopwatch.Frequency);
                            continue;
                        }

                        // Closing the encoder's pipe interrupts a pending Windows pipe read.
                        encoder.Dispose();
                        try { await read.ConfigureAwait(false); } catch (Exception) { }
                        cancellation.ThrowIfCancellationRequested();
                        if (completed == controls || completed == sendVideo)
                        {
                            await completed.ConfigureAwait(false);
                            throw new EndOfStreamException("Телефон отключён.");
                        }
                        throw new IOException("Кодировщик не выдаёт кадры. " + encoder.LastError);
                    }
                }
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
                    heartbeat.CancelAfter(3000);
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
