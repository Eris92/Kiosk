using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Konfiguracja z serwera: when the PC's own client.json names a ConfigSource (a folder or share), every start copies
/// that whole folder (client.json, logo, kiosk-news.json, …) into a local copy and uses its client.json. Paths in it
/// may be relative to the folder (e.g. "BrandLogo": "logo.jpg"). Without the server the last copy is used; without a
/// copy, the PC's own. Locally Konfiguracja is then only a preview; only ConfigSource itself can be changed there.
///
/// The PC's shared configuration (C:\ProgramData\Kiosk) is fetched by the Kiosk Card service: on the network it is the
/// computer's account (DOMAIN\PC$), or the account saved by scripts\Set-KioskConfigSource.ps1 -Credential, so a domain
/// share needs no access for the person signed in. Only when the service cannot, the Kiosk tries with that person's account.
/// </summary>
internal static class CentralConfig
{
    internal const string FileName = "client.json";
    private const string Pointer = "current.txt";
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
            if (answer.Code == 0) folder = Current(MachineCache);
            else problem = answer.Message;
        }
        if (folder == null)
        {
            try { folder = Sync(source, UserCache); problem = null; }
            catch (Exception ex) { problem ??= ex.Message; Audit.Write("central_config_sync_failed", ex.Message); }
        }
        folder ??= Newest(Current(MachineCache), Current(UserCache));
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
        await Task.WhenAll(machine, person, account ?? Task.FromResult(new CardService.Response()));
        // A service from before "testconfig" treats it as a card request and answers about the card.
        static string Line(CardService.Response r) => (r.Code == 0 ? "✔ " : "✖ ") +
            (r.Message.EndsWith("[połączenie]", StringComparison.Ordinal) ? "usługa Kiosk Card jest w starszej wersji – zaktualizuj Kiosk (Ctrl+Alt+U)" : r.Message);
        return "Komputer (usługa Kiosk Card): " + Line(machine.Result) +
            (account != null ? "\nKonto " + typed!.User.Trim() + " (zapisze się po Zapisz): " + Line(account.Result) : "") +
            "\nTwoje konto (" + Environment.UserName + "): " + Line(person.Result);
    }

    /// <summary>
    /// Konfiguracja → "Test": can this account reach the folder, and is its client.json usable? Nothing is copied.
    /// The service checks as the computer (or the saved account), the Konfiguracja window as the person signed in.
    /// </summary>
    internal static CardService.Response Test(string source, bool savedAccount, bool administrator = true, AccountChange? typed = null)
    {
        try
        {
            source = source.Trim().Trim('"');
            if (source.Length == 0 || !Path.IsPathFullyQualified(source)) return new CardService.Response { Code = -2, Message = "podaj pełną ścieżkę, np. \\\\serwer\\kiosk" };
            // The service signs in to the server as the computer (or the saved account): any other server could
            // collect that sign-in. A person may test only the saved server; another one, an administrator.
            var saved = SavedServer();
            var sameServer = saved != null && string.Equals(Server(source), saved, StringComparison.OrdinalIgnoreCase);
            if (savedAccount && Server(source) != null && !sameServer && !administrator)
                return new CardService.Response { Code = -2, Message = "inny serwer niż zapisany – sprawdzi go Konfiguracja administratora (Ctrl+Alt+K)" };
            using (typed is { User.Length: > 0 } ? Connect(source, new ShareAccount(typed.User.Trim(), typed.Password)) : savedAccount && sameServer ? Connect(source) : null)
            {
                if (!Directory.Exists(source)) return new CardService.Response { Code = -1, Message = "brak dostępu do folderu" };
                var file = Path.Combine(source, FileName);
                if (!File.Exists(file)) return new CardService.Response { Code = -1, Message = "folder dostępny, ale nie ma w nim " + FileName };
                Configuration.Load(file);
                int files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length;
                return new CardService.Response { Message = "OK – " + FileName + " poprawny, plików: " + files };
            }
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    /// <summary>
    /// Copies the server's folder into a new folder of the copy, checks its client.json, then points the copy at it.
    /// Earlier folders are removed when nothing uses them any more (an open logo in another session keeps its folder).
    /// </summary>
    private static string Sync(string source, string cache, string required = FileName)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("brak dostępu do " + source);
        if (!File.Exists(Path.Combine(source, required))) throw new FileNotFoundException("w " + source + " nie ma " + required);
        Directory.CreateDirectory(cache);
        var folder = Path.Combine(cache, DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Copy(source, folder);
            if (required == FileName) Configuration.Load(Path.Combine(folder, FileName));
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
            if (Under(source, MachineCache) && Configuration.Load(SharedConfig).ConfigSource is { Length: > 0 } server)
                source = Path.Combine(server, Path.GetRelativePath(configFolder, source));
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
        var answer = CardService.Call(new CardService.Request { Op = "syncnews" });
        if (answer.Code == 0 && Current(NewsCache, News.FileName) is { } copy) return copy;
        try { if (File.Exists(Path.Combine(folder, News.FileName))) return folder; }
        catch (Exception) { }
        return Current(NewsCache, News.FileName) ?? folder;
    }

    /// <summary>The folder whose client.json this PC uses: the server's copy (C:\ProgramData\Kiosk\Central\…) or C:\ProgramData\Kiosk.</summary>
    internal static string MachineConfigFolder()
    {
        try { if (Configuration.Load(SharedConfig).ConfigSource.Length > 0 && Current(MachineCache) is { } folder) return folder; }
        catch (Exception) { }
        return KioskData;
    }

    private static bool Under(string path, string folder) =>
        path.Length > 0 && Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string? Current(string cache, string required = FileName)
    {
        try
        {
            var folder = Path.Combine(cache, File.ReadAllText(Path.Combine(cache, Pointer)).Trim());
            return File.Exists(Path.Combine(folder, required)) ? folder : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? Newest(string? a, string? b) =>
        a == null ? b : b == null ? a : Directory.GetLastWriteTimeUtc(a) >= Directory.GetLastWriteTimeUtc(b) ? a : b;

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var folder in Directory.GetDirectories(from)) Copy(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

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
    internal sealed record Secrets(AccountChange? Account, string? AccountsKey, AccountChange? NewsAccount = null)
    {
        internal bool Any => Account != null || AccountsKey != null || NewsAccount != null;
    }

    internal static void WriteSecrets(Secrets secrets)
    {
        if (secrets.Account != null) WriteAccount(secrets.Account);
        if (secrets.AccountsKey != null) SharedAccounts.WriteKey(secrets.AccountsKey);
        if (secrets.NewsAccount != null) NewsAccount.Write(secrets.NewsAccount);
    }

    /// <summary>Saves or removes the share's account; needs an administrator (or SYSTEM).</summary>
    internal static void WriteAccount(AccountChange change)
    {
        if (File.Exists(CredentialFile)) File.Delete(CredentialFile);
        if (change.User.Trim().Length == 0) { Audit.Write("config_source_account_removed"); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new ShareAccount(change.User.Trim(), change.Password));
        try { WriteSecret(CredentialFile, plain, Entropy); }
        finally { Array.Clear(plain); }
        Audit.Write("config_source_account_saved", change.User.Trim());
    }

    /// <summary>A secret for this computer: DPAPI (computer) in a file only SYSTEM and administrators may open.</summary>
    internal static void WriteSecret(string path, byte[] plain, byte[] entropyBytes)
    {
        if (File.Exists(path)) File.Delete(path);
        var blob = Protect(plain, entropyBytes);
        // Only SYSTEM (the Kiosk Card service) and administrators: DPAPI for the computer alone would let any local user read it.
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.None, security);
        file.Write(blob);
    }

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
