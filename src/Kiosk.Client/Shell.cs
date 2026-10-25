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
    /// <summary>Shown instead of the text (icon-only button, e.g. Menu).</summary>
    internal string? Glyph { get; init; }

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
        if (Primary && Glyph != null && Text.Length == 0)
        {
            // Start button as in Windows 11: no frame, a light square on hover, the four-pane logo in the accent.
            if (hovered && Enabled)
            {
                using var hover = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(5));
                using var hoverFill = new SolidBrush(KioskTheme.ShellHover);
                g.FillPath(hoverFill, hover);
            }
            int size = S(20), pane = (size - S(2)) / 2, x = (Width - size) / 2, y = (Height - size) / 2;
            var color = Enabled ? KioskTheme.AccentLine : KioskTheme.Muted;
            using var paneBrush = new SolidBrush(color);
            foreach (var (px, py) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                using var square = KioskTheme.RoundedRectangle(new Rectangle(x + px * (pane + S(2)), y + py * (pane + S(2)), pane, pane), S(2));
                g.FillPath(paneBrush, square);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -S(2), -S(2)), KioskTheme.AccentLine, KioskTheme.ShellBar);
            return;
        }
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

/// <summary>The bottom bar: one Menu button, open windows and session actions, like the Windows taskbar.</summary>
internal sealed class Taskbar : Panel
{
    private readonly List<TabButton> tasks = new();
    private readonly ToolTip tips = new() { InitialDelay = 300 };
    private readonly Label clock = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent };
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 1000 };
    private string taskSignature = "";
    /// <summary>One font for every task button (a new one per rebuild was never released).</summary>
    private static readonly Font TaskFont = new("Segoe UI", 10, FontStyle.Bold);

    internal TaskButton MenuButton { get; } = new("", primary: true) { Glyph = KioskIcons.Menu };
    internal TaskButton LockButton { get; } = new("Zablokuj");
    internal TaskButton LogoutButton { get; } = new("Wyloguj");
    internal TaskButton ExitButton { get; } = new("Zamknij test");
    internal Label Status { get; } = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent };
    internal event Action<string>? TaskClicked;
    internal event Action<string>? TaskCloseRequested;
    /// <summary>Right-click on a window button: its key, the button and the click point (for a context menu).</summary>
    internal event Action<string, Control, Point>? TaskMenuRequested;
    internal int TaskCount => tasks.Count;
    private bool centered = true;
    /// <summary>Start and the window icons in the middle (Windows 11) or on the left.</summary>
    internal bool Centered
    {
        get => centered;
        set { if (centered == value) return; centered = value; ArrangeControls(); }
    }

    internal Taskbar()
    {
        Dock = DockStyle.Bottom;
        BackColor = KioskTheme.ShellBar;
        ForeColor = KioskTheme.Text;
        DoubleBuffered = true;
        Status.Font = new Font("Segoe UI", 9.5f);
        Status.ForeColor = KioskTheme.Muted;
        clock.Font = new Font("Segoe UI", 9f);
        clock.ForeColor = KioskTheme.Text;
        Controls.AddRange(new Control[] { MenuButton, LockButton, LogoutButton, ExitButton, Status, clock });
        // A clean bar: Menu, windows and the clock. Lock, sign-out and close live in Menu → System.
        LockButton.Visible = LogoutButton.Visible = ExitButton.Visible = Status.Visible = false;
        clockTimer.Tick += (_, _) => UpdateClock();
        VisibleChanged += (_, _) =>
        {
            if (Disposing || IsDisposed) return;
            if (Visible) { UpdateClock(); clockTimer.Start(); }
            else clockTimer.Stop();
        };
        UpdateClock();
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = Scale(52); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ArrangeControls(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var line = new Pen(KioskTheme.ShellBorder);
        e.Graphics.DrawLine(line, 0, 0, Width, 0);
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        clock.Text = now.ToString("HH:mm") + "\n" + now.ToString("dd.MM.yyyy");
    }

    /// <summary>Rebuilds window buttons only when the set of windows or the active one changes.</summary>
    /// <param name="windows">OtherScreen: the window is on another screen; its button sits on the right, by the clock.</param>
    internal void SetTasks(IReadOnlyList<(string Key, string Title, bool Active, string? Glyph, Image? Icon, bool OtherScreen)> windows)
    {
        // The icon is part of it: a program's own icon is known only once it has started.
        var signature = string.Join("|", windows.Select(w => w.Key + "=" + w.Title + (w.OtherScreen ? "@" : "") + (w.Icon != null ? "#" + w.Icon.GetHashCode() : w.Glyph)));
        if (signature != taskSignature)
        {
            taskSignature = signature;
            foreach (var task in tasks) { Controls.Remove(task); task.Dispose(); }
            tasks.Clear();
            foreach (var window in windows)
            {
                // Same look as a browser tab: title plus its own × close button.
                // Icon only; the name is in the tooltip. Middle click or the right-click menu closes the window.
                var button = new TabButton(window.Title) { Font = TaskFont, Glyph = window.Glyph, IconImage = window.Icon, OtherScreen = window.OtherScreen };
                tips.SetToolTip(button, window.Title);
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
        // Windows 11: Start and the open windows sit together in the middle; the clock on the right.
        int height = Scale(42), top = (Height - height) / 2 + Scale(1), gap = Scale(4), margin = Scale(12), each = Scale(44);

        int right = Width - margin;
        int clockWidth = Scale(80);
        clock.SetBounds(right - clockWidth, top, clockWidth, height);
        right -= clockWidth + Scale(8);
        foreach (var button in new[] { ExitButton, LogoutButton, LockButton })
        {
            if (!button.Visible) continue;
            int width = Measure(button, 96);
            button.SetBounds(right - width, top, width, height);
            right -= width + gap;
        }
        if (Status.Visible)
        {
            int statusWidth = Scale(220);
            Status.SetBounds(right - statusWidth, top, statusWidth, height);
            right -= statusWidth + gap;
        }
        // Windows on other screens: on the right, next to the clock (newest nearest the clock).
        foreach (var task in tasks.Where(t => t.OtherScreen).Reverse())
        {
            right -= each;
            task.SetBounds(right, top, each, height);
            task.Visible = right > margin + each;
            right -= gap;
        }

        var here = tasks.Where(t => !t.OtherScreen).ToList();
        int groupWidth = each + here.Count * (each + gap);
        int left = centered ? Math.Max(margin, Math.Min((Width - groupWidth) / 2, right - groupWidth)) : margin;
        MenuButton.SetBounds(left, top, each, height);
        left += each + gap;
        foreach (var task in here)
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

/// <summary>The Start menu (Windows 11 26H2 look): rounded panel above the taskbar, sections of pinned icons, and a
/// darker footer with Pulpit / Konfiguracja on the left and the power button (Zablokuj, Wyloguj, Uruchom ponownie, Wyłącz, Zamknij aplikację) on the right.</summary>
internal sealed class StartMenu : Form
{
    private readonly Panel content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = KioskTheme.MenuBack };
    private readonly Panel footer = new() { Dock = DockStyle.Bottom, BackColor = KioskTheme.MenuFooter };
    private readonly Panel power = new() { Visible = false, BackColor = KioskTheme.ShellBorder, Padding = new Padding(1) };
    private readonly ToolTip tips = new() { InitialDelay = 400 };
    private FooterButton? powerButton;
    internal DateTime HiddenAt { get; private set; }
    internal int ItemCount { get; private set; }

    internal StartMenu()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = KioskTheme.ShellBorder;
        Padding = new Padding(1);
        KeyPreview = true;
        // Above everything, including programs shown as their own windows over the Kiosk (Edge).
        TopMost = true;
        footer.Paint += (_, e) => { using var line = new Pen(KioskTheme.ShellBorder); e.Graphics.DrawLine(line, 0, 0, footer.Width, 0); };
        Controls.Add(power);
        Controls.Add(content);
        Controls.Add(footer);
        content.MouseDown += (_, _) => power.Visible = false;
        footer.MouseDown += (_, _) => power.Visible = false;
        Deactivate += (_, _) => HideMenu();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            if (power.Visible) power.Visible = false; else HideMenu();
        };
    }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ClassStyle |= 0x20000; return p; } // CS_DROPSHADOW
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounds the corners itself (DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND); older Windows ignores it.
        int round = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    internal void HideMenu()
    {
        if (!Visible) return;
        power.Visible = false;
        Hide();
        HiddenAt = DateTime.UtcNow;
    }

    internal void Populate(string subtitle, IReadOnlyList<MenuSection> sections, IReadOnlyList<MenuItemSpec> footerItems, IReadOnlyList<MenuItemSpec> powerItems)
    {
        content.SuspendLayout();
        foreach (Control control in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(control); control.Dispose(); }
        int columns = 6, tileWidth = Scale(100), tileHeight = Scale(88), gap = Scale(4), margin = Scale(32);
        int width = columns * tileWidth + (columns - 1) * gap;
        int y = Scale(26);
        ItemCount = 0;
        foreach (var section in sections.Where(s => s.Items.Count > 0))
        {
            var title = new Label { Text = section.Title, AutoSize = false, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = KioskTheme.Text, BackColor = Color.Transparent };
            title.SetBounds(margin + Scale(6), y, width, Scale(26));
            content.Controls.Add(title);
            y += Scale(34);
            for (int i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];
                var tile = new StartTile(item.Title, item.Category) { Enabled = item.Enabled, Glyph = item.Glyph, IconImage = item.Icon };
                tile.SetBounds(margin + (i % columns) * (tileWidth + gap), y + (i / columns) * (tileHeight + gap), tileWidth, tileHeight);
                tile.Click += (_, _) => { HideMenu(); item.Run(); };
                content.Controls.Add(tile);
                ItemCount++;
            }
            y += ((section.Items.Count + columns - 1) / columns) * (tileHeight + gap) + Scale(14);
        }
        content.ResumeLayout();

        // Footer: Pulpit, Konfiguracja… on the left, a hint in the middle, the power button on the right.
        foreach (Control control in footer.Controls.Cast<Control>().ToArray()) { footer.Controls.Remove(control); control.Dispose(); }
        footer.Height = Scale(64);
        int buttonHeight = Scale(40), top = (footer.Height - buttonHeight) / 2 + Scale(1), x = margin - Scale(4);
        int footerWidth = margin * 2 + width;
        foreach (var item in footerItems)
        {
            var button = new FooterButton(item.Title, item.Glyph) { Enabled = item.Enabled };
            int w = button.PreferredWidth;
            button.SetBounds(x, top, w, buttonHeight);
            button.Click += (_, _) => { HideMenu(); item.Run(); };
            tips.SetToolTip(button, item.Category);
            footer.Controls.Add(button);
            x += w + Scale(4);
            ItemCount++;
        }
        int right = footerWidth - margin + Scale(4);
        powerButton = null;
        if (powerItems.Count > 0)
        {
            powerButton = new FooterButton("", "\uE7E8");
            powerButton.SetBounds(right - buttonHeight, top, buttonHeight, buttonHeight);
            tips.SetToolTip(powerButton, string.Join(", ", powerItems.Select(p => p.Title)));
            var button = powerButton;
            powerButton.Click += (_, _) => TogglePower(button);
            footer.Controls.Add(powerButton);
            right -= buttonHeight + Scale(8);
        }
        var hint = new Label { Text = subtitle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 9f), ForeColor = KioskTheme.Muted, BackColor = Color.Transparent, AutoEllipsis = true };
        hint.SetBounds(x + Scale(8), top, Math.Max(0, right - x - Scale(8)), buttonHeight);
        footer.Controls.Add(hint);

        // The power list, opened above the power button like in Windows.
        foreach (Control control in power.Controls.Cast<Control>().ToArray()) { power.Controls.Remove(control); control.Dispose(); }
        int rowHeight = Scale(40), listWidth = Scale(220), rowY = Scale(4);
        var list = new Panel { Dock = DockStyle.Fill, BackColor = KioskTheme.ShellActive };
        foreach (var item in powerItems)
        {
            var row = new FooterButton(item.Title, item.Glyph) { Enabled = item.Enabled, BackColor = KioskTheme.ShellActive, Hover = KioskTheme.ShellHover };
            row.SetBounds(Scale(4), rowY, listWidth - Scale(10), rowHeight);
            row.Click += (_, _) => { HideMenu(); item.Run(); };
            list.Controls.Add(row);
            rowY += rowHeight;
            ItemCount++;
        }
        power.Controls.Add(list);
        power.Size = new Size(listWidth, rowY + Scale(4) + 2);
        power.Visible = false;

        ClientSize = new Size(footerWidth + 2, y + Scale(6) + footer.Height + 2);
    }

    /// <summary>Self-test picture: the power list open.</summary>
    internal void OpenPowerList() { if (powerButton != null && !power.Visible) TogglePower(powerButton); }

    private void TogglePower(Control button)
    {
        if (power.Visible) { power.Visible = false; return; }
        var at = PointToClient(button.Parent!.PointToScreen(new Point(button.Right, button.Top)));
        power.Location = new Point(Math.Max(Scale(8), at.X - power.Width), at.Y - power.Height - Scale(6));
        power.Visible = true;
        power.BringToFront();
    }

    internal void ShowAt(Form owner, Rectangle anchor, bool centered = true)
    {
        var screen = Screen.FromRectangle(anchor).WorkingArea;
        var maxHeight = Math.Max(Scale(200), anchor.Top - screen.Top - Scale(24));
        if (Height > maxHeight) Height = maxHeight;
        // Centred above the taskbar, like the Windows 11 Start menu.
        int left = centered ? screen.Left + (screen.Width - Width) / 2 : anchor.Left - Scale(4);
        Location = new Point(Math.Max(screen.Left + Scale(12), left), anchor.Top - Height - Scale(12));
        if (Owner != owner) Owner = owner;
        Show();
        Activate();
        content.Focus(); // No tile looks selected until the keyboard (Tab / arrows) is used.
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

/// <summary>A footer button of the Start menu (Pulpit, Konfiguracja, power) and a row of the power list: icon and text, soft hover.</summary>
internal sealed class FooterButton : Button
{
    private bool hovered;
    private readonly string? glyph;

    internal FooterButton(string text, string? glyph)
    {
        Text = text;
        this.glyph = glyph;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = KioskTheme.MenuFooter;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 9.5f);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal Color Hover { get; init; } = KioskTheme.ShellHover;
    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    internal int PreferredWidth => S(46) + TextRenderer.MeasureText(Text, Font).Width;

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (hovered && Enabled)
        {
            using var back = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(5));
            using var fill = new SolidBrush(Hover);
            g.FillPath(fill, back);
        }
        var color = Enabled ? KioskTheme.Text : KioskTheme.Muted;
        int icon = S(18);
        bool iconOnly = Text.Length == 0;
        int iconLeft = iconOnly ? (Width - icon) / 2 : S(12);
        KioskIcons.Draw(g, new Rectangle(iconLeft, (Height - icon) / 2, icon, icon), glyph, null, color);
        if (!iconOnly)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(iconLeft + icon + S(12), 0, Math.Max(0, Width - iconLeft - icon - S(16)), Height), color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var focus = KioskTheme.RoundedRectangle(new Rectangle(S(1), S(1), Width - S(3), Height - S(3)), S(5));
            using var pen = new Pen(KioskTheme.Text, S(2));
            g.DrawPath(pen, focus);
        }
    }
}

/// <summary>A pinned item in the Start menu: icon on top, name underneath, a soft square on hover.</summary>
internal sealed class StartTile : Button
{
    private bool hovered, pressed;

    internal StartTile(string title, string category)
    {
        Text = title;
        AccessibleDescription = category;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = KioskTheme.MenuBack;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 9f);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        if (category.Length > 0) new ToolTip { InitialDelay = 500 }.SetToolTip(this, title + " · " + category);
    }

    internal Image? IconImage { get; init; }
    internal string? Glyph { get; init; }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        g.Clear(Parent?.BackColor ?? KioskTheme.MenuBack);
        if ((hovered || pressed) && Enabled)
        {
            using var back = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(5));
            using var fill = new SolidBrush(pressed ? KioskTheme.ShellActive : KioskTheme.MenuHover);
            g.FillPath(fill, back);
        }
        int icon = S(32);
        KioskIcons.Draw(g, new Rectangle((Width - icon) / 2, S(14), icon, icon), Glyph, IconImage, Enabled ? KioskTheme.AccentLine : KioskTheme.Muted);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(S(4), S(14) + icon + S(6), Width - S(8), Height - icon - S(22)), Enabled ? KioskTheme.Text : KioskTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var focus = KioskTheme.RoundedRectangle(new Rectangle(S(1), S(1), Width - S(3), Height - S(3)), S(5));
            using var pen = new Pen(KioskTheme.Text, S(2));
            g.DrawPath(pen, focus);
        }
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
    internal string? Glyph { get; init; }
    internal Image? IconImage { get; init; }
    internal bool OtherScreen { get; init; }
    private bool IconOnly => Glyph != null || IconImage != null;

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
        if (IconOnly)
        {
            // Windows 11 taskbar icon: a soft square behind the active / hovered one and a pill under it.
            if (active || hovered)
            {
                using var back = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(5));
                using var backFill = new SolidBrush(hovered ? KioskTheme.ShellHover : KioskTheme.ShellActive);
                g.FillPath(backFill, back);
            }
            int icon = S(24);
            KioskIcons.Draw(g, new Rectangle((Width - icon) / 2, (Height - icon) / 2 - S(2), icon, icon), Glyph, IconImage, KioskTheme.Text);
            int pill = active ? S(16) : S(6);
            using var pillPath = KioskTheme.RoundedRectangle(new Rectangle((Width - pill) / 2, Height - S(5), pill, S(3)), S(1));
            using var pillFill = new SolidBrush(active ? KioskTheme.AccentLine : KioskTheme.Muted);
            g.FillPath(pillFill, pillPath);
            return;
        }
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
