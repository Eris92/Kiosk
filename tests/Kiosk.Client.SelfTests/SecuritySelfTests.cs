using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kiosk.Client;

/// <summary>Hardware-independent regression checks for untrusted card/service data.</summary>
internal static class SecuritySelfTests
{
    internal static void Run()
    {
        var encoded = Cbor.Encode(Cbor.Map((1L, new byte[] { 1, 2, 3 }), (2L, "Zażółć"), (3L, true)));
        if (Cbor.Decode(encoded, out var consumed) is not Dictionary<object, object> decoded ||
            consumed != encoded.Length || !((byte[])decoded[1L]).SequenceEqual(new byte[] { 1, 2, 3 }) ||
            (string)decoded[2L] != "Zażółć" || !(bool)decoded[3L])
            throw new InvalidOperationException("CTAP CBOR round-trip failed.");

        byte[][] malformed = [[], [0x1A, 0], [0x5A, 0x7F, 0xFF, 0xFF, 0xFF], [0xA2, 1, 1, 1, 2],
            [0x61, 0xFF], [0x1B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            Enumerable.Repeat((byte)0x81, 40).Append((byte)0).ToArray()];
        foreach (var bytes in malformed)
            Reject<InvalidDataException>(() => Cbor.Decode(bytes, out _));

        CardService.Validate(new() { Op = "info", Reader = "Self-test reader" });
        CardService.Validate(new() { Op = "open", Reader = "Self-test reader", Pin = "test-pin",
            Salt = Convert.ToBase64String(new byte[32]), CredentialId = Convert.ToBase64String(new byte[16]) });
        Reject<ArgumentException>(() => CardService.Validate(new() { Op = "delete", Reader = "Reader" }));
        Reject<ArgumentException>(() => CardService.Validate(new() { Op = "verify", Reader = "Reader", Pin = "" }));
        Reject<ArgumentException>(() => CardService.Validate(new() { Op = "enroll", Reader = "Reader", Pin = "test-pin",
            Salt = Convert.ToBase64String(new byte[31]) }));

        using (var message = new StringReader("{\"Op\":\"info\"}\n"))
            if (CardService.ReadMessageAsync(message, CancellationToken.None).GetAwaiter().GetResult() != "{\"Op\":\"info\"}")
                throw new InvalidOperationException("Service frame decoding failed.");
        using (var incomplete = new StringReader("{}"))
            Reject<IOException>(() => CardService.ReadMessageAsync(incomplete, CancellationToken.None).GetAwaiter().GetResult());
        using (var oversized = new StringReader(new string('a', 16385) + "\n"))
            Reject<IOException>(() => CardService.ReadMessageAsync(oversized, CancellationToken.None).GetAwaiter().GetResult());

        // Changing the account metadata must fail GCM authentication for newly enrolled vault entries.
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = [1, 2, 3, 4], cipher = new byte[4], tag = new byte[16], output = new byte[4];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag, CardVault.AccountData("user", "domain"));
            aes.Decrypt(nonce, cipher, tag, output, CardVault.AccountData("user", "domain"));
            if (!output.SequenceEqual(plain)) throw new InvalidOperationException("Vault encryption round-trip failed.");
            Reject<AuthenticationTagMismatchException>(() => aes.Decrypt(nonce, cipher, tag, output, CardVault.AccountData("admin", "domain")));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(output); }

        // A damaged or old account file must return to enrollment instead of
        // trapping the person behind an exception on every card insertion.
        var cardId = "self-test-corrupt:" + Guid.NewGuid().ToString("N");
        var cards = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Cards");
        var entryPath = Path.Combine(cards, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cardId))) + ".json");
        Directory.CreateDirectory(cards);
        try
        {
            File.WriteAllText(entryPath, "{\"Version\":1,\"Direct\":true,\"User\":\"test\",\"Salt\":\"AA==\"}");
            if (CardVault.Open(cardId, "unused reader", "unused PIN") is not { Logon: null, Code: CardPin.NoKey })
                throw new InvalidOperationException("Corrupt card account did not return to enrollment.");
            File.WriteAllText(entryPath, "{\"Version\":0,\"Direct\":true,\"User\":\"test\"}");
            if (CardVault.Open(cardId, "unused reader", "unused PIN") is not { Logon: null, Code: CardPin.NoKey })
                throw new InvalidOperationException("Legacy card account bypassed account-metadata authentication.");
        }
        finally { File.Delete(entryPath); }

        // A copied public certificate cannot authorize a different private key.
        var challenge = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
        using (var rsa = RSA.Create(2048))
        using (var impostor = RSA.Create(2048))
        using (var certificate = PublicCertificate(new CertificateRequest("CN=Kiosk test RSA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)))
        {
            if (!CardPin.VerifySignature(certificate, challenge, rsa.SignHash(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) ||
                CardPin.VerifySignature(certificate, challenge, impostor.SignHash(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)))
                throw new InvalidOperationException("RSA card certificate proof was not bound to its private key.");
        }
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var certificate = PublicCertificate(new CertificateRequest("CN=Kiosk test EC", ecdsa, HashAlgorithmName.SHA256)))
        {
            if (!CardPin.VerifySignature(certificate, challenge, ecdsa.SignHash(challenge)) ||
                CardPin.VerifySignature(certificate, challenge, impostor.SignHash(challenge)))
                throw new InvalidOperationException("EC card certificate proof was not bound to its private key.");
        }
    }

    private static X509Certificate2 PublicCertificate(CertificateRequest request)
    {
        using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        return X509CertificateLoader.LoadCertificate(signed.Export(X509ContentType.Cert));
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Security self-test expected " + typeof(T).Name + ".");
    }
}
