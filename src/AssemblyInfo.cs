using System.Reflection;
using System.Runtime.Versioning;
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle("Morror")]
[assembly: AssemblyDescription("Экран Android и iPhone на ПК через USB и Wi-Fi")]
[assembly: AssemblyProduct("Morror")]
[assembly: AssemblyCompany("Morror")]
[assembly: AssemblyVersion(Morror.AppVersion.Number + ".0")]
[assembly: AssemblyFileVersion(Morror.AppVersion.Number + ".0")]
[assembly: AssemblyInformationalVersion(Morror.AppVersion.Number)]

namespace Morror
{
    internal static class AppVersion
    {
        internal const string Number = "0.9.3";
    }
}
