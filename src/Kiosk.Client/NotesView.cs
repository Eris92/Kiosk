using System.Text;

namespace Kiosk.Client;

internal sealed class NotesView : UserControl
{
    internal TextBox Editor { get; } = new()
    {
        Dock = DockStyle.Fill, Multiline = true, AcceptsTab = true, AcceptsReturn = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 12),
        BorderStyle = BorderStyle.None, BackColor = KioskTheme.Surface, ForeColor = KioskTheme.Text
    };
    internal bool Dirty { get; private set; }
    private readonly Label filename = new()
    {
        AutoSize = true, Text = "Nowa notatka", Padding = new Padding(8, 10, 8, 0), ForeColor = KioskTheme.Muted, Font = new Font("Segoe UI", 10)
    };

    internal NotesView()
    {
        Dock = DockStyle.Fill;
        BackColor = KioskTheme.Background; ForeColor = KioskTheme.Text;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(8, 9, 8, 0), BackColor = KioskTheme.Taskbar };
        var open = new Button { Text = "Otwórz…", Size = new Size(120, 36) };
        var save = new Button { Text = "Zapisz jako…", Size = new Size(140, 36) };
        KioskTheme.StyleButton(open);
        KioskTheme.StyleButton(save, primary: true);
        toolbar.Controls.Add(open); toolbar.Controls.Add(save); toolbar.Controls.Add(filename);
        var notice = new Label
        {
            Dock = DockStyle.Bottom, Height = 28, Text = "Zapisz plik przed wylogowaniem. Zamknięcie sesji usuwa niezapisane notatki.",
            Padding = new Padding(10, 6, 10, 0), BackColor = KioskTheme.Taskbar, ForeColor = KioskTheme.Muted
        };
        Controls.Add(Editor); Controls.Add(notice); Controls.Add(toolbar);
        Editor.TextChanged += (_, _) => Dirty = true;
        open.Click += (_, _) => Open();
        save.Click += (_, _) => Save();
    }

    private void Open()
    {
        if (Dirty && MessageBox.Show(this, "Zastąpić niezapisany tekst?", "Notatnik", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        using var dialog = new OpenFileDialog { Filter = "Pliki tekstowe (*.txt)|*.txt|Wszystkie pliki (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed) return;
        try { LoadFile(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nie można otworzyć pliku"); }
    }

    private void Save()
    {
        using var dialog = new SaveFileDialog { Filter = "Pliki tekstowe (*.txt)|*.txt", DefaultExt = "txt", FileName = "Notatka.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed) return;
        try { SaveFile(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nie można zapisać pliku"); }
    }

    internal void LoadFile(string path)
    {
        Editor.Text = File.ReadAllText(path);
        filename.Text = Path.GetFileName(path);
        Dirty = false;
    }

    internal void SaveFile(string path)
    {
        File.WriteAllText(path, Editor.Text, new UTF8Encoding(false));
        filename.Text = Path.GetFileName(path);
        Dirty = false;
    }
}
