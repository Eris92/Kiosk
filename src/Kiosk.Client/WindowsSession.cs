using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Kiosk.Client;

/// <summary>
/// The Windows session the Kiosk runs in when it is the user's shell (SessionMode = WindowsShell).
/// Each person signs in to Windows with their card or Windows Hello, so every program runs as them with
/// full single sign-on; the Kiosk disconnects the session (it keeps running) or signs the person out.
/// </summary>
internal static class WindowsSession
{
    internal static CardIdentity CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var upn = UserName(8);          // NameUserPrincipal: k.lechmyc@investa.pl / user@tenant
        var display = UserName(3);      // NameDisplay: Krzysztof Lechmyc (domain accounts)
        var name = display.Length > 0 ? display : upn.Length > 0 ? upn : identity.Name[(identity.Name.IndexOf('\\') + 1)..];
        // The person's own certificates (a signed-in card propagates its certificates here) for websites.
        string[] thumbprints;
        using (var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser))
        {
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            thumbprints = store.Certificates.Where(c => c.HasPrivateKey).Select(c => c.Thumbprint).ToArray();
        }
        return new CardIdentity("win:" + identity.User?.Value, name, upn) { Thumbprints = thumbprints };
    }

    /// <summary>Like "Switch user": the session and its programs keep running; Windows shows its sign-in screen.</summary>
    internal static void Disconnect()
    {
        if (!WTSDisconnectSession(IntPtr.Zero, CurrentSession, false)) throw new System.ComponentModel.Win32Exception();
    }

    internal static void SignOut()
    {
        if (!ExitWindowsEx(0, 0)) throw new System.ComponentModel.Win32Exception(); // EWX_LOGOFF
    }

    /// <summary>True while nobody is looking at this session (after Disconnect, until the person signs in again).</summary>
    internal static bool IsDisconnected()
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, CurrentSession, 8, out var buffer, out _)) return false; // WTSConnectState
        try { return Marshal.ReadInt32(buffer) == 4; } // WTSDisconnected
        finally { WTSFreeMemory(buffer); }
    }

    private static string UserName(int format)
    {
        uint size = 512;
        var name = new StringBuilder((int)size);
        return GetUserNameEx(format, name, ref size) ? name.ToString() : "";
    }

    private const int CurrentSession = -1; // WTS_CURRENT_SESSION
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSDisconnectSession(IntPtr server, int session, bool wait);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("secur32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
}
