using System.Text;
using System.Text.Json;
using Kiosk.Common;

namespace Kiosk.NewsEditor;

/// <summary>
/// Statystyki, read only: the files the kiosks copy to StatsFolder (&lt;folder&gt;\&lt;computer&gt;\yyyy-MM.jsonl), all computers together.
/// No names: "Osoby" counts the codes the kiosks make from accounts.
/// </summary>
internal sealed class StatsView : TabPage
{
    private sealed record Entry(DateTimeOffset Time, string Pc, string Kind, string Name, string Category, string Who);

    private readonly EditorSettings settings;
    private readonly ComboBox period = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Items = { "Ostatnie 7 dni", "Ostatnie 30 dni", "Ostatnie 90 dni", "Ostatni rok" } };
    private readonly Label folder = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0), ForeColor = Color.DimGray };
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI", 12, FontStyle.Bold), Padding = new Padding(4, 8, 4, 0) };
    private readonly DataGridView computers = Grid(("Pc", "Komputer", 120), ("SignIns", "Logowania", 80), ("People", "Osoby", 60), ("Opened", "Otwarcia", 70), ("Last", "Ostatnio", 110));
    private readonly DataGridView options = Grid(("Name", "Opcja / komunikat", 220), ("Kind", "Rodzaj", 80), ("Count", "Otwarcia", 70), ("People", "Osoby", 60));
    private readonly Button export = new() { Text = "Eksportuj do Excela (CSV)", AutoSize = true };
    private List<Entry> shown = [];
    private CancellationTokenSource? loading;
    private CancellationTokenSource? exporting;
    private int loadGeneration;

    internal StatsView(EditorSettings settings)
    {
        this.settings = settings;
        Padding = new Padding(8);
        period.SelectedIndex = 1;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var refresh = new Button { Text = "Odśwież", AutoSize = true };
        var choose = new Button { Text = "Zmień folder…", AutoSize = true };
        bar.Controls.AddRange([period, refresh, choose, export, folder]);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        split.Controls.Add(computers, 0, 0);
        split.Controls.Add(options, 1, 0);
        Controls.Add(split);
        Controls.Add(summary);
        Controls.Add(bar);
        refresh.Click += async (_, _) => await LoadAsync();
        period.SelectedIndexChanged += async (_, _) => await LoadAsync();
        choose.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Folder statystyk kiosków (Konfiguracja Kiosku → Statystyki → Eksport na dysk)", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            settings.StatsFolder = dialog.SelectedPath;
            try { settings.Save(); } catch (Exception) { }
            await LoadAsync();
        };
        export.Click += async (_, _) => await ExportAsync();
        Enter += async (_, _) => { if (shown.Count == 0) await LoadAsync(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { loadGeneration++; loading?.Cancel(); exporting?.Cancel(); }
        base.Dispose(disposing);
    }

    private static DataGridView Grid(params (string Name, string Header, int Weight)[] columns)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = SystemColors.Window
        };
        foreach (var (name, header, weight) in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight });
        return grid;
    }

    private async Task LoadAsync()
    {
        if (IsDisposed) return;
        loading?.Cancel();
        loading = null;
        var generation = ++loadGeneration;
        var root = settings.StatsFolder;
        folder.Text = root.Length == 0 ? "Nie ustawiono folderu statystyk (Zmień folder… albo StatsFolder w KioskKomunikaty.json)." : "Folder: " + root;
        if (root.Length == 0)
        {
            shown = [];
            summary.Text = "Brak folderu statystyk.";
            computers.Rows.Clear();
            options.Rows.Clear();
            return;
        }
        var days = new[] { 7, 30, 90, 365 }[Math.Max(0, period.SelectedIndex)];
        var since = DateTimeOffset.Now.AddDays(-days);
        summary.Text = "Wczytuję…";
        shown = [];
        computers.Rows.Clear();
        options.Rows.Clear();
        var currentLoad = new CancellationTokenSource();
        loading = currentLoad;
        List<Entry> read;
        try { read = await Task.Run(() => Read(root, since, currentLoad.Token), currentLoad.Token); }
        catch (OperationCanceledException) when (currentLoad.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (!IsDisposed && generation == loadGeneration) summary.Text = "Nie udało się odczytać: " + ex.Message;
            return;
        }
        finally
        {
            if (ReferenceEquals(loading, currentLoad)) loading = null;
            currentLoad.Dispose();
        }
        if (IsDisposed || generation != loadGeneration) return;
        shown = read;
        var signIns = shown.Where(e => e.Kind == "logowanie").ToList();
        summary.Text = "Logowania: " + signIns.Count + "   Osoby: " + signIns.Where(e => e.Who.Length > 0).Select(e => e.Who).Distinct().Count() +
                       "   Otwarcia: " + shown.Count(e => e.Kind != "logowanie") + "   Komputery: " + shown.Select(e => e.Pc).Distinct().Count();
        computers.Rows.Clear();
        foreach (var pc in shown.GroupBy(e => e.Pc).OrderBy(g => g.Key))
            computers.Rows.Add(pc.Key, pc.Count(e => e.Kind == "logowanie"), pc.Where(e => e.Who.Length > 0).Select(e => e.Who).Distinct().Count(),
                pc.Count(e => e.Kind != "logowanie"), pc.Max(e => e.Time).LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        options.Rows.Clear();
        foreach (var option in shown.Where(e => e.Kind != "logowanie").GroupBy(e => (e.Name, e.Kind)).OrderByDescending(g => g.Count()).Take(200))
            options.Rows.Add(option.Key.Name, option.Key.Kind, option.Count(), option.Where(e => e.Who.Length > 0).Select(e => e.Who).Distinct().Count());
    }

    private static List<Entry> Read(string root, DateTimeOffset since, CancellationToken cancellation)
    {
        var list = new List<Entry>();
        cancellation.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Nie ma folderu " + root + ".");
        var first = since.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var enumeration = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        void ReadFolder(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "????-??.jsonl", enumeration))
            {
                cancellation.ThrowIfCancellationRequested();
                var month = Path.GetFileNameWithoutExtension(file);
                if (month.Length != 7 || month[4] != '-' ||
                    !char.IsAsciiDigit(month[0]) || !char.IsAsciiDigit(month[1]) ||
                    !char.IsAsciiDigit(month[2]) || !char.IsAsciiDigit(month[3]) ||
                    !char.IsAsciiDigit(month[5]) || !char.IsAsciiDigit(month[6]) ||
                    !int.TryParse(month.AsSpan(0, 4), out var year) || year < 1 ||
                    !int.TryParse(month.AsSpan(5, 2), out var number) || number is < 1 or > 12 ||
                    string.CompareOrdinal(month, first) < 0) continue;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var lines = new BoundedLineReader(reader, 65536);
                    while (true)
                    {
                        string? line;
                        try { line = lines.ReadLine(cancellation); }
                        catch (InvalidDataException) { continue; }
                        if (line == null) break;
                        cancellation.ThrowIfCancellationRequested();
                        if (line.Length == 0) continue;
                        try
                        {
                            using var json = JsonDocument.Parse(line);
                            var r = json.RootElement;
                            if (r.ValueKind != JsonValueKind.Object) continue;
                            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                            if (!r.TryGetProperty("Time", out var t) || t.ValueKind != JsonValueKind.String ||
                                !t.TryGetDateTimeOffset(out var time) || time < since) continue;
                            list.Add(new Entry(time, S("Pc"), S("Kind"), S("Name"), S("Category"), S("Who")));
                        }
                        catch (JsonException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        ReadFolder(root);
        foreach (var computer in Directory.EnumerateDirectories(root, "*", enumeration))
        {
            cancellation.ThrowIfCancellationRequested();
            ReadFolder(computer);
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    private async Task ExportAsync()
    {
        if (exporting != null || IsDisposed) return;
        if (shown.Count == 0) { MessageBox.Show(this, "Brak danych do eksportu.", "Statystyki"); return; }
        using var dialog = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "kiosk-statystyki-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var entries = shown; // Read replaces this list; it never changes a completed snapshot.
        var target = dialog.FileName;
        using var stop = new CancellationTokenSource();
        exporting = stop;
        export.Enabled = false;
        export.Text = "Eksportowanie…";
        try
        {
            await Task.Run(() => SaveCsv(target, entries, stop.Token), stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, "Nie udało się zapisać: " + ex.Message, "Statystyki"); }
        finally
        {
            if (ReferenceEquals(exporting, stop)) exporting = null;
            if (!IsDisposed) { export.Enabled = true; export.Text = "Eksportuj do Excela (CSV)"; }
        }
    }

    private static void SaveCsv(string target, IEnumerable<Entry> entries, CancellationToken cancellation)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new StreamWriter(file, new UTF8Encoding(true))) WriteCsv(writer, entries, cancellation);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static void WriteCsv(TextWriter writer, IEnumerable<Entry> entries, CancellationToken cancellation)
    {
        static string Cell(string s)
        {
            // Excel evaluates formulas inside quoted CSV fields. A tab at the start keeps the field as text.
            var first = 0;
            while (first < s.Length && (char.IsWhiteSpace(s[first]) || char.IsControl(s[first]) || s[first] == '\uFEFF')) first++;
            if ((s.Length > 0 && s[0] is '\t' or '\r' or '\n' or '\0') ||
                (first < s.Length && s[first] is '=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠')) s = "\t" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        writer.Write("Czas;Komputer;Rodzaj;Nazwa;Kategoria;Osoba\r\n");
        foreach (var e in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            writer.Write(Cell(e.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")));
            writer.Write(';'); writer.Write(Cell(e.Pc)); writer.Write(';'); writer.Write(Cell(e.Kind));
            writer.Write(';'); writer.Write(Cell(e.Name)); writer.Write(';'); writer.Write(Cell(e.Category));
            writer.Write(';'); writer.Write(Cell(e.Who)); writer.Write("\r\n");
        }
    }

#if KIOSK_SELF_TEST
    internal static void SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "Kiosk.NewsEditor.Stats-" + Guid.NewGuid().ToString("N"));
        var computer = Path.Combine(root, "PC1");
        var deeper = Path.Combine(computer, "archive");
        Directory.CreateDirectory(deeper);
        try
        {
            var time = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
            var line = JsonSerializer.Serialize(new { Time = time, Pc = "PC1", Kind = "opcja", Name = "Przykład", Category = "Pomoc", Who = "abc" });
            var earlier = JsonSerializer.Serialize(new { Time = time.AddHours(-1), Pc = "PC1", Kind = "opcja", Name = "Wcześniej", Category = "Pomoc", Who = "abc" });
            File.WriteAllText(Path.Combine(root, "2026-10.jsonl"), line + "\n{\"Time\":42}\n[]\n");
            File.WriteAllText(Path.Combine(computer, "2026-10.jsonl"), earlier);
            File.WriteAllText(Path.Combine(deeper, "2026-10.jsonl"), line);
            File.WriteAllText(Path.Combine(computer, "2026-09.jsonl"), line);
            var read = Read(root, time.AddDays(-1), CancellationToken.None);
            if (read.Count != 2 || read[0].Time != time.AddHours(-1) || read[1].Time != time)
                throw new InvalidOperationException("Statistics read escaped the month or one-folder boundary.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { Read(root, time.AddDays(-1), cancelled.Token); throw new InvalidOperationException("Statistics read ignored cancellation."); }
            catch (OperationCanceledException) { }
            using var csvWriter = new StringWriter();
            WriteCsv(csvWriter, [new Entry(time, "PC1", "opcja", "=1+1", " \r+2", "＠SUM")], CancellationToken.None);
            var csv = csvWriter.ToString();
            if (!csv.Contains("\"\t=1+1\"", StringComparison.Ordinal) ||
                !csv.Contains("\"\t \r+2\"", StringComparison.Ordinal) ||
                !csv.Contains("\"\t＠SUM\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Statistics CSV accepted a spreadsheet formula.");
            using var cancelledCsv = new StringWriter();
            try { WriteCsv(cancelledCsv, read, cancelled.Token); throw new InvalidOperationException("Statistics CSV ignored cancellation."); }
            catch (OperationCanceledException) { }
            var exported = Path.Combine(root, "export.csv");
            SaveCsv(exported, read, CancellationToken.None);
            var saved = File.ReadAllText(exported);
            var earlierPosition = saved.IndexOf("Wcześniej", StringComparison.Ordinal);
            var laterPosition = saved.IndexOf("Przykład", StringComparison.Ordinal);
            if (!saved.StartsWith("Czas;Komputer;Rodzaj;Nazwa;Kategoria;Osoba\r\n", StringComparison.Ordinal) ||
                earlierPosition < 0 || laterPosition < 0 || earlierPosition > laterPosition)
                throw new InvalidOperationException("Statistics CSV did not preserve chronological order.");
            File.WriteAllText(exported, "previous export");
            try { SaveCsv(exported, read, cancelled.Token); throw new InvalidOperationException("Cancelled statistics CSV replaced the previous export."); }
            catch (OperationCanceledException) { }
            if (File.ReadAllText(exported) != "previous export" || Directory.EnumerateFiles(root, "export.csv.*.tmp").Any())
                throw new InvalidOperationException("Cancelled statistics CSV damaged the previous export or left a temporary file.");
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("Kiosk.NewsEditor.Stats-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
    }
#endif
}
