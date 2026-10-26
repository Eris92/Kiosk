using System.Globalization;

namespace Kiosk.NewsEditor;

/// <summary>Komunikaty (list on the left, the chosen message on the right, a preview) and Statystyki.</summary>
internal sealed class MainForm : Form
{
    // Keep in step with the Kiosk client's picture limits. The editor is a separate program.
    private const int MaxPictureBytes = 8 * 1024 * 1024;
    private const long MaxPicturePixels = 16_000_000;
    private const int PictureWidth = 1920, PictureHeight = 1080;
    private readonly EditorSettings settings;
    private NewsFile file;
    private bool dirty, loading;

    private readonly EntryList list = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Segoe UI", 10) };
    private readonly CheckBox enabled = new() { Text = "Pokazuj na kioskach", AutoSize = true };
    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Items = { "Slajd (na dole pulpitu)", "Pasek (u góry ekranu)" } };
    private readonly TextBox title = new() { Dock = DockStyle.Fill };
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox image = new() { Width = 300 };
    private readonly TextBox video = new() { Width = 300 };
    private readonly CheckBox sound = new() { Text = "Z dźwiękiem", AutoSize = true };
    private readonly TextBox button = new() { Width = 300, PlaceholderText = "napis przycisku (puste = bez przycisku)" };
    private readonly TextBox action = new() { Dock = DockStyle.Fill, PlaceholderText = "https://…, plik z folderu (np. zglos-problem.html), app:Nazwa, rdp:Nazwa, cmd:Nazwa" };
    private readonly DateTimePicker from = Picker();
    private readonly DateTimePicker until = Picker();
    private readonly TextBox color = new() { Width = 110, PlaceholderText = "red / #C62828" };
    private readonly TextBox textColor = new() { Width = 110, PlaceholderText = "white / #FFFFFF" };
    private readonly Panel preview = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 36, 44) };
    private readonly Label folderLabel = new() { AutoSize = true, Margin = new Padding(8, 8, 8, 0) };
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(12, 8, 8, 0), ForeColor = Color.DimGray };
    private readonly Button save = new() { Text = "Zapisz na serwerze", AutoSize = true };
    private readonly TableLayoutPanel fields = new() { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
    private Image? previewImage;

    internal MainForm(EditorSettings settings)
    {
        this.settings = settings;
        file = new NewsFile(settings.NewsFolder);
        Text = "Komunikaty Kiosku";
        Font = new Font("Segoe UI", 9.5f);
        Size = new Size(1180, 780);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var newsTab = new TabPage("Komunikaty");
        tabs.TabPages.Add(newsTab);
        tabs.TabPages.Add(new StatsView(settings) { Text = "Statystyki" });
        Controls.Add(tabs);

        // Left: the messages and what to do with them.
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        listButtons.Controls.AddRange([
            Small("Nowy", AddEntry), Small("Duplikuj", Duplicate), Small("Usuń", Remove),
            Small("▲ Wyżej", () => MoveEntry(-1)), Small("▼ Niżej", () => MoveEntry(1))
        ]);
        left.Controls.Add(list);
        left.Controls.Add(listButtons);
        left.Controls.Add(new Label { Text = "Kolejność na liście = kolejność na kioskach", Dock = DockStyle.Top, Height = 24, ForeColor = Color.DimGray });

        // Right: the chosen message.
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Row("", enabled);
        Row("Rodzaj", kind);
        Row("Tytuł", title);
        Row("Tekst", text);
        Row("Obraz", Line(image, Small("Wybierz…", () => PickMedia(image, "Obrazy|*.png;*.jpg;*.jpeg;*.gif;*.bmp")), Small("Bez obrazu", () => image.Text = "")));
        Row("Film (MP4)", Line(video, Small("Wybierz…", () => PickMedia(video, "Filmy MP4|*.mp4")), Small("Bez filmu", () => video.Text = ""), sound));
        Row("Przycisk", button);
        Row("Akcja", action);
        Row("Start", Line(from, new Label { Text = "zaznacz, by pokazać dopiero od tej chwili", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 6, 0, 0) }));
        Row("Stop", Line(until, new Label { Text = "zaznacz, by ukryć po tej chwili", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 6, 0, 0) }));
        Row("Kolor tła", Line(color, Small("Wybierz…", () => PickColor(color))));
        Row("Kolor tekstu", Line(textColor, Small("Wybierz…", () => PickColor(textColor))));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        fields.Controls.Add(new Label { Text = "Podgląd", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, fields.RowCount);
        fields.Controls.Add(preview, 1, fields.RowCount++);
        preview.Paint += (_, e) => PaintPreview(e.Graphics);
        preview.Resize += (_, _) => preview.Invalidate();

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(fields);
        newsTab.Controls.Add(split);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        bottom.Controls.AddRange([save, Small("Wczytaj ponownie", Reload), Small("Zmień folder…", ChangeFolder), folderLabel, status]);
        newsTab.Controls.Add(bottom);
        save.Click += (_, _) => Save();

        list.SelectedIndexChanged += (_, _) => Show(list.SelectedItem as NewsEntry);
        foreach (Control c in new Control[] { title, text, image, video, button, action, color, textColor }) c.TextChanged += (_, _) => Changed();
        foreach (var c in new[] { enabled, sound }) c.CheckedChanged += (_, _) => Changed();
        kind.SelectedIndexChanged += (_, _) => Changed();
        from.ValueChanged += (_, _) => Changed();
        until.ValueChanged += (_, _) => Changed();
        FormClosing += (_, e) => { if (dirty && !AskSave()) e.Cancel = true; };
        Shown += (_, _) => Reload();
    }

    private static DateTimePicker Picker() =>
        new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", ShowCheckBox = true, Checked = false, Width = 170 };

    private static Button Small(string caption, Action click)
    {
        var b = new Button { Text = caption, AutoSize = true, Margin = new Padding(2) };
        b.Click += (_, _) => click();
        return b;
    }

    private static FlowLayoutPanel Line(params Control[] controls)
    {
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        line.Controls.AddRange(controls);
        return line;
    }

    private void Row(string caption, Control control)
    {
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, fields.RowCount);
        fields.Controls.Add(control, 1, fields.RowCount++);
    }

    // ------------------------------------------------------------------ the file

    private void Reload()
    {
        if (dirty && !AskSave()) return;
        folderLabel.Text = "Folder: " + file.Folder;
        try
        {
            if (!Directory.Exists(file.Folder)) throw new DirectoryNotFoundException("Nie ma folderu " + file.Folder + ". Wybierz go: Zmień folder…");
            file.Load();
            Status(File.Exists(file.Path_) ? "Wczytano " + file.Entries.Count + " komunikatów." : "Brak " + NewsFile.FileName + ": pierwszy zapis go utworzy.");
        }
        catch (Exception ex) { file = new NewsFile(file.Folder); Status("Nie udało się wczytać: " + ex.Message); }
        list.Items.Clear();
        foreach (var entry in file.Entries) list.Items.Add(entry);
        SetDirty(false);
        if (list.Items.Count > 0) list.SelectedIndex = 0; else Show(null);
    }

    private void Save()
    {
        try
        {
            if (file.ChangedElsewhere && MessageBox.Show(this, "Ktoś inny zapisał komunikaty po ich wczytaniu. Zastąpić jego zmiany Twoimi?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            file.Save();
            SetDirty(false);
            Status("Zapisano " + DateTime.Now.ToString("HH:mm:ss") + ". Kioski pobiorą zmiany przy najbliższym odświeżeniu komunikatów (poprzednia wersja: " + NewsFile.FileName + ".bak).");
        }
        catch (Exception ex) { MessageBox.Show(this, "Nie udało się zapisać: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private bool AskSave()
    {
        var answer = MessageBox.Show(this, "Zapisać zmiany w komunikatach?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.Yes) { Save(); return !dirty; }
        return true;
    }

    private void ChangeFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Folder z " + NewsFile.FileName + " (zwykle udział na serwerze)", InitialDirectory = file.Folder, UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dirty && !AskSave()) return;
        dirty = false;
        settings.NewsFolder = dialog.SelectedPath;
        try { settings.Save(); } catch (Exception ex) { Status("Nie zapamiętano folderu: " + ex.Message); }
        file = new NewsFile(dialog.SelectedPath);
        Reload();
    }

    private void SetDirty(bool value)
    {
        dirty = value;
        save.Enabled = value;
        Text = "Komunikaty Kiosku" + (value ? " *" : "");
    }

    private void Status(string message) => status.Text = message;

    // ------------------------------------------------------------------ the list

    private NewsEntry? Current => list.SelectedItem as NewsEntry;

    private void AddEntry() => Insert(new NewsEntry { Title = "Nowy komunikat" });
    private void Duplicate() { if (Current is { } e) Insert(e.Copy()); }

    private void Insert(NewsEntry entry)
    {
        var at = list.SelectedIndex < 0 ? list.Items.Count : list.SelectedIndex + 1;
        file.Entries.Insert(at, entry);
        list.Items.Insert(at, entry);
        list.SelectedIndex = at;
        SetDirty(true);
        title.Focus();
        title.SelectAll();
    }

    private void Remove()
    {
        if (Current is not { } e) return;
        if (MessageBox.Show(this, "Usunąć komunikat „" + e.Title + "”? (Pliki obrazu i filmu zostają w folderze.)", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var at = list.SelectedIndex;
        file.Entries.RemoveAt(at);
        list.Items.RemoveAt(at);
        if (list.Items.Count > 0) list.SelectedIndex = Math.Min(at, list.Items.Count - 1); else Show(null);
        SetDirty(true);
    }

    private void MoveEntry(int by)
    {
        var at = list.SelectedIndex;
        var to = at + by;
        if (at < 0 || to < 0 || to >= list.Items.Count) return;
        var e = file.Entries[at];
        file.Entries.RemoveAt(at);
        file.Entries.Insert(to, e);
        list.Items.RemoveAt(at);
        list.Items.Insert(to, e);
        list.SelectedIndex = to;
        SetDirty(true);
    }

    // ------------------------------------------------------------------ the chosen message

    private void Show(NewsEntry? e)
    {
        loading = true;
        fields.Enabled = e != null;
        e ??= new NewsEntry { Enabled = false };
        enabled.Checked = e.Enabled;
        kind.SelectedIndex = e.IsBar ? 1 : 0;
        title.Text = e.Title;
        text.Text = e.Text;
        image.Text = e.Image;
        video.Text = e.Video;
        sound.Checked = e.Sound;
        button.Text = e.Button;
        action.Text = e.Action;
        SetMoment(from, NewsEntry.Moment(e.From, false));
        SetMoment(until, NewsEntry.Moment(e.Until, true));
        color.Text = e.Color;
        textColor.Text = e.TextColor;
        loading = false;
        LoadPreviewImage(e);
        preview.Invalidate();
    }

    private static void SetMoment(DateTimePicker picker, DateTime? moment)
    {
        picker.Value = moment is { } m && m >= picker.MinDate && m <= picker.MaxDate ? new DateTime(m.Year, m.Month, m.Day, m.Hour, m.Minute, 0) : DateTime.Today.AddHours(DateTime.Now.Hour + 1);
        picker.Checked = moment != null;
    }

    /// <summary>A field changed: into the message, the list line and the preview.</summary>
    private void Changed()
    {
        if (loading || Current is not { } e) return;
        var pictureChanged = e.Image != image.Text.Trim();
        e.Enabled = enabled.Checked;
        e.IsBar = kind.SelectedIndex == 1;
        e.Title = title.Text;
        e.Text = text.Text;
        e.Image = image.Text.Trim();
        e.Video = video.Text.Trim();
        e.Sound = sound.Checked;
        e.Button = button.Text;
        e.Action = action.Text.Trim();
        // Only a moment the editor actually touched is rewritten (a day alone written by hand stays as it is).
        var oldFrom = NewsEntry.Moment(e.From, false);
        var oldUntil = NewsEntry.Moment(e.Until, true);
        if (!from.Checked) e.From = "";
        else if (oldFrom == null || Minute(oldFrom.Value) != Minute(from.Value)) e.From = from.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (!until.Checked) e.Until = "";
        else if (oldUntil == null || Minute(oldUntil.Value) != Minute(until.Value)) e.Until = until.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        e.Color = color.Text.Trim();
        e.TextColor = textColor.Text.Trim();
        list.RefreshEntry(list.SelectedIndex);
        if (pictureChanged) LoadPreviewImage(e);
        preview.Invalidate();
        SetDirty(true);
    }

    private static DateTime Minute(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);

    /// <summary>A picture or film from anywhere: copied into the messages folder (the kiosks read it from there), named by its file name.</summary>
    private void PickMedia(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, Title = "Wybierz plik – zostanie skopiowany do folderu komunikatów" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var source = dialog.FileName;
        var name = Path.GetFileName(source);
        var destination = Path.Combine(file.Folder, name);
        try
        {
            if (target == image) using (ReadPreviewImage(source)) { }
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destination) && MessageBox.Show(this, "W folderze jest już " + name + ". Zastąpić go?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    var stem = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    for (var i = 2; File.Exists(destination); i++) { name = stem + "-" + i + ext; destination = Path.Combine(file.Folder, name); }
                }
                Cursor = Cursors.WaitCursor;
                Status("Kopiuję " + name + "…");
                Refresh();
                File.Copy(source, destination, true);
                Status("Skopiowano " + name + " do folderu komunikatów.");
            }
            target.Text = name;
        }
        catch (Exception ex) { MessageBox.Show(this, "Nie udało się skopiować: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    private void PickColor(TextBox target)
    {
        using var dialog = new ColorDialog { FullOpen = true, Color = ParseColor(target.Text, Color.Firebrick) };
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
    }

    private static Color ParseColor(string text, Color fallback)
    {
        text = text.Trim();
        if (text.Length == 0) return fallback;
        try
        {
            var parts = text.Split(',');
            if (parts.Length == 3) return Color.FromArgb(int.Parse(parts[0].Trim()), int.Parse(parts[1].Trim()), int.Parse(parts[2].Trim()));
            var c = ColorTranslator.FromHtml(text);
            return c.IsEmpty ? fallback : c;
        }
        catch (Exception) { return fallback; }
    }

    // ------------------------------------------------------------------ preview

    private void LoadPreviewImage(NewsEntry e)
    {
        previewImage?.Dispose();
        previewImage = null;
        if (e.Image.Length == 0) return;
        try
        {
            var path = Path.IsPathFullyQualified(e.Image) ? e.Image : Path.Combine(file.Folder, e.Image);
            previewImage = ReadPreviewImage(path);
        }
        catch (Exception ex) { previewImage = null; Status("Nie można wyświetlić obrazu: " + ex.Message); }
    }

    private static Image ReadPreviewImage(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxPictureBytes) throw new InvalidDataException("plik ma ponad 8 MB.");
        using var decoded = Image.FromStream(stream);
        if ((long)decoded.Width * decoded.Height > MaxPicturePixels)
            throw new InvalidDataException("obraz ma za dużą rozdzielczość.");
        var scale = Math.Min(1d, Math.Min((double)PictureWidth / decoded.Width, (double)PictureHeight / decoded.Height));
        if (scale == 1d) return new Bitmap(decoded);
        var result = new Bitmap(Math.Max(1, (int)Math.Round(decoded.Width * scale)), Math.Max(1, (int)Math.Round(decoded.Height * scale)));
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(decoded, new Rectangle(0, 0, result.Width, result.Height));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { previewImage?.Dispose(); previewImage = null; }
        base.Dispose(disposing);
    }

    /// <summary>Roughly how the kiosks show it: a coloured bar across the top, or a slide with its picture.</summary>
    private void PaintPreview(Graphics g)
    {
        g.Clear(preview.BackColor);
        if (Current is not { } e) return;
        var area = preview.ClientRectangle;
        using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 10.5f);
        if (e.IsBar)
        {
            var bar = new Rectangle(area.X, area.Y, area.Width, 56);
            using var back = new SolidBrush(ParseColor(e.Color, Color.FromArgb(198, 40, 40)));
            g.FillRectangle(back, bar);
            var fore = ParseColor(e.TextColor, Color.White);
            var line = e.Title + (e.Text.Length > 0 ? "   " + e.Text : "");
            TextRenderer.DrawText(g, line, titleFont, new Rectangle(bar.X + 16, bar.Y, bar.Width - 32 - (e.Button.Length > 0 ? 150 : 0), bar.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (e.Button.Length > 0)
            {
                var b = new Rectangle(bar.Right - 150, bar.Y + 12, 134, 32);
                using var pen = new Pen(fore, 1.5f);
                g.DrawRectangle(pen, b);
                TextRenderer.DrawText(g, e.Button, bodyFont, b, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            TextRenderer.DrawText(g, "(pasek u góry pulpitu kiosku)", bodyFont, new Rectangle(area.X, bar.Bottom + 12, area.Width, 24), Color.Gray, TextFormatFlags.HorizontalCenter);
            return;
        }
        var slide = Rectangle.Inflate(area, -12, -12);
        var textLeft = slide.X;
        if (previewImage != null)
        {
            var height = slide.Height;
            var width = Math.Min(slide.Width / 2, (int)(height * (double)previewImage.Width / Math.Max(1, previewImage.Height)));
            g.DrawImage(previewImage, new Rectangle(slide.X, slide.Y, width, height));
            textLeft = slide.X + width + 16;
        }
        else if (e.Video.Length > 0)
        {
            var box = new Rectangle(slide.X, slide.Y, Math.Min(slide.Width / 2, slide.Height * 16 / 9), slide.Height);
            using var dark = new SolidBrush(Color.Black);
            g.FillRectangle(dark, box);
            TextRenderer.DrawText(g, "▶ " + e.Video + (e.Sound ? " (z dźwiękiem)" : ""), bodyFont, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            textLeft = box.Right + 16;
        }
        var textArea = new Rectangle(textLeft, slide.Y, slide.Right - textLeft, slide.Height);
        TextRenderer.DrawText(g, e.Title, titleFont, new Rectangle(textArea.X, textArea.Y, textArea.Width, 32), Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, e.Text, bodyFont, new Rectangle(textArea.X, textArea.Y + 38, textArea.Width, textArea.Height - 90), Color.Gainsboro, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (e.Button.Length > 0)
        {
            var b = new Rectangle(textArea.X, textArea.Bottom - 40, 160, 34);
            using var accent = new SolidBrush(Color.FromArgb(0, 120, 212));
            g.FillRectangle(accent, b);
            TextRenderer.DrawText(g, e.Button, bodyFont, b, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A ListBox whose line can be redrawn after its message changed.</summary>
    private sealed class EntryList : ListBox
    {
        internal void RefreshEntry(int index) { if (index >= 0 && index < Items.Count) RefreshItem(index); }
    }
}
