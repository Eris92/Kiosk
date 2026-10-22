using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Client;

/// <summary>
/// "Zaloguj" with the card alone: card + PIN signs the person in to Windows (Entra ID / domain, the same FIDO2 sign-in
/// as the Windows sign-in screen), no login and no password. Windows lets only LocalSystem host that sign-in for
/// another account, so the Kiosk Card service starts the sign-in program (fido\FidoRun.exe, from Program Files) in
/// this Windows session. Its Edge-FIDO2 PIN screen (card, then the PIN in Windows' own field) is drawn inside the
/// Kiosk's own window, in the place the Kiosk gives it: it is the Kiosk's sign-in screen. It keeps the person's Windows
/// token in its own memory
/// and starts their programs (Edge, apps, remote desktops) when the Kiosk asks; the token never reaches the Kiosk.
/// Closing the sign-in ends that program, and every program of the person in this session with it.
/// </summary>
internal sealed class FidoSignIn : IDisposable
{
    internal const string Folder = "fido";
    private const string Program = "FidoRun.exe";
    private readonly string pipe;
    /// <summary>The sign-in program the service started: only its pipe is believed (0 = not known).</summary>
    private readonly uint server;

    private FidoSignIn(string pipe, uint server) { this.pipe = pipe; this.server = server; }

    internal string Sid { get; private set; } = "";
    internal string WindowsName { get; private set; } = "";
    /// <summary>The account as the card names it (e.g. jan@firma.pl).</summary>
    internal string Account { get; private set; } = "";
    internal bool IsAdministrator { get; private set; }

    internal sealed record Outcome(FidoSignIn? SignIn, string Message);

    /// <summary>The sign-in ended because Windows does not know the card (FidoRun's KioskBroker.UnknownKey).</summary>
    internal const string UnknownKey = "UNKNOWN_KEY ";

    /// <summary>
    /// Starts Windows' card sign-in inside the Kiosk window <paramref name="place"/> and waits until it listens for a
    /// card: Windows reads only a card that arrives while it listens, never one already lying on the reader.
    /// Null (with the reason in the Kiosk's log) when it could not start.
    /// </summary>
    /// <summary>Why the last <see cref="PrepareAsync"/> returned null, shown on the screen instead of a bare "did not start".</summary>
    internal static string LastError { get; private set; } = "";

    internal static async Task<FidoSignIn?> PrepareAsync(IntPtr place, int dpi)
    {
        LastError = "";
        var embed = place.ToInt64() + ";" + dpi + ";" + Hex(KioskTheme.Surface) + ";" + Hex(KioskTheme.Text) + ";" + Hex(KioskTheme.Muted) + ";" + Hex(KioskTheme.AccentLine);
        CardService.Response started;
        try { started = await Task.Run(() => CardService.Call(new CardService.Request { Op = "fidosignin", Embed = embed })); }
        catch (Exception ex) { Audit.Write("fido_prepare_failed", LastError = ex.Message); return null; }
        if (started.Code != 0) { Audit.Write("fido_prepare_failed", LastError = started.Message); return null; }
        var signIn = new FidoSignIn(started.Secret ?? "KioskFido-" + Environment.ProcessId, uint.TryParse(started.Message, out var pid) ? pid : 0);
        try
        {
            var listening = await Task.Run(() => signIn.Send("listening", 30000, 60000));
            if (!listening.StartsWith("OK", StringComparison.Ordinal))
            {
                Audit.Write("fido_prepare_failed", LastError = listening.StartsWith("ERROR ", StringComparison.Ordinal) ? listening[6..] : listening);
                _ = Task.Run(signIn.Dispose);
                return null;
            }
            Audit.Write("fido_listening");
            return signIn;
        }
        catch (Exception ex)
        {
            Audit.Write("fido_prepare_failed", LastError = ex.Message);
            _ = Task.Run(signIn.Dispose);
            return null;
        }
    }

    private static string Hex(Color color) => color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

    /// <summary>"--embed" as the Kiosk sends it: window;dpi;surface;text;muted;accent. Anything else never reaches a command line.</summary>
    internal static bool ValidEmbed(string? value) =>
        value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[1-9][0-9]{0,18};[0-9]{2,3};[0-9A-F]{6};[0-9A-F]{6};[0-9A-F]{6};[0-9A-F]{6}$");

    /// <summary>
    /// The Edge-FIDO2 sign-in inside the Kiosk: the person taps the card and types the PIN in Windows' own PIN field
    /// there. Returns once signed in, failed or cancelled.
    /// </summary>
    internal async Task<Outcome> WaitAsync()
    {
        string answer;
        try { answer = await Task.Run(() => Send("wait", 30000, Timeout.Infinite)); }
        catch (Exception ex) { _ = Task.Run(Dispose); return new(null, "Nie udało się połączyć z logowaniem kartą: " + ex.Message); }
        if (!answer.StartsWith("OK ", StringComparison.Ordinal))
        {
            _ = Task.Run(Dispose);   // Not on the window's thread: closing can take seconds.
            return new(null, answer.StartsWith("ERROR ", StringComparison.Ordinal) ? answer[6..] : answer);
        }
        var parts = answer[3..].Split('\t');
        Sid = parts[0];
        WindowsName = parts.Length > 1 ? parts[1] : "";
        Account = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : WindowsName;
        IsAdministrator = parts.Length > 3 && parts[3] == "1";
        return new(this, "");
    }

    /// <summary>"Zaloguj": the Edge-FIDO2 PIN screen in <paramref name="place"/>; a card already lying on the reader must be tapped again.</summary>
    internal static async Task<Outcome?> SignInAsync(IntPtr place, int dpi)
    {
        var signIn = await PrepareAsync(place, dpi);
        if (signIn == null) return new(null, "Logowanie kartą nie wystartowało.");
        return await signIn.WaitAsync();
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
        // Another program that took this pipe's name first could answer "signed in as an administrator": not believed.
        if (server != 0 && (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var answering) || answering != server))
            return "ERROR Logowanie kartą: odpowiedział nieznany program.";
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
    internal static CardService.Response Launch(int clientPid, int session, string? clientProgram, string? embed)
    {
        try
        {
            if (clientPid <= 0 || session <= 0) return new CardService.Response { Code = -2, Message = "Nie rozpoznano programu Kiosk." };
            if (!ValidEmbed(embed)) return new CardService.Response { Code = -2, Message = "Nieprawidłowe miejsce logowania kartą." };
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
                    var line = "\"" + program + "\" --mode kiosk-broker --target authenticated --client-pid " + clientPid + " --embed " + embed;
                    if (!CreateProcessAsUser(token, program, new StringBuilder(line), IntPtr.Zero, IntPtr.Zero, false, 0x400, IntPtr.Zero,
                            Path.GetDirectoryName(program), ref startup, out var process))
                        throw new Win32Exception();
                    CloseHandle(process.Thread);
                    CloseHandle(process.Process);
                    Audit.Write("fido_signin_started", "session " + session);
                    return new CardService.Response { Message = process.ProcessId.ToString(), Secret = "KioskFido-" + clientPid + "-" + process.ProcessId };
                }
            }
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = "Nie udało się uruchomić logowania kartą: " + ex.Message }; }
    }

    /// <summary>The Kiosk runs as an administrator and the service is not installed: the sign-in program starts from here.</summary>
    internal static CardService.Response LaunchHere(string? embed)
    {
        try
        {
            if (!ValidEmbed(embed)) return new CardService.Response { Code = -2, Message = "Nieprawidłowe miejsce logowania kartą." };
            var program = Path.Combine(AppContext.BaseDirectory, Folder, Program);
            if (!File.Exists(program)) return new CardService.Response { Code = -1, Message = "Brak modułu logowania kartą (" + program + ")." };
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program,
                "--mode kiosk-broker --target authenticated --client-pid " + Environment.ProcessId + " --embed " + embed) { UseShellExecute = false })!;
            return new CardService.Response { Message = process.Id.ToString(), Secret = "KioskFido-" + Environment.ProcessId + "-" + process.Id };
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = "Nie udało się uruchomić logowania kartą: " + ex.Message }; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint process);
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
