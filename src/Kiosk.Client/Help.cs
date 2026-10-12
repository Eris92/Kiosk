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
        var heading = new Label
        {
            Text = title + "  ·  okno gościa: po zamknięciu nic nie zostaje zapisane  ·  zamknie się po " + idleSeconds + " s bez aktywności", AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), Font = new Font("Segoe UI", 11), ForeColor = KioskTheme.Text
        };
        var close = new Button { Text = "Zamknij", Dock = DockStyle.Right, Width = 140 };
        KioskTheme.StyleButton(close, primary: true);
        close.Click += (_, _) => Close();
        header.Controls.Add(heading);
        header.Controls.Add(close);
        Controls.Add(browser);
        Controls.Add(header);
        // Nobody touches the mouse, keyboard or screen for the time set in Konfiguracja → Szybka pomoc: the window closes.
        limit.Tick += (_, _) => { try { if (Native.IdleMilliseconds() >= idleSeconds * 1000L) Close(); } catch (Exception) { } };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
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
