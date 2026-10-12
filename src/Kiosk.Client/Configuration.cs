using System.Text.Json;

namespace Kiosk.Client;

internal sealed record Bookmark
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
}

internal sealed record ApplicationEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";

    /// <summary>Path of the built-in Kiosk notepad; listed like any other application so it can be removed.</summary>
    internal const string BuiltInNotes = "kiosk:notatnik";
    internal static ApplicationEntry Notes => new() { Name = "Notatnik", Path = BuiltInNotes };
    internal bool IsBuiltInNotes => string.Equals(Path, BuiltInNotes, StringComparison.OrdinalIgnoreCase);

    /// <summary>Path of the built-in Kiosk browser (Edge); removing the entry turns the browser off.</summary>
    internal const string BuiltInBrowser = "kiosk:przegladarka";
    internal static ApplicationEntry Browser => new() { Name = "Przeglądarka", Path = BuiltInBrowser };
    internal bool IsBuiltInBrowser => string.Equals(Path, BuiltInBrowser, StringComparison.OrdinalIgnoreCase);
    internal bool IsBuiltIn => IsBuiltInNotes || IsBuiltInBrowser;
}

/// <summary>
/// Szybka pomoc: a button on the desktop that works locked and unlocked. Action: https://… (a guest browser window when
/// nobody is signed in), kiosk:zmien-pin, kiosk:nowa-karta (set the card up again), kiosk:reset-karty.
/// </summary>
internal sealed record HelpAction
{
    public string Name { get; init; } = "";
    public string Action { get; init; } = "";

    internal const string ChangePin = "kiosk:zmien-pin", Reenroll = "kiosk:nowa-karta", ResetCard = "kiosk:reset-karty";
    internal static readonly string[] BuiltIn = [ChangePin, Reenroll, ResetCard];

    internal static HelpAction[] Defaults =>
    [
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
        config = config.Migrate(); config.Validate(); return config;
    }
    internal static bool IsWebUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal const string SaveArgument = "--save-config";

    /// <summary>
    /// Saves the configuration. The shared configuration folder is read-only for users (Install-KioskShell.ps1), so in
    /// a person's own Windows session Windows asks for an administrator, and only that one copy step runs elevated.
    /// </summary>
    internal static void Save(string path, Config config)
    {
        config.Validate();
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        try { Replace(path, json); return; }
        catch (UnauthorizedAccessException) { }
        var staged = Path.Combine(Path.GetTempPath(), "Kiosk-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(staged, json);
        try
        {
            using var copy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
                Arguments = "\"" + SaveArgument + "\" \"" + staged + "\" \"" + path + "\""
            })!;
            copy.WaitForExit();
            if (copy.ExitCode != 0) throw new UnauthorizedAccessException("Administrator nie zapisał konfiguracji (kod " + copy.ExitCode + ").");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new UnauthorizedAccessException("Konfigurację zapisuje administrator Windows tego komputera. Zapis anulowany.");
        }
        finally { File.Delete(staged); }
    }

    /// <summary>Kiosk.Client.exe --save-config &lt;staged file&gt; &lt;configuration&gt; (elevated): validates and copies.</summary>
    internal static int SaveElevated(string staged, string path)
    {
        try
        {
            Load(staged);   // validates
            Replace(path, File.ReadAllText(staged));
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
