// Test fixture only. It never connects to Android or starts an ADB server.
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Windows;

class FakeDevice
{
    static string Storage { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage"); } }
    static string Local(string remote)
    {
        string root = Path.GetFullPath(Storage) + Path.DirectorySeparatorChar;
        if (remote != "/sdcard" && !remote.StartsWith("/sdcard/", StringComparison.Ordinal)) throw new ArgumentException("Not a shared test path");
        string path = Path.GetFullPath(Path.Combine(Storage, remote.Length > 7 ? remote.Substring(8).Replace('/', Path.DirectorySeparatorChar) : ""));
        if (path != root.TrimEnd(Path.DirectorySeparatorChar) && !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Escaped fixture storage");
        return path;
    }
    static string LastShellArgument(string command)
    {
        int quote = command.IndexOf('\'');
        return command.Substring(quote + 1, command.Length - quote - 2).Replace("'\\''", "'");
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) == "adb")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Directory.CreateDirectory(Local("/sdcard/DCIM/Camera"));
            int pull = Array.IndexOf(args, "pull"), push = Array.IndexOf(args, "push");
            if (Array.IndexOf(args, "exec-out") >= 0)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(Storage, "screenshot.png"));
                using (var output = Console.OpenStandardOutput()) output.Write(bytes, 0, bytes.Length);
                return 0;
            }
            if (pull >= 0)
            {
                if (args[pull+1].EndsWith("/fail.png")) { File.WriteAllBytes(args[pull+2],new byte[]{1,2,3}); return 1; }
                File.Copy(Local(args[pull+1]), args[pull+2], false); return 0;
            }
            if (push >= 0)
            {
                if (Path.GetFileName(args[push+1]).StartsWith("fail-send-")) { File.WriteAllBytes(Local(args[push+2]),new byte[]{1,2,3}); return 1; }
                if (Path.GetFileName(args[push+1]).StartsWith("short-send-")) { File.WriteAllBytes(Local(args[push+2]),new byte[]{1}); return 0; }
                File.Copy(args[push+1], Local(args[push+2]), false); return 0;
            }
            if (args.Length > 3 && args[2] == "shell")
            {
                string command = args[3];
                if (command == "ip -o -4 addr show") { Console.WriteLine("1: lo inet 127.0.0.1/8\n4: wlan0 inet 192.168.1.20/24 brd 192.168.1.255"); return 0; }
                if (command.StartsWith("ls -1pa -- "))
                {
                    string folder = Local(LastShellArgument(command));
                    if (!Directory.Exists(folder)) return 1;
                    foreach (var path in Directory.GetDirectories(folder)) Console.WriteLine(Path.GetFileName(path) + "/");
                    foreach (var path in Directory.GetFiles(folder)) Console.WriteLine(Path.GetFileName(path));
                    return 0;
                }
                if (command.StartsWith("mkdir -p -- ")) { Directory.CreateDirectory(Local(LastShellArgument(command))); return 0; }
                if (command.StartsWith("test ! -e ")) return File.Exists(Local(LastShellArgument(command))) ? 1 : 0;
                if (command.StartsWith("rm -f -- ")) { File.Delete(Local(LastShellArgument(command))); return 0; }
                if (command.StartsWith("stat -c %s -- ")) { Console.WriteLine(new FileInfo(Local(LastShellArgument(command))).Length); return 0; }
                if (command.StartsWith("content call ")) { Console.WriteLine(command.Contains("no-index-") ? "Error: media scan unavailable" : "Result: Bundle[{}]"); return 0; }
                if (command.StartsWith("content query ")) { Console.WriteLine(command.Contains("no-index-") ? "No result found." : "Row: 0 _id=42, mime_type=image/png"); return 0; }
                if (command.StartsWith("am start ")) { Console.WriteLine("Starting: Intent"); return 0; }
                if (command.StartsWith("am broadcast ")) return 0;
            }
            if (args.Contains("tcpip")) { File.WriteAllText(Path.Combine(Storage, "wifi-enabled"), "test only"); Console.WriteLine("restarting in TCP mode port: 5555"); return 0; }
            if (args.Length > 0 && args[0] == "connect") { Console.WriteLine("connected to " + args[1]); return 0; }
            if (args.Contains("get-state")) { Console.WriteLine("device"); return 0; }
            if (args.Length > 0 && args[0] == "devices") Console.WriteLine("List of devices attached\nTEST-PHONE device usb:1-1 model:Galaxy_S24 device:generic" + (File.Exists(Path.Combine(Storage, "wifi-enabled")) ? "\n192.168.1.20:5555 device model:Galaxy_S24" : ""));
            else if (Array.IndexOf(args, "getprop") >= 0)
            {
                if (Array.IndexOf(args, "ro.build.version.sdk") >= 0) Console.WriteLine("35");
                else Console.WriteLine("[ro.product.manufacturer]: [Samsung]\n[ro.product.model]: [Galaxy S24]\n[ro.build.version.sdk]: [35]\n[ro.build.version.release]: [15]");
            }
            else if (Array.IndexOf(args, "wm") >= 0) Console.WriteLine("Physical size: 1440x3120");
            else if (Array.IndexOf(args, "dumpsys") >= 0) Console.WriteLine("mDisplayInfo=DisplayInfo{displayId 0, renderFrameRate 120.0, modes [{fps=60.0}, {fps=120.0}]}");
            return 0;
        }
        var application = new Application();
        application.Run(new Window { Title = "Morror test fixture", Width = 180, Height = 90, Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual });
        return 0;
    }
}
