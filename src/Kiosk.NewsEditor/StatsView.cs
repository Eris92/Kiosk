using System.Text;
using System.Text.Json;

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
    private List<Entry> shown = [];

    internal StatsView(EditorSettings settings)
    {
        this.settings = settings;
        Padding = new Padding(8);
        period.SelectedIndex = 1;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var refresh = new Button { Text = "Odśwież", AutoSize = true };
        var choose = new Button { Text = "Zmień folder…", AutoSize = true };
        var export = new Button { Text = "Eksportuj do Excela (CSV)", AutoSize = true };
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
        export.Click += (_, _) => Export();
        Enter += async (_, _) => { if (shown.Count == 0) await LoadAsync(); };
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
        var root = settings.StatsFolder;
        folder.Text = root.Length == 0 ? "Nie ustawiono folderu statystyk (Zmień folder… albo StatsFolder w KioskKomunikaty.json)." : "Folder: " + root;
        if (root.Length == 0) return;
        var days = new[] { 7, 30, 90, 365 }[Math.Max(0, period.SelectedIndex)];
        var since = DateTimeOffset.Now.AddDays(-days);
        summary.Text = "Wczytuję…";
        try { shown = await Task.Run(() => Read(root, since)); }
        catch (Exception ex) { summary.Text = "Nie udało się odczytać: " + ex.Message; return; }
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

    private static List<Entry> Read(string root, DateTimeOffset since)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Nie ma folderu " + root + ".");
        var first = since.ToString("yyyy-MM");
        foreach (var file in Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            var month = Path.GetFileNameWithoutExtension(file);
            if (month.Length >= 7 && string.CompareOrdinal(month[^7..], first) < 0) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        var r = json.RootElement;
                        string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        if (!r.TryGetProperty("Time", out var t) || !t.TryGetDateTimeOffset(out var time) || time < since) continue;
                        list.Add(new Entry(time, S("Pc"), S("Kind"), S("Name"), S("Category"), S("Who")));
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    private void Export()
    {
        if (shown.Count == 0) { MessageBox.Show(this, "Brak danych do eksportu.", "Statystyki"); return; }
        using var dialog = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "kiosk-statystyki-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        static string Cell(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var text = new StringBuilder("Czas;Komputer;Rodzaj;Nazwa;Kategoria;Osoba\r\n");
        foreach (var e in shown.OrderBy(e => e.Time))
            text.Append(Cell(e.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(';').Append(Cell(e.Pc)).Append(';').Append(Cell(e.Kind)).Append(';')
                .Append(Cell(e.Name)).Append(';').Append(Cell(e.Category)).Append(';').Append(Cell(e.Who)).Append("\r\n");
        try { File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { MessageBox.Show(this, "Nie udało się zapisać: " + ex.Message, "Statystyki"); }
    }
}
