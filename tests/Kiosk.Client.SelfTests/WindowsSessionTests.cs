namespace Kiosk.Client;

internal static class WindowsSessionTests
{
    internal static void Run()
    {
        var locked = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (WindowsSession.RetentionExpired(null, locked.AddHours(1), 600) ||
            WindowsSession.RetentionExpired(locked, locked.AddHours(1), 0) ||
            WindowsSession.RetentionExpired(locked, locked.AddSeconds(599), 600) ||
            !WindowsSession.RetentionExpired(locked, locked.AddSeconds(600), 600))
            throw new InvalidOperationException("Windows session retention boundary is incorrect.");
        if (!WindowsSession.CanResume(WindowsSession.Availability.Active, false, false) ||
            !WindowsSession.CanResume(WindowsSession.Availability.Unknown, true, false) ||
            WindowsSession.CanResume(WindowsSession.Availability.Unknown, false, false) ||
            WindowsSession.CanResume(WindowsSession.Availability.Locked, true, false) ||
            WindowsSession.CanResume(WindowsSession.Availability.Disconnected, true, false) ||
            WindowsSession.CanResume(WindowsSession.Availability.Active, false, true) ||
            WindowsSession.CanResume(WindowsSession.Availability.Unknown, true, true))
            throw new InvalidOperationException("Windows session resumed without an active state or unlock notification.");

        var signOut = new SignOutRetry();
        var calls = 0;
        if (!signOut.Request("session_expired", 1000, () => calls++, out var failure) || failure != null ||
            signOut.Request("windows_lock_unconfirmed", 10999, () => calls++, out failure) || calls != 1 ||
            signOut.Reason != "session_expired")
            throw new InvalidOperationException("Windows logoff was retried during the throttle window or changed its reason.");
        if (!signOut.Request("windows_lock_unconfirmed", 11000, () => throw new InvalidOperationException("simulated"), out failure) ||
            failure?.Message != "simulated" ||
            signOut.Request("windows_lock_unconfirmed", 11001, () => calls++, out failure) || calls != 1 ||
            !signOut.Request("windows_lock_unconfirmed", 21000, () => calls++, out failure) || calls != 2 || failure != null)
            throw new InvalidOperationException("Windows logoff failure did not use bounded retry.");
    }
}
