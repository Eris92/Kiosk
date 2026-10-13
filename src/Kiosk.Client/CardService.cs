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
/// It also installs the latest official Kiosk release on request ("update"), so updating needs no administrator.
/// </summary>
internal static class CardService
{
    internal const string ServiceName = "KioskCard";
    internal const string Argument = "--card-service";
    private const string PipeName = "KioskCard";
    private const string RelyingParty = CtapCard.RelyingParty;

    internal sealed class Request
    {
        public string Op { get; set; } = "";          // verify | enroll | open | info | haspin | setpin | changepin | reset | update | firstsignin | syncconfig | testconfig
        public string NewPin { get; set; } = "";
        public string Reader { get; set; } = "";
        public string Pin { get; set; } = "";
        public string? CredentialId { get; set; }
        public string? Salt { get; set; }
        public string Source { get; set; } = "";      // testconfig: the folder or share to check
    }

    internal sealed class Response
    {
        public int Code { get; set; }                 // 0 = OK, CTAP error code, or -1 / -2
        public string Message { get; set; } = "";
        public string? CredentialId { get; set; }
        public string? Secret { get; set; }
    }

    // ------------------------------------------------------------------ client (Kiosk)

    /// <summary>Sends one request to the service; when this process is itself allowed to talk FIDO2 (administrator), runs it directly.</summary>
    internal static Response Call(Request request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            var line = JsonSerializer.SerializeToUtf8Bytes(request);
            pipe.Write(line);
            pipe.WriteByte((byte)'\n');
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            return JsonSerializer.Deserialize<Response>(reader.ReadLine() ?? "{}") ?? new Response { Code = -1, Message = "Pusta odpowiedź usługi." };
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
            if (request.Op == "update") return Updater.InstallLatest();   // No card, no path: the latest official release.
            if (request.Op == "syncconfig") return CentralConfig.SyncForMachine(); // No path: the server named in the PC's own configuration.
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
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var line = reader.ReadLine();
                var request = line == null ? null : JsonSerializer.Deserialize<Request>(line);
                var response = request == null ? new Response { Code = -2, Message = "Brak polecenia." }
                    // The session comes from Windows (the pipe's client), not from the request.
                    : request.Op == "firstsignin" ? FirstSignIn.Arm(GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) ? session : -1)
                    // Read-only check, nothing is copied; another server than the saved one only for an elevated administrator.
                    : request.Op == "testconfig" ? CentralConfig.Test(request.Source, savedAccount: true, administrator: ClientIsAdministrator(pipe))
                    : Handle(request);
                pipe.Write(JsonSerializer.SerializeToUtf8Bytes(response));
                pipe.WriteByte((byte)'\n');
                pipe.Flush();
                pipe.WaitForPipeDrain();
            }
            catch (Exception) { Thread.Sleep(200); } // One bad client must not stop the service.
        }
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
}
