using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Threading;

namespace Morror
{
    internal static class MonitorApkFirewall
    {
        internal const string ReadyPrefix = @"Local\Morror.ApkFirewallReady.";
        internal const string StopPrefix = @"Local\Morror.ApkFirewallStop.";

        internal static void Allow(string id, IPAddress address, int port)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
                port < 1024 || port > 65535 || !ScreenNetwork.Read().Any(network => network.Address.Equals(address)))
                throw new ArgumentException("Недопустимый адрес для скачивания APK.");

            using (var stopped = EventWaitHandle.OpenExisting(StopPrefix + id, EventWaitHandleRights.Synchronize))
            using (var ready = EventWaitHandle.OpenExisting(ReadyPrefix + id, EventWaitHandleRights.Modify))
            {
                if (stopped.WaitOne(0)) return;
                string name = "Morror APK " + id;
                string application = Assembly.GetExecutingAssembly().Location;
                bool added = false;
                try
                {
                    Run("add", "rule", "name=" + name, "dir=in", "action=allow", "program=" + application,
                        "protocol=TCP", "localip=" + address, "localport=" + port, "remoteip=localsubnet", "profile=private,public");
                    added = true;
                    ready.Set();
                    stopped.WaitOne(TimeSpan.FromMinutes(5));
                }
                finally { if (added) Run("delete", "rule", "name=" + name, "program=" + application); }
            }
        }

        static void Run(params string[] args)
        {
            string exe = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            var start = new ProcessStartInfo(exe, Protocol.Arguments(new[] { "advfirewall", "firewall" }.Concat(args)))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var process = Process.Start(start))
            {
                if (!process.WaitForExit(8000))
                {
                    process.Kill();
                    throw new IOException("Брандмауэр Windows не ответил вовремя.");
                }
                if (process.ExitCode != 0)
                    throw new IOException("Брандмауэр Windows отклонил изменение (код " + process.ExitCode + ").");
            }
        }
    }
}
