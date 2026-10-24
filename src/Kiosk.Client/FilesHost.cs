namespace Kiosk.Client;

/// <summary>
/// Pliki of a person signed in with the card alone: this program started again as that person (like the remote
/// desktop, see <see cref="RdpUserHost"/>), showing only Pliki. It already runs as them, so their folders open with
/// their own rights; the Kiosk embeds this window like any program. A file opens in its program, also as them.
/// </summary>
internal static class FilesHost
{
    internal const string Argument = "--files-host";

    internal static int Run()
    {
        using var form = new Form
        {
            Text = "Pliki", FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized,
            BackColor = KioskTheme.Background, StartPosition = FormStartPosition.Manual
        };
        var files = new FilesView(work => work(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        files.OpenRequested += path =>
        {
            var command = FileAssociation.Command(path);
            if (command == null)
            {
                MessageBox.Show(form, FileAssociation.IsProgram(path) ? "Z Plików nie można uruchamiać programów ani skryptów." :
                    "Brak programu do otwarcia „" + Path.GetFileName(path) + "” na tym komputerze.", "Pliki");
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command.Value.Program, command.Value.Arguments) { UseShellExecute = false }); }
            catch (Exception ex) { MessageBox.Show(form, "Nie udało się otworzyć pliku: " + ex.Message, "Pliki"); }
        };
        form.Controls.Add(files);
        Application.Run(form);
        return 0;
    }
}
