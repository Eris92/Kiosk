using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// "Kiosk Card": a small Windows service (LocalSystem) that talks FIDO2 to the card for Kiosk. Windows lets only
/// administrators and services send FIDO2 commands to a card, so the Kiosk (a normal user) asks the service over
/// a local pipe. The service does only this (plus changing the PIN or resetting the card for its holder, Szybka pomoc):
/// check the card PIN, create the Kiosk credential and return its
/// hmac-secret, always for the relying party "kiosk.local" — never signatures or sign-ins for other sites.
/// It also installs the latest official Kiosk release on request ("update"), so updating needs no administrator,
/// and is the Kiosk's watchdog (Watchdog.cs): a crashed or hung Kiosk starts again, a stopped smart card service too.
/// </summary>
internal static class CardService
{
    internal const string ServiceName = "KioskCard";
    internal const string Argument = "--card-service";
    private const string PipeName = "KioskCard";
    private const string RelyingParty = CtapCard.RelyingParty;

    internal sealed class Request
    {
        public string Op { get; set; } = "";          // watch | unwatch | verify | enroll | open | info | haspin | setpin | changepin | reset | update | firstsignin | syncconfig | testconfig | syncnews | stat | syncstats | testnetwork | sharedaccount | runas | newsaccount | fidosignin
        public string NewPin { get; set; } = "";
        public string Reader { get; set; } = "";
        public string Pin { get; set; } = "";
        public string? CredentialId { get; set; }
        public string? Salt { get; set; }
        public string Source { get; set; } = "";      // testconfig: the folder or share to check
        public string Embed { get; set; } = "";       // fidosignin: the Kiosk window the sign-in screen is drawn in (FidoSignIn.ValidEmbed)
    }

    internal sealed class Response
    {
        public int Code { get; set; }                 // 0 = OK, CTAP error code, or -1 / -2
        public string Message { get; set; } = "";
        public string? CredentialId { get; set; }
        public string? Secret { get; set; }
        public long Process { get; set; }             // runas: the suspended program's handles, valid in the Kiosk's process
        public long Thread { get; set; }
    }

    // ------------------------------------------------------------------ client (Kiosk)

    /// <summary>Sends one request to the service; when this process is itself allowed to talk FIDO2 (administrator), runs it directly.</summary>
    internal static Response Call(Request request)
    {
        try
        {
            // Impersonation (Windows' default, said out loud): runas starts the program as this Kiosk's account.
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Impersonation);
            pipe.Connect(3000);
            // Only the real service answers (it runs in session 0, where a signed-in user cannot start anything):
            // a program that took the pipe's name first never sees a PIN.
            // Windows names the server's session itself (a standard user may not open a SYSTEM process to ask it).
            if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var serverSession) || serverSession != 0)
                return new Response { Code = -1, Message = "Usługa „Kiosk Card” nie odpowiada (nieznany serwer potoku)." };
            var line = JsonSerializer.SerializeToUtf8Bytes(request);
            pipe.Write(line);
            pipe.WriteByte((byte)'\n');
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            return JsonSerializer.Deserialize<Response>(reader.ReadLine() ?? "{}") ?? new Response { Code = -1, Message = "Pusta odpowiedź usługi." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The service restarting (an update) or a broken answer: an error the caller shows, not a crash.
            return new Response { Code = -1, Message = "Usługa „Kiosk Card” nie odpowiedziała: " + ex.Message };
        }
        catch (TimeoutException)
        {
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return Handle(request);
            return new Response { Code = -1, Message = "Usługa „Kiosk Card” nie działa. Zainstaluj ją: scripts\\Install-KioskCardService.ps1." };
        }
        finally { request.Pin = ""; request.NewPin = ""; }
    }

    // ------------------------------------------------------------------ the work

    private static Response Handle(Request request)
    {
        string step = "połączenie";
        try
        {
            if (request.Op is "watch" or "unwatch") return new Response { Code = -1, Message = "Watchdog działa w usłudze Kiosk Card." }; // Never the card below.
            if (request.Op == "update") return Updater.InstallLatest(request.Source);   // No card, no path: the latest official release.
            if (request.Op == "dotnet") return Updater.InstallRuntime();  // Only the .NET 10 Desktop Runtime, from Microsoft.
            if (request.Op == "fidosignin") return FidoSignIn.LaunchHere(request.Embed); // Kiosk itself runs as administrator (no service).
            if (request.Op == "sharedaccount") return SharedAccounts.ForService(request.Source, Environment.ProcessPath); // Administrator, no service.
            if (request.Op == "runas") return SharedAccountPrograms.Start(request.Source, Environment.ProcessPath, null, Environment.ProcessId, -1);
            if (request.Op == "newsaccount") return NewsAccount.ForService(Environment.ProcessPath);
            if (request.Op == "syncconfig") return CentralConfig.SyncForMachine(); // No path: the server named in the PC's own configuration.
            if (request.Op == "syncnews") return CentralConfig.SyncNewsForMachine();   // Only the messages folder an administrator configured.
            if (request.Op == "stat") return Stats.Append(request.Source);              // One statistics event (Konfiguracja → Statystyki).
            if (request.Op == "syncstats") return CentralConfig.SyncStatsForMachine();  // This PC's statistics to the server's Stats folder.
            if (request.Op == "testnetwork") return CentralConfig.TestNetworkForMachine(); // Konto Kiosku → Sprawdź: only the saved folders.
            if (request.Op == "reset")
            {
                // The card's own reset: the holder starts over (no PIN, no keys). Only right after power-up, so power it again first.
                step = "reset karty";
                CtapCard.PowerCycle(request.Reader);
                using var fresh = new CtapCard(request.Reader);
                fresh.Reset();
                return new Response { Message = "Karta została zresetowana." };
            }
            using var card = new CtapCard(request.Reader);
            if (request.Op == "info") return new Response { Message = card.Info() }; // Diagnostics, no PIN.
            if (request.Op == "haspin") return new Response { Code = card.HasPin() ? 0 : 0x35 };
            if (request.Op == "setpin")
            {
                // Only a card that has no PIN yet: changing an existing PIN is not something Kiosk does.
                if (card.HasPin()) return new Response { Code = -2, Message = "Karta ma już PIN." };
                step = "ustawianie PIN-u";
                card.SetPin(request.Pin);
                return new Response { Message = "PIN karty ustawiony." };
            }
            if (request.Op == "changepin")
            {
                step = "zmiana PIN-u";
                card.ChangePin(request.Pin, request.NewPin);
                return new Response { Message = "PIN karty zmieniony." };
            }
            step = "PIN";
            card.UsePin(request.Pin);
            switch (request.Op)
            {
                case "verify":
                    return new Response { Message = "PIN karty potwierdzony." };
                case "enroll":
                {
                    step = "tworzenie klucza";
                    var id = card.MakeCredential(RelyingParty);
                    // CTAP 2.1 cards drop the token's permissions after makeCredential: get a fresh one for the assertion.
                    step = "PIN (2)";
                    card.UsePin(request.Pin);
                    step = "odczyt klucza";
                    var secret = card.HmacSecret(RelyingParty, id, Convert.FromBase64String(request.Salt!));
                    return new Response { CredentialId = Convert.ToBase64String(id), Secret = Convert.ToBase64String(secret) };
                }
                case "open":
                {
                    var secret = card.HmacSecret(RelyingParty, Convert.FromBase64String(request.CredentialId!), Convert.FromBase64String(request.Salt!));
                    return new Response { Secret = Convert.ToBase64String(secret) };
                }
                default:
                    return new Response { Code = -2, Message = "Nieznane polecenie." };
            }
        }
        catch (CtapException ex) { return new Response { Code = ex.Code, Message = ex.Message + " [" + step + "]" }; }
        catch (Exception ex) { return new Response { Code = -1, Message = ex.Message }; }
        finally { request.Pin = ""; request.NewPin = ""; }
    }

    // ------------------------------------------------------------------ service

    private static IntPtr statusHandle;
    private static readonly ManualResetEventSlim stopping = new();
    private static ServiceMain? main;            // kept alive for the native callback
    private static HandlerEx? handler;

    /// <summary>Entry point when started by the Service Control Manager.</summary>
    internal static int Run()
    {
        main = ServiceMainProc;
        var table = new[] { new ServiceTableEntry { Name = ServiceName, Proc = main }, default };
        return StartServiceCtrlDispatcher(table) ? 0 : Marshal.GetLastWin32Error();
    }

    private static void ServiceMainProc(int argc, IntPtr argv)
    {
        handler = (control, eventType, eventData, _) =>
        {
            if (control is 1 or 5) { SetStatus(3); stopping.Set(); } // STOP, SHUTDOWN -> STOP_PENDING
            // SESSIONCHANGE / WTS_SESSION_LOGON: Szybka pomoc → Pierwsze logowanie waits for it.
            if (control == 0x0E && eventType == 5 && eventData != IntPtr.Zero) FirstSignIn.SignedIn(Marshal.ReadInt32(eventData, 4));
            return 0;
        };
        statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, handler, IntPtr.Zero);
        SetStatus(4); // RUNNING
        var listener = new Thread(Listen) { IsBackground = true };
        listener.Start();
        Watchdog.Start(stopping);
        stopping.Wait();
        SetStatus(1); // STOPPED
    }

    private static void Listen()
    {
        // SYSTEM and administrators: full; signed-in (interactive) users: may connect and send requests.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!stopping.IsSet)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
                pipe.WaitForConnection();
                // Each client on its own thread: a slow request (a server share, an update) or a client that sends
                // nothing never holds up the PIN, the sign-in or the watchdog's reports. The next instance waits at once.
                var client = pipe;
                pipe = null;
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
            catch (Exception) { pipe?.Dispose(); Thread.Sleep(200); } // One bad client must not stop the service.
        }
    }

    /// <summary>One request of a kind at a time: the card (two PIN checks at once would confuse it), the shared files, an update.</summary>
    private static readonly object cardWork = new(), fileWork = new(), updateWork = new();

    private static object Gate(string op) => op switch
    {
        "update" or "dotnet" => updateWork,
        "syncconfig" or "syncnews" or "stat" or "syncstats" or "testnetwork" => fileWork,
        _ => cardWork,
    };

    private static void Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            // A client that connects and sends nothing is cut off.
            using var silent = new System.Threading.Timer(_ => { try { pipe.Dispose(); } catch (Exception) { } }, null, 10_000, Timeout.Infinite);
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var line = reader.ReadLine();
                silent.Change(Timeout.Infinite, Timeout.Infinite);
                if (line is { Length: > 65536 }) return;
                var request = line == null ? null : JsonSerializer.Deserialize<Request>(line);
                var response = request == null ? new Response { Code = -2, Message = "Brak polecenia." }
                    // The session comes from Windows (the pipe's client), not from the request.
                    : request.Op == "firstsignin" ? FirstSignIn.Arm(GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) ? session : -1)
                    // Read-only check, nothing is copied; another server than the saved one only for an elevated administrator.
                    : request.Op == "testconfig" ? CentralConfig.Test(request.Source, savedAccount: true, administrator: ClientIsAdministrator(pipe))
                    // A shared account's password only for the installed Kiosk program, named by Windows (the pipe's client process).
                    : request.Op == "sharedaccount" ? SharedAccounts.ForService(request.Source, ClientProgram(pipe))
                    // A program with a shared account: started here as the Kiosk's account, the password stays in the service.
                    : request.Op == "runas" ? SharedAccountPrograms.Start(request.Source, ClientProgram(pipe), pipe,
                        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var runasPid) ? (int)runasPid : -1,
                        GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var runasSession) ? runasSession : -1)
                    : request.Op == "newsaccount" ? NewsAccount.ForService(ClientProgram(pipe))
                    // Watchdog: the Kiosk process Windows names reports in or says goodbye.
                    : request.Op is "watch" or "unwatch" ? Watchdog.Request(request.Op, GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var watchedPid) ? (int)watchedPid : -1,
                        ClientProgram(pipe), request.Source)
                    // Card + PIN sign-in: the PIN screen for the Kiosk process and session Windows names, nobody else.
                    : request.Op == "fidosignin" ? FidoSignIn.Launch(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) ? (int)client : -1,
                        GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var clientSession) ? clientSession : -1, ClientProgram(pipe), request.Embed)
                    : Locked(Gate(request.Op), () => Handle(request));
                pipe.Write(JsonSerializer.SerializeToUtf8Bytes(response));
                pipe.WriteByte((byte)'\n');
                pipe.Flush();
                pipe.WaitForPipeDrain();
            }
            catch (Exception) { } // One bad client must not stop the service.
        }
    }

    private static Response Locked(object gate, Func<Response> action) { lock (gate) return action(); }

    private static string? ClientProgram(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id)) return null;
        try { using var process = System.Diagnostics.Process.GetProcessById((int)id); return process.MainModule?.FileName; }
        catch (Exception) { return null; }
    }

    /// <summary>The pipe's client runs elevated as an administrator (the Ctrl+Alt+K Konfiguracja window).</summary>
    private static bool ClientIsAdministrator(NamedPipeServerStream pipe)
    {
        var administrator = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception) { administrator = false; }
        return administrator;
    }

    private static void SetStatus(int state)
    {
        var status = new ServiceStatus { Type = 0x10, State = state, ControlsAccepted = state == 4 ? 0x1 | 0x4 | 0x80 : 0, WaitHint = 3000 };
        SetServiceStatus(statusHandle, ref status);
    }

    private delegate void ServiceMain(int argc, IntPtr argv);
    private delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry { public string? Name; public ServiceMain? Proc; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public int Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerEx handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint process);
}
