using System.Drawing.Drawing2D;

namespace Kiosk.Client;

/// <summary>A flat, rounded button used on the taskbar (Menu, open windows, tray actions).</summary>
internal sealed class TaskButton : Button
{
    private bool hovered;
    private bool active;

    internal TaskButton(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = KioskTheme.Surface;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 10, FontStyle.Bold);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal bool Primary { get; }
    /// <summary>Shown instead of the text (icon-only button, e.g. Menu).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? Glyph { get; init; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Active
    {
        get => active;
        set { if (active == value) return; active = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        g.Clear(Parent?.BackColor ?? KioskTheme.Background);
        var fillColor = Primary
            ? (Enabled ? (hovered ? KioskTheme.AccentHover : KioskTheme.Accent) : KioskTheme.Blend(KioskTheme.Accent, KioskTheme.Surface, 0.6))
            : active ? KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.2) : hovered && Enabled ? Color.FromArgb(38, 54, 78) : KioskTheme.Surface;
        using var path = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(8));
        using var fill = new SolidBrush(fillColor);
        g.FillPath(fill, path);
        if (!Primary || KioskTheme.AccentLine != KioskTheme.Accent)
        {
            using var stroke = new Pen(Primary ? KioskTheme.Blend(KioskTheme.Accent, KioskTheme.AccentLine, 0.5) : active ? KioskTheme.AccentLine : KioskTheme.Border);
            g.DrawPath(stroke, path);
        }
        var textColor = Primary ? KioskTheme.OnAccent : Enabled ? KioskTheme.Text : KioskTheme.Muted;
        if (Glyph != null && Text.Length == 0) KioskIcons.Draw(g, new Rectangle(0, S(8), Width, Height - S(16)), Glyph, null, textColor);
        else TextRenderer.DrawText(g, Text, Font, new Rectangle(S(10), 0, Math.Max(0, Width - S(20)), Height), textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (active)
        {
            int barWidth = Math.Max(S(16), Width / 3);
            using var bar = new SolidBrush(KioskTheme.AccentLine);
            g.FillRectangle(bar, (Width - barWidth) / 2, Height - S(4), barWidth, S(3));
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -S(3), -S(3)), KioskTheme.AccentLine, fillColor);
    }
}

/// <summary>The bottom bar: one Menu button, open windows and a clock, like the Windows taskbar.</summary>
internal sealed class Taskbar : Panel
{
    private readonly List<TabButton> tasks = new();
    private readonly ToolTip tips = new() { InitialDelay = 300 };
    private readonly Label clock = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent };
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 1000 };
    private long displayedMinute = -1;
    private (string Key, string Title, string? Glyph, Image? Icon, bool OtherScreen)[] taskWindows = [];

    internal TaskButton MenuButton { get; } = new("", primary: true) { Glyph = KioskIcons.Menu };
    internal event Action<string>? TaskClicked;
    internal event Action<string>? TaskCloseRequested;
    /// <summary>Right-click on a window button: its key, the button and the click point (for a context menu).</summary>
    internal event Action<string, Control, Point>? TaskMenuRequested;
    internal int TaskCount => tasks.Count;

    internal Taskbar()
    {
        Dock = DockStyle.Bottom;
        BackColor = KioskTheme.Taskbar;
        ForeColor = KioskTheme.Text;
        DoubleBuffered = true;
        clock.Font = new Font("Segoe UI", 9.5f);
        clock.ForeColor = KioskTheme.Text;
        Controls.AddRange(new Control[] { MenuButton, clock });
        clockTimer.Tick += (_, _) => UpdateClock();
        clockTimer.Start();
        UpdateClock();
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = Scale(58); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ArrangeControls(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var line = new Pen(KioskTheme.Border);
        e.Graphics.DrawLine(line, 0, 0, Width, 0);
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var minute = now.Ticks / TimeSpan.TicksPerMinute;
        if (minute == displayedMinute) return;
        displayedMinute = minute;
        clock.Text = now.ToString("HH:mm") + "\n" + now.ToString("dd.MM.yyyy");
    }

    /// <summary>Rebuilds window buttons only when the set of windows or the active one changes.</summary>
    /// <param name="windows">OtherScreen: the window is on another screen; its button sits on the right, by the clock.</param>
    internal void SetTasks(IReadOnlyList<(string Key, string Title, bool Active, string? Glyph, Image? Icon, bool OtherScreen)> windows)
    {
        // The icon is part of it: a program's own icon is known only once it has started.
        bool changed = windows.Count != taskWindows.Length;
        for (int i = 0; !changed && i < windows.Count; i++)
            changed = windows[i].Key != taskWindows[i].Key || windows[i].Title != taskWindows[i].Title || windows[i].Glyph != taskWindows[i].Glyph ||
                !ReferenceEquals(windows[i].Icon, taskWindows[i].Icon) || windows[i].OtherScreen != taskWindows[i].OtherScreen;
        if (changed)
        {
            taskWindows = windows.Select(w => (w.Key, w.Title, w.Glyph, w.Icon, w.OtherScreen)).ToArray();
            SuspendLayout();
            foreach (var task in tasks) { Controls.Remove(task); task.Dispose(); }
            tasks.Clear();
            foreach (var window in windows)
            {
                // Same look as a browser tab: title plus its own × close button.
                // Icon only; the name is in the tooltip. Middle click or the right-click menu closes the window.
                var button = new TabButton(window.Title, bold: true) { Glyph = window.Glyph, IconImage = window.Icon, OtherScreen = window.OtherScreen };
                tips.SetToolTip(button, window.Title);
                var key = window.Key;
                button.Selected += () => TaskClicked?.Invoke(key);
                button.CloseRequested += () => TaskCloseRequested?.Invoke(key);
                button.MenuRequested += point => TaskMenuRequested?.Invoke(key, button, point);
                tasks.Add(button);
                Controls.Add(button);
            }
            ResumeLayout(false);
            ArrangeControls();
        }
        for (int i = 0; i < tasks.Count; i++) tasks[i].Active = windows[i].Active;
    }

    private void ArrangeControls()
    {
        if (clock == null) return;
        int height = Scale(40), top = (Height - height) / 2, gap = Scale(8), margin = Scale(10);
        MenuButton.SetBounds(margin, top, Scale(58), height);

        int right = Width - margin;
        int clockWidth = Scale(86);
        clock.SetBounds(right - clockWidth, top, clockWidth, height);
        right -= clockWidth + gap * 2;

        int each = Scale(58);
        // Windows on other screens: on the right, next to the clock (newest nearest the clock).
        foreach (var task in tasks.Where(t => t.OtherScreen).Reverse())
        {
            right -= each;
            task.SetBounds(right, top, each, height);
            task.Visible = right > MenuButton.Right;
            right -= gap;
        }
        int left = MenuButton.Right + Scale(18);
        foreach (var task in tasks.Where(t => !t.OtherScreen))
        {
            task.SetBounds(left, top, each, height);
            task.Visible = task.Right <= right;
            left += each + gap;
        }
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); ArrangeControls(); }
    internal void Relayout() => ArrangeControls();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { clockTimer.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed record MenuItemSpec(string Title, string Category, Action Run, bool Enabled = true, string? Glyph = null, Image? Icon = null);
internal sealed record MenuSection(string Title, IReadOnlyList<MenuItemSpec> Items);

/// <summary>The start-menu popup opened from the taskbar's Menu button.</summary>
internal sealed class StartMenu : Form
{
    private static readonly Font HeaderFont = new("Segoe UI", 18, FontStyle.Bold);
    private static readonly Font SubtitleFont = new("Segoe UI", 9.5f);
    private static readonly Font SectionFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private readonly Panel content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = KioskTheme.Surface };
    internal long? HiddenAtMilliseconds { get; private set; }
    internal int ItemCount { get; private set; }

    internal StartMenu()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = KioskTheme.Border;
        Padding = new Padding(1);
        KeyPreview = true;
        // Above everything, including programs shown as their own windows over the Kiosk (Edge).
        TopMost = true;
        Controls.Add(content);
        Deactivate += (_, _) => HideMenu();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HideMenu(); };
    }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ClassStyle |= 0x20000; return p; } // CS_DROPSHADOW
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    internal void HideMenu()
    {
        if (!Visible) return;
        Hide();
        HiddenAtMilliseconds = Environment.TickCount64;
    }

    internal void Populate(string subtitle, IReadOnlyList<MenuSection> sections, Func<bool> canRun)
    {
        content.SuspendLayout();
        foreach (Control control in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(control); control.Dispose(); }
        int columns = 3, tileWidth = Scale(230), tileHeight = Scale(78), gap = Scale(10), margin = Scale(22);
        int y = Scale(18);
        var header = new Label { Text = "Menu", AutoSize = false, Font = HeaderFont, ForeColor = KioskTheme.Text, BackColor = Color.Transparent };
        header.SetBounds(margin, y, columns * tileWidth + (columns - 1) * gap, Scale(38));
        content.Controls.Add(header);
        y += Scale(38);
        var sub = new Label { Text = subtitle, AutoSize = false, Font = SubtitleFont, ForeColor = KioskTheme.Muted, BackColor = Color.Transparent };
        sub.SetBounds(margin, y, header.Width, Scale(24));
        content.Controls.Add(sub);
        y += Scale(30);
        ItemCount = 0;
        foreach (var section in sections.Where(s => s.Items.Count > 0))
        {
            var title = new Label { Text = section.Title.ToUpperInvariant(), AutoSize = false, Font = SectionFont, ForeColor = KioskTheme.AccentLine, BackColor = Color.Transparent };
            title.SetBounds(margin, y + Scale(6), header.Width, Scale(22));
            content.Controls.Add(title);
            y += Scale(32);
            for (int i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];
                var tile = new MenuTile(item.Title, item.Category) { Enabled = item.Enabled, Glyph = item.Glyph, IconImage = item.Icon };
                tile.SetBounds(margin + (i % columns) * (tileWidth + gap), y + (i / columns) * (tileHeight + gap), tileWidth, tileHeight);
                tile.Click += (_, _) => { HideMenu(); if (canRun()) item.Run(); };
                content.Controls.Add(tile);
                ItemCount++;
            }
            y += ((section.Items.Count + columns - 1) / columns) * (tileHeight + gap);
        }
        content.ResumeLayout();
        ClientSize = new Size(margin * 2 + columns * tileWidth + (columns - 1) * gap + 2, y + Scale(14) + 2);
    }

    internal void ShowAt(Form owner, Rectangle anchor)
    {
        var screen = Screen.FromRectangle(anchor).WorkingArea;
        var maxHeight = Math.Max(Scale(200), anchor.Top - screen.Top - Scale(16));
        if (Height > maxHeight) Height = maxHeight;
        Location = new Point(Math.Max(screen.Left, anchor.Left), anchor.Top - Height - Scale(8));
        if (Owner != owner) Owner = owner;
        Show();
        Activate();
        content.Focus(); // No tile looks selected until the keyboard (Tab / arrows) is used.
    }
}

/// <summary>A browser tab: title on the left and its own × close button inside the tab.</summary>
internal sealed class TabButton : Control
{
    private static readonly Font RegularFont = new("Segoe UI", 9.5f);
    private static readonly Font BoldFont = new("Segoe UI", 10, FontStyle.Bold);
    private bool hovered, closeHovered, active;

    internal TabButton(string title, bool bold = false)
    {
        Text = title;
        Font = bold ? BoldFont : RegularFont;
        ForeColor = KioskTheme.Text;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal event Action? Selected;
    internal event Action? CloseRequested;
    internal event Action<Point>? MenuRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? Glyph { get; init; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Image? IconImage { get; init; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool OtherScreen { get; init; }
    private bool IconOnly => Glyph != null || IconImage != null;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Active
    {
        get => active;
        set { if (active == value) return; active = value; Invalidate(); }
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private Rectangle CloseBounds => new(Width - S(30), (Height - S(22)) / 2, S(22), S(22));

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = closeHovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = CloseBounds.Contains(e.Location);
        if (over != closeHovered) { closeHovered = over; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle || (!IconOnly && e.Button == MouseButtons.Left && CloseBounds.Contains(e.Location))) CloseRequested?.Invoke();
        else if (e.Button == MouseButtons.Left) Selected?.Invoke();
        else if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(e.Location);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? KioskTheme.Taskbar);
        var fillColor = active ? KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.2) : hovered ? Color.FromArgb(38, 54, 78) : KioskTheme.Surface;
        using var path = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(8));
        using var fill = new SolidBrush(fillColor);
        using var stroke = new Pen(active ? KioskTheme.AccentLine : KioskTheme.Border);
        g.FillPath(fill, path);
        g.DrawPath(stroke, path);
        if (IconOnly)
        {
            KioskIcons.Draw(g, new Rectangle(S(12), S(7), Width - S(24), Height - S(14)), Glyph, IconImage, KioskTheme.Text);
            if (active)
            {
                int w = Math.Max(S(16), Width / 3);
                using var underline = new SolidBrush(KioskTheme.AccentLine);
                g.FillRectangle(underline, (Width - w) / 2, Height - S(4), w, S(3));
            }
            return;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(S(12), 0, Math.Max(0, CloseBounds.Left - S(16)), Height), KioskTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var close = CloseBounds;
        if (closeHovered)
        {
            using var circle = new SolidBrush(KioskTheme.Blend(fillColor, Color.White, 0.15));
            g.FillEllipse(circle, close);
        }
        using var cross = new Pen(closeHovered ? KioskTheme.Text : KioskTheme.Muted, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int inset = S(7);
        g.DrawLine(cross, close.Left + inset, close.Top + inset, close.Right - inset, close.Bottom - inset);
        g.DrawLine(cross, close.Right - inset, close.Top + inset, close.Left + inset, close.Bottom - inset);
        if (active)
        {
            int barWidth = Math.Max(S(16), Width / 3);
            using var bar = new SolidBrush(KioskTheme.AccentLine);
            g.FillRectangle(bar, (Width - barWidth) / 2, Height - S(4), barWidth, S(3));
        }
    }
}
