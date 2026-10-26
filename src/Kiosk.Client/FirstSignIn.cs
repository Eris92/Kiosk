using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// Szybka pomoc → Pierwsze logowanie: a person signs in to Windows once, so Windows creates their profile and their
/// single sign-on (Entra ID token, Windows Hello); "runas /profile" only loads a profile folder and gives no SSO.
/// The Kiosk asks the Kiosk Card service (LocalSystem) to remember its Windows session, then disconnects it, so the
/// Windows sign-in screen appears. When someone signs in, the service waits until their desktop has started,
/// brings the Kiosk session back to the screen (allowed for LocalSystem without a password) and signs that person out.
/// </summary>
internal static class FirstSignIn
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);   // How long the sign-in screen may take.
    private static readonly TimeSpan Setup = TimeSpan.FromMinutes(5);     // First sign-in: "Hi, we're getting things ready".
    private static readonly object gate = new();
    private static int kioskSession = -1;
    private static long armedUntilMilliseconds;

    /// <summary>Service: the Kiosk in <paramref name="session"/> is about to show the sign-in screen.</summary>
    internal static CardService.Response Arm(int session)
    {
        if (session <= 0 || session != WTSGetActiveConsoleSessionId())
            return new CardService.Response { Code = -2, Message = "Pierwsze logowanie działa tylko na ekranie tego komputera." };
        lock (gate) { kioskSession = session; armedUntilMilliseconds = Environment.TickCount64 + (long)Window.TotalMilliseconds; }
        Audit.Write("first_signin_armed", session.ToString());
        return new CardService.Response { Message = "OK" };
    }

    /// <summary>Service: Windows reported a new sign-in (WTS_SESSION_LOGON).</summary>
    internal static void SignedIn(int session)
    {
        int back;
        lock (gate)
        {
            // Only the sign-in on this PC's screen: an administrator's remote session at that moment is left alone.
            if (kioskSession < 0 || session == kioskSession || Environment.TickCount64 > armedUntilMilliseconds || session != WTSGetActiveConsoleSessionId()) return;
            back = kioskSession;
            kioskSession = -1;
        }
        new Thread(() => Finish(session, back)) { IsBackground = true }.Start();
    }

    private static void Finish(int session, int back)
    {
        try
        {
            // The profile is ready once the person's desktop (Explorer, or the Kiosk as their shell) runs.
            var until = Environment.TickCount64 + (long)Setup.TotalMilliseconds;
            while (Environment.TickCount64 < until && !DesktopStarted(session)) Thread.Sleep(2000);
            Thread.Sleep(TimeSpan.FromSeconds(15)); // Let sign-in finish its work (tokens, OneDrive, Office) before signing out.
            Audit.Write("first_signin_done", session.ToString());
            if (!WTSConnectSession(back, WTSGetActiveConsoleSessionId(), "", true))
                Audit.Write("first_signin_return_failed", "Win32 " + Marshal.GetLastWin32Error());
            if (!WTSLogoffSession(IntPtr.Zero, session, false))
                Audit.Write("first_signin_logoff_failed", "Win32 " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { Audit.Write("first_signin_failed", ex.Message); }
    }

    private static bool DesktopStarted(int session)
    {
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == session && process.ProcessName is "explorer" or "Kiosk.Client") return true;
                }
                catch (Exception) { /* The process exited or cannot be inspected. */ }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [DllImport("kernel32.dll")] private static extern int WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSConnectSession(int logonId, int targetLogonId, string password, bool wait);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSLogoffSession(IntPtr server, int session, bool wait);
}
