using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Morror
{
    public sealed class Device
    {
        public string Serial { get; set; }
        public string State { get; set; }
        public string Model { get; set; }
        public bool Wireless { get; set; }
        public bool Ready { get { return State == "device"; } }
        public string Label { get { return Model + "  ·  " + (Wireless ? "Wi-Fi" : "USB") + (Ready ? "" : "  ·  " + (State == "unauthorized" ? "разрешите доступ" : State == "offline" ? "не в сети" : State)); } }
    }

    public static class Protocol
    {
        public const int NativeQuality = 6;
        static readonly int[] MaxEdges = { 720, 1280, 1920, 2048, 2560, 3840, 0 };
        static readonly string[] VideoRates = { "4M", "8M", "16M", "20M", "24M", "40M", "32M" };
        public static List<Device> ParseDevices(string output)
        {
            var result = new List<Device>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = Regex.Split(line.Trim(), @"\s+");
                if (parts.Length < 2 || line.StartsWith("List ") || line.StartsWith("*")) continue;
                if (parts[1] != "device" && parts[1] != "offline" && parts[1] != "unauthorized" && parts[1] != "no") continue;
                var model = parts.FirstOrDefault(p => p.StartsWith("model:"));
                result.Add(new Device { Serial = parts[0], State = parts[1], Model = model == null ? parts[0] : model.Substring(6).Replace('_', ' '), Wireless = parts[0].Contains(":") || parts[0].Contains("._adb") });
            }
            return result;
        }

        public static string Endpoint(string value)
        {
            value = value.Trim();
            var match = Regex.Match(value, @"^(\d{1,3}(?:\.\d{1,3}){3}|\[[0-9a-fA-F:.%]+\]):(\d{1,5})$");
            IPAddress ip; int port;
            if (!match.Success || !IPAddress.TryParse(match.Groups[1].Value.Trim('[', ']'), out ip) || !int.TryParse(match.Groups[2].Value, out port) || port < 1 || port > 65535)
                throw new ArgumentException("Введите IP:порт телефона, например 192.168.1.20:37123 или [2001:db8::1]:37123.");
            return (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString()) + ":" + port;
        }

        public static string PairingCode(string value)
        {
            if (!Regex.IsMatch(value, @"^\d{6}$", RegexOptions.ECMAScript)) throw new ArgumentException("Введите шестизначный код с экрана телефона.");
            return value;
        }

        // Windows CommandLineToArgvW quoting: never route device input through a shell.
        public static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static string Arguments(IEnumerable<string> args) { return string.Join(" ", args.Select(Quote)); }

        public static string FilePushNotice(string line)
        {
            if (line != null && line.Contains(" successfully pushed to /sdcard/Download/"))
                return "Перетащенный файл сохранён на телефоне в папке Download (Загрузки). Откройте «Файлы» на телефоне и выберите его. Это отправка файла, а не вставка в сообщение.";
            if (line != null && line.Contains("Failed to push "))
                return "Не удалось отправить перетащенный файл. Откройте «Фото и буфер обмена» в меню Morror и повторите передачу: там будет показана причина ошибки.";
            return null;
        }

        public static string[] MirrorArguments(string serial, int quality, int fps, bool audio, bool control, bool alwaysOnTop = false, bool clipboardSync = true, bool physicalKeyboard = true, bool screenOff = false)
        {
            if (quality < 0 || quality >= MaxEdges.Length || !new[] { 0, 30, 60, 90, 120, 144, 165, 180, 240 }.Contains(fps)) throw new ArgumentException("Некорректные параметры трансляции.");
            var result = new List<string> { "--serial=" + serial, "--window-title=Morror — Android", "--print-fps", "--video-codec=h264", "--video-bit-rate=" + VideoRates[quality] };
            if (fps != 0) result.Add("--max-fps=" + fps);
            if (MaxEdges[quality] != 0) result.Add("--max-size=" + MaxEdges[quality]);
            if (!audio) result.Add("--no-audio");
            if (!control) result.Add("--no-control");
            else result.Add(physicalKeyboard ? "--keyboard=uhid" : "--keyboard=sdk");
            if (screenOff && control) result.Add("--turn-screen-off");
            if (alwaysOnTop) result.Add("--always-on-top");
            if (!clipboardSync || !control) result.Add("--no-clipboard-autosync");
            return result.ToArray();
        }
    }

    public sealed class CommandResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
        public string Combined { get { return (Output + "\n" + Error).Trim(); } }
    }

    public static class Commands
    {
        public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
        {
            return new ProcessStartInfo(exe, Protocol.Arguments(args)) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        }

        public static async Task<CommandResult> Run(string exe, int timeout, params string[] args)
        {
            return await RunCancellable(exe, timeout, CancellationToken.None, args);
        }

        public static async Task<CommandResult> RunCancellable(string exe, int timeout, CancellationToken cancellation, params string[] args)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var process = new Process { StartInfo = StartInfo(exe, args) })
            {
                process.Start();
                using (cancellation.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
                {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                bool exited = await Task.Run(() => process.WaitForExit(timeout));
                cancellation.ThrowIfCancellationRequested();
                if (!exited)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("Телефон не ответил вовремя. Проверьте подключение и повторите попытку.");
                }
                return new CommandResult { ExitCode = process.ExitCode, Output = await stdout, Error = await stderr };
                }
            }
        }
    }
}
