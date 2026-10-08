using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Remembers which account a FIDO2 card belongs to, so after the first "Zaloguj" the card and its PIN are enough.
/// The account password is encrypted (AES-GCM) with a key the card itself derives (FIDO2 hmac-secret) only after
/// its PIN is verified (typed in Kiosk, sent to the card encrypted), for a credential that exists only on that card. Without the card and its PIN nobody,
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
        var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(FileFor(cardId)))!;
        if (!entry.Direct) { Forget(cardId); return new(null, CardPin.NoKey, "Kartę trzeba zapamiętać ponownie."); }
        byte[] key;
        try
        {
            using var card = new CtapCard(reader);
            card.UsePin(pin);
            key = card.HmacSecret(RelyingParty, Convert.FromBase64String(entry.CredentialId), Convert.FromBase64String(entry.Salt));
        }
        catch (CtapException ex) when (ex.Code == 0x2E) { Forget(cardId); return new(null, CardPin.NoKey, ex.Message); }
        catch (CtapException ex) { return new(null, ex.Code == 0x31 ? CardPin.WrongPin : CardPin.Failed, ex.Message); }
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
        catch (CryptographicException) { Forget(cardId); return new(null, CardPin.NoKey, "Karta nie pasuje do zapisanego konta."); }
        finally { Array.Clear(plain); Array.Clear(key); }
    }

    /// <summary>First use of a card (or a changed password): the account is saved for this card.</summary>
    internal static Result Enroll(string cardId, UserLogon logon, string reader, string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        byte[] key, credentialId;
        try
        {
            using var card = new CtapCard(reader);
            card.UsePin(pin);
            credentialId = card.MakeCredential(RelyingParty);
            key = card.HmacSecret(RelyingParty, credentialId, salt);
        }
        catch (CtapException ex) { return new(null, ex.Code == 0x31 ? CardPin.WrongPin : CardPin.Failed, ex.Message); }
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
                Direct = true, CredentialId = Convert.ToBase64String(credentialId), Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Cipher = Convert.ToBase64String(cipher),
                User = logon.User, Domain = logon.Domain
            };
            File.WriteAllText(FileFor(cardId), JsonSerializer.Serialize(entry));
            return new(logon, CardPin.Verified, "");
        }
        finally { Array.Clear(password); Array.Clear(plain); Array.Clear(key); }
    }
}
