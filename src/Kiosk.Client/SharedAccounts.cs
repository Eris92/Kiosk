using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Konta wspólne: accounts defined top-down for pages (sign-in with PageAction) and programs (ApplicationEntry.RunAs).
/// accounts.kiosk lies next to client.json (on the server with Konfiguracja z serwera, so one file serves every PC);
/// each account is encrypted with the key of the accounts (AES-256 + HMAC-SHA256 from PBKDF2 of the key), written by
/// scripts\Set-KioskSharedAccount.ps1. Every PC keeps that key encrypted for itself, readable only by SYSTEM and
/// administrators (Install-KioskShell.ps1 -AccountsKey, Konfiguracja → Aktualizacja). The Kiosk Card service decrypts
/// an account only for the installed Kiosk program, never for other programs of the person signed in.
/// </summary>
internal static class SharedAccounts
{
    internal const string FileName = "accounts.kiosk";
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("Kiosk.SharedAccounts");
    private static string KeyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kiosk", "AccountsKey.cred");

    internal static bool KeySaved => File.Exists(KeyFile);

    internal sealed record Account(string User, string Password)
    {
        /// <summary>DOMAIN\user or user@domain → (user, domain) as Windows takes them.</summary>
        internal (string User, string? Domain) Split()
        {
            int slash = User.IndexOf('\\');
            return slash > 0 ? (User[(slash + 1)..], User[..slash]) : (User, User.Contains('@') ? null : ".");
        }
    }

    private sealed class Entry { public string Iv { get; set; } = ""; public string Data { get; set; } = ""; public string Mac { get; set; } = ""; }
    private sealed class AccountsFile
    {
        public int Version { get; set; } = 1;
        public string Salt { get; set; } = "";
        public int Iterations { get; set; } = 200000;
        public Dictionary<string, Entry>? Accounts { get; set; }
    }

    /// <summary>Saves (or with an empty key removes) this PC's key of the shared accounts; needs an administrator.</summary>
    internal static void WriteKey(string key)
    {
        if (key.Length == 0) { if (File.Exists(KeyFile)) File.Delete(KeyFile); Audit.Write("accounts_key_removed"); return; }
        var plain = Encoding.UTF8.GetBytes(key);
        try { CentralConfig.WriteSecret(KeyFile, plain, KeyEntropy); }
        finally { Array.Clear(plain); }
        Audit.Write("accounts_key_saved");
    }

    // ------------------------------------------------------------------ service

    internal sealed record Use(string Name, string Url, bool News = false);

    /// <summary>Kiosk Card service, "sharedaccount": an account only for its configured page or SharePoint site.</summary>
    internal static CardService.Response ForService(string request, string? clientProgram)
    {
        try
        {
            // Only the installed Kiosk (Program Files, which users cannot change) gets a password; any other program of
            // the person signed in could otherwise ask the service for it.
            if (!IsInstalledKiosk(clientProgram)) return new CardService.Response { Code = -2, Message = "Konta wspólne podaje usługa tylko programowi Kiosk z Program Files." };
            var use = JsonSerializer.Deserialize<Use>(request);
            if (use?.Name is not { Length: >= 1 and <= 128 } || use.Url is not { Length: >= 1 and <= 4096 } ||
                !Uri.TryCreate(use.Url, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps)
                return new CardService.Response { Code = -2, Message = "Konto wspólne wymaga poprawnego adresu HTTPS." };
            var configFolder = CentralConfig.MachineConfigFolder();
            var config = Configuration.Load(Path.Combine(configFolder, CentralConfig.FileName)).Active();
            if (!AuthorizedUse(config, use)) return new CardService.Response { Code = -2, Message = "Konto wspólne nie jest przypisane do tej strony." };
            var account = Read(Path.Combine(configFolder, FileName), use.Name);
            Audit.Write("shared_account_used", use.Name);
            return new CardService.Response { Message = account.User, Secret = account.Password };
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    /// <summary>Use the service's configuration, not the caller's, to decide where a password may be filled.</summary>
    internal static bool AuthorizedUse(Config config, Use use)
    {
        if (string.IsNullOrWhiteSpace(use.Name) || !Uri.TryCreate(use.Url, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps)
            return false;
        bool Same(string? name) => string.Equals(name?.Trim(), use.Name.Trim(), StringComparison.OrdinalIgnoreCase);
        return use.News
            ? config.NewsSharePoint.Length > 0 && Same(config.NewsSharePointAccount) &&
              string.Equals(config.NewsSharePoint, use.Url, StringComparison.OrdinalIgnoreCase)
            : config.PageActions.Any(a => Same(a.Account) && a.Matches(use.Url));
    }

    /// <summary>The service itself (SharedAccountPrograms): the account stays in the service.</summary>
    internal static Account ForProgram(string name) => Read(Path.Combine(CentralConfig.MachineConfigFolder(), FileName), name);

    internal static bool IsInstalledKiosk(string? program)
    {
        if (program == null || !string.Equals(Path.GetFileName(program), "Kiosk.Client.exe", StringComparison.OrdinalIgnoreCase)) return false;
        // Only the legacy root EXE or a version installed by Install-KioskShell.ps1.
        // Other Program Files subfolders must not gain the service's sign-in privileges.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kiosk");
        var relative = Path.GetRelativePath(root, Path.GetFullPath(program)).Split(Path.DirectorySeparatorChar);
        return relative.Length == 1 && string.Equals(relative[0], "Kiosk.Client.exe", StringComparison.OrdinalIgnoreCase) ||
            relative.Length == 3 && string.Equals(relative[0], "ClientVersions", StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(relative[1], "^release-[a-f0-9]{32}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            string.Equals(relative[2], "Kiosk.Client.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static Account Read(string file, string name)
    {
        if (!KeySaved) throw new InvalidOperationException("Na tym komputerze nie zapisano klucza kont wspólnych (Konfiguracja → Aktualizacja albo Install-KioskShell.ps1 -AccountsKey).");
        if (!File.Exists(file)) throw new FileNotFoundException("Brak pliku " + FileName + " obok konfiguracji.");
        var accounts = JsonSerializer.Deserialize<AccountsFile>(File.ReadAllText(file)) ?? throw new InvalidDataException(FileName + " jest pusty.");
        // The file's own names, without regard to case (the MAC covers the name in lower case).
        if (!new Dictionary<string, Entry>(accounts.Accounts ?? new Dictionary<string, Entry>(), StringComparer.OrdinalIgnoreCase).TryGetValue(name, out var entry)) throw new KeyNotFoundException("W " + FileName + " nie ma konta „" + name + "”.");
        var key = CentralConfig.ReadSecret(KeyFile, KeyEntropy);
        byte[] material;
        try { material = Rfc2898DeriveBytes.Pbkdf2(key, Convert.FromBase64String(accounts.Salt), accounts.Iterations, HashAlgorithmName.SHA256, 64); }
        finally { Array.Clear(key); }
        try
        {
            var iv = Convert.FromBase64String(entry.Iv);
            var data = Convert.FromBase64String(entry.Data);
            byte[] signed = [.. Encoding.UTF8.GetBytes(name.ToLowerInvariant()), .. iv, .. data];
            var mac = HMACSHA256.HashData(material[32..], signed);
            if (!CryptographicOperations.FixedTimeEquals(mac, Convert.FromBase64String(entry.Mac)))
                throw new CryptographicException("Konto „" + name + "” nie pasuje do klucza tego komputera (inny klucz albo zmieniony plik).");
            using var aes = Aes.Create();
            aes.Key = material[..32];
            var plain = aes.DecryptCbc(data, iv);
            try { return JsonSerializer.Deserialize<Account>(plain) ?? throw new InvalidDataException("Puste konto."); }
            finally { Array.Clear(plain); }
        }
        finally { Array.Clear(material); }
    }

    // ------------------------------------------------------------------ Kiosk

    /// <summary>The account from the Kiosk Card service.</summary>
    internal static Account Get(string name, string url, bool news)
    {
        var answer = CardService.Call(new CardService.Request { Op = "sharedaccount", Source = JsonSerializer.Serialize(new Use(name, url, news)) });
        if (answer.Code != 0 || answer.Secret == null)
            throw new InvalidOperationException("Konto wspólne „" + name + "”: " + answer.Message);
        return new Account(answer.Message, answer.Secret);
    }

    private sealed record CachedAccount(Account Value, long AddedAt);
    private const int MaxCachedAccounts = 64;
    private const long CacheLifetimeMilliseconds = 5 * 60 * 1000;
    private static readonly Dictionary<(string Name, string Url, bool News), CachedAccount> cache = new();
    private static long cacheGeneration;

    internal static void Forget()
    {
        lock (cache) { cache.Clear(); cacheGeneration++; }
    }

    /// <summary>For pages: keep a separate credential per approved URL and use, for a bounded time and space.</summary>
    internal static Account? TryGetCached(string name, string url, bool news = false) =>
        TryGetCachedCore(name, url, news, approvedUrl => Get(name, approvedUrl, news));

    private static Account? TryGetCachedCore(string name, string url, bool news, Func<string, Account> fetch)
    {
        // PageAction.Matches authorizes scheme, host, port and path; a changed query does not widen that rule.
        // SharePoint news uses an exact configured URL and keeps the full value.
        var approvedUrl = !news && Uri.TryCreate(url, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps
            ? page.GetLeftPart(UriPartial.Path) : url;
        var key = (name, approvedUrl, news);
        long generation;
        lock (cache)
        {
            generation = cacheGeneration;
            if (cache.TryGetValue(key, out var known))
            {
                if (Environment.TickCount64 - known.AddedAt < CacheLifetimeMilliseconds) return known.Value;
                cache.Remove(key);
            }
        }
        try
        {
            var account = fetch(approvedUrl);
            lock (cache)
            {
                // A configuration change during the service call must not restore its old password.
                if (cacheGeneration != generation) return null;
                var now = Environment.TickCount64;
                foreach (var expired in cache.Where(pair => now - pair.Value.AddedAt >= CacheLifetimeMilliseconds).Select(pair => pair.Key).ToArray())
                    cache.Remove(expired);
                if (cache.Count >= MaxCachedAccounts)
                    cache.Remove(cache.MinBy(pair => pair.Value.AddedAt).Key);
                cache[key] = new CachedAccount(account, now);
            }
            return account;
        }
        catch (Exception ex) { Audit.Write("shared_account_failed", name + ": " + ex.Message); return null; }
    }

#if KIOSK_SELF_TEST
    internal static void CacheSelfTest()
    {
        Forget();
        var requests = 0;
        Account Fetch(string _) { requests++; return new Account("test", "secret"); }
        TryGetCachedCore("test", "https://example.test/login?state=one", false, Fetch);
        TryGetCachedCore("test", "https://example.test/login?state=two", false, Fetch);
        if (requests != 1) throw new InvalidOperationException("A changed page query fetched the same account twice.");
        Forget();
        requests = 0;
        for (var index = 0; index <= MaxCachedAccounts; index++)
            if (TryGetCachedCore("test", "https://example.test/" + index, false, Fetch) == null)
                throw new InvalidOperationException("The bounded account cache did not store an allowed result.");
        TryGetCachedCore("test", "https://example.test/0", false, Fetch);
        if (requests != MaxCachedAccounts + 2)
            throw new InvalidOperationException("The account cache did not evict its oldest URL.");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pending = Task.Run(() => TryGetCachedCore("test", "https://example.test/in-flight", false, _ =>
        {
            started.Set();
            release.Wait();
            return new Account("old", "old-secret");
        }));
        if (!started.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("The account fetch did not start.");
        Forget();
        release.Set();
        if (pending.GetAwaiter().GetResult() != null)
            throw new InvalidOperationException("An account fetched before configuration changed was reused.");
        Forget();
    }
#endif
}

/// <summary>Starts a program on this PC that uses a shared account for the network (like "runas /netonly").</summary>
internal interface IProgramStarter
{
    (IntPtr Process, IntPtr Thread) Start(string application, string commandLine, string? directory, short show = 4);
}

/// <summary>A program from the PC's configuration with its shared account for the network. The Kiosk Card service starts
/// it as this Kiosk's own account (its token, its session) and hands over the suspended process: the password never
/// leaves the service.</summary>
internal sealed class NetworkAccountStarter(string applicationName) : IProgramStarter
{
    public (IntPtr Process, IntPtr Thread) Start(string application, string commandLine, string? directory, short show = 4)
    {
        var answer = CardService.Call(new CardService.Request { Op = "runas", Source = applicationName });
        if (answer.Code != 0 || answer.Process == 0 || answer.Thread == 0)
            throw new InvalidOperationException("Konto wspólne programu „" + applicationName + "”: " + answer.Message);
        return ((IntPtr)answer.Process, (IntPtr)answer.Thread);
    }
}

/// <summary>The service's side of NetworkAccountStarter.</summary>
internal static class SharedAccountPrograms
{
    /// <param name="pipe">The client's pipe (the service), or null: an administrator's Kiosk without the service starts it itself.</param>
    internal static CardService.Response Start(string applicationName, string? clientProgram, NamedPipeServerStream? pipe, int clientPid, int clientSession)
    {
        if (!SharedAccounts.IsInstalledKiosk(clientProgram))
            return new CardService.Response { Code = -2, Message = "Konta wspólne podaje usługa tylko programowi Kiosk z Program Files." };
        IntPtr token = IntPtr.Zero, primary = IntPtr.Zero, environment = IntPtr.Zero, client = IntPtr.Zero;
        var started = new AppNative.ProcessInfo();
        char[]? password = null;
        try
        {
            // Only a program an administrator gave this account, with its own path and arguments: nothing taken from the caller.
            var folder = CentralConfig.MachineConfigFolder();
            var entry = Configuration.Load(Path.Combine(folder, CentralConfig.FileName)).Applications.FirstOrDefault(a =>
                    a.Enabled && !a.IsBuiltIn && a.RunAs.Trim().Length > 0 && string.Equals(a.Name, applicationName, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("w konfiguracji tego komputera nie ma takiego programu z kontem wspólnym.");
            var account = SharedAccounts.ForProgram(entry.RunAs.Trim());
            var (user, domain) = account.Split();
            var secret = password = (account.Password + "\0").ToCharArray();
            var directory = string.IsNullOrWhiteSpace(entry.WorkingDirectory) ? Path.GetDirectoryName(entry.Path) : entry.WorkingDirectory;
            var commandLine = new StringBuilder('"' + entry.Path + "\" " + entry.Arguments);
            var startup = new AppNative.StartupInfo { Size = Marshal.SizeOf<AppNative.StartupInfo>(), Flags = 1, ShowWindow = 4, Desktop = "winsta0\\default" };
            Audit.Write("shared_account_used", entry.RunAs.Trim() + " → " + entry.Name);
            if (pipe == null)
            {
                // LOGON_NETCREDENTIALS_ONLY: this Kiosk's own account runs it, the shared one is used on the network only.
                startup.Desktop = null;
                if (!CreateProcessWithLogonW(user, domain, secret, 2, entry.Path, commandLine, 0x4 | 0x400, IntPtr.Zero, directory, ref startup, out started))
                    throw new Win32Exception();
                return new CardService.Response { Process = (long)started.Process, Thread = (long)started.Thread };
            }

            // LOGON32_LOGON_NEW_CREDENTIALS clones the caller's token (here: the Kiosk's, by impersonation) with the
            // shared account for the network only, like runas /netonly; it checks no password with the domain.
            string? clientSid = null;
            var error = 0;
            pipe.RunAsClient(() =>
            {
                using (var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query)) clientSid = identity.User?.Value;
                if (!LogonUser(user, domain, secret, 9, 3, out token)) error = Marshal.GetLastPInvokeError();
            });
            if (error != 0 || token == IntPtr.Zero) throw new Win32Exception(error);
            if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out primary)) throw new Win32Exception(); // MAXIMUM_ALLOWED, primary
            // Never more than the Kiosk itself: the program runs as the Kiosk's account, in its session.
            using (var check = new WindowsIdentity(primary))
                if (clientSid == null || check.User?.Value != clientSid) throw new InvalidOperationException("konto programu nie jest kontem Kiosku.");
            if (!SetTokenInformation(primary, 12, ref clientSession, sizeof(int))) throw new Win32Exception(); // TokenSessionId
            if (!CreateEnvironmentBlock(out environment, primary, false)) throw new Win32Exception();
            if (!CreateProcessAsUser(primary, entry.Path, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x4 | 0x400, environment, directory, ref startup, out started))
                throw new Win32Exception();
            // The suspended process goes to the Kiosk, which puts it into its job and resumes it.
            client = OpenProcess(0x40, false, clientPid); // PROCESS_DUP_HANDLE
            if (client == IntPtr.Zero
                || !DuplicateHandle(GetCurrentProcess(), started.Process, client, out var remoteProcess, 0, false, 2)
                || !DuplicateHandle(GetCurrentProcess(), started.Thread, client, out var remoteThread, 0, false, 2))
            {
                var failed = Marshal.GetLastPInvokeError();
                TerminateProcess(started.Process, 1);
                throw new Win32Exception(failed);
            }
            return new CardService.Response { Process = (long)remoteProcess, Thread = (long)remoteThread };
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
        finally
        {
            if (password != null) Array.Clear(password);
            if (pipe != null)
            {
                if (started.Process != IntPtr.Zero) CloseHandle(started.Process);
                if (started.Thread != IntPtr.Zero) CloseHandle(started.Thread);
            }
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (client != IntPtr.Zero) CloseHandle(client);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithLogonW(string user, string? domain, char[] password, int logonFlags, string application,
        StringBuilder commandLine, int creationFlags, IntPtr environment, string? directory, ref AppNative.StartupInfo startup, out AppNative.ProcessInfo process);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, char[] password, int logonType, int provider, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int value, int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, int creationFlags, IntPtr environment, string? directory, ref AppNative.StartupInfo startup, out AppNative.ProcessInfo process);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
