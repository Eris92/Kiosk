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

    /// <summary>
    /// Konfiguracja → "Test": can this account reach the folder, and is its client.json usable? Nothing is copied.
    /// The service checks as the computer (or the saved account), the Konfiguracja window as the person signed in.
    /// </summary>
    internal static CardService.Response Test(string source, bool savedAccount)
    {
        try
        {
            source = source.Trim().Trim('"');
            if (source.Length == 0 || !Path.IsPathFullyQualified(source)) return new CardService.Response { Code = -2, Message = "podaj pełną ścieżkę, np. \\\\serwer\\kiosk" };
            using (savedAccount ? Connect(source) : null)
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
    private static string Sync(string source, string cache)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("brak dostępu do " + source);
        if (!File.Exists(Path.Combine(source, FileName))) throw new FileNotFoundException("w " + source + " nie ma " + FileName);
        Directory.CreateDirectory(cache);
        var folder = Path.Combine(cache, DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Copy(source, folder);
            Configuration.Load(Path.Combine(folder, FileName));
        }
        catch { TryDelete(folder); throw; }
        var pointer = Path.Combine(cache, Pointer);
        File.WriteAllText(pointer + ".tmp", Path.GetFileName(folder));
        File.Move(pointer + ".tmp", pointer, true);
        foreach (var old in Directory.GetDirectories(cache))
            if (!string.Equals(old, folder, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
        Audit.Write("central_config_synced", source);
        return folder;
    }

    private static string? Current(string cache)
    {
        try
        {
            var folder = Path.Combine(cache, File.ReadAllText(Path.Combine(cache, Pointer)).Trim());
            return File.Exists(Path.Combine(folder, FileName)) ? folder : null;
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

    /// <summary>With a saved account: a connection to the share root for the copy (closed afterwards). Without: null.</summary>
    private static IDisposable? Connect(string source)
    {
        if (!File.Exists(CredentialFile) || !source.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var account = JsonSerializer.Deserialize<ShareAccount>(Unprotect(File.ReadAllBytes(CredentialFile)))
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

    private static byte[] Unprotect(byte[] data)
    {
        var input = Pin(data);
        var entropy = Pin(Entropy);
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
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? user, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
