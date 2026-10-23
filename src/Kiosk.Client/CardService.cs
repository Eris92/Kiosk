using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
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
    private const int MaximumMessageLength = 16384;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(90);

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
        public int Code { get; set; } = -1;            // 0 = OK, CTAP error code, or -1 / -2
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
            Validate(request);
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            pipe.Connect(3000);
            // A different local process must not impersonate the service and receive the card PIN.
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Equals(owner))
                return new Response { Message = "Nie można potwierdzić tożsamości usługi Kiosk Card. Uruchom ponownie zaktualizowaną usługę." };
            using var timeout = new CancellationTokenSource(OperationTimeout);
            return ExchangeAsync(pipe, request, timeout.Token).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return Handle(request);
            return new Response { Code = -1, Message = "Usługa „Kiosk Card” nie działa. Zainstaluj ją: scripts\\Install-KioskCardService.ps1." };
        }
        catch (OperationCanceledException) { return new Response { Message = "Przekroczono czas odpowiedzi usługi Kiosk Card." }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new Response { Code = -2, Message = "Nie udało się wykonać polecenia usługi Kiosk Card." }; }
        finally { request.Pin = ""; }
    }

    private static async Task<Response> ExchangeAsync(Stream pipe, Request request, CancellationToken cancel)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(request);
        try
        {
            await pipe.WriteAsync(line, cancel);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, cancel);
            await pipe.FlushAsync(cancel);
        }
        finally { CryptographicOperations.ZeroMemory(line); }
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        return JsonSerializer.Deserialize<Response>(await ReadMessageAsync(reader, cancel))
            ?? new Response { Message = "Pusta odpowiedź usługi." };
    }

    internal static async Task<string> ReadMessageAsync(TextReader reader, CancellationToken cancel)
    {
        var message = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), cancel);
                if (count == 0) throw new IOException("Niepełne polecenie usługi Kiosk Card.");
                int newline = Array.IndexOf(buffer, '\n', 0, count);
                int length = newline < 0 ? count : newline;
                if (message.Length + length > MaximumMessageLength) throw new IOException("Polecenie usługi Kiosk Card jest zbyt długie.");
                message.Append(buffer, 0, length);
                if (newline >= 0) return message.ToString();
            }
        }
        finally { Array.Clear(buffer); message.Clear(); }
    }

    internal static void Validate(Request request)
    {
        if (request.Op is not ("info" or "verify" or "enroll" or "open") ||
            string.IsNullOrWhiteSpace(request.Reader) || request.Reader.Length > 1024 || request.Reader.Contains('\0'))
            throw new ArgumentException("Nieprawidłowe polecenie usługi Kiosk Card.");
        if (request.Op == "info") return;
        if (string.IsNullOrEmpty(request.Pin) || request.Pin.Contains('\0') || Encoding.UTF8.GetByteCount(request.Pin) > 63)
            throw new ArgumentException("Nieprawidłowy format PIN-u karty.");
        if (request.Op is "enroll" or "open")
        {
            if (request.Salt == null || request.Salt.Length > 88 || Convert.FromBase64String(request.Salt).Length != 32)
                throw new ArgumentException("Nieprawidłowa sól klucza karty.");
            if (request.Op == "open" && (request.CredentialId == null || request.CredentialId.Length > 8192 ||
                Convert.FromBase64String(request.CredentialId).Length is < 1 or > 4096))
                throw new ArgumentException("Nieprawidłowy identyfikator klucza karty.");
        }
    }

    // ------------------------------------------------------------------ the work

    private static Response Handle(Request request)
    {
        string step = "połączenie";
        try
        {
            Validate(request); // Validate all inputs before touching the card or consuming a PIN attempt.
            using var card = new CtapCard(request.Reader);
            if (request.Op == "info") return new Response { Code = 0, Message = card.Info() }; // Diagnostics, no PIN.
            step = "PIN";
            card.UsePin(request.Pin);
            switch (request.Op)
            {
                case "verify":
                    return new Response { Code = 0, Message = "PIN karty potwierdzony." };
                case "enroll":
                {
                    step = "tworzenie klucza";
                    var id = card.MakeCredential(RelyingParty);
                    // CTAP 2.1 cards drop the token's permissions after makeCredential: get a fresh one for the assertion.
                    step = "PIN (2)";
                    card.UsePin(request.Pin);
                    step = "odczyt klucza";
                    var secret = card.HmacSecret(RelyingParty, id, Convert.FromBase64String(request.Salt!));
                    try { return new Response { Code = 0, CredentialId = Convert.ToBase64String(id), Secret = Convert.ToBase64String(secret) }; }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                }
                case "open":
                {
                    var secret = card.HmacSecret(RelyingParty, Convert.FromBase64String(request.CredentialId!), Convert.FromBase64String(request.Salt!));
                    try { return new Response { Code = 0, Secret = Convert.ToBase64String(secret) }; }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                }
                default:
                    return new Response { Code = -2, Message = "Nieznane polecenie." };
            }
        }
        catch (CtapException ex) { return new Response { Code = ex.Code, Message = ex.Message + " [" + step + "]" }; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException)
        { return new Response { Code = -2, Message = "Nieprawidłowe polecenie usługi Kiosk Card." }; }
        catch (Exception) { return new Response { Code = -1, Message = "Nie udało się odczytać karty." }; }
        finally { request.Pin = ""; }
    }

    // ------------------------------------------------------------------ service

    private static IntPtr statusHandle;
    private static readonly ManualResetEventSlim stopping = new();
    private static readonly CancellationTokenSource shutdown = new();
    private static ServiceMain? main;            // kept alive for the native callback
    private static HandlerEx? handler;
    private static int listenerExitCode;

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
            if (control is 1 or 5) { SetStatus(3, 1); shutdown.Cancel(); stopping.Set(); } // STOP, SHUTDOWN -> STOP_PENDING
            return 0;
        };
        statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, handler, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) return;
        SetStatus(2, 1); // START_PENDING until the pipe is bound and its ACL is set.
        PipeSecurity security;
        NamedPipeServerStream firstPipe;
        try
        {
            security = PipePermissions();
            firstPipe = CreatePipe(security);
        }
        catch (Exception ex)
        {
            Audit.Write("card_service_start_failed", ex.Message);
            SetStatus(1, exitCode: 1);
            return;
        }
        var listener = new Thread(() => Listen(firstPipe, security)) { IsBackground = true };
        try { listener.Start(); }
        catch (Exception ex)
        {
            firstPipe.Dispose();
            Audit.Write("card_service_start_failed", ex.Message);
            SetStatus(1, exitCode: 1);
            return;
        }
        if (!stopping.IsSet) SetStatus(4); // RUNNING only when a client can connect.
        stopping.Wait();
        SetStatus(3, 1);
        // A card operation may still be in progress. Keep SCM informed while it
        // finishes instead of reporting STOPPED with a live listener thread.
        int checkpoint = 2;
        while (listener.IsAlive && !listener.Join(2000) && checkpoint <= 47)
            SetStatus(3, checkpoint++);
        if (listener.IsAlive) Audit.Write("card_service_stop_timeout");
        SetStatus(1, exitCode: listener.IsAlive ? 1 : Volatile.Read(ref listenerExitCode));
    }

    private static PipeSecurity PipePermissions()
    {
        // SYSTEM and administrators: full; signed-in (interactive) users: may connect and send requests.
        var security = new PipeSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private static NamedPipeServerStream CreatePipe(PipeSecurity security) =>
        NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);

    private static NamedPipeServerStream? CreateNextPipe(PipeSecurity security)
    {
        var started = Environment.TickCount64;
        while (!stopping.IsSet)
        {
            try { return CreatePipe(security); }
            // The previous client can briefly retain its disconnected pipe
            // handle. Keep the first-instance guarantee while it closes.
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) &&
                Environment.TickCount64 - started < 3000) { stopping.Wait(100); }
        }
        return null;
    }

    private static void Listen(NamedPipeServerStream pipe, PipeSecurity security)
    {
        try
        {
            while (!stopping.IsSet)
            {
                using (pipe)
                {
                    try { ServeConnection(pipe); }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
                    catch (Exception) { stopping.Wait(200); } // One bad client must not stop the service.
                }
                if (stopping.IsSet) return;
                try
                {
                    var next = CreateNextPipe(security);
                    if (next == null) return;
                    pipe = next;
                }
                catch (Exception ex)
                {
                    Audit.Write("card_service_pipe_failed", ex.Message);
                    Volatile.Write(ref listenerExitCode, 1);
                    stopping.Set();
                    return;
                }
            }
        }
        finally { pipe.Dispose(); }
    }

    private static void ServeConnection(NamedPipeServerStream pipe)
    {
        pipe.WaitForConnectionAsync(shutdown.Token).GetAwaiter().GetResult();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var line = ReadMessageAsync(reader, timeout.Token).GetAwaiter().GetResult();
        var request = JsonSerializer.Deserialize<Request>(line);
        var response = request == null ? new Response { Code = -2, Message = "Brak polecenia." } : Handle(request);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        response.Secret = null;
        try
        {
            pipe.WriteAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.FlushAsync(timeout.Token).GetAwaiter().GetResult();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void SetStatus(int state, int checkpoint = 0, int exitCode = 0)
    {
        var pending = state is 2 or 3;
        var status = new ServiceStatus { Type = 0x10, State = state, ControlsAccepted = state == 4 ? 0x1 | 0x4 : 0,
            Win32ExitCode = exitCode, CheckPoint = pending ? checkpoint : 0, WaitHint = pending ? 5000 : 0 };
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
