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
    }
}
