using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiosk.NewsEditor;

/// <summary>One message of kiosk-news.json, as the Kiosk reads it (fields it does not know are kept as they are).</summary>
internal sealed class NewsEntry
{
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Image { get; set; } = "";
    public string Video { get; set; } = "";
    public bool Sound { get; set; }
    public string Button { get; set; } = "";
    public string Action { get; set; } = "";
    public string From { get; set; } = "";
    public string Until { get; set; } = "";
    public string Color { get; set; } = "";
    public string TextColor { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }

    [JsonIgnore] public bool IsBar
    {
        get => Kind.Equals("pasek", StringComparison.OrdinalIgnoreCase) || Kind.Equals("bar", StringComparison.OrdinalIgnoreCase);
        set => Kind = value ? "Pasek" : "";
    }

    internal NewsEntry Copy()
    {
        var copy = (NewsEntry)MemberwiseClone();
        copy.Other = Other == null ? null : new Dictionary<string, JsonElement>(Other);
        return copy;
    }

    private static readonly string[] Formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd"];

    /// <summary>From / Until as the Kiosk reads them: a day alone is its start (From) or its end (Until).</summary>
    internal static DateTime? Moment(string text, bool end)
    {
        if (!DateTime.TryParseExact((text ?? "").Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var moment)) return null;
        return end && text!.Trim().Length == 10 ? moment.Date.AddDays(1).AddTicks(-1) : moment;
    }

    /// <summary>What the kiosks show now: "widoczny", "wyłączony", "od …", "wygasł".</summary>
    internal string State
    {
        get
        {
            if (!Enabled) return "wyłączony";
            if (Moment(From, false) is { } from && DateTime.Now < from) return "od " + from.ToString("dd.MM HH:mm");
            if (Moment(Until, true) is { } until && DateTime.Now > until) return "wygasł";
            return "widoczny";
        }
    }

    public override string ToString() =>
        (IsBar ? "▬ " : "▣ ") + (Title.Length > 0 ? Title : Text.Length > 0 ? Text : Image.Length > 0 ? Image : Video.Length > 0 ? Video : "(bez tytułu)") + "   · " + State;
}

/// <summary>kiosk-news.json in the messages folder: read, and written back with its comments, a .bak copy and a check for others' changes.</summary>
internal sealed class NewsFile
{
    internal const string FileName = "kiosk-news.json";
    internal string Folder { get; }
    internal string Path_ => Path.Combine(Folder, FileName);
    internal List<NewsEntry> Entries { get; private set; } = [];
    private string header = "";
    private DateTime loadedStamp;

    internal NewsFile(string folder) => Folder = folder;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    internal void Load()
    {
        if (!File.Exists(Path_)) { Entries = []; header = DefaultHeader; loadedStamp = DateTime.MinValue; return; }
        var text = File.ReadAllText(Path_);
        // The comment lines above the list (help for whoever opens the file in Notepad) stay.
        var lines = text.Split('\n');
        header = string.Concat(lines.TakeWhile(l => l.TrimStart().StartsWith("//") || l.Trim().Length == 0).Select(l => l.TrimEnd('\r') + "\r\n"));
        Entries = JsonSerializer.Deserialize<List<NewsEntry>>(text, ReadOptions)?.Where(e => e != null).ToList() ?? [];
        loadedStamp = File.GetLastWriteTimeUtc(Path_);
    }

    /// <summary>Someone else saved the file since it was read (another editor, Notepad).</summary>
    internal bool ChangedElsewhere => File.Exists(Path_) && File.GetLastWriteTimeUtc(Path_) != loadedStamp;

    internal void Save()
    {
        var json = new StringBuilder(header.Length > 0 ? header : DefaultHeader);
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartArray();
                foreach (var e in Entries) Write(writer, e);
                writer.WriteEndArray();
            }
            json.Append(Encoding.UTF8.GetString(stream.ToArray()).Replace("\n", "\r\n")).Append("\r\n");
        }
        if (File.Exists(Path_)) File.Copy(Path_, Path_ + ".bak", true);
        var temporary = Path_ + ".tmp";
        File.WriteAllText(temporary, json.ToString(), new UTF8Encoding(false));
        File.Move(temporary, Path_, true);
        loadedStamp = File.GetLastWriteTimeUtc(Path_);
    }

    private static void Write(Utf8JsonWriter writer, NewsEntry e)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("Enabled", e.Enabled);
        void Text(string name, string value) { if (value.Trim().Length > 0) writer.WriteString(name, value.Trim()); }
        Text("Kind", e.Kind);
        Text("Title", e.Title);
        Text("Text", e.Text);
        Text("Image", e.Image);
        Text("Video", e.Video);
        if (e.Sound) writer.WriteBoolean("Sound", true);
        Text("Button", e.Button);
        Text("Action", e.Action);
        Text("From", e.From);
        Text("Until", e.Until);
        Text("Color", e.Color);
        Text("TextColor", e.TextColor);
        if (e.Other != null) foreach (var (name, value) in e.Other) { writer.WritePropertyName(name); value.WriteTo(writer); }
        writer.WriteEndObject();
    }

    private const string DefaultHeader =
        "// Komunikaty Kiosku (edytowane programem KioskKomunikaty). Kind \"Pasek\" = pasek u góry pulpitu, inaczej slajd na dole.\r\n" +
        "// From / Until: \"2026-10-20 16:00\" albo \"2026-10-20\" (cały dzień). Color, TextColor: nazwa (red, green…) albo #RRGGBB.\r\n";
}
