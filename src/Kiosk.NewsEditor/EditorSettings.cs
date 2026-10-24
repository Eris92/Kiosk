using System.Text.Json;

namespace Kiosk.NewsEditor;

/// <summary>
/// KioskKomunikaty.json: next to the program (prepared by the administrator), and the user's own changes in
/// %APPDATA%\Kiosk\KioskKomunikaty.json, which win where they are set. The program never needs to write its own folder.
/// It is meant to sit in the messages folder itself (next to kiosk-news.json, like the example pictures): with no folder
/// set, that is the folder it edits.
/// </summary>
internal sealed class EditorSettings
{
    public string NewsFolder { get; set; } = "";
    public string StatsFolder { get; set; } = "";

    private const string FileName = "KioskKomunikaty.json";
    private static string Shared => Path.Combine(AppContext.BaseDirectory, FileName);
    private static string Personal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kiosk", FileName);
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    internal static EditorSettings Load()
    {
        var settings = From(Shared) ?? new EditorSettings();
        if (From(Personal) is { } mine)
        {
            if (mine.NewsFolder.Trim().Length > 0) settings.NewsFolder = mine.NewsFolder;
            if (mine.StatsFolder.Trim().Length > 0) settings.StatsFolder = mine.StatsFolder;
        }
        settings.NewsFolder = settings.NewsFolder.Trim().Trim('"');
        settings.StatsFolder = settings.StatsFolder.Trim().Trim('"');
        if (settings.NewsFolder.Length == 0) settings.NewsFolder = AppContext.BaseDirectory.TrimEnd('\\');
        return settings;
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Personal)!);
        File.WriteAllText(Personal, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static EditorSettings? From(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path), Read) : null; }
        catch (Exception) { return null; }
    }
}
