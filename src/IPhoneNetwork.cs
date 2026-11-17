using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Morror
{
    internal static class IPhoneNetwork
    {
        internal const string ConfigureSwitch = "--configure-airplay-network";

        internal static string Describe()
        {
            object manager = null;
            try
            {
                manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
                bool connected = false, privateNetwork = false;
                foreach (dynamic network in ((dynamic)manager).GetNetworks(1))
                {
                    try { connected = true; privateNetwork |= (int)network.GetCategory() == 1; }
                    finally { Marshal.ReleaseComObject(network); }
                }
                if (!connected) return "Нет подключённой сети. Подключите ПК и iPhone к одному роутеру.";
                if (!privateNetwork) return "Нет частной сети. Для домашней сети выберите в параметрах Windows профиль «Частная сеть», затем настройте сеть AirPlay.";
                return "Частная сеть доступна. Если Morror не виден на iPhone, нажмите «Настроить сеть AirPlay».";
            }
            catch (Exception error) { Trace.WriteLine(error); return "Проверьте: сеть Windows должна быть частной, ПК и iPhone — в одной сети."; }
            finally { if (manager != null) Marshal.ReleaseComObject(manager); }
        }

        internal static async Task Configure()
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, ConfigureSwitch)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    await Task.Run(() => process.WaitForExit());
                    if (process.ExitCode != 0) throw new IOException("Не удалось настроить брандмауэр Windows. Проверьте правила для iPhoneMirror.WirelessHost.exe или ограничения администратора сети.");
                }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 1223) throw new IOException("Настройка сети отменена. Для добавления правил AirPlay нужно подтвердить запрос Windows.");
                throw;
            }
        }

        // Only the explicit setup button launches this entry point with elevation.
        // Rules are restricted to our bundled receiver, private profiles and the LAN.
        internal static void AllowReceiver(string directory)
        {
            string host = Path.GetFullPath(Path.Combine(directory, "Wireless", "iPhoneMirror.WirelessHost.exe"));
            if (!File.Exists(host)) throw new IOException("В сборке отсутствует приёмник AirPlay.");
            string id;
            using (var hash = SHA256.Create())
                id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(host.ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
            dynamic policy = null, rules = null;
            try
            {
                policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                rules = policy.Rules;
                // Cancelling Windows' first network prompt can leave explicit block
                // rules, which take precedence over allow rules. The user's setup
                // action clears only their private-profile part for this exact EXE.
                foreach (dynamic existing in rules)
                {
                    try
                    {
                        if ((bool)existing.Enabled && (int)existing.Direction == 1 && (int)existing.Action == 0 &&
                            (((int)existing.Profiles & 2) != 0) &&
                            string.Equals((string)existing.ApplicationName, host, StringComparison.OrdinalIgnoreCase))
                        {
                            int remainingProfiles = (int)existing.Profiles & ~2;
                            // Update this COM rule instance: Windows can have TCP and
                            // UDP block rules sharing a display name.
                            if (remainingProfiles == 0) existing.Enabled = false;
                            else existing.Profiles = remainingProfiles;
                        }
                    }
                    finally { Marshal.ReleaseComObject(existing); }
                }
                foreach (int protocol in new[] { 6, 17 })
                {
                    dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
                    try
                    {
                        rule.Name = "Morror AirPlay " + id + (protocol == 6 ? " TCP" : " UDP");
                        rule.Description = "AirPlay: Morror, private local network only";
                        rule.ApplicationName = host;
                        rule.Protocol = protocol;
                        rule.Direction = 1;
                        rule.Action = 1;
                        rule.Profiles = 2;
                        rule.RemoteAddresses = "LocalSubnet";
                        rule.Enabled = true;
                        rules.Add(rule);
                    }
                    finally { Marshal.ReleaseComObject(rule); }
                }
            }
            finally
            {
                if (rules != null) Marshal.ReleaseComObject(rules);
                if (policy != null) Marshal.ReleaseComObject(policy);
            }
        }
    }
}
