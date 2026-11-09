using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Morror;

class CoreTests
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "echo") { foreach (var arg in args.Skip(1)) Console.WriteLine(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(arg))); return 0; }
        if (args.Length > 0 && args[0] == "wait") { Thread.Sleep(15000); return 0; }
        if (args.Length > 0 && args[0] == "fail") { Console.Error.WriteLine("device offline"); return 7; }
        try
        {
            var devices = Protocol.ParseDevices("* daemon started successfully\r\nList of devices attached\r\nABC123 device usb:1-2 product:foo model:Pixel_8 device:shiba\r\nDENIED unauthorized usb:1-3\r\n192.168.1.20:37123 offline\r\nadb-xyz._adb-tls-connect._tcp device model:Galaxy_S23\r\n");
            Check(devices.Count == 4, "ADB device list and daemon messages");
            Check(devices[0].Model == "Pixel 8" && devices[0].Ready && !devices[0].Wireless, "USB device model and ready state");
            Check(!devices[1].Ready && devices[1].Label.Contains("разрешите"), "Unauthorized device cannot mirror");
            Check(devices[2].Wireless && !devices[2].Ready && devices[3].Wireless, "TCP and mDNS wireless devices");
            Check(Protocol.Endpoint(" 192.168.1.20:37123 ") == "192.168.1.20:37123", "Valid wireless endpoint");
            Check(Protocol.Endpoint("[2001:db8::20]:37123") == "[2001:db8::20]:37123", "Bracketed IPv6 wireless endpoints are supported");
            Check(Reject(() => Protocol.Endpoint("192.168.1.20:0")) && Reject(() => Protocol.Endpoint("192.168.1.20:65536")), "Port boundaries");
            Check(Reject(() => Protocol.Endpoint("999.1.1.1:42")) && Reject(() => Protocol.Endpoint("192.168.1.2:42 & calc")), "Reject invalid addresses and shell input");
            Check(Protocol.PairingCode("012345") == "012345" && Reject(() => Protocol.PairingCode("12345x")), "Pairing code preserves leading zero");
            var mirror = Protocol.MirrorArguments("ABC", 1, 60, false, false);
            Check(Protocol.MirrorArguments("ABC", 1, 60, true, true).Contains("--keyboard=uhid") && !mirror.Any(a => a.StartsWith("--keyboard=")), "Physical keyboard is the default only when control is enabled");
            Check(Protocol.MirrorArguments("ABC", 1, 60, true, true, false, true, false).Contains("--keyboard=sdk"), "Older phones can opt into the compatible keyboard");
            Check(Protocol.MirrorArguments("ABC", 0, 30, true, true, false, true, true, true).Contains("--turn-screen-off") && !Protocol.MirrorArguments("ABC", 0, 30, true, false, false, true, true, true).Contains("--turn-screen-off"), "Optional screen power saving requires enabled control");
            Check(mirror.Contains("--serial=ABC") && mirror.Contains("--max-size=1280") && mirror.Contains("--no-audio") && mirror.Contains("--no-control"), "Device, quality and disabled capabilities");
            Check(!Protocol.MirrorArguments("ABC", Protocol.NativeQuality, 30, true, true).Any(a => a.StartsWith("--max-size")), "Original resolution is uncapped");
            foreach (int fps in new[] { 30, 60, 90, 120, 144, 165, 180, 240 })
            {
                var stream = Protocol.MirrorArguments("PIXEL8", 1, fps, true, true);
                Check(stream.Contains("--max-fps=" + fps) && stream.Contains("--print-fps") && !stream.Contains("--no-audio"), fps + " FPS reaches the engine with live FPS reporting and audio");
            }
            Check(Reject(() => Protocol.MirrorArguments("PIXEL8", 1, 119, true, true)), "Unsupported frame rate is rejected");
            Check(!Protocol.MirrorArguments("PHONE", Protocol.NativeQuality, 0, true, true).Any(a => a.StartsWith("--max-fps")), "Device FPS mode does not impose a software capture limit");
            int[] edges = { 720, 1280, 1920, 2048, 2560, 3840 };
            for (int quality = 0; quality < edges.Length; quality++)
                Check(Protocol.MirrorArguments("PHONE", quality, 240, true, true).Contains("--max-size=" + edges[quality]), edges[quality] + " px resolution maps to the correct long-edge limit");
            Check(Reject(() => Protocol.MirrorArguments("PHONE", 7, 60, true, true)), "Invalid resolution selection is rejected");
            Check(Protocol.MirrorArguments("PIXEL8", 1, 120, true, true, true).Contains("--always-on-top"), "Pinning is passed to the mirror engine at startup");
            Check(!Protocol.MirrorArguments("PIXEL8", 1, 120, true, true).Contains("--always-on-top"), "Pinning remains optional");
            Check(!Protocol.MirrorArguments("PHONE",1,60,true,true).Contains("--no-clipboard-autosync"), "Text clipboard synchronization is enabled by default while control is enabled");
            Check(Protocol.MirrorArguments("PHONE",1,60,true,true,false,false).Contains("--no-clipboard-autosync"), "Clipboard synchronization can be disabled explicitly");
            Check(Protocol.MirrorArguments("PHONE",1,60,true,false).Contains("--no-clipboard-autosync"), "Disabling input also disables automatic clipboard synchronization");
            Check(Protocol.FilePushNotice("INFO: photo.png successfully pushed to /sdcard/Download/").Contains("Download"), "Mirror drag-and-drop success explains the actual phone folder");
            Check(Protocol.FilePushNotice("ERROR: Failed to push photo.png to /sdcard/Download/").Contains("Не удалось"), "Mirror drag-and-drop failures produce a visible error notice");
            Check(Protocol.FilePushNotice("scrcpy-server: 1 file pushed") == null, "Engine startup upload is not reported as a transferred user photo");
            string exe = Assembly.GetExecutingAssembly().Location;
            var values = new[] { "", "a b", "конец\\", "a\"b", "a\\\"b", "x & y", "телефон" };
            var echoed = Commands.Run(exe, 5000, new[] { "echo" }.Concat(values).ToArray()).GetAwaiter().GetResult();
            // Empty argument produces an empty line; preserve it for round-trip validation.
            var lines = echoed.Output.Replace("\r", "").Split('\n');
            Check(echoed.ExitCode == 0 && values.Select((v, i) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(v)) == lines[i]).All(x => x), "Actual Windows argv round-trip including Unicode, quotes and backslashes");
            var failure = Commands.Run(exe, 5000, "fail").GetAwaiter().GetResult();
            Check(failure.ExitCode == 7 && failure.Error.Contains("device offline"), "Exit code and stderr propagation");
            bool timeout = false; var watch = Stopwatch.StartNew();
            try { Commands.Run(exe, 150, "wait").GetAwaiter().GetResult(); } catch (TimeoutException) { timeout = true; }
            Check(timeout && watch.ElapsedMilliseconds < 5000, "Unresponsive command times out");
            Console.WriteLine(count + " checks passed."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
