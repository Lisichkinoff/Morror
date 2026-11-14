using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Morror
{
    public sealed class QrSession
    {
        public string ServiceName { get; private set; }
        public string Password { get; private set; }
        public string Payload { get { return "WIFI:T:ADB;S:" + ServiceName + ";P:" + Password + ";;"; } }
        public QrSession()
        {
            using (var random = RandomNumberGenerator.Create())
            {
                var name = new byte[8]; var secret = new byte[16];
                random.GetBytes(name); random.GetBytes(secret);
                ServiceName = "studio-morror-" + BitConverter.ToString(name).Replace("-", "").ToLowerInvariant();
                Password = BitConverter.ToString(secret).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    public sealed class MdnsService
    {
        public string Name;
        public string Type;
        public string Address;
        public string Host { get { return Address.Substring(0, Address.LastIndexOf(':')); } }
    }

    public sealed class PairingResult
    {
        public bool Connected;
        public string Address;
    }

    public sealed class WirelessPairing
    {
        readonly Func<string[], CancellationToken, Task<CommandResult>> execute;
        readonly Func<CancellationToken, Task> pause;
        public WirelessPairing(string adb) : this((args, token) => Commands.RunCancellable(adb, args[0] == "pair" ? 20000 : 10000, token, args), token => Task.Delay(1000, token)) { }
        internal WirelessPairing(Func<string[], CancellationToken, Task<CommandResult>> execute, Func<CancellationToken, Task> pause) { this.execute = execute; this.pause = pause; }

        public static List<MdnsService> ParseServices(string output)
        {
            var services = new List<MdnsService>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line.Trim(), @"^(\S+)\s+(_adb-tls-(?:pairing|connect)\._tcp)\.?\s+(\S+)\s*$");
                if (!match.Success) continue;
                try
                {
                    string type = match.Groups[2].Value;
                    string name = match.Groups[1].Value.TrimEnd('.');
                    if (name.EndsWith("." + type, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - type.Length - 1);
                    services.Add(new MdnsService { Name = name, Type = type, Address = Protocol.Endpoint(match.Groups[3].Value) });
                }
                catch (ArgumentException) { } // Ignore unresolved/invalid service records.
            }
            return services;
        }

        async Task<List<MdnsService>> Discover(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = await execute(new[] { "mdns", "services" }, cancellation);
            if (result.ExitCode != 0) throw new InvalidOperationException("Не удалось включить обнаружение телефона. Попробуйте ручное сопряжение по коду.");
            return ParseServices(result.Output);
        }

        public async Task<PairingResult> ConnectHost(string endpoint, CancellationToken cancellation, int attempts = 8)
        {
            endpoint = Protocol.Endpoint(endpoint);
            string host = endpoint.Substring(0, endpoint.LastIndexOf(':'));
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                foreach (var service in (await Discover(cancellation)).Where(s => s.Type == "_adb-tls-connect._tcp" && s.Host == host).GroupBy(s => s.Address).Select(g => g.First()))
                {
                    try { if (await Connect(service.Address, cancellation)) return new PairingResult { Connected = true, Address = service.Address }; }
                    catch (TimeoutException) { } // A stale port must not hide the next service.
                }
                await pause(cancellation);
            }
            return new PairingResult();
        }

        public async Task<bool> Connect(string endpoint, CancellationToken cancellation)
        {
            endpoint = Protocol.Endpoint(endpoint);
            var connected = await execute(new[] { "connect", endpoint }, cancellation);
            if (connected.ExitCode != 0 || connected.Combined.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var state = await execute(new[] { "-s", endpoint, "get-state" }, cancellation);
            return state.ExitCode == 0 && state.Output.Trim() == "device";
        }

        public async Task<PairingResult> PairByCode(string endpoint, string code, string connectAddress, CancellationToken cancellation)
        {
            endpoint = Protocol.Endpoint(endpoint);
            code = Protocol.PairingCode(code);
            if (!string.IsNullOrWhiteSpace(connectAddress)) connectAddress = Protocol.Endpoint(connectAddress);
            await PairEndpoint(endpoint, code, cancellation);
            return await ConnectAfterPair(endpoint, connectAddress, cancellation);
        }

        async Task PairEndpoint(string endpoint, string secret, CancellationToken cancellation)
        {
            var paired = await execute(new[] { "pair", endpoint, secret }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (paired.ExitCode != 0 || paired.Combined.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Сопряжение не завершено. Проверьте адрес и код на телефоне и повторите попытку.");
        }

        async Task<PairingResult> ConnectAfterPair(string endpoint, string connectAddress, CancellationToken cancellation)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(connectAddress))
                    return new PairingResult { Connected = await Connect(connectAddress, cancellation), Address = connectAddress };
                return await ConnectHost(endpoint, cancellation, 8);
            }
            catch (InvalidOperationException) { return new PairingResult(); }
            catch (TimeoutException) { return new PairingResult(); }
        }

        public static string WifiAddress(string output)
        {
            // Read the Wi-Fi interface itself, never a VPN/mobile default route.
            var match = Regex.Match(output, @"(?m)^\d+:\s+(?:wlan|swlan|wifi)\S*\s+inet\s+(\d{1,3}(?:\.\d{1,3}){3})/\d+");
            if (!match.Success) throw new InvalidOperationException("Подключите телефон к Wi-Fi и оставьте USB-кабель подключённым.");
            return Protocol.Endpoint(match.Groups[1].Value + ":5555");
        }

        public async Task<PairingResult> Pair(QrSession session, Action<string> progress, CancellationToken cancellation)
        {
            MdnsService phone = null;
            progress("Ожидаем сканирование QR-кода на телефоне…");
            while (phone == null)
            {
                cancellation.ThrowIfCancellationRequested();
                phone = (await Discover(cancellation)).FirstOrDefault(s => s.Type == "_adb-tls-pairing._tcp" && string.Equals(s.Name, session.ServiceName, StringComparison.OrdinalIgnoreCase));
                if (phone == null) await pause(cancellation);
            }
            progress("Телефон найден. Выполняем сопряжение…");
            await PairEndpoint(phone.Address, session.Password, cancellation);
            progress("Сопряжение выполнено. Подключаем телефон по Wi-Fi…");
            // Only connect TLS services on the address of the phone that scanned this QR.
            // A stale discovery record must not prevent trying other ports on that phone.
            return await ConnectAfterPair(phone.Address, null, cancellation);
        }
    }
}
