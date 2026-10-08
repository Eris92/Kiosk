namespace Kiosk.Client;

/// <summary>
/// The card PIN typed in Kiosk (FIDO2 cards), in the Kiosk style. The text is handed to the card and the box is
/// cleared. The dialog closes by itself when the card is taken away.
/// </summary>
internal sealed class PinDialog : Form
{
    private readonly TextBox pin = new()
    {
        UseSystemPasswordChar = true, Font = new Font("Segoe UI", 18), BorderStyle = BorderStyle.FixedSingle,
        BackColor = KioskTheme.Background, ForeColor = KioskTheme.Text, MaxLength = 63, Dock = DockStyle.Fill,
        TextAlign = HorizontalAlignment.Center, Margin = new Padding(0, 8, 0, 16)
    };
    private readonly System.Windows.Forms.Timer watch = new() { Interval = 250 };

    private PinDialog(string message, Func<bool>? cardPresent)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = KioskTheme.Surface;
        ForeColor = KioskTheme.Text;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(32, 24, 32, 28);

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        var heading = new Label { Text = "PIN karty", Font = new Font("Segoe UI", 16, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        var info = new Label { Text = message, ForeColor = KioskTheme.Muted, Font = new Font("Segoe UI", 10.5f), AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 4) };
        var ok = new TaskButton("Zaloguj", primary: true) { Dock = DockStyle.Fill, Height = 44, Margin = new Padding(0, 0, 6, 0) };
        var cancel = new TaskButton("Anuluj") { Dock = DockStyle.Fill, Height = 44, Margin = new Padding(6, 0, 0, 0) };
        layout.Controls.Add(heading, 0, 0); layout.SetColumnSpan(heading, 2);
        layout.Controls.Add(info, 0, 1); layout.SetColumnSpan(info, 2);
        layout.Controls.Add(pin, 0, 2); layout.SetColumnSpan(pin, 2);
        layout.Controls.Add(ok, 0, 3);
        layout.Controls.Add(cancel, 1, 3);
        Controls.Add(layout);

        ok.Click += (_, _) => Finish(DialogResult.OK);
        cancel.Click += (_, _) => Finish(DialogResult.Cancel);
        pin.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Finish(DialogResult.OK); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(DialogResult.Cancel); }
        };
        // Card taken away: nobody is there to type the PIN any more.
        watch.Tick += (_, _) => { if (cardPresent?.Invoke() == false) Finish(DialogResult.Abort); };
        Shown += (_, _) => { pin.Focus(); watch.Start(); };
        FormClosed += (_, _) => watch.Dispose();
        Paint += (_, e) => { using var pen = new Pen(KioskTheme.Border); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); };
    }

    private void Finish(DialogResult result)
    {
        if (result == DialogResult.OK && pin.TextLength == 0) return;
        watch.Stop();
        DialogResult = result;
        Close();
    }

    /// <summary>Asks for the card PIN; null when cancelled or the card was removed.</summary>
    internal static string? Ask(IWin32Window owner, string message, Func<bool>? cardPresent = null)
    {
        using var dialog = new PinDialog(message, cardPresent);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        var value = dialog.pin.Text;
        dialog.pin.Clear();
        return value;
    }
}
