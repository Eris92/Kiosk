using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Kiosk.Client;

/// <summary>
/// The Windows session the Kiosk runs in when it is the user's shell (SessionMode = WindowsShell).
/// Each person signs in to Windows with their own account and the Kiosk runs programs under that
/// Windows token. Available SSO depends on the account, target and device policies. A local console
/// is locked; a remote Windows session is disconnected. Either session can later be signed out.
/// </summary>
internal static class WindowsSession
{
    internal enum Availability { Unknown, Active, Locked, Disconnected }
    internal const int SessionChangeMessage = 0x02B1; // WM_WTSSESSION_CHANGE
    internal const int ConsoleDisconnect = 0x2, RemoteDisconnect = 0x4, SessionLock = 0x7, SessionUnlock = 0x8;
    internal const int ConsoleConnect = 0x1, RemoteConnect = 0x3;

    internal static CardIdentity CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var upn = UserName(8);          // NameUserPrincipal: user@tenant
        var display = UserName(3);      // NameDisplay for accounts that provide it
        var name = display.Length > 0 ? display : upn.Length > 0 ? upn : identity.Name[(identity.Name.IndexOf('\\') + 1)..];
        // A Windows account's certificate store can include old or unrelated
        // smart cards. Resolve the inserted card only when a website asks for
        // client authentication, not during every Kiosk startup.
        return new CardIdentity("win:" + identity.User?.Value, name, upn);
    }

    /// <summary>Keep this user's programs running while Windows takes over authentication.</summary>
    internal static void LockOrDisconnect()
    {
        // A physical console cannot be disconnected. LockWorkStation is the
        // supported path there; an RDP session can be disconnected instead.
        if (IsConsole())
        {
            if (!LockWorkStation()) throw new System.ComponentModel.Win32Exception();
        }
        else if (!WTSDisconnectSession(IntPtr.Zero, CurrentSession, false))
            throw new System.ComponentModel.Win32Exception();
    }

    internal static void SignOut()
    {
        if (!ExitWindowsEx(0, 0)) throw new System.ComponentModel.Win32Exception(); // EWX_LOGOFF
    }

    internal static bool RegisterNotifications(IntPtr window) => WTSRegisterSessionNotification(window, 0);
    internal static void UnregisterNotifications(IntPtr window) => WTSUnRegisterSessionNotification(window);

    internal static bool RetentionExpired(DateTime? unavailableSince, DateTime nowUtc, int seconds) =>
        unavailableSince.HasValue && seconds > 0 && nowUtc - unavailableSince.Value >= TimeSpan.FromSeconds(seconds);

    // Some RDS hosts only expose connection state through WTSQuerySessionInformation.
    // Their own unlock notification is sufficient when the queried state is Unknown.
    // After an explicit lock event, a temporarily stale Active query is not an unlock.
    internal static bool CanResume(Availability state, bool unlockNotified, bool lockNotified) =>
        !lockNotified && (state == Availability.Active || state == Availability.Unknown && unlockNotified);

    /// <summary>Windows' own state, including a locked local console.</summary>
    internal static Availability State()
    {
        // WTSSessionInfoEx contains a DWORD Level followed by an aligned
        // WTSINFOEX_LEVEL1. x64 places the latter at offset 8; its first
        // three fields are SessionId, SessionState and SessionFlags.
        if (WTSQuerySessionInformation(IntPtr.Zero, CurrentSession, 25, out var buffer, out var bytes))
        {
            try
            {
                if (buffer != IntPtr.Zero && bytes >= 20 && Marshal.ReadInt32(buffer) == 1)
                {
                    var connection = Marshal.ReadInt32(buffer, 12);
                    var flags = Marshal.ReadInt32(buffer, 16);
                    if (connection == 4) return Availability.Disconnected; // WTSDisconnected
                    if (connection == 0) return flags switch
                    {
                        0 => Availability.Locked,
                        1 => Availability.Active,
                        _ => Availability.Unknown
                    };
                }
            }
            finally { WTSFreeMemory(buffer); }
        }
        // Some RDS environments do not return WTSSessionInfoEx. They still
        // expose the connection state; lock/unlock notifications fill the gap.
        if (!WTSQuerySessionInformation(IntPtr.Zero, CurrentSession, 8, out buffer, out bytes)) return Availability.Unknown;
        try { return buffer != IntPtr.Zero && bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == 4
            ? Availability.Disconnected : Availability.Unknown; }
        finally { WTSFreeMemory(buffer); }
    }

    private static bool IsConsole()
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, CurrentSession, 16, out var buffer, out var bytes))
            throw new System.ComponentModel.Win32Exception(); // WTSClientProtocolType
        try
        {
            if (buffer == IntPtr.Zero || bytes < sizeof(short)) throw new InvalidDataException("Nie można ustalić typu sesji Windows.");
            return Marshal.ReadInt16(buffer) == 0;
        }
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
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("secur32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
}

/// <summary>Throttles asynchronous Windows logoff requests while retaining the first reason.</summary>
internal sealed class SignOutRetry
{
    private const long RetryMilliseconds = 10_000;
    private long nextAttemptAt;
    private bool attempted;

    internal string? Reason { get; private set; }

    /// <returns>True when a logoff call was made; failure is populated if that call threw.</returns>
    internal bool Request(string reason, long nowMilliseconds, Action signOut, out Exception? failure)
    {
        Reason ??= reason;
        failure = null;
        if (attempted && nowMilliseconds < nextAttemptAt) return false;
        attempted = true;
        nextAttemptAt = nowMilliseconds + RetryMilliseconds;
        try { signOut(); }
        catch (Exception ex) { failure = ex; }
        return true;
    }
}
