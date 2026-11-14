using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Morror
{
    internal static class StreamSettings
    {
        internal static string UserPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morror", "settings.json"); }
        }

        internal static Dictionary<string, object> Load(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, object>();
            var values = new JavaScriptSerializer { MaxJsonLength = 16384 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            return values ?? new Dictionary<string, object>();
        }

        internal static void Save(string path, Dictionary<string, object> values)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(values));
                if (!File.Exists(path))
                {
                    try { File.Move(temporary, path); return; }
                    catch (IOException) { if (!File.Exists(path)) throw; }
                }
                File.Replace(temporary, path, null);
            }
            finally { File.Delete(temporary); }
        }
    }
}
