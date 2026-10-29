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
    private bool fileBusy;
    private long editVersion;
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
        Editor.TextChanged += (_, _) => { Dirty = true; editVersion++; };
        open.Click += (_, _) => _ = OpenAsync(open, save);
        save.Click += (_, _) => _ = SaveAsync(open, save);
    }

    private async Task OpenAsync(Button open, Button save)
    {
        if (fileBusy) return;
        if (Dirty && MessageBox.Show(this, "Zastąpić niezapisany tekst?", "Notatnik", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        using var dialog = new OpenFileDialog { Filter = "Pliki tekstowe (*.txt)|*.txt|Wszystkie pliki (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed) return;
        fileBusy = true;
        open.Enabled = save.Enabled = Editor.Enabled = false;
        try
        {
            var content = await File.ReadAllTextAsync(dialog.FileName);
            if (IsDisposed) return;
            Editor.Text = content;
            filename.Text = Path.GetFileName(dialog.FileName);
            Dirty = false;
        }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "Nie można otworzyć pliku"); }
        finally
        {
            fileBusy = false;
            if (!IsDisposed) open.Enabled = save.Enabled = Editor.Enabled = true;
        }
    }

    private async Task SaveAsync(Button open, Button save)
    {
        if (fileBusy) return;
        using var dialog = new SaveFileDialog { Filter = "Pliki tekstowe (*.txt)|*.txt", DefaultExt = "txt", FileName = "Notatka.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed) return;
        var path = dialog.FileName;
        var content = Editor.Text;
        var version = editVersion;
        fileBusy = true;
        open.Enabled = save.Enabled = false;
        try
        {
            await Task.Run(() => WriteText(path, content));
            if (IsDisposed) return;
            filename.Text = Path.GetFileName(path);
            Dirty = editVersion != version;
        }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "Nie można zapisać pliku"); }
        finally
        {
            fileBusy = false;
            if (!IsDisposed) open.Enabled = save.Enabled = true;
        }
    }

    internal void LoadFile(string path)
    {
        Editor.Text = File.ReadAllText(path);
        filename.Text = Path.GetFileName(path);
        Dirty = false;
    }

    internal void SaveFile(string path)
    {
        WriteText(path, Editor.Text);
        filename.Text = Path.GetFileName(path);
        Dirty = false;
    }

    private static void WriteText(string path, string text)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true))
                {
                    writer.Write(text);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
