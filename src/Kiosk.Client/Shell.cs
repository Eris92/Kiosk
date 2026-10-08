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
    internal string Key { get; init; } = "";

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
        TextRenderer.DrawText(g, Text, Font, new Rectangle(S(10), 0, Math.Max(0, Width - S(20)), Height), textColor,
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

/// <summary>The bottom bar: one Menu button, open windows and session actions, like the Windows taskbar.</summary>
internal sealed class Taskbar : Panel
{
    private readonly List<TabButton> tasks = new();
    private readonly Label clock = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent };
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 1000 };
    private string taskSignature = "";

    internal TaskButton MenuButton { get; } = new("☰   Menu", primary: true);
    internal TaskButton LockButton { get; } = new("Zablokuj");
    internal TaskButton LogoutButton { get; } = new("Wyloguj");
    internal TaskButton ExitButton { get; } = new("Zamknij test");
    internal Label Status { get; } = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent };
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
        Status.Font = new Font("Segoe UI", 9.5f);
        Status.ForeColor = KioskTheme.Muted;
        clock.Font = new Font("Segoe UI", 9.5f);
        clock.ForeColor = KioskTheme.Text;
        Controls.AddRange(new Control[] { MenuButton, LockButton, LogoutButton, ExitButton, Status, clock });
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
        clock.Text = now.ToString("HH:mm") + "\n" + now.ToString("dd.MM.yyyy");
    }

    /// <summary>Rebuilds window buttons only when the set of windows or the active one changes.</summary>
    internal void SetTasks(IReadOnlyList<(string Key, string Title, bool Active)> windows)
    {
        var signature = string.Join("|", windows.Select(w => w.Key + "=" + w.Title));
        if (signature != taskSignature)
        {
            taskSignature = signature;
            foreach (var task in tasks) { Controls.Remove(task); task.Dispose(); }
            tasks.Clear();
            foreach (var window in windows)
            {
                // Same look as a browser tab: title plus its own × close button.
                var button = new TabButton(window.Title) { Font = new Font("Segoe UI", 10, FontStyle.Bold) };
                var key = window.Key;
                button.Selected += () => TaskClicked?.Invoke(key);
                button.CloseRequested += () => TaskCloseRequested?.Invoke(key);
                button.MenuRequested += point => TaskMenuRequested?.Invoke(key, button, point);
                tasks.Add(button);
                Controls.Add(button);
            }
            ArrangeControls();
        }
        for (int i = 0; i < tasks.Count; i++) tasks[i].Active = windows[i].Active;
    }

    private int Measure(Control control, int minimum) =>
        Math.Max(Scale(minimum), TextRenderer.MeasureText(control.Text, control.Font).Width + Scale(28));

    private void ArrangeControls()
    {
        if (clock == null) return;
        int height = Scale(40), top = (Height - height) / 2, gap = Scale(8), margin = Scale(10);
        MenuButton.SetBounds(margin, top, Measure(MenuButton, 120), height);

        int right = Width - margin;
        int clockWidth = Scale(86);
        clock.SetBounds(right - clockWidth, top, clockWidth, height);
        right -= clockWidth + gap * 2;
        foreach (var button in new[] { ExitButton, LogoutButton, LockButton })
        {
            if (!button.Visible) continue;
            int width = Measure(button, 96);
            button.SetBounds(right - width, top, width, height);
            right -= width + gap;
        }
        int statusWidth = Scale(220);
        Status.SetBounds(right - statusWidth, top, statusWidth, height);
        right -= statusWidth + gap;

        int left = MenuButton.Right + Scale(18);
        int available = Math.Max(0, right - left);
        int each = tasks.Count == 0 ? 0 : Math.Min(Scale(230), Math.Max(Scale(90), (available - gap * (tasks.Count - 1)) / tasks.Count));
        foreach (var task in tasks)
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
        if (disposing) clockTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed record MenuItemSpec(string Title, string Category, Action Run, bool Enabled = true);
internal sealed record MenuSection(string Title, IReadOnlyList<MenuItemSpec> Items);

/// <summary>The start-menu popup opened from the taskbar's Menu button.</summary>
internal sealed class StartMenu : Form
{
    private readonly Panel content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = KioskTheme.Surface };
    internal DateTime HiddenAt { get; private set; }
    internal int ItemCount { get; private set; }

    internal StartMenu()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = KioskTheme.Border;
        Padding = new Padding(1);
        KeyPreview = true;
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
        HiddenAt = DateTime.UtcNow;
    }

    internal void Populate(string subtitle, IReadOnlyList<MenuSection> sections)
    {
        content.SuspendLayout();
        foreach (Control control in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(control); control.Dispose(); }
        int columns = 3, tileWidth = Scale(230), tileHeight = Scale(78), gap = Scale(10), margin = Scale(22);
        int y = Scale(18);
        var header = new Label { Text = "Menu", AutoSize = false, Font = new Font("Segoe UI", 18, FontStyle.Bold), ForeColor = KioskTheme.Text, BackColor = Color.Transparent };
        header.SetBounds(margin, y, columns * tileWidth + (columns - 1) * gap, Scale(38));
        content.Controls.Add(header);
        y += Scale(38);
        var sub = new Label { Text = subtitle, AutoSize = false, Font = new Font("Segoe UI", 9.5f), ForeColor = KioskTheme.Muted, BackColor = Color.Transparent };
        sub.SetBounds(margin, y, header.Width, Scale(24));
        content.Controls.Add(sub);
        y += Scale(30);
        ItemCount = 0;
        foreach (var section in sections.Where(s => s.Items.Count > 0))
        {
            var title = new Label { Text = section.Title.ToUpperInvariant(), AutoSize = false, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), ForeColor = KioskTheme.AccentLine, BackColor = Color.Transparent };
            title.SetBounds(margin, y + Scale(6), header.Width, Scale(22));
            content.Controls.Add(title);
            y += Scale(32);
            for (int i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];
                var tile = new MenuTile(item.Title, item.Category) { Enabled = item.Enabled };
                tile.SetBounds(margin + (i % columns) * (tileWidth + gap), y + (i / columns) * (tileHeight + gap), tileWidth, tileHeight);
                tile.Click += (_, _) => { HideMenu(); item.Run(); };
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
        content.Controls.OfType<MenuTile>().FirstOrDefault(t => t.Enabled)?.Focus();
    }
}

/// <summary>A browser tab: title on the left and its own × close button inside the tab.</summary>
internal sealed class TabButton : Control
{
    private bool hovered, closeHovered, active;

    internal TabButton(string title)
    {
        Text = title;
        Font = new Font("Segoe UI", 9.5f);
        ForeColor = KioskTheme.Text;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal event Action? Selected;
    internal event Action? CloseRequested;
    internal event Action<Point>? MenuRequested;

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
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && CloseBounds.Contains(e.Location))) CloseRequested?.Invoke();
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
