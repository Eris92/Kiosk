using System.Globalization;
using System.Security.Cryptography;
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
    private string? loadedFingerprint;

    internal NewsFile(string folder) => Folder = folder;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    internal void Load()
    {
        if (!File.Exists(Path_)) { Entries = []; header = DefaultHeader; loadedFingerprint = null; return; }
        var bytes = File.ReadAllBytes(Path_);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        // The comment lines above the list (help for whoever opens the file in Notepad) stay.
        var lines = text.Split('\n');
        header = string.Concat(lines.TakeWhile(l => l.TrimStart().StartsWith("//") || l.Trim().Length == 0).Select(l => l.TrimEnd('\r') + "\r\n"));
        Entries = JsonSerializer.Deserialize<List<NewsEntry>>(text, ReadOptions)?.Where(e => e != null).ToList() ?? [];
        loadedFingerprint = Fingerprint(bytes);
    }

    /// <summary>Someone else saved the file since it was read (another editor, Notepad).</summary>
    internal bool ChangedElsewhere(string? currentFingerprint) => currentFingerprint != loadedFingerprint;

    internal string? CurrentFingerprint() => File.Exists(Path_) ? Fingerprint(File.ReadAllBytes(Path_)) : null;

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal void Save(string? expectedFingerprint)
    {
        // Other editor instances serialize their final check and rename through this sidecar lock.
        using var editLock = new FileStream(Path_ + ".edit.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (CurrentFingerprint() != expectedFingerprint)
            throw new InvalidOperationException("Komunikaty zmieniły się na dysku. Wczytaj je ponownie przed zapisem.");
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
        var data = new UTF8Encoding(false).GetBytes(json.ToString());
        var temporary = Path_ + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = Path_ + ".bak." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            // External editors need not use our lock. Recheck immediately before publishing.
            if (CurrentFingerprint() != expectedFingerprint)
                throw new InvalidOperationException("Komunikaty zmieniły się podczas zapisu. Wczytaj je ponownie.");
            if (File.Exists(Path_))
            {
                File.Copy(Path_, backup);
                File.Move(backup, Path_ + ".bak", overwrite: true);
            }
            File.Move(temporary, Path_, overwrite: true);
            loadedFingerprint = Fingerprint(data);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(backup)) File.Delete(backup);
        }
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

#if KIOSK_SELF_TEST
    internal static void SelfTest()
    {
        var folder = Path.Combine(Path.GetTempPath(), "KioskNewsFileTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = new NewsFile(folder);
            first.Load();
            first.Entries.Add(new NewsEntry { Title = "first" });
            first.Save(null);
            var second = new NewsFile(folder);
            second.Load();
            var before = first.CurrentFingerprint();
            var stamp = File.GetLastWriteTimeUtc(first.Path_);
            second.Entries[0].Title = "second";
            second.Save(second.CurrentFingerprint());
            File.SetLastWriteTimeUtc(first.Path_, stamp); // A timestamp-only check would miss this edit.
            if (!first.ChangedElsewhere(first.CurrentFingerprint()))
                throw new InvalidOperationException("A change with a reused timestamp was not detected.");
            try { first.Save(before); throw new InvalidOperationException("Stale editor overwrote newer messages."); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Komunikaty zmieniły", StringComparison.Ordinal)) { }
            if (!File.ReadAllText(first.Path_).Contains("second", StringComparison.Ordinal) ||
                !File.ReadAllText(first.Path_ + ".bak").Contains("first", StringComparison.Ordinal) ||
                Directory.EnumerateFiles(folder, "*.tmp").Any())
                throw new InvalidOperationException("Concurrent save damaged the current file, backup or temporary files.");
            using (var held = new FileStream(first.Path_ + ".edit.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { first.Save(first.CurrentFingerprint()); throw new InvalidOperationException("Another editor's lock was ignored."); }
                catch (IOException) { }
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
#endif

    private const string DefaultHeader =
        "// Komunikaty Kiosku (edytowane programem KioskKomunikaty). Kind \"Pasek\" = pasek u góry pulpitu, inaczej slajd na dole.\r\n" +
        "// From / Until: \"2026-10-20 16:00\" albo \"2026-10-20\" (cały dzień). Color, TextColor: nazwa (red, green…) albo #RRGGBB.\r\n";
}
