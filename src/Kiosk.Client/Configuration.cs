using System.Text.Json;

namespace Kiosk.Client;

internal sealed record Bookmark
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;
}

internal sealed record ApplicationEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    /// <summary>A shared account (accounts.kiosk) for the network, like "runas /netonly": the program runs on this PC as
    /// usual and opens shares and servers (e.g. its own EXE on a file server) as that account. Empty = as usual.</summary>
    public string RunAs { get; init; } = "";
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Path of the built-in Kiosk notepad; listed like any other application so it can be removed.</summary>
    internal const string BuiltInNotes = "kiosk:notatnik";
    internal static ApplicationEntry Notes => new() { Name = "Notatnik", Path = BuiltInNotes };
    internal bool IsBuiltInNotes => string.Equals(Path, BuiltInNotes, StringComparison.OrdinalIgnoreCase);

    /// <summary>Path of the built-in Kiosk browser (Edge); removing the entry turns the browser off.</summary>
    internal const string BuiltInBrowser = "kiosk:przegladarka";
    internal static ApplicationEntry Browser => new() { Name = "Przeglądarka", Path = BuiltInBrowser };
    internal bool IsBuiltInBrowser => string.Equals(Path, BuiltInBrowser, StringComparison.OrdinalIgnoreCase);
    /// <summary>Path of the built-in file manager (Pliki); Explorer itself cannot run inside the Kiosk.</summary>
    internal const string BuiltInFiles = "kiosk:pliki";
    internal static ApplicationEntry Files => new() { Name = "Pliki", Path = BuiltInFiles };
    internal bool IsBuiltInFiles => string.Equals(Path, BuiltInFiles, StringComparison.OrdinalIgnoreCase);
    internal bool IsBuiltIn => IsBuiltInNotes || IsBuiltInBrowser || IsBuiltInFiles;
}

/// <summary>
/// What the Kiosk does on a page by itself (start pages, screens, the Kiosk browser): clicks buttons by their text
/// (e.g. accepting cookies) and signs in with a shared account (accounts.kiosk): login, password, the sign-in button.
/// </summary>
internal sealed record PageAction
{
    /// <summary>The start of the address (https://portal.firma.pl/login) or just a host (portal.firma.pl).</summary>
    public string Url { get; init; } = "";
    /// <summary>Texts of buttons or links to click when they appear, separated by ";" (e.g. "Akceptuj wszystkie;Accept all").</summary>
    public string Click { get; init; } = "";
    /// <summary>A shared account (accounts.kiosk) whose login and password are filled in. Empty = no sign-in.</summary>
    public string Account { get; init; } = "";
    /// <summary>Text of the sign-in button; empty = the form's own submit button.</summary>
    public string Submit { get; init; } = "";
    /// <summary>Wszyscy, Kiosk (screens, lock screen, Szybka pomoc: nobody signed in) or Użytkownicy (a person's browser).</summary>
    public PageScope Scope { get; init; }
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Built-in actions only: each text is clicked once per page (a click that opens a Windows window).</summary>
    internal bool Once { get; init; }

    internal bool Matches(string url)
    {
        if (Url.Trim().Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var rule = Url.Trim();
        if (rule.Contains("://")) return url.StartsWith(rule, StringComparison.OrdinalIgnoreCase);
        rule = rule.TrimEnd('/').ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        return host == rule || host.EndsWith("." + rule, StringComparison.Ordinal);
    }
}

/// <summary>
/// Szybka pomoc: a button on the desktop that works locked and unlocked. Action: https://… (a guest browser window when
/// nobody is signed in), kiosk:zmien-pin, kiosk:nowa-karta (set the card up again), kiosk:reset-karty,
/// kiosk:pierwsze-logowanie (one Windows sign-in for the profile and SSO, then back to the Kiosk).
/// </summary>
internal sealed record HelpAction
{
    public string Name { get; init; } = "";
    public string Action { get; init; } = "";
    /// <summary>Unticked in Konfiguracja: stays on the list, but Kiosk does not use it.</summary>
    public bool Enabled { get; init; } = true;

    internal const string ChangePin = "kiosk:zmien-pin", Reenroll = "kiosk:nowa-karta", ResetCard = "kiosk:reset-karty",
        FirstSignIn = "kiosk:pierwsze-logowanie";
    internal static readonly string[] BuiltIn = [ChangePin, Reenroll, ResetCard, FirstSignIn];

    internal static HelpAction[] Defaults =>
    [
        new() { Name = "Pierwsze logowanie", Action = FirstSignIn },
        new() { Name = "Skonfiguruj MFA", Action = "https://aka.ms/mfasetup" },
        new() { Name = "Resetuj hasło", Action = "https://aka.ms/sspr" },
        new() { Name = "Zmień PIN karty", Action = ChangePin },
        new() { Name = "Skonfiguruj kartę od nowa", Action = Reenroll },
        new() { Name = "Resetuj kartę", Action = ResetCard },
    ];
}

internal static class Configuration
{
    internal static Config Load(string path)
    {
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), new JsonSerializerOptions
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Konfiguracja jest pusta.");
        config = config.Migrate();
        // Logo and messages folder may be given relative to the configuration's folder (Konfiguracja z serwera).
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Resolve(string value) => value.Length > 0 && !Path.IsPathFullyQualified(value) ? Path.GetFullPath(Path.Combine(folder, value)) : value;
        config = config with { BrandLogo = Resolve(config.BrandLogo ?? ""), NewsFolder = Resolve(config.NewsFolder ?? "") };
        config.Validate(); return config;
    }
    internal static bool IsWebUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal const string SaveArgument = "--save-config";

    /// <summary>
    /// Saves the configuration. The shared configuration folder is read-only for users (Install-KioskShell.ps1), so in
    /// a person's own Windows session Windows asks for an administrator, and only that one copy step runs elevated.
    /// </summary>
    /// <param name="secrets">The share's account and the shared accounts' key to save or remove (null = unchanged), in the same step.</param>
    internal static void Save(string path, Config config, CentralConfig.Secrets? secrets = null)
    {
        config.Validate();
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Replace(path, json);
            if (secrets is { Any: true }) CentralConfig.WriteSecrets(secrets);
            return;
        }
        catch (UnauthorizedAccessException) { }
        var staged = Path.Combine(Path.GetTempPath(), "Kiosk-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(staged, json);
        var stagedAccount = secrets is { Any: true } ? CentralConfig.StageSecrets(secrets) : null;
        try
        {
            using var copy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
                Arguments = "\"" + SaveArgument + "\" \"" + staged + "\" \"" + path + "\"" + (stagedAccount == null ? "" : " \"" + stagedAccount + "\"")
            })!;
            copy.WaitForExit();
            if (copy.ExitCode != 0) throw new UnauthorizedAccessException("Administrator nie zapisał konfiguracji (kod " + copy.ExitCode + ").");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new UnauthorizedAccessException("Konfigurację zapisuje administrator Windows tego komputera. Zapis anulowany.");
        }
        finally { File.Delete(staged); if (stagedAccount != null) File.Delete(stagedAccount); }
    }

    /// <summary>Kiosk.Client.exe --save-config &lt;staged file&gt; &lt;configuration&gt; [&lt;staged account&gt;] (elevated): validates and copies.</summary>
    internal static int SaveElevated(string staged, string path, string? stagedAccount = null)
    {
        try
        {
            Load(staged);   // validates
            Replace(path, File.ReadAllText(staged));
            if (stagedAccount != null) CentralConfig.WriteSecrets(CentralConfig.ReadStaged(stagedAccount));
            return 0;
        }
        catch (Exception ex) { Audit.Write("settings_save_failed", ex.Message); return 2; }
    }

    private static void Replace(string path, string json)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
