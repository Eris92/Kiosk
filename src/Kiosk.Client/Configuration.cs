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
        if (rule.Contains("://"))
        {
            // The same scheme and host exactly (https://portal.firma.pl must not match portal.firma.pl.evil.com),
            // then the path from a "/" boundary: the shared account's password is typed only where it belongs.
            if (!Uri.TryCreate(rule, UriKind.Absolute, out var allowed) || allowed.Scheme != uri.Scheme ||
                !allowed.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) || allowed.Port != uri.Port) return false;
            var path = allowed.AbsolutePath.TrimEnd('/');
            return path.Length == 0 || uri.AbsolutePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
        }
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
    /// <summary>
    /// Punkt samoobsługowy: only for someone signed in with the card (greyed out before). Not set: MFA, password
    /// reset and card reset work without signing in, everything else needs it.
    /// </summary>
    public bool? SignedInOnly { get; init; }
    internal bool NeedsSignIn => SignedInOnly ?? DefaultNeedsSignIn(Action);
    /// <summary>
    /// Szybka pomoc folder: "Konto", or "Konto/Hasło" for a subcategory; empty = straight in the list.
    /// The column shows the folders first, a folder opens its buttons and subfolders.
    /// </summary>
    public string Category { get; init; } = "";
    /// <summary>Punkt samoobsługowy: the line under the tile's name, e.g. "Naruszenie danych osobowych".</summary>
    public string Description { get; init; } = "";
    /// <summary>The tile's icon by name (<see cref="TileIcons.Names"/>); empty = chosen from the action.</summary>
    public string Icon { get; init; } = "";
    /// <summary>The tile's circle colour #RRGGBB; empty = a colour of the Kiosk's set by position.</summary>
    public string Color { get; init; } = "";
    /// <summary>
    /// Opened beside the page, at its right: a second page (https://…) or a file such as a PDF with instructions
    /// (a full path, or a path relative to the configuration's folder). Empty = the page alone.
    /// </summary>
    public string Side { get; init; } = "";
    /// <summary>Set for a category with its own action (Menu → Kategorie): the category's path.</summary>
    internal string? LinkPath { get; init; }
    internal string CategoryPath => string.Join("/", (Category ?? "").Split('/').Select(s => s.Trim()).Where(s => s.Length > 0));

    /// <summary>"Obok": empty, a https://… page or a file path (no other kind of address).</summary>
    internal static bool ValidSide(string? side) =>
        string.IsNullOrWhiteSpace(side) || Uri.TryCreate(side.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsFile)
        || !side.Contains("://") && side.Trim().IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0;

    internal static bool DefaultNeedsSignIn(string action)
    {
        var a = action.Trim().ToLowerInvariant();
        return !(a == ResetCard || a.Contains("mfa") || a.Contains("sspr") || a.Contains("passwordreset") || a.Contains("security-info") || a.Contains("mysignins"));
    }

    internal const string ChangePin = "kiosk:zmien-pin", Reenroll = "kiosk:nowa-karta", ResetCard = "kiosk:reset-karty",
        FirstSignIn = "kiosk:pierwsze-logowanie";
    internal static readonly string[] BuiltIn = [ChangePin, Reenroll, ResetCard, FirstSignIn];

    internal static HelpAction[] Defaults =>
    [
        new() { Name = "SharePoint", Action = "https://www.microsoft365.com/launch/sharepoint", Description = "Strony i dokumenty firmy", Icon = "dokument" },
        new() { Name = "Pierwsze logowanie", Action = FirstSignIn, Category = "Pomoc/Konto", Description = "Profil Windows i logowanie jednokrotne" },
        new() { Name = "Skonfiguruj MFA", Action = "https://aka.ms/mfasetup", Category = "Pomoc/Konto", Description = "Klucz zabezpieczeń i metody logowania" },
        new() { Name = "Resetuj hasło", Action = "https://aka.ms/sspr", Category = "Pomoc/Konto", Description = "Nowe hasło do konta", Icon = "klucz" },
        new() { Name = "Zmień PIN karty", Action = ChangePin, Category = "Pomoc/Karta", Description = "Nowy PIN do logowania kartą" },
        new() { Name = "Skonfiguruj kartę od nowa", Action = Reenroll, Category = "Pomoc/Karta", Description = "Np. po zmianie hasła" },
        new() { Name = "Resetuj kartę", Action = ResetCard, Category = "Pomoc/Karta", Description = "Usuwa PIN i wszystkie klucze z karty" },
    ];
}

/// <summary>
/// Menu → Kategorie: a category of the left menu ("Pomoc") or a subcategory ("Pomoc/Konto"). The list's order is the
/// order of the menu and of the panels; entries pick their category from this list.
/// </summary>
internal sealed record MenuCategory
{
    public string Name { get; init; } = "";
    /// <summary>The category's icon by name (<see cref="TileIcons.Names"/>); empty = chosen from its name or first tile.</summary>
    public string Icon { get; init; } = "";
    /// <summary>
    /// Like an entry of Menu: https://… or kiosk:…; a click on the category opens it at once instead of its tiles
    /// (when it has no entries). Empty = the category only groups entries.
    /// </summary>
    public string Action { get; init; } = "";
    public string Description { get; init; } = "";
    public string Color { get; init; } = "";
    /// <summary>Like an entry of Menu: a second page or a file (PDF) beside the category's page.</summary>
    public string Side { get; init; } = "";
    public bool? SignedInOnly { get; init; }
    /// <summary>Punkt samoobsługowy: in the left menu's second group, under a line (e.g. Szybka pomoc, Zgłoś incydent).</summary>
    public bool Bottom { get; init; }
    internal string Path => Normalize(Name);

    /// <summary>The category with an action as an entry of the menu: in its parent category, and opened from the left menu.</summary>
    internal HelpAction? Link => (Action ?? "").Trim().Length == 0 ? null : new HelpAction
    {
        Name = Path.Split('/')[^1], Action = Action.Trim(), Description = Description ?? "", Icon = Icon ?? "", Color = Color ?? "", Side = Side ?? "",
        SignedInOnly = SignedInOnly, Category = Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "", LinkPath = Path
    };

    internal static string Normalize(string? name) => string.Join("/", (name ?? "").Split('/').Select(s => s.Trim()).Where(s => s.Length > 0));

    /// <summary>The list with every category the entries use (and their parents) added at the end, in the entries' order.</summary>
    internal static MenuCategory[] Complete(MenuCategory[]? categories, IEnumerable<HelpAction>? items)
    {
        var list = (categories ?? []).Where(c => c != null && c.Path.Length > 0).GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First() with { Name = g.Key }).ToList();
        foreach (var path in (items ?? []).Where(i => i != null).Select(i => i.CategoryPath).Where(p => p.Length > 0))
        {
            var parts = path.Split('/');
            for (int n = 1; n <= parts.Length; n++)
            {
                var part = string.Join("/", parts.Take(n));
                if (!list.Any(c => c.Path.Equals(part, StringComparison.OrdinalIgnoreCase))) list.Add(new MenuCategory { Name = part });
            }
        }
        return list.ToArray();
    }

    /// <summary>
    /// The entries in the order the Kiosk shows them: without a category first, then by the category list (a category,
    /// then its subcategories by their own place), and inside one category in the order of the list in Konfiguracja.
    /// </summary>
    internal static HelpAction[] Sort(IEnumerable<HelpAction> items, MenuCategory[] categories)
    {
        int Place(string path)
        {
            if (path.Length == 0) return -1;
            int index = Array.FindIndex(categories, c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }
        int[] Key(HelpAction item)
        {
            var parts = (item.LinkPath ?? item.CategoryPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? [-1] : Enumerable.Range(1, parts.Length).Select(n => Place(string.Join("/", parts.Take(n)))).ToArray();
        }
        var comparer = Comparer<int[]>.Create((a, b) =>
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        });
        return items.Select((item, index) => (item, index)).OrderBy(x => Key(x.item), comparer).ThenBy(x => x.index).Select(x => x.item).ToArray();
    }
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
        config = config with { BrandLogo = Resolve(config.BrandLogo ?? ""), NewsFolder = Resolve(config.NewsFolder ?? ""), Folder = folder };
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
