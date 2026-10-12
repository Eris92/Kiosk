using System.Text.Json.Serialization;

namespace Kiosk.Client;

[JsonConverter(typeof(JsonStringEnumConverter<RdpAuthentication>))]
internal enum RdpAuthentication { SmartCard, WindowsCurrentUser, EntraId }

/// <summary>How the in-app lock is released. Independent of the Windows lock screen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnlockMethod>))]
internal enum UnlockMethod { Card, CardAndWindowsHello, CardAndPin }

[JsonConverter(typeof(JsonStringEnumConverter<SessionMode>))]
internal enum SessionMode { Kiosk, WindowsShell }

/// <summary>Where Start and the window icons sit on the taskbar: in the middle (Windows 11) or on the left.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskbarAlignment>))]
internal enum TaskbarAlignment { Center, Left }

internal sealed record RdpConnection
{
    public string Name { get; init; } = "";
    public string Server { get; init; } = "";
    public int Port { get; init; } = 3389;
    public string UserName { get; init; } = "";
    public RdpAuthentication Authentication { get; init; } = RdpAuthentication.SmartCard;
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>What a screen shows while nobody works at the Kiosk: a web page or an application, view only.</summary>
internal sealed record ScreenContent
{
    /// <summary>0 = every screen, 1 = the main screen, 2, 3… = the other screens in Windows' order.</summary>
    public int Screen { get; init; }
    public string Url { get; init; } = "";
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>A command in Menu → Polecenia: a program or script run in the background (copy files, sync, restart…).</summary>
internal sealed record CommandEntry
{
    public string Name { get; init; } = "";
    /// <summary>EXE, .cmd/.bat or .ps1.</summary>
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    /// <summary>Run as the signed-in person (after "Zaloguj"); otherwise as the Kiosk account.</summary>
    public bool AsUser { get; init; } = true;
    public bool Confirm { get; init; }
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;
}

internal sealed record AdminCard
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;
}

internal sealed record Config
{
    public RdpConnection[] RdpConnections { get; init; } = [];
    public string ReaderName { get; init; } = "";
    public string BrowserUrl { get; init; } = "https://www.google.com";
    public Bookmark[] Bookmarks { get; init; } = [];
    /// <summary>When true the browser opens only bookmarked sites (and their subdomains) plus BrowserAllowedDomains.</summary>
    public bool EnableBrowser { get; init; } = true;
    /// <summary>When Kiosk starts it installs a newer GitHub release by itself (Konfiguracja → Aktualizacja).</summary>
    public bool AutoUpdate { get; init; } = true;
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
    public TaskbarAlignment TaskbarAlignment { get; init; } = TaskbarAlignment.Center;
    /// <summary>Desktop corner: the big name, the line under it and an optional logo (PNG/JPG). Empty texts and logo show nothing.</summary>
    public string BrandTitle { get; init; } = "Kiosk";
    public string BrandSubtitle { get; init; } = "Twój pulpit pracy";
    public string BrandLogo { get; init; } = "";
    /// <summary>Folder or share with kiosk-news.json: company messages scrolling on the desktop. Empty = none.</summary>
    public string NewsFolder { get; init; } = "";
    public int NewsSeconds { get; init; } = 8;
    /// <summary>Szybka pomoc on the desktop, also on the lock screen (MFA, password reset, card PIN). Empty = none.</summary>
    public HelpAction[] HelpActions { get; init; } = HelpAction.Defaults;
    /// <summary>Cards that may open Konfiguracja. Members of this PC's local Administrators group also may, when AllowWindowsAdministrators is on.</summary>
    public AdminCard[] AdminCards { get; init; } = [];
    public bool AllowWindowsAdministrators { get; init; } = true;
    public bool RequireCredentialPrompt { get; init; } = true;
    public string AutoConnectRdpName { get; init; } = "";
    public int ConnectTimeoutSeconds { get; init; } = 60;
    public bool TestMode { get; init; } = true;
    /// <summary>2: the built-in Notatnik is an entry in Applications (older files get it added once).</summary>
    public int ConfigVersion { get; init; }
    internal const int CurrentVersion = 4;

    // Read older installations without losing their target or settings.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Server { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Port { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? IdleSeconds { get; init; }

    /// <summary>What Kiosk works with: the lists without the entries unticked in Konfiguracja.</summary>
    internal Config Active()
    {
        var applications = Applications.Where(a => a.Enabled).ToArray();
        return this with
        {
            RdpConnections = RdpConnections.Where(c => c.Enabled).ToArray(),
            Bookmarks = Bookmarks.Where(b => b.Enabled).ToArray(),
            Applications = applications,
            EnableBrowser = applications.Any(a => a.IsBuiltInBrowser),
            IdleScreens = IdleScreens.Where(s => s.Enabled).ToArray(),
            Commands = Commands.Where(c => c.Enabled).ToArray(),
            AdminCards = AdminCards.Where(a => a.Enabled).ToArray(),
            HelpActions = HelpActions.Where(h => h.Enabled).ToArray(),
        };
    }

    internal Config Migrate()
    {
        if (RdpConnections == null) throw new InvalidDataException("Lista RDP nie może być null.");
        var applications = ConfigVersion < 2 && Applications != null && !Applications.Any(a => a.IsBuiltInNotes) ? [.. Applications, ApplicationEntry.Notes] : Applications!;
        // Version 3: the browser became an entry of the application list instead of the EnableBrowser switch.
        if (ConfigVersion < 3 && applications != null && EnableBrowser && !applications.Any(a => a.IsBuiltInBrowser)) applications = [ApplicationEntry.Browser, .. applications];
        return this with
        {
            RdpConnections = RdpConnections.Length == 0 && !string.IsNullOrWhiteSpace(Server)
                ? [new RdpConnection { Name = "Pulpit zdalny", Server = Server, Port = Port ?? 3389 }]
                : RdpConnections,
            LockAfterSeconds = IdleSeconds ?? LockAfterSeconds,
            ChangeUserAfterSeconds = IdleSeconds.HasValue ? Math.Max(ChangeUserAfterSeconds, IdleSeconds.Value * 2) : ChangeUserAfterSeconds,
            Applications = applications,
            EnableBrowser = applications?.Any(a => a.IsBuiltInBrowser) == true,
            // Version 4: automatic update is on by default (build-66 saved it switched off without asking).
            AutoUpdate = ConfigVersion < 4 || AutoUpdate,
            ConfigVersion = CurrentVersion,
            Server = null, Port = null, IdleSeconds = null
        };
    }

    public void Validate()
    {
        if (ReaderName == null || AutoConnectRdpName == null || RdpConnections == null || Bookmarks == null || Applications == null)
            throw new InvalidDataException("Konfiguracja zawiera pustą listę lub wartość null.");
        if (LockAfterSeconds is < 10 or > 86400 || ChangeUserAfterSeconds is < 0 or > 86400 ||
            ConnectTimeoutSeconds is < 10 or > 300)
            throw new InvalidDataException("Blokada: 10–86400 s, przechowywanie sesji: 0–86400 s (0 = do wylogowania), limit łączenia: 10–300 s.");
        if (!Configuration.IsWebUrl(BrowserUrl)) throw new InvalidDataException("Strona startowa musi mieć adres HTTP/HTTPS.");
        foreach (var connection in RdpConnections)
        {
            if (connection == null || string.IsNullOrWhiteSpace(connection.Name) || string.IsNullOrWhiteSpace(connection.Server) ||
                connection.Server.Any(char.IsWhiteSpace) || connection.Port is < 1 or > 65535 || connection.UserName == null ||
                !Enum.IsDefined(connection.Authentication)) throw new InvalidDataException("Każde połączenie RDP wymaga nazwy, hosta, portu i sposobu logowania.");
            if (connection.Authentication == RdpAuthentication.EntraId &&
                (System.Net.IPAddress.TryParse(connection.Server, out _) || !connection.Server.Contains('.')))
                throw new InvalidDataException("Połączenie Entra wymaga pełnej nazwy DNS komputera; adres IP nie jest obsługiwany.");
        }
        if (RdpConnections.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != RdpConnections.Length)
            throw new InvalidDataException("Nazwy połączeń RDP muszą być unikalne.");
        if (!Enum.IsDefined(UnlockMethod)) throw new InvalidDataException("Nieznany sposób odblokowania.");
        if (!Enum.IsDefined(SessionMode)) throw new InvalidDataException("Nieznany tryb sesji.");
        if (ScreenRefreshSeconds is < 0 or > 86400) throw new InvalidDataException("Odświeżanie ekranów: 0–86400 s.");
        foreach (var c in Commands ?? [])
            if (c.Name.Length == 0 || c.Path.Length == 0) throw new InvalidDataException("Każde polecenie potrzebuje nazwy i ścieżki.");
        if (IdleAfterSeconds is < 3 or > 86400) throw new InvalidDataException("Czas powrotu ekranu do treści musi mieć 3–86400 s.");
        foreach (var s in IdleScreens ?? [])
        {
            if (s.Screen < 0) throw new InvalidDataException("Numer ekranu nie może być ujemny.");
            if (s.Url.Length > 0 && !(Uri.TryCreate(s.Url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
                throw new InvalidDataException("Strona dla ekranu " + s.Screen + " musi być adresem HTTP/HTTPS.");
            if (s.Url.Length == 0 && s.Path.Length == 0) throw new InvalidDataException("Ekran " + s.Screen + " potrzebuje strony albo aplikacji.");
        }
        if (NewsFolder == null || NewsFolder.Length > 0 && !Path.IsPathFullyQualified(NewsFolder))
            throw new InvalidDataException("Folder komunikatów podaj jako pełną ścieżkę, np. \\\\serwer\\kiosk\\komunikaty albo C:\\ProgramData\\Kiosk\\news.");
        foreach (var help in HelpActions ?? throw new InvalidDataException("Lista Szybkiej pomocy nie może być null."))
            if (help == null || string.IsNullOrWhiteSpace(help.Name) ||
                !(HelpAction.BuiltIn.Contains(help.Action, StringComparer.OrdinalIgnoreCase) ||
                  Uri.TryCreate(help.Action, UriKind.Absolute, out var helpUri) && helpUri.Scheme == Uri.UriSchemeHttps))
                throw new InvalidDataException("Szybka pomoc: każdy przycisk potrzebuje nazwy i akcji https://… albo " + string.Join(", ", HelpAction.BuiltIn) + ".");
        if (NewsSeconds is < 3 or > 300) throw new InvalidDataException("Czas komunikatu: od 3 do 300 sekund.");
        if (BrandTitle == null || BrandSubtitle == null || BrandLogo == null) throw new InvalidDataException("Napis i logo pulpitu nie mogą być null.");
        if (BrandLogo.Length > 0 && (!Path.IsPathFullyQualified(BrandLogo) ||
            !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(Path.GetExtension(BrandLogo).ToLowerInvariant())))
            throw new InvalidDataException("Logo podaj jako pełną ścieżkę do pliku PNG, JPG, BMP lub GIF.");
        if (!KioskTheme.IsColor(AccentColor)) throw new InvalidDataException("Kolor przewodni musi mieć postać #RRGGBB.");
        if (AdminCards == null || AdminCards.Any(a => a == null || string.IsNullOrWhiteSpace(a.Id) || a.Name == null))
            throw new InvalidDataException("Każda karta administratora wymaga identyfikatora.");
        if (AutoConnectRdpName.Length > 0 && !RdpConnections.Any(r => r.Name.Equals(AutoConnectRdpName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Wybierz istniejące połączenie do automatycznego uruchamiania.");
        foreach (var bookmark in Bookmarks)
            if (bookmark == null || string.IsNullOrWhiteSpace(bookmark.Name) || !Configuration.IsWebUrl(bookmark.Url))
                throw new InvalidDataException("Każda zakładka wymaga nazwy i adresu HTTP/HTTPS.");
        if (BrowserAllowedDomains == null || BrowserAllowedDomains.Any(d => string.IsNullOrWhiteSpace(d) ||
            d.Any(char.IsWhiteSpace) || d.Contains('/') || d.Contains(':')))
            throw new InvalidDataException("Dodatkowe domeny podaj jako same nazwy, np. login.microsoftonline.com.");
        if (BrowserOnlyBookmarks && !Bookmarks.Any(b => b.Enabled))
            throw new InvalidDataException("Tryb „tylko strony z zakładek” wymaga co najmniej jednej zakładki.");
        foreach (var app in Applications)
            if (app == null || string.IsNullOrWhiteSpace(app.Name) || app.Arguments == null || app.WorkingDirectory == null)
                throw new InvalidDataException("Aplikacja wymaga nazwy i pełnej ścieżki do pliku EXE.");
            else if (!app.IsBuiltIn && (!Path.IsPathFullyQualified(app.Path) ||
                !string.Equals(Path.GetExtension(app.Path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                app.Arguments == null || app.WorkingDirectory == null ||
                (app.WorkingDirectory.Length > 0 && !Path.IsPathFullyQualified(app.WorkingDirectory))))
                throw new InvalidDataException("Aplikacja wymaga nazwy i pełnej ścieżki do pliku EXE.");
        if (Applications.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Applications.Length)
            throw new InvalidDataException("Nazwy aplikacji muszą być unikalne.");
        if (Applications.Count(a => a.IsBuiltInNotes) > 1) throw new InvalidDataException("Wbudowany Notatnik może być na liście tylko raz.");
        if (Applications.Count(a => a.IsBuiltInBrowser) > 1) throw new InvalidDataException("Wbudowana przeglądarka może być na liście tylko raz.");
    }
}
