using System;
using System.Globalization;
using Morror;

class DeviceTests
{
    static int count;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    static int Main()
    {
        try
        {
            var samsung = DeviceCapabilities.Parse("[ro.product.manufacturer]: [Samsung]\n[ro.product.model]: [Galaxy test]\n[ro.build.version.sdk]: [35]\n[ro.build.version.release]: [15]\n", "Physical size: 1440x3120", "mDisplayInfo=DisplayInfo{displayId 0, renderFrameRate 120.0, modes [{fps=60.0}, {fps=120.0}]}\n");
            Check(samsung.Model == "Galaxy test" && samsung.Manufacturer == "Samsung" && samsung.ApiLevel == 35 && samsung.AndroidVersion == "15", "Phone identity and Android version come from reported properties");
            Check(samsung.Width == 1440 && samsung.Height == 3120 && samsung.RefreshRate == 120, "Native display size and active reported rate are parsed");
            var xiaomi = DeviceCapabilities.Parse("[ro.product.manufacturer]: [Xiaomi]\r\n[ro.product.model]: [Test model]\r\n", "Physical size: 1440x3200\nOverride size: 1080x2400\n", "DisplayInfo{displayId=0, renderFrameRate=144.0}");
            Check(xiaomi.Model == "Test model" && xiaomi.Manufacturer == "Xiaomi" && xiaomi.Width == 1080 && xiaomi.Height == 2400 && xiaomi.RefreshRate == 144, "Manufacturer variants and an active resolution override are supported");
            var old = DeviceCapabilities.Parse("[ro.product.model]: [Older phone]\n[ro.build.version.sdk]: [29]", "", "DisplayInfo{displayId 0, modes [{fps=60.0}, {fps=240.0}]}");
            Check(old.ApiLevel == 29 && old.RefreshRate == 0 && old.Width == 0, "Unsupported metadata stays unknown instead of guessing a 240 Hz capability");
            var external = DeviceCapabilities.Parse("", "", "DisplayInfo{displayId 1, renderFrameRate 240.0}\nDisplayInfo{displayId 0, renderFrameRate 90.0}");
            Check(external.RefreshRate == 90, "An external display cannot supply the phone's refresh-rate label");
            var missing = DeviceCapabilities.Parse("Permission denied", "error", "unsupported output");
            Check(missing.Model == "" && missing.RefreshRate == 0 && missing.ResolutionLabel == "Не определено", "Unavailable metadata does not prevent generic mirroring");
            var gaming = DeviceCapabilities.Parse("[ro.product.manufacturer]: [Test maker]\n[ro.product.model]: [Gaming test]\n", "Physical size: 1080x2400", "DisplayInfo{displayId 0, renderFrameRate 240.0}");
            Check(gaming.RefreshRate == 240 && gaming.Model == "Gaming test", "A reported 240 Hz display is preserved without a 120 Hz ceiling");
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
                var fractional = DeviceCapabilities.Parse("", "", "DisplayInfo{displayId 0, renderFrameRate 59.94006}");
                Check(Math.Abs(fractional.RefreshRate - 59.94006) < 0.001, "Fractional Android refresh rates parse independently of Windows language");
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
            Console.WriteLine(count + " device metadata checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
