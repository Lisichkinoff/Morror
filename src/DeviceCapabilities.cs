using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Morror
{
    public sealed class DeviceCapabilities
    {
        public string Model = "";
        public string Manufacturer = "";
        public string AndroidVersion = "";
        public int ApiLevel;
        public int Width;
        public int Height;
        public double RefreshRate;
        public string ResolutionLabel { get { return Width > 0 && Height > 0 ? Width + " × " + Height : "Не определено"; } }
        public string RefreshLabel { get { return RefreshRate > 0 ? RefreshRate.ToString("0.#", CultureInfo.CurrentCulture) + " Гц" : "Не определена"; } }

        static string Property(string properties, string key)
        {
            var match = Regex.Match(properties, @"^\[" + Regex.Escape(key) + @"\]:\s*\[([^\r\n]*)\]\s*$", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        public static DeviceCapabilities Parse(string properties, string size, string displays)
        {
            var info = new DeviceCapabilities { Model = Property(properties, "ro.product.model"), Manufacturer = Property(properties, "ro.product.manufacturer"), AndroidVersion = Property(properties, "ro.build.version.release") };
            int.TryParse(Property(properties, "ro.build.version.sdk"), out info.ApiLevel);
            var sizes = Regex.Matches(size, @"(?:Physical|Override) size:\s*(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
            if (sizes.Count > 0)
            {
                var last = sizes[sizes.Count - 1];
                int.TryParse(last.Groups[1].Value, out info.Width); int.TryParse(last.Groups[2].Value, out info.Height);
            }
            // Only a reported render rate for display 0 is used. Supported modes or
            // another attached display must never be presented as the active rate.
            foreach (string line in displays.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("DisplayInfo{") || !Regex.IsMatch(line, @"\bdisplayId\s*[= ]\s*0\b")) continue;
                var rate = Regex.Match(line, @"\brenderFrameRate\s*[= ]\s*([0-9]+(?:\.[0-9]+)?)");
                double value;
                if (rate.Success && double.TryParse(rate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 1000) info.RefreshRate = value;
                break;
            }
            return info;
        }

        public static async Task<DeviceCapabilities> Read(string adb, string serial, CancellationToken cancellation)
        {
            var reads = new[] { ReadOptional(adb, serial, cancellation, "getprop"), ReadOptional(adb, serial, cancellation, "wm", "size"), ReadOptional(adb, serial, cancellation, "dumpsys", "display") };
            var output = await Task.WhenAll(reads);
            return Parse(output[0], output[1], output[2]);
        }

        static async Task<string> ReadOptional(string adb, string serial, CancellationToken cancellation, params string[] command)
        {
            try
            {
                var args = new[] { "-s", serial, "shell" }.Concat(command).ToArray();
                var result = await Commands.RunCancellable(adb, 8000, cancellation, args);
                return result.ExitCode == 0 ? result.Output : "";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return ""; }
        }
    }
}
