namespace Kiosk.Client;

/// <summary>
/// Konfiguracja → Statystyki → Logi: every folder Kiosk writes logs or statistics to keeps at most LogLimitMb
/// (the oldest files go first), and unpacked updates do not pile up in %TEMP%\Kiosk-update.
/// Kiosk cleans its own user's folders at start; the Kiosk Card service the PC's (Watchdog, every hour).
/// </summary>
internal static class Retention
{
    private static string AuditFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Logs");
    private static string FidoFolder => Path.Combine(Path.GetTempPath(), "FidoRun");
    private static string UpdateFolder => Path.Combine(Path.GetTempPath(), "Kiosk-update");

    /// <summary>Kiosk at start (its own user's logs and temp) and the service (the PC's: its logs, card sign-in logs, statistics).</summary>
    internal static void Run(int limitMb, bool machine)
    {
        long removed = 0;
        if (limitMb > 0)
        {
            var limit = limitMb * 1024L * 1024;
            removed += Prune(AuditFolder, "*.jsonl", limit);
            removed += Prune(FidoFolder, "*.log", limit);
            if (machine) removed += Prune(Stats.Folder, "????-??.jsonl", limit);
        }
        removed += CleanUpdates();
        if (removed > 0) Audit.Write("log_cleanup", (removed / 1024 / 1024) + " MB" + (machine ? " (usługa)" : ""));
    }

    /// <summary>The service: the limit saved in this PC's Konfiguracja (or the server's).</summary>
    internal static void RunForMachine()
    {
        var limit = 100;
        try { limit = Configuration.Load(Path.Combine(CentralConfig.MachineConfigFolder(), CentralConfig.FileName)).LogLimitMb; }
        catch (Exception) { }
        Run(limit, machine: true);
    }

    /// <summary>Deletes the oldest files matching <paramref name="pattern"/> until the folder holds at most <paramref name="limit"/> bytes.</summary>
    internal static long Prune(string folder, string pattern, long limit)
    {
        if (!Directory.Exists(folder)) return 0;
        var files = new DirectoryInfo(folder).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0, removed = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total <= limit) continue;
            try { file.Delete(); removed += file.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // In use: next time.
        }
        return removed;
    }

    /// <summary>Unpacked releases older than a day: an update in progress keeps its own folder.</summary>
    private static long CleanUpdates()
    {
        if (!Directory.Exists(UpdateFolder)) return 0;
        long removed = 0;
        foreach (var folder in new DirectoryInfo(UpdateFolder).GetDirectories())
        {
            if (DateTime.UtcNow - folder.LastWriteTimeUtc < TimeSpan.FromDays(1)) continue;
            try
            {
                var size = folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                folder.Delete(true);
                removed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
