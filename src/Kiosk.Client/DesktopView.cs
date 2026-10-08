using System.Drawing.Drawing2D;

namespace Kiosk.Client;

internal static class KioskTheme
{
    internal static readonly Color Background = Color.FromArgb(13, 20, 35);
    internal static readonly Color Surface = Color.FromArgb(24, 35, 54);
    internal static readonly Color Border = Color.FromArgb(44, 60, 81);
    internal static readonly Color Text = Color.FromArgb(240, 245, 252);
    internal static readonly Color Muted = Color.FromArgb(157, 174, 195);
    internal static readonly Color Taskbar = Color.FromArgb(17, 26, 42);
    internal const string DefaultAccent = "#5AB5FF";

    /// <summary>Accent presets offered in Konfiguracja (any #RRGGBB also works).</summary>
    internal static readonly (string Name, string Hex)[] Presets =
    [
        ("Niebieski", "#5AB5FF"), ("Zielony", "#3DDC84"), ("Turkusowy", "#2DD4BF"), ("Fioletowy", "#A78BFA"),
        ("Różowy", "#F472B6"), ("Pomarańczowy", "#FB923C"), ("Czerwony", "#F87171"), ("Żółty", "#FACC15"),
        ("Czarny", "#000000"), ("Biały", "#E5E7EB")
    ];

    /// <summary>Fill colour of primary buttons, the Menu button and highlights; chosen in Konfiguracja.</summary>
    internal static Color Accent { get; private set; } = ColorTranslator.FromHtml(DefaultAccent);
    /// <summary>Accent for thin lines and icons: a near-black accent would vanish on the dark background.</summary>
    internal static Color AccentLine => Luminance(Accent) < 0.12 ? Text : Accent;
    internal static Color AccentHover => Luminance(Accent) < 0.12 ? Color.FromArgb(40, 40, 46) : Blend(Accent, Color.White, 0.2);
    internal static Color AccentPressed => Luminance(Accent) < 0.12 ? Color.FromArgb(25, 25, 30) : Blend(Accent, Color.Black, 0.15);
    internal static Color AccentSoft => Blend(Surface, AccentLine, 0.18);
    /// <summary>Readable text on an accent fill.</summary>
    internal static Color OnAccent => Luminance(Accent) > 0.45 ? Background : Color.White;
    internal static event Action? Changed;

    internal static bool IsColor(string? value) =>
        value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");

    internal static void SetAccent(string? hex)
    {
        Accent = ColorTranslator.FromHtml(IsColor(hex) ? hex! : DefaultAccent);
        Changed?.Invoke();
    }

    internal static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount), (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    internal static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? AccentLine : Border;
        button.FlatAppearance.BorderSize = primary && Luminance(Accent) >= 0.12 ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Color.FromArgb(38, 54, 78);
        button.FlatAppearance.MouseDownBackColor = primary ? AccentPressed : Color.FromArgb(49, 67, 93);
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? OnAccent : Text;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>The desktop shown between apps and while the card session is locked.</summary>
internal sealed class DesktopView : UserControl
{
    private readonly Label brand = new() { Text = "Kiosk", AutoSize = false };
    private readonly Label subtitle = new() { Text = "Twój pulpit pracy", AutoSize = false };
    private readonly Label clock = new() { AutoSize = false, TextAlign = ContentAlignment.TopRight };
    private readonly Label date = new() { AutoSize = false, TextAlign = ContentAlignment.TopRight };
    private readonly StatusCard card = new();
    private readonly Label heading = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label detail = new() { AutoSize = false, TextAlign = ContentAlignment.TopCenter };
    private readonly Label hint = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button resume = new() { Text = "Wznów sesję", Visible = false };
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 1000 };
    // Card PIN asked right on the lock card (FIDO2 cards), instead of a separate window.
    private readonly Label pinPrompt = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private readonly TextBox pinBox = new() { UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center, MaxLength = 63, Visible = false };
    private readonly Button pinOk = new() { Text = "Zaloguj", Visible = false };
    private readonly Button pinCancel = new() { Text = "Anuluj", Visible = false };
    private readonly System.Windows.Forms.Timer pinWatch = new() { Interval = 250 };
    private TaskCompletionSource<string?>? pinRequest;
    private Func<bool>? pinCardPresent;
    private bool locked;
    internal bool AskingPin => pinRequest != null;

    internal event Action? ResumeRequested;

    internal DesktopView()
    {
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        brand.Font = new Font("Segoe UI", 24, FontStyle.Bold);
        subtitle.Font = new Font("Segoe UI", 10);
        subtitle.ForeColor = KioskTheme.Muted;
        clock.Font = new Font("Segoe UI", 25, FontStyle.Regular);
        date.Font = new Font("Segoe UI", 10);
        date.ForeColor = KioskTheme.Muted;
        heading.Font = new Font("Segoe UI", 22, FontStyle.Bold);
        detail.Font = new Font("Segoe UI", 11);
        detail.ForeColor = KioskTheme.Muted;
        hint.Font = new Font("Segoe UI", 10);
        hint.ForeColor = KioskTheme.Muted;

        foreach (var label in new[] { brand, subtitle, clock, date }) label.BackColor = Color.Transparent;
        foreach (var label in new[] { heading, detail, hint, pinPrompt }) label.BackColor = KioskTheme.Surface;
        pinPrompt.Font = new Font("Segoe UI", 11);
        pinPrompt.ForeColor = KioskTheme.Muted;
        pinPrompt.Visible = false;
        pinBox.Font = new Font("Segoe UI", 20);
        pinBox.BackColor = KioskTheme.Background;
        pinBox.ForeColor = KioskTheme.Text;
        KioskTheme.StyleButton(resume, primary: true);
        KioskTheme.StyleButton(pinOk, primary: true);
        KioskTheme.StyleButton(pinCancel);
        pinOk.Click += (_, _) => FinishPin(true);
        pinCancel.Click += (_, _) => FinishPin(false);
        pinBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FinishPin(true); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; FinishPin(false); }
        };
        pinWatch.Tick += (_, _) => { if (pinCardPresent?.Invoke() == false) FinishPin(false); }; // Card taken away.
        KioskTheme.Changed += OnThemeChanged;
        resume.Click += (_, _) => ResumeRequested?.Invoke();
        card.Controls.AddRange(new Control[] { heading, detail, hint, resume, pinPrompt, pinBox, pinOk, pinCancel });
        Controls.AddRange(new Control[] { card, brand, subtitle, clock, date });
        clockTimer.Tick += (_, _) => UpdateClock();
        clockTimer.Start();
        UpdateClock();
        UpdateStatus("Witaj w Kiosku", "Przyłóż kartę, aby rozpocząć pracę.");
    }

    private void OnThemeChanged()
    {
        KioskTheme.StyleButton(resume, primary: true);
        KioskTheme.StyleButton(pinOk, primary: true);
        KioskTheme.StyleButton(pinCancel);
        Invalidate(true);
    }

    internal void UpdateStatus(string title, string description, bool locked = false, string buttonText = "Wznów sesję")
    {
        // Called on every shell tick; repaint only when something changed.
        if (heading.Text == title && detail.Text == description && this.locked == locked && resume.Text == buttonText) return;
        resume.Text = buttonText;
        this.locked = locked;
        heading.Text = title;
        detail.Text = description;
        hint.Text = locked ? "Twoje aplikacje czekają na wznowienie sesji." : "Aplikacje i połączenia znajdziesz w menu na dole.";
        resume.Visible = locked && !AskingPin;
        hint.Visible = !AskingPin;
        card.Locked = locked;
        ArrangeControls();
        card.Invalidate();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        clock.Text = now.ToString("HH:mm");
        date.Text = now.ToString("dddd, d MMMM", System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ArrangeControls();
    }

    private void ArrangeControls()
    {
        if (card == null) return;
        int margin = Scale(36);
        int clockWidth = Math.Min(Scale(290), Math.Max(Scale(150), Width / 3));
        brand.SetBounds(margin, Scale(24), Math.Max(0, Width - clockWidth - margin * 2), Scale(47));
        subtitle.SetBounds(margin + Scale(2), Scale(75), Math.Max(0, Width - clockWidth - margin * 2), Scale(25));
        clock.SetBounds(Width - clockWidth - margin, Scale(25), clockWidth, Scale(47));
        date.SetBounds(Width - clockWidth - margin, Scale(77), clockWidth, Scale(25));

        int cardWidth = Math.Min(Scale(660), Math.Max(Scale(240), Width - margin * 2));
        int cardHeight = Scale(AskingPin ? 470 : locked ? 370 : 324);
        int top = Math.Max(Scale(125), (Height - cardHeight) / 2 + Scale(12));
        card.SetBounds((Width - cardWidth) / 2, top, cardWidth, cardHeight);
        int innerMargin = Scale(30);
        int textWidth = Math.Max(0, cardWidth - innerMargin * 2);
        heading.SetBounds(innerMargin, Scale(110), textWidth, Scale(70));
        detail.SetBounds(innerMargin, Scale(191), textWidth, Scale(64));
        hint.SetBounds(innerMargin, Scale(locked ? 320 : 270), textWidth, Scale(35));
        int buttonWidth = Math.Min(Scale(220), textWidth);
        resume.SetBounds((cardWidth - buttonWidth) / 2, Scale(265), buttonWidth, Scale(43));
        int pinWidth = Math.Min(Scale(360), textWidth);
        int pinLeft = (cardWidth - pinWidth) / 2;
        pinPrompt.SetBounds(innerMargin, Scale(262), textWidth, Scale(30));
        pinBox.SetBounds(pinLeft, Scale(300), pinWidth, pinBox.PreferredHeight);
        int half = (pinWidth - Scale(12)) / 2;
        pinOk.SetBounds(pinLeft, Scale(385), half, Scale(46));
        pinCancel.SetBounds(pinLeft + half + Scale(12), Scale(385), half, Scale(46));
    }

    /// <summary>
    /// Shows the PIN field on the lock card and waits for it; null when cancelled or the card is taken away.
    /// The typed text is handed over once and the field is cleared.
    /// </summary>
    internal Task<string?> AskPinAsync(string message, Func<bool>? cardPresent)
    {
        pinRequest?.TrySetResult(null);
        pinRequest = new TaskCompletionSource<string?>();
        pinCardPresent = cardPresent;
        pinPrompt.Text = message;
        foreach (var c in new Control[] { pinPrompt, pinBox, pinOk, pinCancel }) c.Visible = true;
        resume.Visible = hint.Visible = false;
        ArrangeControls();
        card.Invalidate();
        pinBox.Clear();
        pinBox.Focus();
        pinWatch.Start();
        return pinRequest.Task;
    }

    private void FinishPin(bool accepted)
    {
        if (pinRequest == null || accepted && pinBox.TextLength == 0) return;
        var request = pinRequest;
        var value = accepted ? pinBox.Text : null;
        pinBox.Clear();
        pinWatch.Stop();
        pinRequest = null;
        foreach (var c in new Control[] { pinPrompt, pinBox, pinOk, pinCancel }) c.Visible = false;
        resume.Visible = locked;
        hint.Visible = true;
        ArrangeControls();
        card.Invalidate();
        request.TrySetResult(value);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        using var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(21, 36, 59), KioskTheme.Background, 70f);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var accent = new Pen(Color.FromArgb(20, KioskTheme.AccentLine), Scale(1));
        int ringSize = Scale(580);
        e.Graphics.DrawEllipse(accent, Width - ringSize / 2, -ringSize / 2, ringSize, ringSize);
        e.Graphics.DrawEllipse(accent, -ringSize / 2, Height - ringSize / 3, ringSize, ringSize);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { clockTimer.Dispose(); pinWatch.Dispose(); pinRequest?.TrySetResult(null); KioskTheme.Changed -= OnThemeChanged; }
        base.Dispose(disposing);
    }

    private sealed class StatusCard : Panel
    {
        internal bool Locked { get; set; }

        internal StatusCard()
        {
            BackColor = KioskTheme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 1 || Height <= 1) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
            using var cardPath = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(20));
            using var border = new Pen(KioskTheme.Border);
            g.DrawPath(border, cardPath);

            int diameter = S(62);
            var iconBounds = new Rectangle((Width - diameter) / 2, S(32), diameter, diameter);
            using var circle = new SolidBrush(KioskTheme.AccentSoft);
            g.FillEllipse(circle, iconBounds);
            using var line = new Pen(KioskTheme.AccentLine, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int centerX = Width / 2;
            int centerY = iconBounds.Top + diameter / 2;
            if (Locked)
            {
                g.DrawArc(line, centerX - S(9), centerY - S(15), S(18), S(23), 180, 180);
                using var lockPath = KioskTheme.RoundedRectangle(new Rectangle(centerX - S(14), centerY - S(2), S(28), S(21)), S(3));
                g.DrawPath(line, lockPath);
                g.DrawLine(line, centerX, centerY + S(6), centerX, centerY + S(11));
            }
            else
            {
                using var display = KioskTheme.RoundedRectangle(new Rectangle(centerX - S(17), centerY - S(12), S(34), S(23)), S(3));
                g.DrawPath(line, display);
                g.DrawLine(line, centerX, centerY + S(12), centerX, centerY + S(18));
                g.DrawLine(line, centerX - S(8), centerY + S(18), centerX + S(8), centerY + S(18));
            }
        }
    }
}

/// <summary>A keyboard-accessible launcher tile for the desktop's application menu.</summary>
internal sealed class MenuTile : Button
{
    private bool hovered;
    private bool pressed;
    private string category;

    internal MenuTile(string title, string category = "")
    {
        Text = title;
        this.category = category;
        Size = new Size(220, 84);
        Margin = new Padding(6);
        Padding = new Padding(16);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = KioskTheme.Surface;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 11, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleLeft;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal string Category
    {
        get => category;
        set { category = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
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
        using var tile = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(12));
        using var fill = new SolidBrush(pressed ? KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.22) : hovered ? Color.FromArgb(32, 48, 69) : KioskTheme.Surface);
        using var stroke = new Pen(hovered || Focused ? KioskTheme.AccentLine : KioskTheme.Border);
        g.FillPath(fill, tile);
        g.DrawPath(stroke, tile);
        var textColor = Enabled ? KioskTheme.Text : KioskTheme.Muted;
        int left = S(17);
        int right = S(17);
        int titleHeight = Math.Max(Font.Height + S(5), S(28));
        int titleTop = category.Length == 0 ? (Height - titleHeight) / 2 : S(17);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(left, titleTop, Math.Max(0, Width - left - right), titleHeight), textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (category.Length > 0)
        {
            using var smallFont = new Font("Segoe UI", 9);
            TextRenderer.DrawText(g, category, smallFont, new Rectangle(left, S(49), Math.Max(0, Width - left - right), S(22)), KioskTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -S(6), -S(6)), KioskTheme.AccentLine, BackColor);
    }
}
