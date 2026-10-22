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

    /// <summary>Kiosk Card service, "sharedaccount": one account of accounts.kiosk next to the client.json this PC uses.</summary>
    internal static CardService.Response ForService(string name, string? clientProgram)
    {
        try
        {
            // Only the installed Kiosk (Program Files, which users cannot change) gets a password; any other program of
            // the person signed in could otherwise ask the service for it.
            if (!IsInstalledKiosk(clientProgram)) return new CardService.Response { Code = -2, Message = "Konta wspólne podaje usługa tylko programowi Kiosk z Program Files." };
            var account = Read(Path.Combine(CentralConfig.MachineConfigFolder(), FileName), name);
            Audit.Write("shared_account_used", name);
            return new CardService.Response { Message = account.User, Secret = account.Password };
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    /// <summary>The service itself (SharedAccountPrograms): the account stays in the service.</summary>
    internal static Account ForProgram(string name) => Read(Path.Combine(CentralConfig.MachineConfigFolder(), FileName), name);

    internal static bool IsInstalledKiosk(string? program)
    {
        if (program == null || !string.Equals(Path.GetFileName(program), "Kiosk.Client.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
        return Path.GetFullPath(program).StartsWith(programFiles, StringComparison.OrdinalIgnoreCase);
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
    internal static Account Get(string name)
    {
        var answer = CardService.Call(new CardService.Request { Op = "sharedaccount", Source = name });
        if (answer.Code != 0 || answer.Secret == null)
            throw new InvalidOperationException("Konto wspólne „" + name + "”: " + answer.Message);
        return new Account(answer.Message, answer.Secret);
    }

    private static readonly Dictionary<string, Account> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For pages: asked once per Kiosk start (the same account signs in to many pages).</summary>
    internal static Account? TryGetCached(string name)
    {
        lock (cache) if (cache.TryGetValue(name, out var known)) return known;
        try
        {
            var account = Get(name);
            lock (cache) cache[name] = account;
            return account;
        }
        catch (Exception ex) { Audit.Write("shared_account_failed", name + ": " + ex.Message); return null; }
    }
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
