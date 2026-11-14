using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace Morror
{
    public sealed class ScreenContact
    {
        public int id { get; set; }
        public double x { get; set; }
        public double y { get; set; }
    }

    // points contains ALL contacts still down, including after an end event.
    public sealed class ScreenTouchFrame
    {
        public string type { get; set; }
        public ScreenContact[] points { get; set; }
        public long sequence { get; set; }
        public int generation { get; set; }
    }

    public sealed class ScreenTouchState
    {
        HashSet<int> contacts = new HashSet<int>();
        long sequence = -1;
        public bool HasContacts { get { return contacts.Count > 0; } }
        public void Reset() { contacts.Clear(); sequence = -1; }
        public bool Accept(ScreenTouchFrame frame)
        {
            if (frame == null || frame.points == null || frame.points.Length > 10 || frame.sequence <= sequence) return false;
            if (frame.points.Any(p => p == null || p.id < 1 || p.id > 10 || double.IsNaN(p.x) || double.IsNaN(p.y) || double.IsInfinity(p.x) || double.IsInfinity(p.y) || p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1)) return false;
            var next = new HashSet<int>(frame.points.Select(p => p.id));
            if (next.Count != frame.points.Length) return false;
            if (frame.type == "start") { if (next.Count != contacts.Count + 1 || !contacts.IsSubsetOf(next)) return false; }
            else if (frame.type == "move") { if (next.Count == 0 || !contacts.SetEquals(next)) return false; }
            else if (frame.type == "end") { if (next.Count != contacts.Count - 1 || !next.IsSubsetOf(contacts)) return false; }
            else if (frame.type == "cancel") { if (next.Count != 0) return false; }
            else return false;
            contacts = next; sequence = frame.sequence; return true;
        }
    }

    public sealed class ScreenChoice
    {
        public string Name { get; set; }
        public string DeviceName { get; set; }
        public Rectangle Bounds { get; set; }
        public bool Primary { get; set; }
        public override string ToString() { return Name + " · " + Bounds.Width + " × " + Bounds.Height + (Primary ? " · основной" : " · дополнительный"); }
        public static ScreenChoice[] Read()
        {
            return VirtualMonitorDriver.ReadScreens();
        }
    }

    public sealed class ScreenNetwork
    {
        public IPAddress Address { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Address + " · " + Name; }
        public static ScreenNetwork[] Read()
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.") &&
                    a.IPv4Mask != null && !a.IPv4Mask.Equals(IPAddress.Broadcast))
                    .Select(a => new ScreenNetwork { Address = a.Address, Name = n.Name })).ToArray();
        }
    }

    public static class SecondScreenProtocol
    {
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 262144, RecursionLimit = 24 }; }
        public static string Token()
        {
            var bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
        public static bool EqualToken(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0;
        }
        public static Uri BrowserUrl(string text)
        {
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Введите адрес сайта с https:// или http://.");
            return uri;
        }
        public static Point MapToScreen(ScreenContact contact, Rectangle bounds)
        {
            return new Point(bounds.Left + (int)Math.Round(contact.x * (bounds.Width - 1)), bounds.Top + (int)Math.Round(contact.y * (bounds.Height - 1)));
        }
    }
}
