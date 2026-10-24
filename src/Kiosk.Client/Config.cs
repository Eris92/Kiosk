using System.Text.Json.Serialization;

namespace Kiosk.Client;

[JsonConverter(typeof(JsonStringEnumConverter<RdpAuthentication>))]
internal enum RdpAuthentication { SmartCard, WindowsCurrentUser, EntraId }

/// <summary>How the in-app lock is released. Independent of the Windows lock screen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnlockMethod>))]
internal enum UnlockMethod { Card, CardAndWindowsHello, CardAndPin }

[JsonConverter(typeof(JsonStringEnumConverter<SessionMode>))]
internal enum SessionMode { Kiosk, WindowsShell }

internal sealed record RdpConnection
{
    public string Name { get; init; } = "";
    public string Server { get; init; } = "";
    public int Port { get; init; } = 3389;
    public string UserName { get; init; } = "";
    public RdpAuthentication Authentication { get; init; } = RdpAuthentication.SmartCard;
}

/// <summary>What a screen shows while nobody works at the Kiosk: a web page or an application, view only.</summary>
internal sealed record ScreenContent
{
    /// <summary>0 = every screen, 1 = the main screen, 2, 3… = the other screens in Windows' order.</summary>
    public int Screen { get; init; }
    public string Url { get; init; } = "";
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
}

/// <summary>A command in Menu → Polecenia: a program or script run in the background (copy files, sync, restart…).</summary>
internal sealed record CommandEntry
{
    public string Name { get; init; } = "";
    /// <summary>EXE, .cmd/.bat or .ps1.</summary>
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    /// <summary>Run as the signed-in person. In Kiosk mode this requires "Zaloguj"; WindowsShell already runs as that person.</summary>
    public bool AsUser { get; init; } = true;
    public bool Confirm { get; init; }
}

internal sealed record AdminCard
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
}

internal sealed record Config
{
    public RdpConnection[] RdpConnections { get; init; } = [];
    public string ReaderName { get; init; } = "";
    public string BrowserUrl { get; init; } = "https://www.google.com";
    public Bookmark[] Bookmarks { get; init; } = [];
    /// <summary>When true the browser opens only bookmarked sites (and their subdomains) plus BrowserAllowedDomains.</summary>
    public bool EnableBrowser { get; init; } = true;
    public bool BrowserOnlyBookmarks { get; init; }
    public string[] BrowserAllowedDomains { get; init; } = [];
    public ApplicationEntry[] Applications { get; init; } = [];
    /// <summary>Screens' content while nobody is signed in (view only). Empty = the usual Kiosk desktop.</summary>
    public ScreenContent[] IdleScreens { get; init; } = [];
    /// <summary>Without a card, the main screen returns to its idle content after this many seconds without input.</summary>
    public int IdleAfterSeconds { get; init; } = 5;
    /// <summary>Screens' web pages are reloaded every this many seconds (0 = never).</summary>
    public int ScreenRefreshSeconds { get; init; }
    public CommandEntry[] Commands { get; init; } = [];
    public int LockAfterSeconds { get; init; } = 300;
    public int ChangeUserAfterSeconds { get; init; } = 600;
    public UnlockMethod UnlockMethod { get; init; } = UnlockMethod.Card;
    /// <summary>"Zaloguj" also asks for the person's Windows / Entra login and password (like runas); their apps,
    /// Edge and "current user" remote desktops then run as them. Kiosk mode only.</summary>
    public bool SignInAsUser { get; init; }
    /// <summary>Kiosk: one Windows account, the Kiosk switches people by card. WindowsShell: every person signs in to
    /// Windows with their card / Windows Hello and the Kiosk is their shell; lock = disconnect, Wyloguj = sign out.</summary>
    public SessionMode SessionMode { get; init; } = SessionMode.Kiosk;
    public bool DisconnectOnCardRemoval { get; init; } = true;
    public string AccentColor { get; init; } = KioskTheme.DefaultAccent;
    /// <summary>Cards that may open Konfiguracja. Members of this PC's local Administrators group also may, when AllowWindowsAdministrators is on.</summary>
    public AdminCard[] AdminCards { get; init; } = [];
    public bool AllowWindowsAdministrators { get; init; } = true;
    public bool RequireCredentialPrompt { get; init; } = true;
    public string AutoConnectRdpName { get; init; } = "";
    public int ConnectTimeoutSeconds { get; init; } = 60;
    public bool TestMode { get; init; }
    /// <summary>2: the built-in Notatnik is an entry in Applications (older files get it added once).</summary>
    public int ConfigVersion { get; init; }
    internal const int CurrentVersion = 2;

    // Read older installations without losing their target or settings.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Server { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Port { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? IdleSeconds { get; init; }

    internal Config Migrate()
    {
        if (ConfigVersion > CurrentVersion || ConfigVersion < 0)
            throw new InvalidDataException("Wersja konfiguracji nie jest obsługiwana przez ten Kiosk.");
        if (RdpConnections == null || Applications == null || Applications.Any(a => a == null))
            throw new InvalidDataException("Listy połączeń i aplikacji nie mogą zawierać null.");
        return this with
        {
            RdpConnections = RdpConnections.Length == 0 && !string.IsNullOrWhiteSpace(Server)
                ? [new RdpConnection { Name = "Pulpit zdalny", Server = Server, Port = Port ?? 3389 }]
                : RdpConnections,
            LockAfterSeconds = IdleSeconds ?? LockAfterSeconds,
            ChangeUserAfterSeconds = IdleSeconds.HasValue ? Math.Max(ChangeUserAfterSeconds, IdleSeconds.Value * 2) : ChangeUserAfterSeconds,
            Applications = ConfigVersion < 2 && Applications != null && !Applications.Any(a => a.IsBuiltInNotes) ? [.. Applications, ApplicationEntry.Notes] : Applications!,
            ConfigVersion = CurrentVersion,
            Server = null, Port = null, IdleSeconds = null
        };
    }

    public void Validate()
    {
        if (ReaderName == null || AutoConnectRdpName == null || RdpConnections == null || Bookmarks == null || Applications == null || IdleScreens == null || Commands == null)
            throw new InvalidDataException("Konfiguracja zawiera pustą listę lub wartość null.");
        if (LockAfterSeconds is < 10 or > 86400 || ChangeUserAfterSeconds is < 0 or > 86400 ||
            ConnectTimeoutSeconds is < 10 or > 300)
            throw new InvalidDataException("Blokada: 10–86400 s, przechowywanie sesji: 0–86400 s (0 = do wylogowania), limit łączenia: 10–300 s.");
        if (!Configuration.IsWebUrl(BrowserUrl)) throw new InvalidDataException("Strona startowa musi mieć adres HTTP/HTTPS.");
        foreach (var connection in RdpConnections)
        {
            if (connection == null || string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 128 ||
                !Configuration.IsRdpServer(connection.Server) || connection.Port is < 1 or > 65535 ||
                connection.UserName == null || connection.UserName.Length > 1024 ||
                !Enum.IsDefined(connection.Authentication)) throw new InvalidDataException("Każde połączenie RDP wymaga nazwy, hosta, portu i sposobu logowania.");
            if (connection.Authentication == RdpAuthentication.EntraId &&
                !Configuration.IsEntraRdpServer(connection.Server))
                throw new InvalidDataException("Połączenie Entra wymaga pełnej nazwy DNS komputera; adres IP nie jest obsługiwany.");
        }
        if (RdpConnections.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != RdpConnections.Length)
            throw new InvalidDataException("Nazwy połączeń RDP muszą być unikalne.");
        if (!Enum.IsDefined(UnlockMethod)) throw new InvalidDataException("Nieznany sposób odblokowania.");
        if (!Enum.IsDefined(SessionMode)) throw new InvalidDataException("Nieznany tryb sesji.");
        if (ScreenRefreshSeconds is < 0 or > 86400) throw new InvalidDataException("Odświeżanie ekranów: 0–86400 s.");
        foreach (var c in Commands)
            if (c == null || string.IsNullOrWhiteSpace(c.Name) || c.Path == null || c.Arguments == null ||
                !Path.IsPathFullyQualified(c.Path) ||
                !new[] { ".exe", ".cmd", ".bat", ".ps1" }.Contains(Path.GetExtension(c.Path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Każde polecenie wymaga nazwy i pełnej ścieżki do pliku EXE, CMD, BAT lub PS1.");
        if (Commands.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Commands.Length)
            throw new InvalidDataException("Nazwy poleceń muszą być unikalne.");
        if (IdleAfterSeconds is < 3 or > 86400) throw new InvalidDataException("Czas powrotu ekranu do treści musi mieć 3–86400 s.");
        foreach (var s in IdleScreens)
        {
            if (s == null || s.Url == null || s.Path == null || s.Arguments == null)
                throw new InvalidDataException("Konfiguracja ekranu nie może zawierać null.");
            if (s.Screen < 0) throw new InvalidDataException("Numer ekranu nie może być ujemny.");
            if (s.Url.Length > 0 && !(Uri.TryCreate(s.Url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
                throw new InvalidDataException("Strona dla ekranu " + s.Screen + " musi być adresem HTTP/HTTPS.");
            if (s.Url.Length == 0 && s.Path.Length == 0) throw new InvalidDataException("Ekran " + s.Screen + " potrzebuje strony albo aplikacji.");
            if (s.Url.Length > 0 && s.Path.Length > 0) throw new InvalidDataException("Wybierz stronę albo aplikację dla ekranu, nie obie naraz.");
            if (s.Path.Length > 0 && (!Path.IsPathFullyQualified(s.Path) || !string.Equals(Path.GetExtension(s.Path), ".exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Aplikacja ekranu wymaga pełnej ścieżki do pliku EXE.");
        }
        if (IdleScreens.Select(s => s.Screen).Distinct().Count() != IdleScreens.Length)
            throw new InvalidDataException("Każdy numer ekranu można skonfigurować tylko raz.");
        if (!KioskTheme.IsColor(AccentColor)) throw new InvalidDataException("Kolor przewodni musi mieć postać #RRGGBB.");
        if (AdminCards == null || AdminCards.Any(a => a == null || string.IsNullOrWhiteSpace(a.Id) || a.Name == null))
            throw new InvalidDataException("Każda karta administratora wymaga identyfikatora.");
        if (!TestMode && AdminCards.Any(a => !IsAuthenticatedAdminIdentity(a.Id)))
            throw new InvalidDataException("Administrator wymaga odcisku certyfikatu karty lub identyfikatora uwierzytelnionej sesji Windows. UID karty nie potwierdza tożsamości.");
        if (AutoConnectRdpName.Length > 0 && !RdpConnections.Any(r => r.Name.Equals(AutoConnectRdpName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Wybierz istniejące połączenie do automatycznego uruchamiania.");
        foreach (var bookmark in Bookmarks)
            if (bookmark == null || string.IsNullOrWhiteSpace(bookmark.Name) || !Configuration.IsWebUrl(bookmark.Url))
                throw new InvalidDataException("Każda zakładka wymaga nazwy i adresu HTTP/HTTPS.");
        if (BrowserAllowedDomains == null || BrowserAllowedDomains.Any(d => string.IsNullOrWhiteSpace(d) ||
            d.Any(char.IsWhiteSpace) || d.Contains('/') || d.Contains(':')))
            throw new InvalidDataException("Dodatkowe domeny podaj jako same nazwy, np. login.microsoftonline.com.");
        if (BrowserOnlyBookmarks && Bookmarks.Length == 0)
            throw new InvalidDataException("Tryb „tylko strony z zakładek” wymaga co najmniej jednej zakładki.");
        foreach (var app in Applications)
            if (app == null || string.IsNullOrWhiteSpace(app.Name) || app.Path == null ||
                app.Arguments == null || app.WorkingDirectory == null)
                throw new InvalidDataException("Aplikacja wymaga nazwy i pełnej ścieżki do pliku EXE.");
            else if (!app.IsBuiltInNotes && (!Path.IsPathFullyQualified(app.Path) ||
                !string.Equals(Path.GetExtension(app.Path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                app.Arguments == null || app.WorkingDirectory == null ||
                (app.WorkingDirectory.Length > 0 && !Path.IsPathFullyQualified(app.WorkingDirectory))))
                throw new InvalidDataException("Aplikacja wymaga nazwy i pełnej ścieżki do pliku EXE.");
        if (Applications.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Applications.Length)
            throw new InvalidDataException("Nazwy aplikacji muszą być unikalne.");
        if (Applications.Any(a => RdpConnections.Any(r => a.Name.Equals(RdpUserHost.WindowName(r), StringComparison.OrdinalIgnoreCase)) ||
            SignInAsUser && SessionMode == SessionMode.Kiosk && EnableBrowser && a.Name.Equals("Przeglądarka", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Nazwa aplikacji jest zarezerwowana przez pulpit zdalny lub przeglądarkę.");
        if (Applications.Count(a => a.IsBuiltInNotes) > 1) throw new InvalidDataException("Wbudowany Notatnik może być na liście tylko raz.");
    }

    private static bool IsAuthenticatedAdminIdentity(string id) =>
        id.StartsWith("win:S-1-", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("cert:", StringComparison.OrdinalIgnoreCase) && id.Length is 45 or 69 && id.AsSpan(5).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
