using System.Drawing.Drawing2D;

namespace Kiosk.Client;

/// <summary>
/// Szybka pomoc on the desktop: buttons that work locked and unlocked (MFA, password reset, card PIN…).
/// A column at the left when there is room beside the card, otherwise a row under it.
/// </summary>
internal sealed class HelpBar : Control
{
    private HelpAction[] items = [];
    private readonly List<Rectangle> buttons = [];
    private int hover = -1;

    internal bool Vertical { get; set; } = true;
    internal int Count => items.Length;
    internal HelpAction[] Items => items;
    internal event Action<HelpAction>? ActionRequested;

    internal HelpBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    internal void SetItems(HelpAction[] actions) { items = actions; Invalidate(); }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private int HeadingHeight => S(30);
    private int ButtonHeight => S(40);
    private int Gap => S(8);

    private static string GlyphFor(HelpAction item) => item.Action.ToLowerInvariant() switch
    {
        HelpAction.ChangePin => "",   // key
        HelpAction.Reenroll => "",    // person
        HelpAction.FirstSignIn => "\uE77B",  // contact
        HelpAction.ResetCard => "",   // restore
        var a when a.Contains("mfa") || a.Contains("security") => "", // lock
        _ => ""                        // globe
    };

    private int TextWidth(HelpAction item)
    {
        using var font = new Font("Segoe UI", 10);
        return TextRenderer.MeasureText(item.Name, font).Width;
    }

    private int ButtonWidth(HelpAction item) => S(52) + TextWidth(item);

    /// <summary>The size the bar needs: the widest button in a column, or all buttons side by side.</summary>
    internal Size Needed(bool vertical)
    {
        if (items.Length == 0) return Size.Empty;
        return vertical
            ? new Size(items.Max(ButtonWidth), HeadingHeight + items.Length * ButtonHeight + (items.Length - 1) * Gap)
            : new Size(items.Sum(ButtonWidth) + (items.Length - 1) * Gap, ButtonHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = buttons.FindIndex(b => b.Contains(e.Location));
        if (index != hover) { hover = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = buttons.FindIndex(b => b.Contains(e.Location));
        if (index >= 0 && index < items.Length) ActionRequested?.Invoke(items[index]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        buttons.Clear();
        if (items.Length == 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 10);
        int x = 0, y = 0;
        if (Vertical)
        {
            using var heading = new Font("Segoe UI", 10, FontStyle.Bold);
            TextRenderer.DrawText(g, "Szybka pomoc", heading, new Rectangle(S(4), 0, Width, HeadingHeight), KioskTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            y = HeadingHeight;
        }
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            int width = Vertical ? Width - 1 : ButtonWidth(item);
            var bounds = new Rectangle(x, y, width, ButtonHeight - 1);
            buttons.Add(bounds);
            using var path = KioskTheme.RoundedRectangle(bounds, S(10));
            using (var fill = new SolidBrush(Color.FromArgb(i == hover ? 255 : 215, i == hover ? KioskTheme.AccentSoft : KioskTheme.Surface)))
                g.FillPath(fill, path);
            using (var border = new Pen(i == hover ? KioskTheme.AccentLine : KioskTheme.Border)) g.DrawPath(border, path);
            KioskIcons.Draw(g, new Rectangle(bounds.Left + S(10), bounds.Top, S(24), bounds.Height), GlyphFor(item), null, KioskTheme.AccentLine);
            TextRenderer.DrawText(g, item.Name, font, new Rectangle(bounds.Left + S(42), bounds.Top, bounds.Width - S(48), bounds.Height), KioskTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Vertical) y += ButtonHeight + Gap; else x += width + Gap;
        }
    }
}

/// <summary>
/// A guest browser over the Kiosk for Szybka pomoc while nobody is signed in (or the session is locked):
/// InPrivate, one page without tabs, only Microsoft sign-in sites and the help page's own site,
/// closed with its button or after the set time without activity.
/// </summary>
internal sealed class HelpBrowserForm : Form
{
    internal static readonly string[] SignInDomains =
    [
        "aka.ms", "microsoft.com", "microsoftonline.com", "microsoftonline-p.com", "live.com", "msauth.net", "msftauth.net",
        "windows.net", "office.com", "office365.com", "microsoftazuread-sso.com", "azure.com", "msidentity.com",
        "microsoft365.com", "cloud.microsoft", "sharepoint.com", "sharepointonline.com"
    ];

    private readonly BrowserView browser;
    private readonly string url;
    private readonly int idleSeconds;
    private readonly HelpCountdown countdown = new() { Dock = DockStyle.Bottom };

    /// <param name="windowsAccount">Punkt samoobsługowy, signed in with the card: this program runs as the person
    /// (<see cref="CardPageHost"/>), the page signs in with their Windows account (SSO) and keeps <paramref name="profileFolder"/>.</param>
    internal HelpBrowserForm(string title, string url, IEnumerable<string> extraDomains, int idleSeconds = 60, bool windowsAccount = false, string? profileFolder = null)
    {
        this.idleSeconds = idleSeconds;
        this.url = url;
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        // Hosted by the Kiosk (windowsAccount): an unowned window the Kiosk finds and keeps over its own screen.
        ShowInTaskbar = windowsAccount;
        TopMost = !windowsAccount;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        var policy = new SitePolicy(true, [], SignInDomains.Concat(extraDomains).Append(host));
        policy.Trust(url);
        browser = new BrowserView([], policy, null, windowsAccount: windowsAccount, singlePage: true, profileFolder: profileFolder) { Dock = DockStyle.Fill };
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = KioskTheme.ShellBar };
        var heading = new Label
        {
            Text = title + (windowsAccount ? "" : "  ·  okno gościa: po zamknięciu nic nie zostaje zapisane"), AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), Font = new Font("Segoe UI", 11), ForeColor = KioskTheme.Text
        };
        var close = new Button { Text = windowsAccount ? "Wróć do Kiosku" : "Zamknij", Dock = DockStyle.Right, Width = windowsAccount ? 180 : 140 };
        KioskTheme.StyleButton(close, primary: true);
        close.Click += (_, _) => Close();
        header.Controls.Add(heading);
        header.Controls.Add(close);
        countdown.BackColor = KioskTheme.ShellBar;
        countdown.Expired += Close;
        Controls.Add(browser);
        Controls.Add(countdown);
        Controls.Add(header);
        // Nobody uses the page (click, touch, key, scroll) or this window for the time set in Konfiguracja → Szybka pomoc: it closes.
        // Counted here rather than from Windows' last input, which other programs and remote sessions keep fresh.
        browser.Activity += Touched;
        HelpCountdown.Watch(this, Touched);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private void Touched() => countdown.Restart();

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        countdown.Start(idleSeconds);
        try { await browser.InitializeAsync(url); }
        catch (Exception ex)
        {
            Audit.Write("help_browser_error", ex.Message);
            MessageBox.Show(this, "Nie udało się otworzyć strony: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

}

/// <summary>
/// A yes / no question for Szybka pomoc, drawn like the rest of the Kiosk (no title bar). Nobody answers, moves the mouse
/// or presses a key for the help time set in Konfiguracja: it answers "no" by itself. The bar shows the time left.
/// </summary>
internal sealed class HelpConfirm : Form
{
    private readonly HelpCountdown countdown = new() { Dock = DockStyle.Bottom };

    private HelpConfirm(string title, string text, int idleSeconds, bool warning)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = KioskTheme.Surface;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 10.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 290);
        Padding = new Padding(1);
        var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 52, Padding = new Padding(24, 18, 24, 0), Font = new Font("Segoe UI", 14, FontStyle.Bold) };
        var message = new Label { Text = text, Dock = DockStyle.Fill, Padding = new Padding(24, 10, 24, 8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10), BackColor = Color.Transparent };
        var no = new Button { Text = "Nie", Width = 140, Height = 40, DialogResult = DialogResult.No };
        var yes = new Button { Text = warning ? "Tak, wykonaj" : "Tak", Width = 160, Height = 40, DialogResult = DialogResult.Yes };
        KioskTheme.StyleButton(no, primary: warning);
        KioskTheme.StyleButton(yes, primary: !warning);
        buttons.Controls.Add(no);
        buttons.Controls.Add(yes);
        countdown.BackColor = KioskTheme.Surface;
        countdown.Padding = new Padding(24, 0, 24, 10);
        Controls.Add(message);
        Controls.Add(heading);
        Controls.Add(buttons);
        Controls.Add(countdown);
        AcceptButton = warning ? no : yes; // A dangerous step is never the default.
        CancelButton = no;
        Paint += (_, e) => { using var border = new Pen(KioskTheme.Border); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); };
        HelpCountdown.Watch(this, countdown.Restart);
        countdown.Expired += () => DialogResult = DialogResult.No;
        Shown += (_, _) => countdown.Start(idleSeconds);
    }

    internal static bool Ask(IWin32Window owner, string title, string text, int idleSeconds, bool warning = false)
    {
        using var dialog = new HelpConfirm(title, text, idleSeconds, warning);
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    }
}

/// <summary>
/// The Szybka pomoc countdown, the same everywhere (help page, yes / no questions, PIN field): the seconds left
/// and a bar in the accent colour that shrinks. Restart() on activity; Expired when the time is up.
/// </summary>
internal sealed class HelpCountdown : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
    private DateTime last = DateTime.UtcNow;
    private int seconds = 60;
    internal event Action? Expired;

    internal HelpCountdown()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = KioskTheme.Surface;
        timer.Tick += (_, _) =>
        {
            if (SecondsLeft > 0) { Invalidate(); return; }
            timer.Stop();
            Expired?.Invoke();
        };
    }

    private double SecondsLeft => seconds - (DateTime.UtcNow - last).TotalSeconds;
    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(30) + Padding.Vertical; }

    internal void Start(int idleSeconds) { seconds = Math.Max(1, idleSeconds); last = DateTime.UtcNow; timer.Start(); Invalidate(); }
    internal void Stop() => timer.Stop();
    internal void Restart() { last = DateTime.UtcNow; Invalidate(); }

    /// <summary>Activity in a window: a real mouse move (Windows repeats "move" over a resting cursor), a click or a key.</summary>
    internal static void Watch(Control root, Action touched)
    {
        var lastCursor = Cursor.Position;
        void Hook(Control control)
        {
            control.MouseMove += (_, _) => { if (Cursor.Position != lastCursor) { lastCursor = Cursor.Position; touched(); } };
            control.MouseDown += (_, _) => touched();
            control.KeyDown += (_, _) => touched();
            foreach (Control child in control.Controls) Hook(child);
            control.ControlAdded += (_, e) => { if (e.Control != null) Hook(e.Control); };
        }
        Hook(root);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var area = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
        double left = Math.Max(0, SecondsLeft);
        using var font = new Font("Segoe UI", 9.5f);
        TextRenderer.DrawText(g, "Bez aktywności zamknie się za " + (int)Math.Ceiling(left) + " s", font,
            new Rectangle(area.Left, area.Top, area.Width, area.Height - S(8)), KioskTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        var track = new Rectangle(area.Left, area.Bottom - S(5), area.Width, S(5));
        using (var back = new SolidBrush(KioskTheme.Border)) g.FillRectangle(back, track);
        using var fill = new SolidBrush(KioskTheme.AccentLine);
        g.FillRectangle(fill, new Rectangle(track.Left, track.Top, (int)(track.Width * left / seconds), track.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
