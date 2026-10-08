using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Kiosk.Client;

/// <summary>
/// PIN check for FIDO2 cards (security keys registered in Entra ID), which have no certificate key for
/// <see cref="CardPin"/>. Windows' own security key dialog asks for the card PIN and a tap; the card checks
/// the PIN itself. A throw-away, non-resident credential is requested with user verification required, so
/// nothing is stored on the card and Kiosk never sees the PIN. Success means "this card's PIN was entered".
/// </summary>
internal static class FidoPin
{
    private const string RelyingParty = "kiosk.local";

    internal static bool Available => WebAuthNGetApiVersionNumber() >= 1;

    /// <summary>Blocking; run off the UI thread. The dialog is owned by <paramref name="owner"/>.</summary>
    internal static CardPin.Result Verify(IntPtr owner)
    {
        var handles = new List<GCHandle>();
        var strings = new List<IntPtr>();
        IntPtr Pin(object value) { var h = GCHandle.Alloc(value, GCHandleType.Pinned); handles.Add(h); return h.AddrOfPinnedObject(); }
        IntPtr attestation = IntPtr.Zero;
        try
        {
            var userId = RandomNumberGenerator.GetBytes(16);
            var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var clientData = Encoding.UTF8.GetBytes("{\"type\":\"webauthn.create\",\"challenge\":\"" + challenge + "\",\"origin\":\"https://" + RelyingParty + "\"}");
            var rp = new RpEntity { Version = 1, Id = RelyingParty, Name = "Kiosk" };
            var user = new UserEntity { Version = 1, IdLength = userId.Length, Id = Pin(userId), Name = "kiosk", DisplayName = "Odblokowanie Kiosku" };
            var publicKey = Marshal.StringToHGlobalUni("public-key");
            strings.Add(publicKey);
            var es256 = new CoseParameter { Version = 1, Type = publicKey, Algorithm = -7 };
            var rs256 = new CoseParameter { Version = 1, Type = publicKey, Algorithm = -257 };
            var algorithms = new[] { es256, rs256 };
            var parameters = new CoseParameters { Count = algorithms.Length, Items = Marshal.UnsafeAddrOfPinnedArrayElement(algorithms, 0) };
            handles.Add(GCHandle.Alloc(algorithms, GCHandleType.Pinned));
            var data = new ClientData { Version = 1, Length = clientData.Length, Json = Pin(clientData), HashAlgorithm = "SHA-256" };
            var options = new MakeCredentialOptions
            {
                Version = 1, TimeoutMilliseconds = 120_000,
                Attachment = 2,            // cross-platform: the card, not Windows Hello on this PC
                RequireResidentKey = false,  // nothing is stored on the card
                UserVerification = 1,      // required: the card PIN must be entered
                Attestation = 1            // none
            };
            int hr = WebAuthNAuthenticatorMakeCredential(owner, ref rp, ref user, ref parameters, ref data, ref options, out attestation);
            if (hr == unchecked((int)0x800704C7) || hr == unchecked((int)0x80090036)) return new(CardPin.Cancelled, "Anulowano wpisywanie PIN-u.");
            if (hr != 0 || attestation == IntPtr.Zero) return new(CardPin.Failed, "Nie potwierdzono PIN-u karty (0x" + hr.ToString("X8") + ").");
            // WEBAUTHN_CREDENTIAL_ATTESTATION: dwVersion, pwszFormatType, cbAuthenticatorData, pbAuthenticatorData, ...
            int length = Marshal.ReadInt32(attestation, 16);
            IntPtr authenticatorData = Marshal.ReadIntPtr(attestation, 24);
            // Authenticator data: 32-byte RP ID hash, then flags; 0x04 = user verified (PIN checked by the card).
            bool verified = length > 32 && (Marshal.ReadByte(authenticatorData, 32) & 0x04) != 0;
            return verified ? new(CardPin.Verified, "PIN karty potwierdzony.") : new(CardPin.Failed, "Karta nie potwierdziła PIN-u.");
        }
        catch (Exception ex) { return new(CardPin.Failed, "Nie udało się sprawdzić PIN-u karty: " + ex.Message); }
        finally
        {
            if (attestation != IntPtr.Zero) WebAuthNFreeCredentialAttestation(attestation);
            foreach (var h in handles) h.Free();
            foreach (var s in strings) Marshal.FreeHGlobal(s);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RpEntity { public int Version; public string Id; public string Name; public string? Icon; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserEntity { public int Version; public int IdLength; public IntPtr Id; public string Name; public string? Icon; public string DisplayName; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CoseParameter { public int Version; public IntPtr Type; public int Algorithm; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CoseParameters { public int Count; public IntPtr Items; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ClientData { public int Version; public int Length; public IntPtr Json; public string HashAlgorithm; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MakeCredentialOptions
    {
        public int Version, TimeoutMilliseconds;
        public int CredentialCount; public IntPtr Credentials;   // WEBAUTHN_CREDENTIALS (exclude list)
        public int ExtensionCount; public IntPtr Extensions;     // WEBAUTHN_EXTENSIONS
        public int Attachment;
        [MarshalAs(UnmanagedType.Bool)] public bool RequireResidentKey;
        public int UserVerification, Attestation, Flags;
    }

    [DllImport("webauthn.dll")] private static extern int WebAuthNGetApiVersionNumber();
    [DllImport("webauthn.dll")]
    private static extern int WebAuthNAuthenticatorMakeCredential(IntPtr hWnd, ref RpEntity rp, ref UserEntity user, ref CoseParameters parameters,
        ref ClientData clientData, ref MakeCredentialOptions options, out IntPtr attestation);
    [DllImport("webauthn.dll")] private static extern void WebAuthNFreeCredentialAttestation(IntPtr attestation);
}
