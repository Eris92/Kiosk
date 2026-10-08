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
/// a local pipe. The service does only this: check the card PIN, create the Kiosk credential and return its
/// hmac-secret, always for the relying party "kiosk.local" — never signatures or sign-ins for other sites.
/// </summary>
internal static class CardService
{
    internal const string ServiceName = "KioskCard";
    internal const string Argument = "--card-service";
    private const string PipeName = "KioskCard";
    private const string RelyingParty = CtapCard.RelyingParty;

    internal sealed class Request
    {
        public string Op { get; set; } = "";          // verify | enroll | open
        public string Reader { get; set; } = "";
        public string Pin { get; set; } = "";
        public string? CredentialId { get; set; }
        public string? Salt { get; set; }
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
        finally { request.Pin = ""; }
    }

    // ------------------------------------------------------------------ the work

    private static Response Handle(Request request)
    {
        try
        {
            using var card = new CtapCard(request.Reader);
            if (request.Op == "info") return new Response { Message = card.Info() }; // Diagnostics, no PIN.
            card.UsePin(request.Pin);
            switch (request.Op)
            {
                case "verify":
                    return new Response { Message = "PIN karty potwierdzony." };
                case "enroll":
                {
                    var id = card.MakeCredential(RelyingParty);
                    // CTAP 2.1 cards drop the token's permissions after makeCredential: get a fresh one for the assertion.
                    card.UsePin(request.Pin);
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
        catch (CtapException ex) { return new Response { Code = ex.Code, Message = ex.Message }; }
        catch (Exception ex) { return new Response { Code = -1, Message = ex.Message }; }
        finally { request.Pin = ""; }
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
        handler = (control, _, _, _) =>
        {
            if (control is 1 or 5) { SetStatus(3); stopping.Set(); } // STOP, SHUTDOWN -> STOP_PENDING
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
                var response = request == null ? new Response { Code = -2, Message = "Brak polecenia." } : Handle(request);
                pipe.Write(JsonSerializer.SerializeToUtf8Bytes(response));
                pipe.WriteByte((byte)'\n');
                pipe.Flush();
                pipe.WaitForPipeDrain();
            }
            catch (Exception) { Thread.Sleep(200); } // One bad client must not stop the service.
        }
    }

    private static void SetStatus(int state)
    {
        var status = new ServiceStatus { Type = 0x10, State = state, ControlsAccepted = state == 4 ? 0x1 | 0x4 : 0, WaitHint = 3000 };
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
}
