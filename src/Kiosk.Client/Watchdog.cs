using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kiosk.Client;

/// <summary>
/// Watchdog, part of the Kiosk Card service (LocalSystem; Windows restarts the service itself, Install-KioskCardService.ps1).
/// Each Kiosk reports to the service every 15 s from its window's thread. When a Kiosk ends without saying goodbye
/// (crash, killed) while its person is still signed in, or stops reporting for 90 s (hung), the service starts it
/// again in the same session, as the same user with the same arguments: at most 3 times in 15 minutes per session.
/// It also starts the Windows smart card service (SCardSvr) when card readers cannot be reached because it stopped.
/// </summary>
internal static class Watchdog
{
    private static readonly TimeSpan BeatEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HungAfter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(15);
    private const int MaxRestarts = 3;

    // ------------------------------------------------------------------ Kiosk

    /// <summary>Kiosk: reports from the window's own thread (a hung window stops reporting), says goodbye when closed on purpose.</summary>
    internal static void Watch(Form form)
    {
        var arguments = Join(Environment.GetCommandLineArgs().Skip(1));
        var skip = 0;
        var registered = false;
        var timer = new System.Windows.Forms.Timer { Interval = (int)BeatEvery.TotalMilliseconds };
        void Beat()
        {
            if (skip > 0) { skip--; return; }
            // Off the window's thread: without the service the pipe waits seconds before giving up.
            _ = Task.Run(() =>
            {
                if (CardService.Call(new CardService.Request { Op = "watch", Source = arguments }).Code == 0) registered = true;
                // No service: ask again in 5 minutes. Once watched, never pause: missed reports would read as a hung Kiosk.
                else if (!registered) skip = 20;
            });
        }
        timer.Tick += (_, _) => Beat();
        form.FormClosed += (_, _) =>
        {
            timer.Dispose();
            if (registered) CardService.Call(new CardService.Request { Op = "unwatch" });
        };
        Beat();
        timer.Start();
    }

    // ------------------------------------------------------------------ service

    private sealed class Watched
    {
        public required int ProcessId;
        public required Process Process;
        public required SafeAccessTokenHandle Token;   // The Kiosk's own token: a restart runs as exactly that user.
        public required string Program;
        public required string Arguments;
        public DateTime LastBeat = DateTime.UtcNow;
    }

    private static readonly object gate = new();
    private static readonly Dictionary<int, Watched> watched = [];             // by Windows session
    private static readonly Dictionary<int, List<DateTime>> restarts = [];     // by Windows session

    /// <summary>Service: "watch" / "unwatch" from the pipe's client process, which Windows names (not the request).</summary>
    internal static CardService.Response Request(string op, int clientPid, string? program, string arguments)
    {
        if (clientPid <= 0 || program == null || !ProcessIdToSessionId((uint)clientPid, out var session))
            return new CardService.Response { Code = -2, Message = "Nie rozpoznano programu Kiosk." };
        lock (gate)
        {
            watched.TryGetValue(session, out var current);
            if (op == "unwatch")
            {
                if (current?.ProcessId == clientPid) { Forget(session); Audit.Write("watchdog_unwatch", "session " + session); }
                return new CardService.Response();
            }
            if (current?.ProcessId == clientPid) { current.LastBeat = DateTime.UtcNow; return new CardService.Response(); }
            if (!Path.GetFileName(program).Equals("Kiosk.Client.exe", StringComparison.OrdinalIgnoreCase))
                return new CardService.Response { Code = -2, Message = "Watchdog pilnuje tylko programu Kiosk." };
            var process = Process.GetProcessById(clientPid);
            var token = PrimaryToken(process);
            if (current != null) Forget(session);
            watched[session] = new Watched { ProcessId = clientPid, Process = process, Token = token, Program = program, Arguments = arguments };
            Audit.Write("watchdog_watch", "session " + session + ", " + program);
            return new CardService.Response();
        }
    }

    /// <summary>Service: checks every 10 s until the service stops.</summary>
    internal static void Start(ManualResetEventSlim stopping)
    {
        new Thread(() =>
        {
            var cleaned = DateTime.MinValue;
            while (!stopping.Wait(TimeSpan.FromSeconds(10)))
            {
                // Logi: the PC's log and statistics folders, every hour (Retention).
                if (DateTime.UtcNow - cleaned > TimeSpan.FromHours(1))
                {
                    cleaned = DateTime.UtcNow;
                    try { Retention.RunForMachine(); } catch (Exception ex) { Audit.Write("log_cleanup_failed", ex.Message); }
                }
                try { CheckSmartCardService(); } catch (Exception ex) { Audit.Write("watchdog_error", ex.Message); }
                try { CheckKiosks(); } catch (Exception ex) { Audit.Write("watchdog_error", ex.Message); }
            }
        }) { IsBackground = true, Name = "Watchdog" }.Start();
    }

    private static void CheckKiosks()
    {
        List<(int Session, Watched Kiosk, string Reason)> restart = [];
        lock (gate)
        {
            foreach (var (session, kiosk) in watched.ToList())
            {
                if (kiosk.Process.HasExited) restart.Add((session, kiosk, "zakończony (kod " + kiosk.Process.ExitCode + ")"));
                else if (DateTime.UtcNow - kiosk.LastBeat > HungAfter) restart.Add((session, kiosk, "nie odpowiada od " + (int)(DateTime.UtcNow - kiosk.LastBeat).TotalSeconds + " s"));
            }
            foreach (var (session, _, _) in restart) watched.Remove(session);
        }
        foreach (var (session, kiosk, reason) in restart)
        {
            try { Restart(session, kiosk, reason); }
            catch (Exception ex) { Audit.Write("watchdog_restart_failed", "session " + session + ": " + ex.Message); }
            finally { kiosk.Token.Dispose(); kiosk.Process.Dispose(); }
        }
    }

    private static void Restart(int session, Watched kiosk, string reason)
    {
        // Signed out (or signing out): nobody to give the Kiosk back to.
        if (!WTSQueryUserToken(session, out var user)) { Audit.Write("watchdog_session_ended", "session " + session + ", Kiosk " + reason); return; }
        user.Dispose();
        var history = restarts.TryGetValue(session, out var list) ? list : restarts[session] = [];
        history.RemoveAll(t => DateTime.UtcNow - t > RestartWindow);
        if (history.Count >= MaxRestarts)
        {
            Audit.Write("watchdog_gave_up", "session " + session + ": Kiosk " + reason + "; " + MaxRestarts + " restarty w " + RestartWindow.TotalMinutes + " min");
            return;
        }
        // An administrator's Kiosk starts elevated again: only from Program Files, which users cannot change.
        if (Elevated(kiosk.Token) && !SharedAccounts.IsInstalledKiosk(kiosk.Program))
        {
            Audit.Write("watchdog_not_restarted", "session " + session + ": Kiosk administratora spoza Program Files (" + kiosk.Program + ")");
            return;
        }
        if (!kiosk.Process.HasExited)
        {
            kiosk.Process.Kill(entireProcessTree: false);
            kiosk.Process.WaitForExit(10000);
        }
        // Windows may have started a shell Kiosk again itself (Shell Launcher, Winlogon): a second one would only fail.
        Thread.Sleep(5000);
        if (Process.GetProcessesByName("Kiosk.Client").Any(p => p.SessionId == session))
        {
            Audit.Write("watchdog_already_running", "session " + session + ": Kiosk " + reason);
            return;
        }
        history.Add(DateTime.UtcNow);
        if (!CreateEnvironmentBlock(out var environment, kiosk.Token, false)) throw new Win32Exception();
        try
        {
            var startup = new AppNative.StartupInfo { Size = Marshal.SizeOf<AppNative.StartupInfo>(), Desktop = @"winsta0\default" };
            var line = "\"" + kiosk.Program + "\"" + (kiosk.Arguments.Length > 0 ? " " + kiosk.Arguments : "");
            if (!CreateProcessAsUser(kiosk.Token, kiosk.Program, new StringBuilder(line), IntPtr.Zero, IntPtr.Zero, false, 0x400, environment, // CREATE_UNICODE_ENVIRONMENT
                    Path.GetDirectoryName(kiosk.Program), ref startup, out var process))
                throw new Win32Exception();
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
            Audit.Write("watchdog_restart_kiosk", "session " + session + ": " + reason);
        }
        finally { DestroyEnvironmentBlock(environment); }
    }

    private static void Forget(int session)
    {
        if (!watched.Remove(session, out var old)) return;
        old.Token.Dispose();
        old.Process.Dispose();
    }

    private static DateTime lastScardStart = DateTime.MinValue;

    /// <summary>Card readers cannot be reached because the smart card service stopped: start it (at most every 5 minutes).</summary>
    private static void CheckSmartCardService()
    {
        var result = SCardEstablishContext(2, IntPtr.Zero, IntPtr.Zero, out var context); // SCARD_SCOPE_SYSTEM
        if (result == 0) { SCardReleaseContext(context); return; }
        // SCARD_E_NO_SERVICE, SCARD_E_SERVICE_STOPPED; no reader at all (SCARD_E_NO_READERS_AVAILABLE) is not a fault.
        if (result is not (unchecked((int)0x8010001D) or unchecked((int)0x8010001E))) return;
        if (DateTime.UtcNow - lastScardStart < TimeSpan.FromMinutes(5)) return;
        lastScardStart = DateTime.UtcNow;
        using var sc = Process.Start(new ProcessStartInfo("sc.exe", "start SCardSvr") { UseShellExecute = false, CreateNoWindow = true })!;
        sc.WaitForExit(30000);
        Audit.Write("watchdog_restart_scard", "0x" + result.ToString("X8") + " -> sc " + sc.ExitCode);
    }

    private static SafeAccessTokenHandle PrimaryToken(Process process)
    {
        if (!OpenProcessToken(process.Handle, 0x02000000, out var own)) throw new Win32Exception(); // MAXIMUM_ALLOWED
        using (own)
        {
            if (!DuplicateTokenEx(own, 0x02000000, IntPtr.Zero, 2, 1, out var primary)) throw new Win32Exception(); // impersonation level, primary
            return primary;
        }
    }

    private static bool Elevated(SafeAccessTokenHandle token) =>
        GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated != 0; // TokenElevation

    private static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(a => "\"" + a.TrimEnd('\\') + "\""));

    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint process, out int session);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(int session, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out int value, int length, out int returned);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inherit, int flags, IntPtr environment, string? directory,
        ref AppNative.StartupInfo startup, out ProcessInformation process);
    [DllImport("winscard.dll")] private static extern int SCardEstablishContext(int scope, IntPtr reserved1, IntPtr reserved2, out IntPtr context);
    [DllImport("winscard.dll")] private static extern int SCardReleaseContext(IntPtr context);
}
