using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Remembers which account a FIDO2 card belongs to, so after the first "Zaloguj" the card and its PIN are enough.
/// The account password is encrypted (AES-GCM) with a key the card itself derives (FIDO2 hmac-secret) only after
/// its PIN is verified, for a credential that exists only on that card. Without the card and its PIN nobody,
/// administrators included, can decrypt it. Files: %LOCALAPPDATA%\Kiosk\Cards, one per card.
/// </summary>
internal static class CardVault
{
    private const string RelyingParty = "kiosk.local";

    private sealed class Entry
    {
        public string CredentialId { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Cipher { get; set; } = "";
        public string User { get; set; } = "";
        public string? Domain { get; set; }
    }

    internal sealed record Result(UserLogon? Logon, int Code, string Message);

    internal static bool Available => FidoNative.WebAuthNGetApiVersionNumber() >= 4;

    private static string FileFor(string cardId)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Cards");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cardId))) + ".json");
    }

    internal static bool IsEnrolled(string cardId) => File.Exists(FileFor(cardId));

    internal static void Forget(string cardId) { try { File.Delete(FileFor(cardId)); } catch (IOException) { } }

    /// <summary>
    /// Card + PIN (one Windows dialog): derives the key on the card and opens the stored account.
    /// Code CardPin.Verified with Logon null means the PIN was fine but the password no longer works (changed).
    /// </summary>
    internal static Result Open(string cardId, IntPtr owner)
    {
        var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(FileFor(cardId)))!;
        var key = DeriveKey(owner, Convert.FromBase64String(entry.CredentialId), Convert.FromBase64String(entry.Salt), out var failure);
        if (key == null) return failure!;
        var cipher = Convert.FromBase64String(entry.Cipher);
        var plain = new byte[cipher.Length];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(Convert.FromBase64String(entry.Nonce), cipher, Convert.FromBase64String(entry.Tag), plain);
            int length = plain.Length / 2;
            var password = new char[length + 1];
            Encoding.Unicode.GetChars(plain, 0, plain.Length, password, 0);
            try
            {
                var logon = UserLogon.TryLogon(entry.User, entry.Domain, password, length, out int code);
                return logon != null ? new(logon, CardPin.Verified, "")
                    : new(null, CardPin.Verified, "Hasło konta zmieniło się (kod " + code + "). Podaj nowe.");
            }
            finally { Array.Clear(password); }
        }
        catch (CryptographicException) { return new(null, CardPin.Failed, "Karta nie pasuje do zapisanego konta."); }
        finally { Array.Clear(plain); Array.Clear(key); }
    }

    /// <summary>First use of a card: a credential with hmac-secret is created on it and the account is saved.</summary>
    internal static Result Enroll(string cardId, UserLogon logon, IntPtr owner)
    {
        var credentialId = CreateCredential(owner, out var failure);
        if (credentialId == null) return failure!;
        return Save(cardId, logon, credentialId, owner);
    }

    /// <summary>The password changed: re-encrypt it for the card's existing credential.</summary>
    internal static Result Update(string cardId, UserLogon logon, IntPtr owner)
    {
        var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(FileFor(cardId)))!;
        return Save(cardId, logon, Convert.FromBase64String(entry.CredentialId), owner);
    }

    private static Result Save(string cardId, UserLogon logon, byte[] credentialId, IntPtr owner)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var key = DeriveKey(owner, credentialId, salt, out var failure);
        if (key == null) return failure!;
        var password = logon.Reveal();
        int length = Array.IndexOf(password, '\0');
        var plain = Encoding.Unicode.GetBytes(password, 0, length);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
            var entry = new Entry
            {
                CredentialId = Convert.ToBase64String(credentialId), Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag), Cipher = Convert.ToBase64String(cipher), User = logon.User, Domain = logon.Domain
            };
            File.WriteAllText(FileFor(cardId), JsonSerializer.Serialize(entry));
            return new(logon, CardPin.Verified, "");
        }
        finally { Array.Clear(password); Array.Clear(plain); Array.Clear(key); }
    }

    // ------------------------------------------------------------------ WebAuthn

    private static byte[]? CreateCredential(IntPtr owner, out Result? failure)
    {
        failure = null;
        var pins = new List<GCHandle>();
        var strings = new List<IntPtr>();
        IntPtr Pin(object o) { var h = GCHandle.Alloc(o, GCHandleType.Pinned); pins.Add(h); return h.AddrOfPinnedObject(); }
        IntPtr Text(string s) { var p = Marshal.StringToHGlobalUni(s); strings.Add(p); return p; }
        IntPtr attestation = IntPtr.Zero;
        try
        {
            var userId = RandomNumberGenerator.GetBytes(16);
            var clientData = ClientData("webauthn.create");
            var rp = new FidoNative.RpEntity { Version = 1, Id = Text(RelyingParty), Name = Text("Kiosk") };
            var user = new FidoNative.UserEntity { Version = 1, IdLength = userId.Length, Id = Pin(userId), Name = Text("Kiosk"), DisplayName = Text("Karta Kiosku") };
            var algorithms = new[]
            {
                new FidoNative.CoseParameter { Version = 1, Type = Text("public-key"), Algorithm = -7 },
                new FidoNative.CoseParameter { Version = 1, Type = Text("public-key"), Algorithm = -257 }
            };
            var parameters = new FidoNative.CoseParameters { Count = algorithms.Length, Items = Pin(algorithms) };
            var data = new FidoNative.ClientData { Version = 1, Length = clientData.Length, Json = Pin(clientData), HashAlgorithm = Text("SHA-256") };
            var enabled = new[] { 1 }; // BOOL TRUE: ask the card for hmac-secret support on this credential
            var extension = new[] { new FidoNative.Extension { Identifier = Text("hmac-secret"), Length = 4, Value = Pin(enabled) } };
            var options = new FidoNative.MakeCredentialOptions
            {
                Version = 1, TimeoutMilliseconds = 120_000, ExtensionCount = 1, Extensions = Pin(extension),
                Attachment = 2, RequireResidentKey = 0, UserVerification = 1, Attestation = 1
            };
            int hr = FidoNative.WebAuthNAuthenticatorMakeCredential(owner, ref rp, ref user, ref parameters, ref data, ref options, out attestation);
            if (hr != 0 || attestation == IntPtr.Zero) { failure = Failure(hr); return null; }
            // WEBAUTHN_CREDENTIAL_ATTESTATION: cbCredentialId at 80, pbCredentialId at 88 (x64).
            int length = Marshal.ReadInt32(attestation, 80);
            var id = new byte[length];
            Marshal.Copy(Marshal.ReadIntPtr(attestation, 88), id, 0, length);
            return id;
        }
        finally
        {
            if (attestation != IntPtr.Zero) FidoNative.WebAuthNFreeCredentialAttestation(attestation);
            foreach (var h in pins) h.Free();
            foreach (var s in strings) Marshal.FreeHGlobal(s);
        }
    }

    /// <summary>Card + PIN: the card proves the PIN and returns HMAC(credential secret, salt) — the vault key.</summary>
    private static byte[]? DeriveKey(IntPtr owner, byte[] credentialId, byte[] salt, out Result? failure)
    {
        failure = null;
        var pins = new List<GCHandle>();
        var strings = new List<IntPtr>();
        IntPtr Pin(object o) { var h = GCHandle.Alloc(o, GCHandleType.Pinned); pins.Add(h); return h.AddrOfPinnedObject(); }
        IntPtr Text(string s) { var p = Marshal.StringToHGlobalUni(s); strings.Add(p); return p; }
        IntPtr assertion = IntPtr.Zero;
        try
        {
            var clientData = ClientData("webauthn.get");
            var data = new FidoNative.ClientData { Version = 1, Length = clientData.Length, Json = Pin(clientData), HashAlgorithm = Text("SHA-256") };
            var allowed = new[] { new FidoNative.Credential { Version = 1, IdLength = credentialId.Length, Id = Pin(credentialId), Type = Text("public-key") } };
            var saltValue = new[] { new FidoNative.HmacSalt { FirstLength = salt.Length, First = Pin(salt) } };
            var salts = new[] { new FidoNative.HmacSaltValues { Global = Pin(saltValue) } };
            var options = new FidoNative.GetAssertionOptions
            {
                Version = 6, TimeoutMilliseconds = 120_000, CredentialCount = 1, Credentials = Pin(allowed),
                Attachment = 2, UserVerification = 1, HmacSecretSaltValues = Pin(salts)
            };
            int hr = FidoNative.WebAuthNAuthenticatorGetAssertion(owner, RelyingParty, ref data, ref options, out assertion);
            if (hr != 0 || assertion == IntPtr.Zero) { failure = Failure(hr); return null; }
            // WEBAUTHN_ASSERTION v3+: authenticator data at 4/8, pHmacSecret at 112 (x64).
            int dataLength = Marshal.ReadInt32(assertion, 4);
            bool verified = dataLength > 32 && (Marshal.ReadByte(Marshal.ReadIntPtr(assertion, 8), 32) & 0x04) != 0;
            IntPtr secret = Marshal.ReadInt32(assertion, 0) >= 3 ? Marshal.ReadIntPtr(assertion, 112) : IntPtr.Zero;
            if (!verified) { failure = new(null, CardPin.Failed, "Karta nie potwierdziła PIN-u."); return null; }
            if (secret == IntPtr.Zero) { failure = new(null, CardPin.NoKey, "Ta karta nie obsługuje zapamiętywania konta (hmac-secret)."); return null; }
            int secretLength = Marshal.ReadInt32(secret, 0);
            var key = new byte[32];
            Marshal.Copy(Marshal.ReadIntPtr(secret, 8), key, 0, Math.Min(32, secretLength));
            return key;
        }
        finally
        {
            if (assertion != IntPtr.Zero) FidoNative.WebAuthNFreeAssertion(assertion);
            foreach (var h in pins) h.Free();
            foreach (var s in strings) Marshal.FreeHGlobal(s);
        }
    }

    private static byte[] ClientData(string type)
    {
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encoding.UTF8.GetBytes("{\"type\":\"" + type + "\",\"challenge\":\"" + challenge + "\",\"origin\":\"https://" + RelyingParty + "\"}");
    }

    private static Result Failure(int hr) =>
        hr is unchecked((int)0x800704C7) or unchecked((int)0x80090036)
            ? new(null, CardPin.Cancelled, "Anulowano.")
            : new(null, CardPin.Failed, "Karta nie potwierdziła PIN-u (0x" + hr.ToString("X8") + ").");
}

internal static class FidoNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct RpEntity { public int Version; public IntPtr Id, Name, Icon; }
    [StructLayout(LayoutKind.Sequential)] internal struct UserEntity { public int Version, IdLength; public IntPtr Id, Name, Icon, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] internal struct CoseParameter { public int Version; public IntPtr Type; public int Algorithm; }
    [StructLayout(LayoutKind.Sequential)] internal struct CoseParameters { public int Count; public IntPtr Items; }
    [StructLayout(LayoutKind.Sequential)] internal struct ClientData { public int Version, Length; public IntPtr Json, HashAlgorithm; }
    [StructLayout(LayoutKind.Sequential)] internal struct Extension { public IntPtr Identifier; public int Length; public IntPtr Value; }
    [StructLayout(LayoutKind.Sequential)] internal struct Credential { public int Version, IdLength; public IntPtr Id, Type; }
    [StructLayout(LayoutKind.Sequential)] internal struct HmacSalt { public int FirstLength; public IntPtr First; public int SecondLength; public IntPtr Second; }
    [StructLayout(LayoutKind.Sequential)] internal struct HmacSaltValues { public IntPtr Global; public int PerCredentialCount; public IntPtr PerCredential; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MakeCredentialOptions
    {
        public int Version, TimeoutMilliseconds;
        public int CredentialCount; public IntPtr Credentials;
        public int ExtensionCount; public IntPtr Extensions;
        public int Attachment, RequireResidentKey, UserVerification, Attestation, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GetAssertionOptions
    {
        public int Version, TimeoutMilliseconds;
        public int CredentialCount; public IntPtr Credentials;           // allow list (only the card's credential)
        public int ExtensionCount; public IntPtr Extensions;
        public int Attachment, UserVerification, Flags;
        public IntPtr U2fAppId, U2fAppIdUsed, CancellationId, AllowCredentialList;
        public int LargeBlobOperation, LargeBlobLength; public IntPtr LargeBlob;
        public IntPtr HmacSecretSaltValues;
        public int BrowserInPrivateMode;
    }

    [DllImport("webauthn.dll")] internal static extern int WebAuthNGetApiVersionNumber();
    [DllImport("webauthn.dll")]
    internal static extern int WebAuthNAuthenticatorMakeCredential(IntPtr hWnd, ref RpEntity rp, ref UserEntity user, ref CoseParameters parameters,
        ref ClientData clientData, ref MakeCredentialOptions options, out IntPtr attestation);
    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)]
    internal static extern int WebAuthNAuthenticatorGetAssertion(IntPtr hWnd, string rpId, ref ClientData clientData, ref GetAssertionOptions options, out IntPtr assertion);
    [DllImport("webauthn.dll")] internal static extern void WebAuthNFreeCredentialAttestation(IntPtr attestation);
    [DllImport("webauthn.dll")] internal static extern void WebAuthNFreeAssertion(IntPtr assertion);
}
