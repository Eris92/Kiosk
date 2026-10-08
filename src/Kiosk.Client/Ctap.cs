using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Kiosk.Client;

/// <summary>A CTAP2 error from the card (e.g. 0x31 wrong PIN, 0x32 PIN blocked).</summary>
internal sealed class CtapException(int code, string message) : Exception(message)
{
    internal int Code { get; } = code;
}

/// <summary>
/// FIDO2 (CTAP 2.0, PIN protocol 1) spoken directly to an NFC card through the PC/SC reader, so the person only
/// types the card PIN in Kiosk: no Windows security key dialog, no second tap. The PIN goes to the card encrypted
/// for this exchange only (ECDH); Kiosk keeps neither the PIN nor the PIN token.
/// </summary>
internal sealed class CtapCard : IDisposable
{
    private IntPtr context, card;
    private uint protocol;
    private ECDiffieHellman? platformKey;
    private byte[]? shared;     // SHA-256 of the ECDH secret with the card (PIN protocol 1)
    private byte[]? pinToken;

    internal CtapCard(string reader)
    {
        Check(SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out context), "Brak usługi kart");
        // Right after another program (or a card reset) used the card it can refuse briefly: reconnect and retry.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Check(SCardConnect(context, reader, 2, 3, out card, out protocol), "Brak karty w czytniku");
                // SELECT the FIDO applet.
                var answer = Apdu([0x00, 0xA4, 0x04, 0x00, 0x08, 0xA0, 0x00, 0x00, 0x06, 0x47, 0x2F, 0x00, 0x01, 0x00]);
                if (!answer.Ok) throw new CtapException(-2, "Karta nie ma aplikacji FIDO2.");
                return;
            }
            catch (CtapException ex) when (ex.Code == -1 && attempt < 4)
            {
                if (card != IntPtr.Zero) { SCardDisconnect(card, 1); card = IntPtr.Zero; } // SCARD_RESET_CARD
                Thread.Sleep(300);
            }
        }
    }

    // ------------------------------------------------------------------ PIN

    /// <summary>Checks the PIN on the card and keeps a PIN token for the next commands.</summary>
    internal void UsePin(string pin)
    {
        var keyAgreement = (Dictionary<object, object>)Command(0x06, Cbor.Map((1L, 1L), (2L, 2L)))[1L];
        var cardKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = (byte[])keyAgreement[-2L], Y = (byte[])keyAgreement[-3L] }
        });
        platformKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        shared = platformKey.DeriveKeyFromHash(cardKey.PublicKey, HashAlgorithmName.SHA256);
        var pinHash = SHA256.HashData(Encoding.UTF8.GetBytes(pin)).AsSpan(0, 16).ToArray();
        var reply = Command(0x06, Cbor.Map((1L, 1L), (2L, 5L), (3L, PlatformCose()), (6L, Encrypt(pinHash))));
        pinToken = Decrypt((byte[])reply[2L]);
    }

    /// <summary>authenticatorGetInfo as text, for diagnostics (no PIN needed).</summary>
    internal string Info()
    {
        var info = Command(0x04, null);
        return string.Join("; ", info.Select(p => p.Key + "=" + Describe(p.Value)));
        static string Describe(object v) => v switch
        {
            byte[] b => Convert.ToHexString(b),
            List<object> l => "[" + string.Join(",", l.Select(Describe)) + "]",
            Dictionary<object, object> m => "{" + string.Join(",", m.Select(p => p.Key + ":" + Describe(p.Value))) + "}",
            _ => v.ToString() ?? ""
        };
    }

    // ------------------------------------------------------------------ credentials

    /// <summary>Creates a non-resident credential with hmac-secret on the card; returns its ID.</summary>
    internal byte[] MakeCredential(string rpId)
    {
        var clientDataHash = RandomNumberGenerator.GetBytes(32);
        var reply = Command(0x01, Cbor.Map(
            (1L, clientDataHash),
            (2L, Cbor.Map(("id", rpId), ("name", "Kiosk"))),
            (3L, Cbor.Map(("id", RandomNumberGenerator.GetBytes(16)), ("name", "Kiosk"), ("displayName", "Karta Kiosku"))),
            (4L, new List<object> { Cbor.Map(("alg", -7L), ("type", "public-key")) }),
            (6L, Cbor.Map(("hmac-secret", true))),
            (8L, PinAuth(clientDataHash)),
            (9L, 1L)));
        var authData = (byte[])reply[2L];
        // rpIdHash 32, flags 1, counter 4, AAGUID 16, credential ID length 2, credential ID.
        int length = authData[53] << 8 | authData[54];
        return authData.AsSpan(55, length).ToArray();
    }

    /// <summary>The card's HMAC of <paramref name="salt"/> for the given credential (FIDO2 hmac-secret).</summary>
    internal byte[] HmacSecret(string rpId, byte[] credentialId, byte[] salt)
    {
        var clientDataHash = RandomNumberGenerator.GetBytes(32);
        var saltEnc = Encrypt(salt);
        var saltAuth = HMACSHA256.HashData(shared!, saltEnc).AsSpan(0, 16).ToArray();
        var reply = Command(0x02, Cbor.Map(
            (1L, rpId),
            (2L, clientDataHash),
            (3L, new List<object> { Cbor.Map(("id", credentialId), ("type", "public-key")) }),
            (4L, Cbor.Map(("hmac-secret", Cbor.Map((1L, PlatformCose()), (2L, saltEnc), (3L, saltAuth))))),
            (6L, PinAuth(clientDataHash)),
            (7L, 1L)));
        var authData = (byte[])reply[2L];
        if ((authData[32] & 0x04) == 0) throw new CtapException(-1, "Karta nie potwierdziła PIN-u.");
        if ((authData[32] & 0x80) == 0) throw new CtapException(-1, "Karta nie obsługuje zapamiętywania konta (hmac-secret).");
        var extensions = (Dictionary<object, object>)Cbor.Decode(authData.AsSpan(37).ToArray(), out _);
        return Decrypt((byte[])extensions["hmac-secret"]);
    }

    // ------------------------------------------------------------------ plumbing

    private Dictionary<object, object> PlatformCose()
    {
        var p = platformKey!.ExportParameters(false);
        return Cbor.Map((1L, 2L), (3L, -25L), (-1L, 1L), (-2L, p.Q.X!), (-3L, p.Q.Y!));
    }

    private byte[] PinAuth(byte[] clientDataHash) => HMACSHA256.HashData(pinToken!, clientDataHash).AsSpan(0, 16).ToArray();

    private byte[] Encrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = shared!;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }

    private byte[] Decrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = shared!;
        return aes.DecryptCbc(data, new byte[16], PaddingMode.None);
    }

    /// <summary>One CTAP2 command: status byte, then a CBOR map.</summary>
    private Dictionary<object, object> Command(byte command, object? parameters)
    {
        var body = parameters == null ? [] : Cbor.Encode(parameters);
        var request = new byte[body.Length + 1];
        request[0] = command;
        body.CopyTo(request, 1);
        var response = Exchange(request);
        if (response.Length == 0) throw new CtapException(-1, "Pusta odpowiedź karty.");
        if (response[0] != 0) throw new CtapException(response[0], CtapMessage(response[0]));
        return response.Length == 1 ? new() : (Dictionary<object, object>)Cbor.Decode(response.AsSpan(1).ToArray(), out _);
    }

    private static string CtapMessage(int code) => code switch
    {
        0x31 => "Nieprawidłowy PIN karty.",
        0x32 => "PIN karty jest zablokowany.",
        0x34 => "Za dużo błędnych PIN-ów. Zabierz kartę i przyłóż ją ponownie.",
        0x35 => "Karta nie ma ustawionego PIN-u.",
        0x2E => "Na karcie nie ma klucza tego Kiosku.",
        _ => "Błąd karty FIDO2 (0x" + code.ToString("X2") + ")."
    };

    /// <summary>NFCCTAP_MSG with command chaining for long requests, GET RESPONSE and keep-alive for long answers.</summary>
    private byte[] Exchange(byte[] request)
    {
        (byte[] Data, bool Ok, int Sw) answer = default;
        for (int offset = 0; offset < request.Length || offset == 0;)
        {
            int size = Math.Min(255, request.Length - offset);
            bool last = offset + size >= request.Length;
            var apdu = new byte[5 + size + (last ? 1 : 0)];
            apdu[0] = (byte)(last ? 0x80 : 0x90); apdu[1] = 0x10; apdu[4] = (byte)size;
            Array.Copy(request, offset, apdu, 5, size);
            answer = Apdu(apdu);
            offset += size;
            if (!last && !answer.Ok) throw new CtapException(-1, "Karta odrzuciła dane (" + answer.Sw.ToString("X4") + ").");
            if (size == 0) break;
        }
        var result = new List<byte>(answer.Data);
        while (true)
        {
            if (answer.Sw == 0x9100) { Thread.Sleep(100); answer = Apdu([0x80, 0x11, 0x00, 0x00, 0x00]); result.Clear(); result.AddRange(answer.Data); continue; }
            if (answer.Sw >> 8 == 0x61) { answer = Apdu([0x00, 0xC0, 0x00, 0x00, (byte)(answer.Sw & 0xFF)]); result.AddRange(answer.Data); continue; }
            break;
        }
        if (!answer.Ok) throw new CtapException(-1, "Karta odpowiedziała błędem " + answer.Sw.ToString("X4") + ".");
        return result.ToArray();
    }

    private (byte[] Data, bool Ok, int Sw) Apdu(byte[] apdu)
    {
        var send = new IoRequest { Protocol = protocol, Length = 8 };
        var response = new byte[258];
        int length = response.Length;
        Check(SCardTransmit(card, ref send, apdu, apdu.Length, IntPtr.Zero, response, ref length), "Błąd komunikacji z kartą (APDU " + Convert.ToHexString(apdu, 0, 2) + ", protokół " + protocol + ")");
        if (length < 2) throw new CtapException(-1, "Za krótka odpowiedź karty.");
        int sw = response[length - 2] << 8 | response[length - 1];
        return (response.AsSpan(0, length - 2).ToArray(), sw == 0x9000, sw);
    }

    private static void Check(int result, string what)
    {
        if (result != 0) throw new CtapException(-1, what + " (0x" + result.ToString("X8") + ").");
    }

    public void Dispose()
    {
        if (pinToken != null) Array.Clear(pinToken);
        if (shared != null) Array.Clear(shared);
        platformKey?.Dispose();
        if (card != IntPtr.Zero) { SCardDisconnect(card, 0); card = IntPtr.Zero; }
        if (context != IntPtr.Zero) { SCardReleaseContext(context); context = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IoRequest { public uint Protocol; public uint Length; }
    [DllImport("winscard.dll")] private static extern int SCardEstablishContext(uint scope, IntPtr r1, IntPtr r2, out IntPtr context);
    [DllImport("winscard.dll")] private static extern int SCardReleaseContext(IntPtr context);
    [DllImport("winscard.dll", EntryPoint = "SCardConnectW", CharSet = CharSet.Unicode)] private static extern int SCardConnect(IntPtr context, string reader, uint share, uint protocols, out IntPtr card, out uint protocol);
    [DllImport("winscard.dll")] private static extern int SCardDisconnect(IntPtr card, uint disposition);
    [DllImport("winscard.dll")] private static extern int SCardTransmit(IntPtr card, ref IoRequest send, byte[] command, int commandLength, IntPtr receive, byte[] response, ref int responseLength);
}

/// <summary>Just enough CBOR for CTAP2: canonical maps (keys in the order given), ints, byte/text strings, arrays, bools.</summary>
internal static class Cbor
{
    internal static Dictionary<object, object> Map(params (object Key, object Value)[] items)
    {
        var map = new Dictionary<object, object>();
        foreach (var (k, v) in items) map[k] = v;
        return map;
    }

    internal static byte[] Encode(object value)
    {
        var output = new List<byte>();
        Write(output, value);
        return output.ToArray();
    }

    private static void Head(List<byte> o, int major, ulong n)
    {
        int m = major << 5;
        if (n < 24) o.Add((byte)(m | (int)n));
        else if (n <= 0xFF) { o.Add((byte)(m | 24)); o.Add((byte)n); }
        else if (n <= 0xFFFF) { o.Add((byte)(m | 25)); o.Add((byte)(n >> 8)); o.Add((byte)n); }
        else { o.Add((byte)(m | 26)); for (int s = 24; s >= 0; s -= 8) o.Add((byte)(n >> s)); }
    }

    private static void Write(List<byte> o, object value)
    {
        switch (value)
        {
            case long n when n >= 0: Head(o, 0, (ulong)n); break;
            case long n: Head(o, 1, (ulong)(-1 - n)); break;
            case byte[] b: Head(o, 2, (ulong)b.Length); o.AddRange(b); break;
            case string s: var t = Encoding.UTF8.GetBytes(s); Head(o, 3, (ulong)t.Length); o.AddRange(t); break;
            case List<object> a: Head(o, 4, (ulong)a.Count); foreach (var x in a) Write(o, x); break;
            case Dictionary<object, object> m:
                Head(o, 5, (ulong)m.Count);
                // CTAP2 canonical order: integer keys ascending (positive before negative), then text keys by length, then bytes.
                foreach (var (k, v) in m.OrderBy(Rank).ThenBy(p => p.Key is string s2 ? s2.Length : 0).ThenBy(p => p.Key is string s3 ? s3 : "", StringComparer.Ordinal))
                { Write(o, k); Write(o, v); }
                break;
            case bool b: o.Add(b ? (byte)0xF5 : (byte)0xF4); break;
            default: throw new ArgumentException("CBOR: " + value.GetType());
        }
    }

    private static long Rank(KeyValuePair<object, object> p) => p.Key is long n ? (n >= 0 ? n : 0x10000 - n) : 0x100000;

    internal static object Decode(byte[] data, out int used)
    {
        int position = 0;
        var value = Read(data, ref position);
        used = position;
        return value;
    }

    private static object Read(byte[] d, ref int p)
    {
        int initial = d[p++], major = initial >> 5, info = initial & 31;
        int bytes = info switch { < 24 => 0, 24 => 1, 25 => 2, 26 => 4, 27 => 8, _ => throw new InvalidDataException("CBOR") };
        ulong n = bytes == 0 ? (ulong)info : 0;
        for (int i = 0; i < bytes; i++) n = n << 8 | d[p++];
        switch (major)
        {
            case 0: return (long)n;
            case 1: return -1 - (long)n;
            case 2: { var b = d.AsSpan(p, (int)n).ToArray(); p += (int)n; return b; }
            case 3: { var s = Encoding.UTF8.GetString(d, p, (int)n); p += (int)n; return s; }
            case 4: { var a = new List<object>(); for (ulong i = 0; i < n; i++) a.Add(Read(d, ref p)); return a; }
            case 5: { var m = new Dictionary<object, object>(); for (ulong i = 0; i < n; i++) { var k = Read(d, ref p); m[k] = Read(d, ref p); } return m; }
            case 7: return info == 21 ? true : info == 20 ? false : (object)n;
            default: throw new InvalidDataException("CBOR");
        }
    }
}
