using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Morror
{
    internal sealed class MonitorApkDownload : IDisposable
    {
        readonly TcpListener listener;
        readonly byte[] apk;
        readonly string route = "/" + SecondScreenProtocol.Token() + "/MorrorMonitor.apk";
        readonly CancellationTokenSource lifetime;
        readonly CancellationTokenRegistration stopListener;
        int disposed;

        internal MonitorApkDownload(IPAddress address, string path)
        {
            using (var file = File.OpenRead(path))
            {
                if (file.Length == 0 || file.Length > 8 * 1024 * 1024)
                    throw new IOException("Файл MorrorMonitor.apk имеет недопустимый размер.");
                apk = new byte[(int)file.Length];
                using (var reader = new BinaryReader(file))
                    if (reader.Read(apk, 0, apk.Length) != apk.Length) throw new EndOfStreamException("Файл APK изменился во время чтения.");
            }
            listener = new TcpListener(address, 0);
            listener.Start(4);
            Url = new UriBuilder("http", address.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, route).Uri;
            lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            stopListener = lifetime.Token.Register(listener.Stop);
            Completion = Serve(lifetime.Token);
        }

        internal Uri Url { get; private set; }
        internal Task Completion { get; private set; }
        internal Uri PhoneUrl(int phonePort)
        {
            return new UriBuilder(Url) { Host = "127.0.0.1", Port = phonePort }.Uri;
        }

        async Task Serve(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (cancellation.IsCancellationRequested) return; throw; }

                using (client)
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    using (deadline.Token.Register(client.Close))
                    {
                        try { await Respond(client.GetStream(), deadline.Token).ConfigureAwait(false); }
                        catch (IOException) { }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { }
                        catch (OperationCanceledException) { }
                    }
                }
            }
        }

        async Task Respond(Stream stream, CancellationToken cancellation)
        {
            var request = new byte[8192];
            int length = 0;
            int headerEnd = -1;
            while (length < request.Length && headerEnd < 0)
            {
                int count = await stream.ReadAsync(request, length, request.Length - length, cancellation).ConfigureAwait(false);
                if (count == 0) return;
                int start = Math.Max(0, length - 3);
                length += count;
                for (int i = start; i < length - 3; i++)
                    if (request[i] == 13 && request[i + 1] == 10 && request[i + 2] == 13 && request[i + 3] == 10)
                    { headerEnd = i; break; }
            }

            int lineEnd = headerEnd < 0 ? 0 : Array.IndexOf(request, (byte)13, 0, headerEnd + 1);
            var line = Encoding.ASCII.GetString(request, 0, lineEnd).Split(' ');
            bool valid = line.Length == 3 && (line[0] == "GET" || line[0] == "HEAD") && line[1] == route &&
                (line[2] == "HTTP/1.1" || line[2] == "HTTP/1.0");
            string header = valid
                ? "HTTP/1.1 200 OK\r\nContent-Type: application/vnd.android.package-archive\r\nContent-Disposition: attachment; filename=\"MorrorMonitor.apk\"\r\nContent-Length: " + apk.Length + "\r\n"
                : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n";
            var bytes = Encoding.ASCII.GetBytes(header + "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
            if (valid && line[0] == "GET") await stream.WriteAsync(apk, 0, apk.Length, cancellation).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            stopListener.Dispose();
            Completion.ContinueWith(task => lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
