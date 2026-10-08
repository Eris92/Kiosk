namespace Kiosk.Client;

/// <summary>The card PIN typed in Kiosk (FIDO2 cards). The text is handed to the card and the box is cleared.</summary>
internal sealed class PinDialog : Form
{
    private readonly TextBox pin = new()
    {
        UseSystemPasswordChar = true, Font = new Font("Segoe UI", 16), BorderStyle = BorderStyle.FixedSingle,
        BackColor = KioskTheme.Background, ForeColor = KioskTheme.Text, Width = 320, MaxLength = 63
    };

    private PinDialog(string title, string message)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = KioskTheme.Surface;
        ForeColor = KioskTheme.Text;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(440, 250);
        var heading = new Label { Text = title, Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoSize = true, Location = new Point(28, 22) };
        var info = new Label { Text = message, ForeColor = KioskTheme.Muted, Location = new Point(30, 64), Size = new Size(380, 50) };
        pin.Location = new Point(30, 118);
        pin.Width = 380;
        var ok = new TaskButton("Zaloguj", primary: true) { Location = new Point(30, 180), Size = new Size(180, 44) };
        var cancel = new TaskButton("Anuluj") { Location = new Point(230, 180), Size = new Size(180, 44) };
        ok.Click += (_, _) => { if (pin.TextLength > 0) { DialogResult = DialogResult.OK; Close(); } };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        pin.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && pin.TextLength > 0) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
        };
        Controls.AddRange([heading, info, pin, ok, cancel]);
        Shown += (_, _) => pin.Focus();
    }

    /// <summary>Asks for the card PIN; null when cancelled.</summary>
    internal static string? Ask(IWin32Window owner, string message, string title = "PIN karty")
    {
        using var dialog = new PinDialog(title, message);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        var value = dialog.pin.Text;
        dialog.pin.Clear();
        return value;
    }
}
