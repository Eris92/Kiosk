using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Punkt samoobsługowy, signed in with the card: a Szybka pomoc page runs in this program started as the person
/// (their Windows account, so the page signs in by itself, SSO). It is the same clean page as the guest window: no
/// address bar, no tabs, only the page's own site and Microsoft sign-in, the countdown and "Wróć do Kiosku".
/// The Kiosk keeps the window over its own screen (<see cref="ApplicationView"/>); closing it returns to the Kiosk.
/// </summary>
internal static class CardPageHost
{
    internal const string Argument = "--card-page";

    internal sealed record Request(string Title, string Url, int Seconds, string? Accent, string[] Domains, string? Profile, int[]? Area = null);

    /// <summary>The command-line argument for <paramref name="request"/>: one token, nothing for a shell to split.</summary>
    internal static string Encode(Request request) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));

    internal static int Run(string encoded)
    {
        Request request;
        try { request = JsonSerializer.Deserialize<Request>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))!; }
        catch (Exception) { return 2; }
        KioskTheme.SetAccent(request.Accent);   // The Kiosk's colours (Konfiguracja → Wygląd).
        // The page's own site and the configured ones sign in with the person's Windows account; Microsoft's do via SSO.
        BrowserView.SignInSeconds = Math.Max(30, request.Seconds);
        BrowserView.IntegratedAuthHosts = request.Domains.Where(d => d.Length > 0 && !HelpBrowserForm.SignInDomains.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Where(d => d.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-')).ToArray();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return 2;
        using var form = new HelpBrowserForm(request.Title, request.Url, request.Domains, request.Seconds, windowsAccount: true, profileFolder: request.Profile)
        {
            // Straight where the Kiosk shows it (its screen area, in pixels): no jump from the top of the screen to its place.
            Bounds = request.Area is [var x, var y, var w, var h] && w > 0 && h > 0 ? new Rectangle(x, y, w, h) : Screen.PrimaryScreen!.Bounds
        };
        Application.Run(form);
        return 0;
    }
}
