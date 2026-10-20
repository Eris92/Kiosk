namespace Kiosk.Client;

/// <summary>
/// Konfiguracja → the "Kolor" columns of Menu and Kategorie: the cell shows a swatch with the code, a click opens a small
/// palette under it (ready colours, a #RRGGBB field with a live preview, "Więcej kolorów…" and "Domyślny").
/// </summary>
internal static class ColorPicker
{
    private static readonly string[] Palette =
    [
        "#5AB5FF", "#1565C0", "#2DD4BF", "#3DDC84", "#2E7D32", "#FACC15",
        "#FB923C", "#EF6C00", "#F87171", "#C62828", "#F472B6", "#A78BFA",
        "#6A1B9A", "#455A64", "#9CA3AF", "#E5E7EB"
    ];

    internal static void Attach(DataGridView grid, string column)
    {
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != column || e.Graphics == null) return;
            var hex = Convert.ToString(e.Value)?.Trim() ?? "";
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            var swatch = new Rectangle(e.CellBounds.Left + 6, e.CellBounds.Top + 4, e.CellBounds.Height - 8, e.CellBounds.Height - 8);
            if (KioskTheme.IsColor(hex))
            {
                using var fill = new SolidBrush(ColorTranslator.FromHtml(hex));
                e.Graphics.FillRectangle(fill, swatch);
            }
            using (var frame = new Pen(Color.FromArgb(110, 110, 110))) e.Graphics.DrawRectangle(frame, swatch);
            var text = hex.Length == 0 ? "Domyślny" : hex.ToUpperInvariant();
            var color = hex.Length == 0 || KioskTheme.IsColor(hex) ? (hex.Length == 0 ? KioskTheme.Muted : e.CellStyle!.ForeColor) : Color.FromArgb(255, 153, 164);
            TextRenderer.DrawText(e.Graphics, text, e.CellStyle!.Font, new Rectangle(swatch.Right + 8, e.CellBounds.Top, e.CellBounds.Right - swatch.Right - 10, e.CellBounds.Height),
                color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            e.Handled = true;
        };
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != column) return;
            var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            var at = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Show(grid, new Point(at.Left, at.Bottom), Convert.ToString(cell.Value)?.Trim() ?? "", hex => Set(grid, cell, hex));
        };
    }

    /// <summary>Through the editor, so the new-row line becomes a row as typing would make it.</summary>
    private static void Set(DataGridView grid, DataGridViewCell cell, string hex)
    {
        grid.CurrentCell = cell;
        if (grid.BeginEdit(true) && grid.EditingControl is { } editor)
        {
            editor.Text = hex;
            grid.NotifyCurrentCellDirty(true);
            grid.EndEdit();
        }
        else cell.Value = hex;
        grid.InvalidateCell(cell);
    }

    private static void Show(Control owner, Point at, string current, Action<string> chosen)
    {
        const int size = 30, gap = 6, columns = 8;
        var panel = new Panel { BackColor = Color.FromArgb(43, 43, 43), Padding = new Padding(10), Size = new Size(columns * (size + gap) - gap + 20, 0) };
        var popup = new ToolStripDropDown { Padding = Padding.Empty, BackColor = panel.BackColor };
        // The value first, then the palette closes (the grid's editor must not open while the palette is going away).
        void Choose(string hex) { popup.Close(); owner.BeginInvoke(() => chosen(hex)); }

        for (int i = 0; i < Palette.Length; i++)
        {
            var hex = Palette[i];
            var swatch = new Button
            {
                FlatStyle = FlatStyle.Flat, BackColor = ColorTranslator.FromHtml(hex), Size = new Size(size, size), Cursor = Cursors.Hand,
                Location = new Point(10 + i % columns * (size + gap), 10 + i / columns * (size + gap)), TabStop = false
            };
            swatch.FlatAppearance.BorderColor = hex.Equals(current, StringComparison.OrdinalIgnoreCase) ? Color.White : Color.FromArgb(70, 70, 70);
            swatch.FlatAppearance.BorderSize = hex.Equals(current, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            swatch.Click += (_, _) => Choose(hex);
            panel.Controls.Add(swatch);
        }
        int y = 10 + (Palette.Length + columns - 1) / columns * (size + gap) + 4;

        // Typed code: the preview follows it, Enter or "OK" takes it when it is a colour.
        var preview = new Panel { Location = new Point(10, y), Size = new Size(size, size), BorderStyle = BorderStyle.FixedSingle };
        var code = new TextBox
        {
            Location = new Point(10 + size + gap, y + 3), Width = 110, MaxLength = 7, Text = current.Length > 0 ? current.ToUpperInvariant() : "#",
            BackColor = Color.FromArgb(45, 45, 45), ForeColor = KioskTheme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10)
        };
        var ok = Small("OK", new Point(code.Right + gap, y));
        void Preview()
        {
            var text = code.Text.Trim();
            if (text.Length > 0 && text[0] != '#') text = "#" + text;
            bool valid = KioskTheme.IsColor(text);
            preview.BackColor = valid ? ColorTranslator.FromHtml(text) : panel.BackColor;
            code.ForeColor = valid || text.Length < 7 ? KioskTheme.Text : Color.FromArgb(255, 153, 164);
            ok.Enabled = valid;
        }
        string Typed() { var text = code.Text.Trim(); return (text.StartsWith('#') ? text : "#" + text).ToUpperInvariant(); }
        code.TextChanged += (_, _) => Preview();
        code.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && ok.Enabled) { e.SuppressKeyPress = true; Choose(Typed()); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; popup.Close(); }
        };
        ok.Click += (_, _) => Choose(Typed());
        Preview();
        y += size + gap + 4;

        var more = Small("Więcej kolorów…", new Point(10, y));
        more.Width = 150;
        more.Click += (_, _) =>
        {
            popup.Close();
            using var dialog = new ColorDialog { FullOpen = true };
            if (KioskTheme.IsColor(current)) dialog.Color = ColorTranslator.FromHtml(current);
            if (dialog.ShowDialog(owner.FindForm()) == DialogResult.OK)
                chosen("#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2"));
        };
        var reset = Small("Domyślny", new Point(more.Right + gap, y));
        reset.Width = 110;
        reset.Click += (_, _) => Choose("");
        panel.Controls.AddRange(new Control[] { preview, code, ok, more, reset });
        panel.Height = y + 34 + 10;

        var host = new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size };
        popup.Items.Add(host);
        // Disposed after Windows Forms has finished closing it (disposing inside Closed broke the closing that follows).
        popup.Closed += (_, _) => owner.BeginInvoke(popup.Dispose);
        popup.Show(owner, at);
        code.Focus();
        code.SelectAll();
    }

    private static Button Small(string text, Point at)
    {
        var button = new Button
        {
            Text = text, Location = at, Size = new Size(60, 30), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = Color.FromArgb(55, 55, 55), ForeColor = KioskTheme.Text, TabStop = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
        return button;
    }
}
