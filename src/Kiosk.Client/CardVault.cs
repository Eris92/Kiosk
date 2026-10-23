using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Remembers which account a FIDO2 card belongs to, so after the first "Zaloguj" the card and its PIN are enough.
/// The account password is encrypted (AES-GCM) with a key the card itself derives (FIDO2 hmac-secret) only after
/// its PIN is verified (typed in Kiosk, sent to the card encrypted), for a credential that exists only on that card.
/// This protects saved files at rest, not a compromised process or administrator-controlled machine.
/// Files: %LOCALAPPDATA%\Kiosk\Cards, one per card.
/// </summary>
internal static class CardVault
{
    private sealed class Entry
    {
        public int Version { get; set; }
        public string CredentialId { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Cipher { get; set; } = "";
        public string User { get; set; } = "";
        public string? Domain { get; set; }
        /// <summary>Set up through Kiosk's own PIN field (direct CTAP2); older entries are set up again.</summary>
        public bool Direct { get; set; }
    }

    internal sealed record Result(UserLogon? Logon, int Code, string Message);

    internal static bool Available => true;

    private static string FileFor(string cardId)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Cards");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cardId))) + ".json");
    }

    internal static bool IsEnrolled(string cardId) => File.Exists(FileFor(cardId));

    internal static void Forget(string cardId) { try { File.Delete(FileFor(cardId)); } catch (IOException) { } }

    /// <summary>
    /// Card + PIN typed in Kiosk: the card derives the key and the stored account is opened.
    /// Code CardPin.Verified with Logon null means the PIN was fine but the password no longer works (changed);
    /// CardPin.NoKey means the card has to be set up again (Enroll).
    /// </summary>
    internal static Result Open(string cardId, string reader, string pin)
    {
        Entry entry;
        try
        {
            using var stream = File.OpenRead(FileFor(cardId));
            if (stream.Length is < 1 or > 16384) throw new InvalidDataException();
            var data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
            entry = JsonSerializer.Deserialize<Entry>(data) ?? throw new InvalidDataException();
            ValidateEntry(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException or InvalidDataException)
        { return new(null, CardPin.NoKey, "Nie można odczytać zapisanego konta. Zapamiętaj kartę ponownie."); }
        if (!entry.Direct) { Forget(cardId); return new(null, CardPin.NoKey, "Kartę trzeba zapamiętać ponownie."); }
        var answer = CardService.Call(new CardService.Request { Op = "open", Reader = reader, Pin = pin, CredentialId = entry.CredentialId, Salt = entry.Salt });
        if (answer.Code == 0x2E) { Forget(cardId); return new(null, CardPin.NoKey, answer.Message); }
        if (answer.Code != 0 || answer.Secret == null) return new(null, answer.Code == 0x31 ? CardPin.WrongPin : CardPin.Failed, answer.Message);
        byte[]? key = null, plain = null;
        try
        {
            key = Convert.FromBase64String(answer.Secret);
            answer.Secret = null;
            var cipher = Convert.FromBase64String(entry.Cipher);
            plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(Convert.FromBase64String(entry.Nonce), cipher,
                Convert.FromBase64String(entry.Tag), plain, AccountData(entry.User, entry.Domain));
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
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { return new(null, CardPin.NoKey, "Karta nie pasuje do zapisanego konta. Zapamiętaj ją ponownie."); }
        finally { answer.Secret = null; if (plain != null) CryptographicOperations.ZeroMemory(plain); if (key != null) CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>First use of a card (or a changed password): the account is saved for this card.</summary>
    internal static Result Enroll(string cardId, UserLogon logon, string reader, string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var answer = CardService.Call(new CardService.Request { Op = "enroll", Reader = reader, Pin = pin, Salt = Convert.ToBase64String(salt) });
        if (answer.Code != 0 || answer.Secret == null || answer.CredentialId == null)
            return new(null, answer.Code == 0x31 ? CardPin.WrongPin : CardPin.Failed, answer.Message);
        byte[]? key = null, plain = null;
        char[]? password = null;
        try
        {
            key = Convert.FromBase64String(answer.Secret);
            answer.Secret = null;
            var credentialId = Convert.FromBase64String(answer.CredentialId);
            password = logon.Reveal();
            int length = Array.IndexOf(password, '\0');
            plain = Encoding.Unicode.GetBytes(password, 0, length);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, AccountData(logon.User, logon.Domain));
            var entry = new Entry
            {
                Version = 1, Direct = true, CredentialId = Convert.ToBase64String(credentialId), Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Cipher = Convert.ToBase64String(cipher),
                User = logon.User, Domain = logon.Domain
            };
            Save(FileFor(cardId), entry);
            return new(logon, CardPin.Verified, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException or ArgumentException)
        { return new(null, CardPin.Failed, "Nie udało się bezpiecznie zapisać konta dla tej karty."); }
        finally
        {
            answer.Secret = null;
            if (password != null) Array.Clear(password);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (key != null) CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static byte[] AccountData(string user, string? domain) => JsonSerializer.SerializeToUtf8Bytes(new[] { user, domain });

    private static void ValidateEntry(Entry entry)
    {
        if (entry.Version != 1 || string.IsNullOrWhiteSpace(entry.User) || entry.User.Length > 514 ||
            entry.User.Contains('\0') || entry.Domain?.Contains('\0') == true || entry.Domain?.Length > 338 ||
            Convert.FromBase64String(entry.Salt).Length != 32 || Convert.FromBase64String(entry.Nonce).Length != 12 ||
            Convert.FromBase64String(entry.Tag).Length != 16 || Convert.FromBase64String(entry.CredentialId).Length is < 1 or > 4096 ||
            Convert.FromBase64String(entry.Cipher).Length is var bytes && (bytes > 1024 || bytes % 2 != 0))
            throw new InvalidDataException("Invalid card vault entry.");
    }

    private static void Save(string path, Entry entry)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, entry);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
