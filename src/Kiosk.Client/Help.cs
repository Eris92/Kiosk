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
    private static readonly string[] SignInDomains =
    [
        "aka.ms", "microsoft.com", "microsoftonline.com", "microsoftonline-p.com", "live.com", "msauth.net", "msftauth.net",
        "windows.net", "office.com", "office365.com", "microsoftazuread-sso.com", "azure.com", "msidentity.com"
    ];

    private readonly BrowserView browser;
    private readonly string url;
    private readonly System.Windows.Forms.Timer limit = new() { Interval = 1000 };
    private readonly int idleSeconds;
    private DateTime lastActivity = DateTime.UtcNow;
    private readonly Label heading;
    private readonly string headingText;

    internal HelpBrowserForm(string title, string url, IEnumerable<string> extraDomains, int idleSeconds = 60)
    {
        this.idleSeconds = idleSeconds;
        this.url = url;
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        var policy = new SitePolicy(true, [], SignInDomains.Concat(extraDomains).Append(host));
        policy.Trust(url);
        browser = new BrowserView([], policy, null, windowsAccount: false, singlePage: true) { Dock = DockStyle.Fill };
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = KioskTheme.ShellBar };
        headingText = title + "  ·  okno gościa: po zamknięciu nic nie zostaje zapisane  ·  zamknie się po " + idleSeconds + " s bez aktywności";
        heading = new Label
        {
            Text = headingText, AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), Font = new Font("Segoe UI", 11), ForeColor = KioskTheme.Text
        };
        var close = new Button { Text = "Zamknij", Dock = DockStyle.Right, Width = 140 };
        KioskTheme.StyleButton(close, primary: true);
        close.Click += (_, _) => Close();
        header.Controls.Add(heading);
        header.Controls.Add(close);
        Controls.Add(browser);
        Controls.Add(header);
        // Nobody uses the page (click, touch, key, scroll) or this window for the time set in Konfiguracja → Szybka pomoc: it closes.
        // Counted here rather than from Windows' last input, which other programs and remote sessions keep fresh.
        browser.Activity += Touched;
        header.MouseMove += (_, _) => Touched();
        heading.MouseMove += (_, _) => Touched();
        limit.Tick += (_, _) =>
        {
            var left = idleSeconds - (int)(DateTime.UtcNow - lastActivity).TotalSeconds;
            if (left <= 0) { Close(); return; }
            heading.Text = left <= 5 ? title + "  ·  brak aktywności: okno zamknie się za " + left + " s" : headingText;
        };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private void Touched() => lastActivity = DateTime.UtcNow;

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Touched();
        limit.Start();
        try { await browser.InitializeAsync(url); }
        catch (Exception ex)
        {
            Audit.Write("help_browser_error", ex.Message);
            MessageBox.Show(this, "Nie udało się otworzyć strony: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) limit.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A yes / no question for Szybka pomoc, drawn like the rest of the Kiosk (no title bar). Nobody answers, moves the mouse
/// or presses a key for the help time (at least 15 s, the text needs reading): it answers "no" by itself. The bar shows the time left.
/// </summary>
internal sealed class HelpConfirm : Form
{
    internal const int MinimumSeconds = 15;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
    private readonly int seconds;
    private DateTime lastActivity = DateTime.UtcNow;
    private readonly Panel progress = new() { Dock = DockStyle.Bottom, Height = 4 };
    private readonly Label countdown;

    private HelpConfirm(string title, string text, int idleSeconds, bool warning)
    {
        seconds = Math.Max(MinimumSeconds, idleSeconds);
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
        countdown = new Label { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(24, 0, 24, 0), ForeColor = KioskTheme.Muted };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10), BackColor = Color.Transparent };
        var no = new Button { Text = "Nie", Width = 140, Height = 40, DialogResult = DialogResult.No };
        var yes = new Button { Text = warning ? "Tak, wykonaj" : "Tak", Width = 160, Height = 40, DialogResult = DialogResult.Yes };
        KioskTheme.StyleButton(no, primary: warning);
        KioskTheme.StyleButton(yes, primary: !warning);
        buttons.Controls.Add(no);
        buttons.Controls.Add(yes);
        progress.BackColor = warning ? Color.FromArgb(198, 40, 40) : KioskTheme.AccentLine;
        Controls.Add(message);
        Controls.Add(heading);
        Controls.Add(countdown);
        Controls.Add(buttons);
        Controls.Add(progress);
        AcceptButton = warning ? no : yes; // A dangerous step is never the default.
        CancelButton = no;
        Paint += (_, e) => { using var border = new Pen(KioskTheme.Border); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); };
        KeyDown += (_, _) => Touched();
        Watch(this);
        timer.Tick += (_, _) => ShowLeft();
        Shown += (_, _) => { Touched(); ShowLeft(); timer.Start(); };
    }

    private void Watch(Control control)
    {
        control.MouseMove += (_, _) => Touched();
        control.MouseDown += (_, _) => Touched();
        foreach (Control child in control.Controls) Watch(child);
    }

    private void Touched() => lastActivity = DateTime.UtcNow;

    private void ShowLeft()
    {
        double left = seconds - (DateTime.UtcNow - lastActivity).TotalSeconds;
        if (left <= 0) { timer.Stop(); DialogResult = DialogResult.No; return; }
        countdown.Text = "Bez odpowiedzi okno zamknie się za " + (int)Math.Ceiling(left) + " s (ruch myszy wydłuża czas).";
        progress.Width = (int)(ClientSize.Width * left / seconds);
        progress.Dock = DockStyle.None;
        progress.SetBounds(0, ClientSize.Height - progress.Height, progress.Width, progress.Height);
    }

    internal static bool Ask(IWin32Window owner, string title, string text, int idleSeconds, bool warning = false)
    {
        using var dialog = new HelpConfirm(title, text, idleSeconds, warning);
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
