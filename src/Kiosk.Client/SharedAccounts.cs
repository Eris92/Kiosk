using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    private static bool IsInstalledKiosk(string? program)
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

internal sealed class NetworkAccountStarter(SharedAccounts.Account account) : IProgramStarter
{
    public (IntPtr Process, IntPtr Thread) Start(string application, string commandLine, string? directory, short show = 4)
    {
        var startup = new AppNative.StartupInfo { Size = Marshal.SizeOf<AppNative.StartupInfo>(), Flags = 1, ShowWindow = show };
        var (user, domain) = account.Split();
        var password = (account.Password + "\0").ToCharArray();
        try
        {
            // LOGON_NETCREDENTIALS_ONLY: this PC's own account runs it; the shared one is used on the network only
            // (also where Windows refuses password sign-in to the PC itself). CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT.
            if (!CreateProcessWithLogonW(user, domain, password, 2, application, new StringBuilder(commandLine), 0x4 | 0x400,
                    IntPtr.Zero, directory, ref startup, out var process))
                throw new Win32Exception();
            return (process.Process, process.Thread);
        }
        finally { Array.Clear(password); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithLogonW(string user, string? domain, char[] password, int logonFlags, string application,
        StringBuilder commandLine, int creationFlags, IntPtr environment, string? directory, ref AppNative.StartupInfo startup, out ProcessInformation process);
}
