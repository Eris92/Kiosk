using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Punkt samoobsługowy, signed in with the card: a Szybka pomoc page runs in this program started as the person
/// (their Windows account, so the page signs in by itself, SSO). It is the same clean page as the guest window: no
/// address bar, no tabs, only the page's own site and Microsoft sign-in, the countdown and "Zamknij".
/// The Kiosk keeps the window over its own screen (<see cref="ApplicationView"/>); closing it returns to the Kiosk.
/// With <see cref="Request.Kiosk"/> it is started once, right after the card sign-in, and stays for the whole card
/// session: the Kiosk sends each page over a pipe only it may use (<see cref="Send"/>), and "Zamknij" only parks the
/// window. A page then opens without starting this program and its browser again.
/// </summary>
internal static class CardPageHost
{
    internal const string Argument = "--card-page";

    /// <param name="Kiosk">The Kiosk process of a reusable window (0 = one page, then the program ends).</param>
    /// <param name="KioskSid">The Kiosk's Windows account: the only one besides the person that may open the pipe.</param>
    internal sealed record Request(string Title, string Url, int Seconds, string? Accent, string[] Domains, string? Profile, int[]? Area = null,
        bool OnlyListed = true, int SideInset = 0, int Kiosk = 0, string KioskSid = "");

    /// <summary>The command-line argument for <paramref name="request"/>: one token, nothing for a shell to split.</summary>
    internal static string Encode(Request request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        try { return Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static int Run(string encoded)
    {
        Request request;
        try { request = JsonSerializer.Deserialize<Request>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))!; }
        catch (Exception) { return 2; }
        KioskTheme.SetAccent(request.Accent);   // The Kiosk's colours (Konfiguracja → Wygląd).
        // The page's own site and the configured ones sign in with the person's Windows account; Microsoft's do via SSO.
        // A reusable window gets every configured page's site at once: its browser keeps them for all its pages.
        BrowserView.SignInSeconds = Math.Max(30, request.Seconds);
        BrowserView.IntegratedAuthHosts = request.Domains.Where(d => d.Length > 0 && !HelpBrowserForm.SignInDomains.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Where(d => d.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-')).ToArray();
        // Straight where the Kiosk shows it (its screen area, in pixels): no jump from the top of the screen to its place.
        var bounds = request.Area is [var x, var y, var w, var h] && w > 0 && h > 0 ? new Rectangle(x, y, w, h) : Screen.PrimaryScreen!.Bounds;
        if (request.Kiosk > 0) return RunReusable(request, bounds);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return 2;
        using var form = new HelpBrowserForm(request.Title, request.Url, request.Domains, request.Seconds, windowsAccount: true, profileFolder: request.Profile, onlyListed: request.OnlyListed)
        {
            Bounds = bounds
        };
        // Menu → "Obok": the element beside the page is already there; its part stays free from the first moment.
        if (request.SideInset > 0) form.SetSideInset(request.SideInset);
        Application.Run(form);
        return 0;
    }

    /// <summary>One window for the card session: waits (off the screen) for the Kiosk's pages.</summary>
    private static int RunReusable(Request request, Rectangle bounds)
    {
        SecurityIdentifier kioskAccount;
        try { kioskAccount = new SecurityIdentifier(request.KioskSid); }
        catch (Exception) { return 2; }
        // Off the screen until the Kiosk shows it in its place: never a flash of an empty window at the start.
        using var form = new HelpBrowserForm(windowsAccount: true, profileFolder: request.Profile, reusable: true)
        {
            Bounds = new Rectangle(-32000, -32000, bounds.Width, bounds.Height)
        };
        form.Shown += (_, _) =>
        {
            form.Park();   // Nothing shown yet: the Kiosk keeps it off its screen.
            form.Warm();
            new Thread(() => Serve(form, request.Kiosk, kioskAccount)) { IsBackground = true, Name = "Card page pipe" }.Start();
        };
        Application.Run(form);
        return 0;
    }

    /// <summary>The pipe name of the reusable window in process <paramref name="host"/> for the Kiosk <paramref name="kiosk"/>.</summary>
    internal static string PipeName(int kiosk, int host) => "KioskPage-" + kiosk + "-" + host;

    private sealed record Command(string Op, string Title = "", string Url = "", int Seconds = 60, string[]? Domains = null, bool OnlyListed = true);

    private static void Serve(HelpBrowserForm form, int kiosk, SecurityIdentifier kioskAccount)
    {
        var security = new PipeSecurity();
        using (var self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(kioskAccount, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!form.IsDisposed)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName(kiosk, Environment.ProcessId), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                pipe.WaitForConnection();
                // Only the Kiosk that started this window (Windows names the caller): another program of the Kiosk's
                // account could otherwise open any site as the person.
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var caller) || caller != kiosk) { Audit.Write("card_page_rejected", caller.ToString()); continue; }
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var line = BoundedPipeLine.Read(reader, 65535, TimeSpan.FromSeconds(10));
                var command = line == null ? null : JsonSerializer.Deserialize<Command>(line);
                var answer = command?.Op switch
                {
                    "open" when Uri.TryCreate(command.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps =>
                        Ui(form, () => form.Open(command.Title, command.Url, command.Domains ?? [], command.Seconds, command.OnlyListed)),
                    "park" => Ui(form, form.Park),
                    _ => "ERROR"
                };
                pipe.Write(Encoding.UTF8.GetBytes(answer + "\n"));
                pipe.Flush();
                // Closing leaves the reply available to the client; do not let it hold the only listener.
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { Thread.Sleep(200); }
        }
    }

    private static string Ui(Form form, Action action)
    {
        form.Invoke(action);
        return "OK";
    }

    // ------------------------------------------------------------------ Kiosk

    /// <summary>
    /// The Kiosk: <paramref name="op"/> ("open" with the page, or "park") for the reusable window of process
    /// <paramref name="host"/>. False when it did not answer (closed, crashed): the Kiosk then starts a page on its own.
    /// </summary>
    internal static bool Send(int host, string op, string title = "", string url = "", int seconds = 60, string[]? domains = null, bool onlyListed = true)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(Environment.ProcessId, host), PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(2000);
            // Only the window this Kiosk started answers (a program that took the name first never gets the page).
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var server) || server != host) return false;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Command(op, title, url, seconds, domains, onlyListed));
            try { client.Write(bytes); client.WriteByte((byte)'\n'); client.Flush(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            using var reader = new StreamReader(client, Encoding.UTF8);
            return BoundedPipeLine.Read(reader, 16, TimeSpan.FromSeconds(5)) == "OK";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            Audit.Write("card_page_send_failed", op + ": " + ex.Message);
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint process);
}
