using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Kiosk.Client;

/// <summary>
/// Konfiguracja z serwera: when the PC's own client.json names a ConfigSource (a folder or share), every start copies
/// its configuration assets (client.json, logo, kiosk-news.json, …) into a local copy and uses its client.json. Paths in it
/// may be relative to the folder (e.g. "BrandLogo": "logo.jpg"). The machine configuration uses only the service-owned
/// copy; without it, the PC's own configuration. Locally Konfiguracja is then only a preview.
///
/// The PC's shared configuration (C:\ProgramData\Kiosk) is fetched by the Kiosk Card service: on the network it is the
/// computer's account (DOMAIN\PC$), or the account saved by scripts\Set-KioskConfigSource.ps1 -Credential, so a domain
/// share needs no access for the person signed in. A user-writable cache must not override machine configuration.
/// </summary>
internal static class CentralConfig
{
    internal const string FileName = "client.json";
    private const string Pointer = "current.txt";
    private const string SourceMarker = ".kiosk-source";
    private const string SyncLock = ".sync.lock";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kiosk.ConfigSource");

    /// <summary>The configuration in use, the PC's own file (holds ConfigSource) and what went wrong with the server.</summary>
    internal sealed record Loaded(Config Effective, Config Local, string? Problem)
    {
        internal bool Central => Local.ConfigSource.Length > 0;
    }

    private static string KioskData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kiosk");
    private static string SharedConfig => Path.Combine(KioskData, FileName);
    private static string MachineCache => Path.Combine(KioskData, "Central");
    private static string UserCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Central");
    /// <summary>Optional account for the share (DPAPI, this computer; readable only by SYSTEM and administrators).</summary>
    private static string CredentialFile => Path.Combine(KioskData, "ConfigSource.cred");

    internal static Loaded Load(string path)
    {
        var local = Configuration.Load(path);
        var source = local.ConfigSource;
        if (source.Length == 0) return new(local, local, null);
        string? problem = null, folder = null;
        if (string.Equals(Path.GetFullPath(path), SharedConfig, StringComparison.OrdinalIgnoreCase))
        {
            var answer = CardService.Call(new CardService.Request { Op = "syncconfig" });
            if (answer.Code != 0) problem = answer.Message;
            folder = Current(MachineCache, source: source);
            if (answer.Code == 0 && folder == null) problem = "brak zgodnej kopii pobranej przez usługę";
        }
        else
        {
            try { folder = Sync(source, UserCache); problem = null; }
            catch (Exception ex) { problem ??= ex.Message; Audit.Write("central_config_sync_failed", ex.Message); }
            folder ??= Current(UserCache, source: source);
        }
        if (folder == null) return new(local, local, "Konfiguracja z serwera niedostępna (" + problem + ") – używam konfiguracji tego komputera.");
        try
        {
            var central = Configuration.Load(Path.Combine(folder, FileName)) with { ConfigSource = source };
            return new(central, local, problem == null ? null : "Konfiguracja z serwera niedostępna (" + problem + ") – używam ostatnio pobranej.");
        }
        catch (Exception ex)
        {
            Audit.Write("central_config_invalid", ex.Message);
            return new(local, local, "Konfiguracja z serwera jest błędna (" + ex.Message + ") – używam konfiguracji tego komputera.");
        }
    }

    /// <summary>Kiosk Card service, "syncconfig": the shared configuration's server folder into C:\ProgramData\Kiosk\Central.</summary>
    internal static CardService.Response SyncForMachine()
    {
        try
        {
            var source = Configuration.Load(SharedConfig).ConfigSource;
            if (source.Length == 0) return new CardService.Response { Code = -2, Message = "Konfiguracja tego komputera nie ma adresu serwera." };
            using (Connect(source)) Sync(source, MachineCache);
            return new CardService.Response { Message = source };
        }
        catch (Exception ex)
        {
            Audit.Write("central_config_sync_failed", ex.Message);
            return new CardService.Response { Code = -1, Message = ex.Message };
        }
    }

    /// <summary>"Test" (Konfiguracja and the first-start screen): both checks as two lines, ✔ or ✖ with the reason.</summary>
    internal static async Task<string> DescribeTestAsync(string source, AccountChange? typed = null)
    {
        // As the Kiosk will read it (the Kiosk Card service: this computer's account, or the saved one), and as you;
        // an account typed in but not saved yet is checked from here.
        var machine = Task.Run(() => CardService.Call(new CardService.Request { Op = "testconfig", Source = source }));
        var person = Task.Run(() => Test(source, savedAccount: false));
        var account = typed is { User.Length: > 0 } ? Task.Run(() => Test(source, savedAccount: false, typed: typed)) : null;
        var results = await Task.WhenAll(machine, person, account ?? Task.FromResult(new CardService.Response()));
        // A service from before "testconfig" treats it as a card request and answers about the card.
        static string Line(CardService.Response r) => (r.Code == 0 ? "✔ " : "✖ ") +
            (r.Message.EndsWith("[połączenie]", StringComparison.Ordinal) ? "usługa Kiosk Card jest w starszej wersji – poproś administratora o wdrożenie nowej paczki" : r.Message);
        return "Komputer (usługa Kiosk Card): " + Line(results[0]) +
            (account != null ? "\nKonto " + typed!.User.Trim() + " (zapisze się po Zapisz): " + Line(results[2]) : "") +
            "\nTwoje konto (" + Environment.UserName + "): " + Line(results[1]);
    }

    /// <summary>
    /// Konfiguracja → "Test": can this account reach the folder, and is its client.json usable? Nothing is copied.
    /// The service checks as the computer (or the saved account), the Konfiguracja window as the person signed in.
    /// </summary>
    internal static CardService.Response Test(string source, bool savedAccount, bool administrator = true, AccountChange? typed = null)
    {
        try
        {
            // "//server/share" is a share too: one spelling, so the check against the saved server below sees it.
            source = source.Trim().Trim('"').Replace('/', '\\');
            if (source.Length == 0 || !Path.IsPathFullyQualified(source)) return new CardService.Response { Code = -2, Message = "podaj pełną ścieżkę, np. \\\\serwer\\kiosk" };
            // The service signs in to the server as the computer (or the saved account): any other server could
            // collect that sign-in. A person may test only the saved server; another one, an administrator.
            var saved = SavedServer();
            var sameServer = saved != null && string.Equals(Server(source), saved, StringComparison.OrdinalIgnoreCase);
            // Not only with the saved account: the computer's own sign-in must not go to any other server either.
            if (Server(source) != null && !sameServer && !administrator)
                return new CardService.Response { Code = -2, Message = "inny serwer niż zapisany – sprawdzi go Konfiguracja administratora (Ctrl+Alt+K)" };
            using (typed is { User.Length: > 0 } ? Connect(source, new ShareAccount(typed.User.Trim(), typed.Password)) : savedAccount && sameServer ? Connect(source) : null)
            {
                if (!Directory.Exists(source)) return new CardService.Response { Code = -1, Message = "brak dostępu do folderu" };
                var file = Path.Combine(source, FileName);
                if (!File.Exists(file)) return new CardService.Response { Code = -1, Message = "folder dostępny, ale nie ma w nim " + FileName };
                Configuration.Load(file);
                return new CardService.Response { Message = "OK – " + FileName + " poprawny" };
            }
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    /// <summary>
    /// Copies the server's folder into a new folder of the copy, checks its client.json, then points the copy at it.
    /// Earlier folders are removed when nothing uses them any more (an open logo in another session keeps its folder).
    /// </summary>
    internal static string Sync(string source, string cache, string required = FileName)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("brak dostępu do " + source);
        if (!File.Exists(Path.Combine(source, required))) throw new FileNotFoundException("w " + source + " nie ma " + required);
        Directory.CreateDirectory(cache);
        using var lease = LockCache(cache);
        var folder = Path.Combine(cache, DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Copy(source, folder, skipStatistics: required == FileName);
            if (required == FileName) Configuration.Load(Path.Combine(folder, FileName));
            File.WriteAllText(Path.Combine(folder, SourceMarker), CanonicalSource(source));
        }
        catch { TryDelete(folder); throw; }
        var pointer = Path.Combine(cache, Pointer);
        File.WriteAllText(pointer + ".tmp", Path.GetFileName(folder));
        File.Move(pointer + ".tmp", pointer, true);
        foreach (var old in Directory.GetDirectories(cache))
            if (!string.Equals(old, folder, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
        Audit.Write(required == FileName ? "central_config_synced" : "news_synced", source);
        return folder;
    }

    // ------------------------------------------------------------------ Statystyki on the server

    /// <summary>
    /// Where every computer's statistics go: Konfiguracja → Statystyki → folder (StatsFolder), else Stats next to the
    /// server's client.json; null when neither is set.
    /// </summary>
    internal static string? StatsFolder(string configured = "")
    {
        try
        {
            if (configured.Trim().Length > 0) return configured.Trim().Trim('"');
            var folder = Configuration.Load(Path.Combine(MachineConfigFolder(), FileName)).StatsFolder.Trim().Trim('"');
            if (folder.Length > 0) return folder;
            return Configuration.Load(SharedConfig).ConfigSource is { Length: > 0 } server ? Path.Combine(server, "Stats") : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Kiosk Card service, "syncstats": this computer's months copied to &lt;folder&gt;\&lt;computer&gt;\ (JSON lines and a CSV for
    /// Excel), as Konto Kiosku (or an older export account saved before it), else the computer.
    /// Nothing is lost when the share is away: the months stay here and go with the next copy.
    /// </summary>
    internal static CardService.Response SyncStatsForMachine()
    {
        try
        {
            var folder = StatsFolder();
            if (folder == null) return new CardService.Response { Code = -2, Message = "Nie ustawiono folderu statystyk." };
            if (!Directory.Exists(Stats.Folder)) return new CardService.Response { Message = "Brak statystyk do wysłania." };
            using (AccountSaved ? Connect(folder) : Stats.ReadAccount() is { } older ? Connect(folder, new ShareAccount(older.User, older.Password)) : null)
            {
                var target = Path.Combine(folder, Environment.MachineName);
                Directory.CreateDirectory(target);
                int count = 0;
                var files = Directory.EnumerateFiles(Stats.Folder, "????-??.jsonl")
                    .Where(file => IsMonthName(Path.GetFileNameWithoutExtension(file))).ToArray();
                foreach (var file in files)
                {
                    var month = Path.GetFileNameWithoutExtension(file);
                    PublishStatsMonth(file, target, Environment.MachineName, month);
                    count++;
                }
                // The server keeps only the months this PC still has (Logi: its limit applies to the copy too).
                PruneStatsExport(target, Environment.MachineName,
                    files.Select(file => Path.GetFileNameWithoutExtension(file)!).ToHashSet(StringComparer.OrdinalIgnoreCase));
                Audit.Write("stats_export_ok", target);
                return new CardService.Response { Message = "Wysłano " + count + " mies. do " + target };
            }
        }
        catch (Exception ex)
        {
            Audit.Write("stats_export_failed", ex.Message);
            return new CardService.Response { Code = -1, Message = ex.Message };
        }
    }

    private static bool IsMonthName(string month) =>
        DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Stage both outputs before replacing either final file, so a failed copy preserves the previous export.</summary>
    internal static void PublishStatsMonth(string source, string target, string computer, string month)
    {
        if (!IsMonthName(month)) throw new ArgumentException("Nieprawidłowy miesiąc statystyk.", nameof(month));
        var suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
        var jsonDestination = Path.Combine(target, month + ".jsonl");
        var csvDestination = Path.Combine(target, computer + "-" + month + ".csv");
        var jsonTemporary = jsonDestination + suffix;
        var csvTemporary = csvDestination + suffix;
        try
        {
            File.Copy(source, jsonTemporary);
            using (var writer = new StreamWriter(csvTemporary, append: false, new UTF8Encoding(true)))
                Stats.WriteCsvFile(jsonTemporary, writer); // The CSV and JSON describe the same copied snapshot.
            File.Move(jsonTemporary, jsonDestination, true);
            File.Move(csvTemporary, csvDestination, true);
        }
        finally
        {
            foreach (var temporary in new[] { jsonTemporary, csvTemporary })
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Audit.Write("stats_export_cleanup_failed", ex.Message); }
            }
        }
    }

    /// <summary>Only files created by this computer's stats export may be removed from a shared folder.</summary>
    internal static void PruneStatsExport(string target, string computer, IReadOnlySet<string> kept)
    {
        var csvPrefix = computer + "-";
        foreach (var old in Directory.EnumerateFiles(target))
        {
            var name = Path.GetFileName(old);
            var temporary = IsOwnedStatsTemporary(name, computer);
            var month = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".jsonl" => Path.GetFileNameWithoutExtension(name),
                ".csv" when Path.GetFileNameWithoutExtension(name).StartsWith(csvPrefix, StringComparison.OrdinalIgnoreCase)
                    => Path.GetFileNameWithoutExtension(name)[csvPrefix.Length..],
                _ => ""
            };
            if (!temporary && (!IsMonthName(month) || kept.Contains(month))) continue;
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Audit.Write("stats_export_prune_failed", ex.Message); }
        }
    }

    private static bool IsOwnedStatsTemporary(string name, string computer)
    {
        if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        var withoutTmp = name[..^4];
        var lastDot = withoutTmp.LastIndexOf('.');
        if (lastDot < 0 || !Guid.TryParseExact(withoutTmp[(lastDot + 1)..], "N", out _)) return false;
        var output = withoutTmp[..lastDot];
        if (output.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return IsMonthName(output[..^6]);
        var prefix = computer + "-";
        return output.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) &&
               output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               IsMonthName(output[prefix.Length..^4]);
    }

    // ------------------------------------------------------------------ Komunikaty on a share

    private static string NewsCache => Path.Combine(KioskData, "News");

    /// <summary>
    /// Kiosk Card service, "syncnews": the messages folder named in this PC's configuration (often next to the server's
    /// client.json) copied as the computer or with the share's saved account into C:\ProgramData\Kiosk\News, so the
    /// Kiosk shows them also to people (and the kiosk account) who cannot open that share themselves.
    /// No path comes from the caller: only the folder an administrator configured.
    /// </summary>
    internal static CardService.Response SyncNewsForMachine()
    {
        try
        {
            var configFolder = MachineConfigFolder();
            var source = Configuration.Load(Path.Combine(configFolder, FileName)).NewsFolder;
            // "NewsFolder": "komunikaty" next to the server's client.json: read from the server itself, not the copy made at start.
            source = NewsSource(source, configFolder);
            if (!source.StartsWith(@"\\", StringComparison.Ordinal)) return new CardService.Response { Code = -2, Message = "Komunikaty nie są na udziale sieciowym." };
            using (Connect(source)) return new CardService.Response { Message = Sync(source, NewsCache, News.FileName) };
        }
        catch (Exception ex)
        {
            Audit.Write("news_sync_failed", ex.Message);
            return new CardService.Response { Code = -1, Message = ex.Message };
        }
    }

    /// <summary>
    /// Where the Kiosk reads the messages: a share is fetched by the Kiosk Card service (its account); without the
    /// service the person's own access is tried, then the last copy. A local folder is read as it is.
    /// </summary>
    internal static string NewsFolderFor(string folder)
    {
        if (!folder.StartsWith(@"\\", StringComparison.Ordinal) && !Under(folder, MachineCache)) return folder;
        var source = NewsSource(folder);
        var answer = CardService.Call(new CardService.Request { Op = "syncnews" });
        if (answer.Code == 0 && Current(NewsCache, News.FileName, source) is { } copy) return Used(copy, "pobrane z " + answer.Message);
        try { if (File.Exists(Path.Combine(folder, News.FileName))) return Used(folder, "usługa: " + answer.Message + "; czytam bezpośrednio"); }
        catch (Exception) { }
        var last = Current(NewsCache, News.FileName, source);
        return Used(last ?? folder, "usługa: " + answer.Message + "; " + (last != null ? "ostatnia kopia" : "brak dostępu i brak kopii"));
    }

    /// <summary>The last copy of the share's messages (shown at start while the share is fetched); null for a local folder.</summary>
    internal static string? NewsCopy(string folder) =>
        folder.StartsWith(@"\\", StringComparison.Ordinal) || Under(folder, MachineCache)
            ? Current(NewsCache, News.FileName, NewsSource(folder)) : null;

    private static string NewsSource(string folder, string? configFolder = null)
    {
        if (!Under(folder, MachineCache)) return folder;
        try
        {
            configFolder ??= MachineConfigFolder();
            var server = Configuration.Load(SharedConfig).ConfigSource;
            return MapNewsSource(folder, configFolder, server);
        }
        catch (Exception) { }
        return folder;
    }

    internal static string MapNewsSource(string folder, string configFolder, string server) =>
        server.Length > 0 && (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(configFolder), StringComparison.OrdinalIgnoreCase) ||
                              Under(folder, configFolder))
            ? CanonicalSource(Path.Combine(server, Path.GetRelativePath(configFolder, folder))) : folder;

    private static string newsReported = "";

    /// <summary>For the Kiosk's log: where the messages came from, once per change (the service's own log is SYSTEM's).</summary>
    private static string Used(string folder, string how)
    {
        var line = folder + " (" + how + ")";
        if (line != newsReported) { newsReported = line; Audit.Write("news_source", line); }
        return folder;
    }

    /// <summary>The folder whose client.json this PC uses: the server's copy (C:\ProgramData\Kiosk\Central\…) or C:\ProgramData\Kiosk.</summary>
    internal static string MachineConfigFolder()
    {
        try
        {
            var source = Configuration.Load(SharedConfig).ConfigSource;
            if (source.Length > 0 && Current(MachineCache, source: source) is { } folder) return folder;
        }
        catch (Exception) { }
        return KioskData;
    }

    private static bool Under(string path, string folder) =>
        path.Length > 0 && Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    internal static string? Current(string cache, string required = FileName, string? source = null)
    {
        try
        {
            var name = File.ReadAllText(Path.Combine(cache, Pointer)).Trim();
            if (name.Length == 0 || name is "." or ".." || Path.GetFileName(name) != name ||
                Path.IsPathFullyQualified(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var folder = Path.Combine(cache, name);
            if (source != null && !string.Equals(File.ReadAllText(Path.Combine(folder, SourceMarker)),
                    CanonicalSource(source), StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(Path.Combine(folder, required)) ? folder : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string CanonicalSource(string source) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));

    private static FileStream LockCache(string cache)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            try { return new FileStream(Path.Combine(cache, SyncLock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) &&
                                         Environment.TickCount64 < deadline)
            { Thread.Sleep(100); }
        }
    }

    /// <summary>Copy configuration assets, excluding monthly exports in the root Stats folder and linked directories.</summary>
    internal static void Copy(string from, string to, bool skipStatistics)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<(string Source, string Destination, bool Root, int StatsDepth, string Computer)>();
        pending.Push((from, to, true, -1, ""));
        while (pending.TryPop(out var next))
        {
            Directory.CreateDirectory(next.Destination);
            foreach (var entry in new DirectoryInfo(next.Source).EnumerateFileSystemInfos("*", options))
            {
                var name = entry.Name;
                if (entry is DirectoryInfo)
                {
                    var statsDepth = next.Root && name.Equals("Stats", StringComparison.OrdinalIgnoreCase) ? 0
                        : next.StatsDepth >= 0 ? next.StatsDepth + 1 : -1;
                    pending.Push((entry.FullName, Path.Combine(next.Destination, name), false, statsDepth,
                        statsDepth == 1 ? name : next.Computer));
                }
                else if (!EditorFile(name) && !(skipStatistics && next.StatsDepth == 1 && IsStatsExportFile(name, next.Computer)))
                    File.Copy(entry.FullName, Path.Combine(next.Destination, name), true);
            }
        }
    }

    private static bool IsStatsExportFile(string name, string computer)
    {
        if (IsOwnedStatsTemporary(name, computer)) return true;
        var month = Path.GetFileNameWithoutExtension(name);
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jsonl" => IsMonthName(month),
            ".csv" => month.StartsWith(computer + "-", StringComparison.OrdinalIgnoreCase) &&
                      IsMonthName(month[(computer.Length + 1)..]),
            _ => false
        };
    }

    /// <summary>The messages editor, its temporary/backup files and its active save lock are not for kiosks.</summary>
    private static bool EditorFile(string name) =>
        name.StartsWith("KioskKomunikaty.", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(News.FileName + ".edit.lock", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ the account for the share

    private sealed record ShareAccount(string User, string Password);

    /// <summary>\\server of a share path, null for a local folder.</summary>
    private static string? Server(string source) =>
        source.StartsWith(@"\\", StringComparison.Ordinal) ? source.TrimStart('\\').Split('\\')[0] : null;

    private static string? SavedServer()
    {
        try { return Server(Configuration.Load(SharedConfig).ConfigSource); }
        catch (Exception) { return null; }
    }

    /// <summary>With a saved account: a connection to the share root for the copy (closed afterwards). Without: null.</summary>
    private static IDisposable? Connect(string source, ShareAccount? typed = null)
    {
        if (typed == null && !File.Exists(CredentialFile) || !source.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var account = typed ?? JsonSerializer.Deserialize<ShareAccount>(Unprotect(File.ReadAllBytes(CredentialFile), Entropy))
            ?? throw new InvalidDataException("Zapisane konto do serwera jest puste.");
        var parts = source.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidDataException("Adres udziału podaj jako \\\\serwer\\udział\\…");
        var share = @"\\" + parts[0] + @"\" + parts[1];
        var resource = new NetResource { Type = 1, RemoteName = share };   // RESOURCETYPE_DISK
        int error = WNetAddConnection2(ref resource, account.Password, account.User, 0);
        if (error == 1219) { WNetCancelConnection2(share, 0, true); error = WNetAddConnection2(ref resource, account.Password, account.User, 0); }
        if (error != 0) throw new System.ComponentModel.Win32Exception(error, "Logowanie do " + share + " jako " + account.User + ": " + new System.ComponentModel.Win32Exception(error).Message);
        return new Disconnect(share);
    }

    /// <summary>Konto Kiosku for the Kiosk Card service (SharePoint's sign-in); null = none saved.</summary>
    internal static SharedAccounts.Account? ReadAccount()
    {
        if (!File.Exists(CredentialFile)) return null;
        var account = JsonSerializer.Deserialize<ShareAccount>(Unprotect(File.ReadAllBytes(CredentialFile), Entropy));
        return account == null ? null : new SharedAccounts.Account(account.User, account.Password);
    }

    /// <summary>The saved login (no password) for Konfiguracja; null when it cannot be read (not an administrator).</summary>
    internal static string? SavedAccountUser()
    {
        try { return ReadAccount()?.User; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Konto Kiosku → "Sprawdź": every folder the Kiosk uses on the network, as that account (typed = not saved yet; null =
    /// the saved one, or the computer). One line per folder, ✔ or ✖ with the reason; the statistics folder also for writing.
    /// </summary>
    internal static string TestNetwork(string configSource, string newsFolder, string statsFolder, AccountChange? typed)
    {
        var account = typed is { User.Length: > 0 } ? new ShareAccount(typed.User.Trim(), typed.Password) : null;
        string Check(string name, string folder, bool write)
        {
            folder = folder.Trim().Trim('"');
            if (folder.Length == 0) return name + ": nie ustawiono";
            try
            {
                using (account != null ? Connect(folder, account) : Connect(folder))
                {
                    if (!Directory.Exists(folder))
                        return "✖ " + name + " (" + folder + "): " + (write ? "folderu nie ma albo brak dostępu" : "brak dostępu do folderu");
                    if (write)
                    {
                        var probe = Path.Combine(folder, ".kiosk-test-" + Environment.MachineName);
                        File.WriteAllText(probe, DateTime.Now.ToString("s"));
                        File.Delete(probe);
                    }
                    return "✔ " + name + " (" + folder + "): " + (write ? "odczyt i zapis" : "odczyt");
                }
            }
            catch (Exception ex) { return "✖ " + name + " (" + folder + "): " + ex.Message; }
        }
        var news = newsFolder.Trim();
        if (news.Length > 0 && !Path.IsPathFullyQualified(news) && configSource.Trim().Length > 0) news = Path.Combine(configSource.Trim(), news);
        return string.Join("\n",
            Check("Konfiguracja z serwera", configSource, false),
            news.StartsWith(@"\\", StringComparison.Ordinal) || news.Length == 0 ? Check("Komunikaty", news, false) : "Komunikaty: folder lokalny (" + news + ")",
            Check("Eksport statystyk", statsFolder.Trim().Length > 0 ? statsFolder : configSource.Trim().Length > 0 ? Path.Combine(configSource.Trim(), "Stats") : "", true));
    }

    /// <summary>Kiosk Card service, "testnetwork": the saved account (or the computer) on the folders of this PC's saved configuration.</summary>
    internal static CardService.Response TestNetworkForMachine()
    {
        try
        {
            var own = Configuration.Load(SharedConfig);
            var configFolder = MachineConfigFolder();
            var news = Configuration.Load(Path.Combine(configFolder, FileName)).NewsFolder;
            // "komunikaty" next to the server's client.json: the server's folder, not the copy made at start (as "syncnews").
            if (Under(news, MachineCache) && own.ConfigSource.Length > 0) news = Path.Combine(own.ConfigSource, Path.GetRelativePath(configFolder, news));
            return new CardService.Response { Message = TestNetwork(own.ConfigSource, news, StatsFolder() ?? "", null) };
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    private sealed class Disconnect(string share) : IDisposable
    {
        public void Dispose() => WNetCancelConnection2(share, 0, true);
    }

    /// <summary>A change of the share's account from Konfiguracja or the first start; an empty User removes the saved one.</summary>
    internal sealed record AccountChange(string User, string Password);

    /// <summary>An account is saved (the file itself is readable only by SYSTEM and administrators).</summary>
    internal static bool AccountSaved => File.Exists(CredentialFile);

    private static readonly byte[] StagedEntropy = Encoding.UTF8.GetBytes("Kiosk.ConfigSource.Staged");

    /// <summary>What the save step changes besides client.json: the share's account and the shared accounts' key (null = unchanged).</summary>
    internal sealed record Secrets(AccountChange? Account, string? AccountsKey, AccountChange? NewsAccount = null, AccountChange? StatsAccount = null)
    {
        internal bool Any => Account != null || AccountsKey != null || NewsAccount != null || StatsAccount != null;
    }

    internal static void WriteSecrets(Secrets secrets)
    {
        if (secrets.Account != null) WriteAccount(secrets.Account);
        if (secrets.AccountsKey != null) SharedAccounts.WriteKey(secrets.AccountsKey);
        if (secrets.NewsAccount != null) NewsAccount.Write(secrets.NewsAccount);
        if (secrets.StatsAccount != null) Stats.WriteAccount(secrets.StatsAccount);
    }

    /// <summary>Publishes the configuration after its secrets, restoring encrypted originals on an ordinary write failure.</summary>
    internal static void WriteSecretsThen(Secrets secrets, Action publishConfiguration)
    {
        Directory.CreateDirectory(KioskData);
        using var saveLock = new FileStream(Path.Combine(KioskData, ".settings-save.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (secrets.Account != null)
        {
            files.Add(CredentialFile);
            files.Add(NewsAccount.File_);
            files.Add(Stats.AccountFile);
        }
        if (secrets.AccountsKey != null) files.Add(SharedAccounts.KeyFile);
        if (secrets.NewsAccount != null) files.Add(NewsAccount.File_);
        if (secrets.StatsAccount != null) files.Add(Stats.AccountFile);
        WriteSecretsThen(files, () => WriteSecrets(secrets), publishConfiguration);
    }

    private static void WriteSecretsThen(IEnumerable<string> files, Action writeSecrets, Action publishConfiguration)
    {
        var original = new List<(string Path, byte[]? Bytes)>();
        try
        {
            foreach (var path in files) original.Add((path, ReadExisting(path)));
            try
            {
                writeSecrets();
                publishConfiguration();
            }
            catch (Exception failure)
            {
                var rollbackFailures = new List<Exception>();
                foreach (var (path, bytes) in original.AsEnumerable().Reverse())
                {
                    try
                    {
                        var current = ReadExisting(path);
                        try
                        {
                            if (current == null && bytes == null || current != null && bytes != null && current.SequenceEqual(bytes)) continue;
                        }
                        finally { if (current != null) Array.Clear(current); }
                        if (bytes == null) File.Delete(path);
                        else WriteProtectedBlob(path, bytes);
                    }
                    catch (Exception ex) { rollbackFailures.Add(new IOException("Nie przywrócono " + Path.GetFileName(path), ex)); }
                }
                Audit.Write("settings_secret_rollback", rollbackFailures.Count == 0 ? "ok" : "failed");
                if (rollbackFailures.Count > 0)
                    throw new IOException("Zapis ustawień nie powiódł się i nie przywrócono wszystkich poprzednich sekretów.",
                        new AggregateException([failure, .. rollbackFailures]));
                throw;
            }
        }
        finally
        {
            foreach (var (_, bytes) in original) if (bytes != null) Array.Clear(bytes);
        }
    }

    private static byte[]? ReadExisting(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    /// <summary>Saves or removes Konto Kiosku; needs an administrator (or SYSTEM). The older separate accounts of the messages and statistics go with it.</summary>
    internal static void WriteAccount(AccountChange change)
    {
        if (change.User.Trim().Length == 0)
        {
            if (File.Exists(CredentialFile)) File.Delete(CredentialFile);
            NewsAccount.RemoveLegacy();
            Stats.RemoveAccount();
            Audit.Write("config_source_account_removed");
            return;
        }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new ShareAccount(change.User.Trim(), change.Password));
        try { WriteSecret(CredentialFile, plain, Entropy); }
        finally { Array.Clear(plain); }
        Audit.Write("config_source_account_saved", change.User.Trim());
        try { NewsAccount.RemoveLegacy(); Stats.RemoveAccount(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Audit.Write("legacy_account_cleanup_failed", ex.Message);
            throw new IOException("Konto Kiosku zapisano, ale nie udało się usunąć starszych kont: " + ex.Message, ex);
        }
    }

    /// <summary>A secret for this computer: DPAPI (computer) in a file only SYSTEM and administrators may open.</summary>
    internal static void WriteSecret(string path, byte[] plain, byte[] entropyBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var blob = Protect(plain, entropyBytes);
        try { WriteProtectedBlob(fullPath, blob); }
        finally { Array.Clear(blob); }
    }

    private static void WriteProtectedBlob(string path, byte[] blob)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // DPAPI for the computer alone would let any local user read a copied blob. Apply the final ACL
            // before writing, then publish the completed file in the same directory.
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using (var file = FileSystemAclExtensions.Create(new FileInfo(temporary), FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl,
                       FileShare.None, 4096, FileOptions.None, security))
            {
                file.Write(blob);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Audit.Write("secret_temporary_cleanup_failed", ex.Message); }
        }
    }

#if KIOSK_SELF_TEST
    internal static void SecretWriteSmokeTest()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kiosk-secret-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "account.cred");
        var entropy = Encoding.UTF8.GetBytes("Kiosk.SecretWriteSmokeTest");
        byte[] first = [1, 2, 3], second = [4, 5, 6];
        try
        {
            WriteSecret(path, first, entropy);
            var original = File.ReadAllBytes(path);
            if (!ReadSecret(path, entropy).SequenceEqual(first))
                throw new InvalidOperationException("Initial secret write failed.");
            var rejected = false;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { WriteSecret(path, second, entropy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
            }
            if (!rejected || !File.ReadAllBytes(path).SequenceEqual(original) ||
                Directory.EnumerateFiles(folder, "*.tmp").Any())
                throw new InvalidOperationException("A failed secret update changed the original or left a temporary file.");
            File.Delete(path);
            File.WriteAllBytes(path, [9]); // An older file may have inherited, broader permissions.
            WriteSecret(path, second, entropy);
            if (!ReadSecret(path, entropy).SequenceEqual(second))
                throw new InvalidOperationException("Secret replacement failed.");
            var access = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            var allowed = new[]
            {
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null).Value,
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null).Value
            }.ToHashSet(StringComparer.Ordinal);
            var rules = access.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
            if (!access.AreAccessRulesProtected || rules.Length != 2 ||
                rules.Any(rule => rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow ||
                    !allowed.Contains(((System.Security.Principal.SecurityIdentifier)rule.IdentityReference).Value)))
                throw new InvalidOperationException("Secret replacement did not retain the restricted ACL.");

            var originalSecret = File.ReadAllBytes(path);
            var another = Path.Combine(folder, "another.cred");
            var config = Path.Combine(folder, "client.json");
            File.WriteAllText(config, "old");
            var failed = false;
            try
            {
                WriteSecretsThen([path, another],
                    () => { WriteProtectedBlob(path, [7]); WriteProtectedBlob(another, [8]); },
                    () => throw new IOException("Simulated configuration publication failure."));
            }
            catch (IOException) { failed = true; }
            if (!failed || !File.ReadAllBytes(path).SequenceEqual(originalSecret) || File.Exists(another) ||
                File.ReadAllText(config) != "old" || Directory.EnumerateFiles(folder, "*.tmp").Any())
                throw new InvalidOperationException("A failed settings save did not restore its original secrets.");

            var published = false;
            failed = false;
            try
            {
                WriteSecretsThen([path, another],
                    () => { WriteProtectedBlob(path, [7]); throw new IOException("Simulated second secret failure."); },
                    () => published = true);
            }
            catch (IOException) { failed = true; }
            if (!failed || published || !File.ReadAllBytes(path).SequenceEqual(originalSecret) || File.Exists(another))
                throw new InvalidOperationException("A failed secret write published settings or left a partial change.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
#endif

    internal static byte[] ReadSecret(string path, byte[] entropyBytes) => Unprotect(File.ReadAllBytes(path), entropyBytes);

    /// <summary>For the elevated save step: the change in this person's temporary folder, protected for this computer.</summary>
    internal static string StageSecrets(Secrets secrets)
    {
        var staged = Path.Combine(Path.GetTempPath(), "Kiosk-secrets-" + Guid.NewGuid().ToString("N") + ".bin");
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
        try { File.WriteAllBytes(staged, Protect(plain, StagedEntropy)); }
        finally { Array.Clear(plain); }
        return staged;
    }

    internal static Secrets ReadStaged(string staged)
    {
        var plain = Unprotect(File.ReadAllBytes(staged), StagedEntropy);
        try { return JsonSerializer.Deserialize<Secrets>(plain) ?? throw new InvalidDataException("Puste dane."); }
        finally { Array.Clear(plain); }
    }

    private static byte[] Protect(byte[] data, byte[] entropyBytes)
    {
        var input = Pin(data);
        var entropy = Pin(entropyBytes);
        try
        {
            if (!CryptProtectData(ref input.Blob, null, ref entropy.Blob, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4, out var output))   // UI_FORBIDDEN | LOCAL_MACHINE
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Nie można zaszyfrować konta do serwera.");
            try
            {
                var blob = new byte[output.Size];
                Marshal.Copy(output.Data, blob, 0, output.Size);
                return blob;
            }
            finally { LocalFree(output.Data); }
        }
        finally { input.Handle.Free(); entropy.Handle.Free(); }
    }

    private static byte[] Unprotect(byte[] data, byte[] entropyBytes)
    {
        var input = Pin(data);
        var entropy = Pin(entropyBytes);
        try
        {
            if (!CryptUnprotectData(ref input.Blob, IntPtr.Zero, ref entropy.Blob, IntPtr.Zero, IntPtr.Zero, 0x1, out var output))   // CRYPTPROTECT_UI_FORBIDDEN
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Nie można odczytać zapisanego konta do serwera.");
            try
            {
                var plain = new byte[output.Size];
                Marshal.Copy(output.Data, plain, 0, output.Size);
                return plain;
            }
            finally { LocalFree(output.Data); }
        }
        finally { input.Handle.Free(); entropy.Handle.Free(); }
    }

    private static (DataBlob Blob, GCHandle Handle) Pin(byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        return (new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() }, handle);
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource { public int Scope, Type, DisplayType, Usage; public string? LocalName, RemoteName, Comment, Provider; }
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? user, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
