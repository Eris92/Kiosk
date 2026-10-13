namespace Kiosk.Client;

/// <summary>
/// Konfiguracja z serwera: when the PC's own client.json names a ConfigSource (a folder or share), every start copies
/// that whole folder (client.json, logo, kiosk-news.json, …) into a local copy and uses its client.json. Paths in it
/// may be relative to the folder (e.g. "BrandLogo": "logo.jpg"). The share is read with the signed-in person's own
/// account, so the copy is kept per account. Without the server the last copy is used; without a copy, the PC's own.
/// Locally Konfiguracja is then only a preview; only ConfigSource itself can be changed there.
/// </summary>
internal static class CentralConfig
{
    internal const string FileName = "client.json";

    /// <summary>The configuration in use, the PC's own file (holds ConfigSource) and what went wrong with the server.</summary>
    internal sealed record Loaded(Config Effective, Config Local, string? Problem)
    {
        internal bool Central => Local.ConfigSource.Length > 0;
    }

    private static string Cache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Central");

    internal static Loaded Load(string path)
    {
        var local = Configuration.Load(path);
        var source = local.ConfigSource;
        if (source.Length == 0) return new(local, local, null);
        string? problem = null;
        try { Sync(source); }
        catch (Exception ex)
        {
            problem = "Konfiguracja z serwera niedostępna (" + ex.Message + ")";
            Audit.Write("central_config_sync_failed", ex.Message);
        }
        var cached = Path.Combine(Cache, FileName);
        if (!File.Exists(cached)) return new(local, local, (problem ?? "Brak konfiguracji z serwera") + " – używam konfiguracji tego komputera.");
        try
        {
            var central = Configuration.Load(cached) with { ConfigSource = source };
            return new(central, local, problem == null ? null : problem + " – używam ostatnio pobranej.");
        }
        catch (Exception ex)
        {
            Audit.Write("central_config_invalid", ex.Message);
            return new(local, local, "Konfiguracja z serwera jest błędna (" + ex.Message + ") – używam konfiguracji tego komputera.");
        }
    }

    /// <summary>Copies the server's folder next to the current copy, checks its client.json, then replaces the copy.</summary>
    private static void Sync(string source)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("brak dostępu do " + source);
        if (!File.Exists(Path.Combine(source, FileName))) throw new FileNotFoundException("w " + source + " nie ma " + FileName);
        var staging = Cache + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Copy(source, staging);
        try { Configuration.Load(Path.Combine(staging, FileName)); }
        catch { Directory.Delete(staging, true); throw; }
        if (Directory.Exists(Cache)) Directory.Delete(Cache, true);
        Directory.Move(staging, Cache);
        Audit.Write("central_config_synced", source);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var folder in Directory.GetDirectories(from)) Copy(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
