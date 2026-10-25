using System.Reflection;

namespace Kiosk.Client;

internal static class BuildVersion
{
    internal static string Label
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            if (version.StartsWith("build-", StringComparison.Ordinal) || version.StartsWith('v')) return version;
            var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            return folder.StartsWith("release-", StringComparison.OrdinalIgnoreCase) ? folder : "wersja lokalna";
        }
    }
}
