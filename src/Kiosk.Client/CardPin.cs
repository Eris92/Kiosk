using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kiosk.Client;

/// <summary>Who a session belongs to. Id is stable across removal and reinsertion; Upn (from the certificate) maps to a Windows account.</summary>
internal sealed record CardIdentity(string Id, string Name, string Upn = "")
{
    /// <summary>Thumbprints of all certificates on the card (for signing in to websites with the card).</summary>
    public string[] Thumbprints { get; init; } = [];
}

/// <summary>
/// Unlocks the in-app lock with the smart card's own PIN. A key on the card in the given reader signs
/// a random challenge through the Smart Card Key Storage Provider, so Windows shows its secure PIN
/// dialog and the card itself checks the PIN. Kiosk never sees or stores the PIN.
/// The check runs in a short-lived child process: the provider caches a verified PIN per process,
/// which would otherwise let every later unlock pass without asking.
/// </summary>
internal static class CardPin
{
    internal const string Argument = "--verify-card-pin";
    internal const int Verified = 0, Failed = 1, NoKey = 2, Cancelled = 3, WrongPin = 4;
    private const string Provider = "Microsoft Smart Card Key Storage Provider";

    internal sealed record Result(int Code, string Message);

    /// <summary>Starts the child check and waits for it; the PIN dialog is owned by <paramref name="owner"/>.</summary>
    internal static async Task<Result> VerifyAsync(string reader, IntPtr owner, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(reader);
        start.ArgumentList.Add(owner.ToInt64().ToString());
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        try { await process.WaitForExitAsync(cancel); }
        catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
        var message = (await output).Trim();
        return new Result(process.ExitCode, message);
    }

    /// <summary>Child-process entry point. Returns the exit code and prints a Polish message.</summary>
    internal static int Run(string reader, IntPtr owner)
    {
        try
        {
            ResetCard(reader);
            Check(NCryptOpenStorageProvider(out var provider, Provider, 0));
            using (provider)
            {
                var scope = @"\\.\" + reader + @"\";
                IntPtr state = IntPtr.Zero;
                var names = new List<string>();
                try
                {
                    while (NCryptEnumKeys(provider, scope, out var item, ref state, Silent) == 0)
                    {
                        names.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item))!);
                        NCryptFreeBuffer(item);
                    }
                }
                finally { if (state != IntPtr.Zero) NCryptFreeBuffer(state); }
                if (names.Count == 0) return Exit(NoKey, "Na karcie w czytniku „" + reader + "” nie znaleziono klucza do sprawdzenia PIN-u.");

                int last = 0;
                foreach (var name in names)
                {
                    // Not silent: a key opened with NCRYPT_SILENT_FLAG refuses to show the PIN dialog (NTE_SILENT_CONTEXT).
                    if (NCryptOpenKey(provider, out var key, scope + name, 0, 0) != 0) continue;
                    using (key)
                    {
                        if (!CanSign(key)) continue;
                        SetProperty(key, "HWND Handle", BitConverter.GetBytes(owner.ToInt64()));
                        SetProperty(key, "Use Context", Encoding.Unicode.GetBytes("Odblokowanie Kiosku\0"));
                        last = Sign(key);
                        if (last == 0) return Exit(Verified, "PIN karty potwierdzony.");
                        if (last is unchecked((int)0x8010006E) or unchecked((int)0x800704C7)) return Exit(Cancelled, "Anulowano wpisywanie PIN-u.");
                        if (last is unchecked((int)0x8010006B) or unchecked((int)0x8010006C)) return Exit(WrongPin, "Nieprawidłowy PIN karty.");
                    }
                }
                return last == 0 ? Exit(NoKey, "Karta nie ma klucza, którym można potwierdzić PIN.")
                    : Exit(Failed, "Nie udało się sprawdzić PIN-u karty (0x" + last.ToString("X8") + ").");
            }
        }
        catch (Exception ex) { return Exit(Failed, "Nie udało się sprawdzić PIN-u karty: " + ex.Message); }
    }

    /// <summary>
    /// Identifies the person on the card without a PIN: the certificates stored next to the card's keys.
    /// The smallest thumbprint keeps the identity stable when a card holds several certificates.
    /// </summary>
    internal static CardIdentity? ReadIdentity(string reader)
    {
        if (NCryptOpenStorageProvider(out var provider, Provider, 0) != 0) return null;
        using (provider)
        {
            var scope = @"\\.\" + reader + @"\";
            var certificates = new List<System.Security.Cryptography.X509Certificates.X509Certificate2>();
            IntPtr state = IntPtr.Zero;
            try
            {
                while (NCryptEnumKeys(provider, scope, out var item, ref state, Silent) == 0)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item))!;
                    NCryptFreeBuffer(item);
                    if (NCryptOpenKey(provider, out var key, scope + name, 0, Silent) != 0) continue;
                    using (key)
                    {
                        if (NCryptGetProperty(key, "SmartCardKeyCertificate", null, 0, out var size, Silent) != 0 || size == 0) continue;
                        var data = new byte[size];
                        if (NCryptGetProperty(key, "SmartCardKeyCertificate", data, size, out _, Silent) != 0) continue;
                        try { certificates.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(data)); }
                        catch (CryptographicException) { }
                    }
                }
            }
            finally { if (state != IntPtr.Zero) NCryptFreeBuffer(state); }
            var chosen = certificates.OrderBy(c => c.Thumbprint, StringComparer.Ordinal).FirstOrDefault();
            var identity = chosen == null ? null : new CardIdentity("cert:" + chosen.Thumbprint,
                chosen.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
                certificates.Select(c => c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.UpnName, false)).FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? "")
            { Thumbprints = certificates.Select(c => c.Thumbprint).ToArray() };
            foreach (var certificate in certificates) certificate.Dispose();
            return identity;
        }
    }

    /// <summary>
    /// Cards such as PIV/YubiKey stay PIN-verified while powered, so a signature would pass silently.
    /// A warm reset clears that state and the PIN must be entered again.
    /// </summary>
    private static void ResetCard(string reader)
    {
        if (SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out var context) != 0) return;
        try
        {
            if (SCardConnect(context, reader, 2, 3, out var card, out _) == 0) // SHARED, T0 | T1
                SCardDisconnect(card, 1); // SCARD_RESET_CARD
        }
        finally { SCardReleaseContext(context); }
    }

    [DllImport("winscard.dll")] private static extern int SCardEstablishContext(uint scope, IntPtr reserved1, IntPtr reserved2, out IntPtr context);
    [DllImport("winscard.dll")] private static extern int SCardReleaseContext(IntPtr context);
    [DllImport("winscard.dll", EntryPoint = "SCardConnectW", CharSet = CharSet.Unicode)] private static extern int SCardConnect(IntPtr context, string reader, uint share, uint protocols, out IntPtr card, out uint protocol);
    [DllImport("winscard.dll")] private static extern int SCardDisconnect(IntPtr card, uint disposition);

    private static int Exit(int code, string message) { Console.Out.Write(message); Console.Out.Flush(); return code; }

    private static bool CanSign(SafeNCryptKeyHandle key)
    {
        var usage = new byte[4];
        if (NCryptGetProperty(key, "Key Usage", usage, 4, out _, Silent) != 0) return true; // Unknown: try it.
        return (BitConverter.ToInt32(usage) & 0x2) != 0; // NCRYPT_ALLOW_SIGNING_FLAG
    }

    private static int Sign(SafeNCryptKeyHandle key)
    {
        var hash = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
        var group = new byte[64];
        bool rsa = NCryptGetProperty(key, "Algorithm Group", group, group.Length, out var length, Silent) == 0 &&
                   Encoding.Unicode.GetString(group, 0, length).TrimEnd('\0') == "RSA";
        var algorithm = Marshal.StringToHGlobalUni("SHA256");
        var padding = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(padding, algorithm); // BCRYPT_PKCS1_PADDING_INFO { pszAlgId }
            IntPtr info = rsa ? padding : IntPtr.Zero;
            int flags = rsa ? 0x2 : 0; // NCRYPT_PAD_PKCS1_FLAG; no NCRYPT_SILENT_FLAG so Windows asks for the PIN.
            int status = NCryptSignHash(key, info, hash, hash.Length, null, 0, out var size, flags);
            if (status != 0) return status;
            var signature = new byte[size];
            return NCryptSignHash(key, info, hash, hash.Length, signature, signature.Length, out _, flags);
        }
        finally { Marshal.FreeHGlobal(padding); Marshal.FreeHGlobal(algorithm); }
    }

    private static void SetProperty(SafeNCryptKeyHandle key, string name, byte[] value) =>
        NCryptSetProperty(key, name, value, value.Length, 0);

    private static void Check(int status)
    {
        if (status != 0) throw new CryptographicException(status);
    }

    private const int Silent = 0x40; // NCRYPT_SILENT_FLAG
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, string name, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptEnumKeys(SafeNCryptProviderHandle provider, string scope, out IntPtr keyName, ref IntPtr state, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptFreeBuffer(IntPtr buffer);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenKey(SafeNCryptProviderHandle provider, out SafeNCryptKeyHandle key, string name, int legacyKeySpec, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptSetProperty(SafeNCryptKeyHandle handle, string property, byte[] value, int size, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptGetProperty(SafeNCryptKeyHandle handle, string property, byte[]? output, int size, out int result, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptSignHash(SafeNCryptKeyHandle key, IntPtr padding, byte[] hash, int hashSize, byte[]? signature, int signatureSize, out int result, int flags);
}
