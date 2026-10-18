using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Client;

/// <summary>
/// "Zaloguj" with the card alone: card + PIN signs the person in to Windows (Entra ID / domain, the same FIDO2 sign-in
/// as the Windows sign-in screen), no login and no password. Windows lets only LocalSystem host that sign-in for
/// another account, so the Kiosk Card service starts the sign-in program (fido\FidoRun.exe, from Program Files) in
/// this Windows session. The Kiosk asks for the PIN on its own screen and hands it to that program, which types it into
/// Windows' own card sign-in (an invisible window), keeps the person's Windows token in its own memory
/// and starts their programs (Edge, apps, remote desktops) when the Kiosk asks; the token never reaches the Kiosk.
/// Closing the sign-in ends that program, and every program of the person in this session with it.
/// </summary>
internal sealed class FidoSignIn : IDisposable
{
    internal const string Folder = "fido";
    private const string Program = "FidoRun.exe";
    private readonly string pipe;

    private FidoSignIn(string pipe) { this.pipe = pipe; }

    internal string Sid { get; private set; } = "";
    internal string WindowsName { get; private set; } = "";
    /// <summary>The account as the card names it (e.g. jan@firma.pl).</summary>
    internal string Account { get; private set; } = "";
    internal bool IsAdministrator { get; private set; }

    internal sealed record Outcome(FidoSignIn? SignIn, string Message);

    /// <summary>
    /// Asks for the PIN with <paramref name="askPin"/> (null = cancelled), then signs in with the card: Windows' sign-in
    /// starts invisibly, <paramref name="wakeCard"/> presents the card lying on the reader to it, and the PIN is handed
    /// over (asked again after a wrong PIN). Null when the person cancelled.
    /// </summary>
    internal static async Task<Outcome?> SignInAsync(Func<string, Task<string?>> askPin, Action wakeCard)
    {
        string answer, message = "Wpisz PIN karty.";
        var pin = await AskAsync(askPin, message);
        if (pin == null) return null;
        var started = await Task.Run(() => CardService.Call(new CardService.Request { Op = "fidosignin" }));
        if (started.Code != 0) return new(null, started.Message);
        // Windows finishes the card sign-in only for the foreground window: let the sign-in program take it.
        int.TryParse(started.Message, out var brokerPid);
        var signIn = new FidoSignIn(started.Secret ?? "KioskFido-" + Environment.ProcessId);
        try
        {
            // Windows' sign-in notices only a card that arrives: once it listens, the card is switched off and on.
            var listening = await Task.Run(() => signIn.Send("listening", 30000, 60000));
            if (listening.StartsWith("ERROR ", StringComparison.Ordinal)) { signIn.Dispose(); return new(null, listening[6..]); }
            if (listening == "OK") { await Task.Delay(800); await Task.Run(wakeCard); }
        }
        catch (Exception ex) { signIn.Dispose(); return new(null, "Nie udało się połączyć z logowaniem kartą: " + ex.Message); }
        while (true)
        {
            if (brokerPid > 0) AllowSetForegroundWindow(brokerPid);
            var entered = pin;
            try { answer = await Task.Run(() => signIn.Send("pin\t" + entered, 30000, 180000)); }
            catch (Exception ex) { signIn.Dispose(); return new(null, "Nie udało się połączyć z logowaniem kartą: " + ex.Message); }
            if (answer.StartsWith("RETRY ", StringComparison.Ordinal))
            {
                var reason = answer[6..].Trim();
                message = (reason.Length > 0 ? reason + " " : "Nieprawidłowy PIN. ") + "Wpisz PIN jeszcze raz.";
                pin = await AskAsync(askPin, message);
                if (pin == null) { signIn.Dispose(); return null; }
                continue;
            }
            break;
        }
        if (!answer.StartsWith("OK ", StringComparison.Ordinal))
        {
            signIn.Dispose();
            return new(null, answer.StartsWith("ERROR ", StringComparison.Ordinal) ? answer[6..] : answer);
        }
        var parts = answer[3..].Split('\t');
        signIn.Sid = parts[0];
        signIn.WindowsName = parts.Length > 1 ? parts[1] : "";
        signIn.Account = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : signIn.WindowsName;
        signIn.IsAdministrator = parts.Length > 3 && parts[3] == "1";
        return new(signIn, "");
    }

    private static async Task<string?> AskAsync(Func<string, Task<string?>> askPin, string message)
    {
        while (await askPin(message) is { } pin)
        {
            if (pin.Length > 0 && !pin.Contains('\t') && !pin.Contains('\n') && !pin.Contains('\r')) return pin;
            message = "Wpisz PIN karty.";
        }
        return null;
    }

    /// <summary>A program as the signed-in person, suspended; the handles belong to this process.</summary>
    internal (IntPtr Process, IntPtr Thread) Start(string application, string commandLine, string? directory, short show)
    {
        var answer = Send("start\t" + Field(application) + "\t" + Field(commandLine) + "\t" + Field(directory ?? "") + "\t" + show, 5000, 120000);
        if (!answer.StartsWith("OK ", StringComparison.Ordinal))
            throw new InvalidOperationException(answer.StartsWith("ERROR ", StringComparison.Ordinal) ? answer[6..] : answer);
        var handles = answer[3..].Split('\t');
        return (new IntPtr(long.Parse(handles[0])), new IntPtr(long.Parse(handles[1])));
    }

    private static string Field(string value) =>
        value.Contains('\t') || value.Contains('\n') || value.Contains('\r') ? throw new ArgumentException("Niedozwolony znak w poleceniu.") : value;

    private string Send(string request, int connectMs, int answerMs)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
        client.Connect(connectMs);
        var bytes = Encoding.UTF8.GetBytes(request + "\n");
        client.Write(bytes);
        client.Flush();
        using var reader = new StreamReader(client, new UTF8Encoding(false));
        var read = reader.ReadLineAsync();
        if (answerMs != Timeout.Infinite && !read.Wait(answerMs)) throw new TimeoutException("Logowanie kartą nie odpowiada.");
        return read.Result ?? "ERROR Logowanie kartą zakończyło się.";
    }

    /// <summary>Sign-out: the sign-in program closes the person's programs and ends.</summary>
    public void Dispose()
    {
        try { Send("close", 2000, 10000); }
        catch (Exception ex) { Audit.Write("fido_signout_error", ex.Message); }
    }

    // ------------------------------------------------------------------ service (LocalSystem)

    /// <summary>Service: starts the sign-in program for the Kiosk process <paramref name="clientPid"/> in its Windows session.</summary>
    internal static CardService.Response Launch(int clientPid, int session, string? clientProgram)
    {
        try
        {
            if (clientPid <= 0 || session <= 0) return new CardService.Response { Code = -2, Message = "Nie rozpoznano programu Kiosk." };
            // Only the installed Kiosk (Program Files) may have programs started as the person who signs in.
            if (!SharedAccounts.IsInstalledKiosk(clientProgram))
                return new CardService.Response { Code = -2, Message = "Logowanie kartą działa tylko dla Kiosku zainstalowanego w Program Files (Ctrl+Alt+U albo Install-KioskShell.ps1)." };
            var program = Path.Combine(AppContext.BaseDirectory, Folder, Program);
            if (!File.Exists(program)) return new CardService.Response { Code = -1, Message = "Brak modułu logowania kartą (" + program + "). Zaktualizuj usługę Kiosk Card." };
            if (!OpenProcessToken(GetCurrentProcess(), 0x02000000, out var own)) throw new Win32Exception(); // MAXIMUM_ALLOWED
            using (own)
            {
                // A copy of this service's own token, moved to the Kiosk's session: the PIN screen appears on its screen.
                if (!DuplicateTokenEx(own, 0x02000000, IntPtr.Zero, 2, 1, out var token)) throw new Win32Exception(); // impersonation level, primary
                using (token)
                {
                    if (!SetTokenInformation(token, 12, ref session, sizeof(int))) throw new Win32Exception(); // TokenSessionId
                    var startup = new AppNative.StartupInfo { Size = Marshal.SizeOf<AppNative.StartupInfo>(), Desktop = @"winsta0\default" };
                    var line = "\"" + program + "\" --mode kiosk-broker --target authenticated --pin-from-client --client-pid " + clientPid;
                    if (!CreateProcessAsUser(token, program, new StringBuilder(line), IntPtr.Zero, IntPtr.Zero, false, 0x400, IntPtr.Zero,
                            Path.GetDirectoryName(program), ref startup, out var process))
                        throw new Win32Exception();
                    CloseHandle(process.Thread);
                    CloseHandle(process.Process);
                    Audit.Write("fido_signin_started", "session " + session);
                    return new CardService.Response { Message = process.ProcessId.ToString(), Secret = "KioskFido-" + clientPid };
                }
            }
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = "Nie udało się uruchomić logowania kartą: " + ex.Message }; }
    }

    /// <summary>The Kiosk runs as an administrator and the service is not installed: the sign-in program starts from here.</summary>
    internal static CardService.Response LaunchHere()
    {
        try
        {
            var program = Path.Combine(AppContext.BaseDirectory, Folder, Program);
            if (!File.Exists(program)) return new CardService.Response { Code = -1, Message = "Brak modułu logowania kartą (" + program + ")." };
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program,
                "--mode kiosk-broker --target authenticated --pin-from-client --client-pid " + Environment.ProcessId) { UseShellExecute = false })!;
            return new CardService.Response { Message = process.Id.ToString(), Secret = "KioskFido-" + Environment.ProcessId };
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = "Nie udało się uruchomić logowania kartą: " + ex.Message }; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type,
        out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, int information, ref int value, int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inherit, int flags, IntPtr environment, string? directory,
        ref AppNative.StartupInfo startup, out ProcessInformation process);
}
