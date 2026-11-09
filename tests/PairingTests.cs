using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Morror;

class PairingTests
{
    static int count;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
    static CommandResult Reply(string text, int exitCode = 0) { return new CommandResult { ExitCode = exitCode, Output = text, Error = "" }; }
    static Task<CommandResult> Done(string text, int exitCode = 0) { return Task.FromResult(Reply(text, exitCode)); }

    [STAThread]
    static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine(count + " QR pairing checks passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static async Task Run()
    {
        var session = new QrSession(); var another = new QrSession();
        Check(session.ServiceName != another.ServiceName && session.Password != another.Password && session.Password.Length == 32, "QR sessions have fresh cryptographic names and secrets");
        Check(session.Payload == "WIFI:T:ADB;S:" + session.ServiceName + ";P:" + session.Password + ";;", "QR payload uses the AOSP Android pairing format");
        bool decoded = true;
        for (int sample = 0; sample < 3; sample++)
        {
            var current = new QrSession();
            var image = (BitmapSource)QrPairingDialog.Render(current.Payload);
            using (var stream = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream); stream.Position = 0;
                using (var bitmap = new Bitmap(stream))
                {
                    var reader = new ZXing.BarcodeReader { AutoRotate = true };
                    var result = reader.Decode(bitmap);
                    decoded &= result != null && result.Text == current.Payload;
                }
            }
        }
        Check(decoded, "Independent ZXing decoder reads the exact payload from generated QR images");
        var services = WirelessPairing.ParseServices("List of discovered mdns services\r\n" + session.ServiceName + " _adb-tls-pairing._tcp. 192.168.1.20:37123\nold _adb._tcp 192.168.1.20:5555\nbad _adb-tls-connect._tcp 999.1.1.1:42\nfull._adb-tls-connect._tcp. _adb-tls-connect._tcp 192.168.1.20:40000\n");
        Check(services.Count == 2 && services[0].Name == session.ServiceName && services[1].Name == "full", "mDNS parser handles suffixes and ignores invalid or legacy services");
        var calls = new List<string[]>();
        var engine = new WirelessPairing((args, token) =>
        {
            calls.Add(args);
            if (args[0] == "mdns") return Done("someone-else _adb-tls-pairing._tcp 192.168.1.30:12345\n" + session.ServiceName + " _adb-tls-pairing._tcp 192.168.1.20:37123\nother _adb-tls-connect._tcp 192.168.1.30:44000\nstale _adb-tls-connect._tcp 192.168.1.20:40000\nvalid _adb-tls-connect._tcp 192.168.1.20:40001\n");
            if (args[0] == "pair") return Done("Successfully paired to 192.168.1.20:37123");
            if (args[0] == "connect") return args[1].EndsWith(":40001") ? Done("connected to " + args[1]) : Done("cannot connect", 1);
            return Done("device\n");
        }, token => Task.FromResult(0));
        var paired = await engine.Pair(session, status => { }, CancellationToken.None);
        Check(paired.Connected && paired.Address == "192.168.1.20:40001", "QR flow pairs and verifies an actual ready TLS connection");
        Check(calls.Count(c => c[0] == "pair") == 1 && calls.First(c => c[0] == "pair")[2] == session.Password, "Only the matching QR service receives the session secret");
        Check(!calls.Any(c => c[0] == "connect" && c[1].StartsWith("192.168.1.30")), "Auto-connect does not target another phone in the same network");
        Check(calls.Any(c => c[0] == "connect" && c[1].EndsWith(":40000")) && calls.Any(c => c[0] == "connect" && c[1].EndsWith(":40001")), "Stale ports do not block trying the current connection port");
        calls.Clear();
        var manual = await engine.ConnectHost("192.168.1.20:37123", CancellationToken.None);
        Check(manual.Connected && !calls.Any(c => c[0] == "pair") && !calls.Any(c => c[0] == "connect" && c[1].StartsWith("192.168.1.30")), "Code pairing reuses only the matching phone connection service without pairing again");
        var staleTimeout = new WirelessPairing((args, token) =>
        {
            if (args[0] == "mdns") return Done("stale _adb-tls-connect._tcp 192.168.1.20:40000\nvalid _adb-tls-connect._tcp 192.168.1.20:40001");
            if (args[0] == "connect" && args[1].EndsWith(":40000")) throw new TimeoutException();
            return Done(args[0] == "connect" ? "already connected to " + args[1] : "device");
        }, token => Task.FromResult(0));
        Check((await staleTimeout.ConnectHost("192.168.1.20:37123", CancellationToken.None)).Connected, "A timed-out stale port does not prevent the current Wi-Fi port from connecting");
        var offline = new WirelessPairing((args, token) => Done(args[0] == "connect" ? "connected to " + args[1] : "offline"), token => Task.FromResult(0));
        Check(!await offline.Connect("192.168.1.20:5555", CancellationToken.None), "A connect success message with an offline transport is not accepted");
        Check(WirelessPairing.WifiAddress("1: lo inet 127.0.0.1/8\n2: rmnet0 inet 10.1.1.8/24\n3: tun0 inet 10.9.1.1/24\n4: wlan0 inet 192.168.1.20/24 brd 192.168.1.255") == "192.168.1.20:5555", "USB to Wi-Fi chooses the Wi-Fi interface instead of VPN, mobile or loopback");
        bool noWifi = false;
        try { WirelessPairing.WifiAddress("2: rmnet0 inet 10.1.1.8/24"); } catch (InvalidOperationException) { noWifi = true; }
        Check(noWifi, "USB to Wi-Fi requires a Wi-Fi address");
        Check(WirelessPairing.ParseServices("ipv6 _adb-tls-connect._tcp [2001:db8::20]:40001").Single().Host == "[2001:db8::20]", "mDNS keeps IPv6 services available for pairing and connection");
        bool canceled = false; int pairCalls = 0;
        var waiting = new WirelessPairing((args, token) => { if (args[0] == "pair") pairCalls++; return Done("List of discovered mdns services\n"); }, token => Task.Delay(10, token));
        using (var source = new CancellationTokenSource(60))
        { try { await waiting.Pair(session, status => { }, source.Token); } catch (OperationCanceledException) { canceled = true; } }
        Check(canceled && pairCalls == 0, "Cancel stops discovery before any pairing attempt");
        var pairedOnly = new WirelessPairing((args, token) => args[0] == "pair" ? Done("Successfully paired") : Done(session.ServiceName + " _adb-tls-pairing._tcp 192.168.1.20:37123"), token => Task.FromResult(0));
        Check(!(await pairedOnly.Pair(session, status => { }, CancellationToken.None)).Connected, "A missing connection service is reported as paired but not connected");
        bool failure = false;
        var denied = new WirelessPairing((args, token) => args[0] == "pair" ? Done("failed to pair") : Done(session.ServiceName + " _adb-tls-pairing._tcp 192.168.1.20:37123"), token => Task.FromResult(0));
        try { await denied.Pair(session, status => { }, CancellationToken.None); } catch (InvalidOperationException) { failure = true; }
        Check(failure, "A zero exit code alone cannot turn failed pairing into success");
        canceled = false; var elapsed = Stopwatch.StartNew();
        using (var source = new CancellationTokenSource(80))
        {
            try { await Commands.RunCancellable(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Morror.Tests.exe"), 15000, source.Token, "wait"); }
            catch (OperationCanceledException) { canceled = true; }
        }
        Check(canceled && elapsed.ElapsedMilliseconds < 5000, "Cancel terminates the owned command promptly instead of leaving it waiting");
    }
}
