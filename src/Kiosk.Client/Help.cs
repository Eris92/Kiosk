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
    private bool signedIn = true;
    /// <summary>Punkt samoobsługowy: nobody signed in with the card yet; buttons that need it are greyed out.</summary>
    internal bool SignedIn
    {
        get => signedIn;
        set { if (signedIn == value) return; signedIn = value; hover = -1; Invalidate(); }
    }
    private bool Available(HelpAction item) => signedIn || !item.NeedsSignIn;

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
        if (index >= 0 && index < items.Length && !Available(items[index])) index = -1;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (index != hover) { hover = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = buttons.FindIndex(b => b.Contains(e.Location));
        if (index >= 0 && index < items.Length && Available(items[index])) ActionRequested?.Invoke(items[index]);
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
            bool available = Available(item);
            int width = Vertical ? Width - 1 : ButtonWidth(item);
            var bounds = new Rectangle(x, y, width, ButtonHeight - 1);
            buttons.Add(bounds);
            using var path = KioskTheme.RoundedRectangle(bounds, S(10));
            // Greyed out until someone signs in with the card: dimmer tile, muted icon and text, no hover.
            using (var fill = new SolidBrush(Color.FromArgb(i == hover ? 255 : available ? 215 : 110, i == hover ? KioskTheme.AccentSoft : KioskTheme.Surface)))
                g.FillPath(fill, path);
            using (var border = new Pen(i == hover ? KioskTheme.AccentLine : available ? KioskTheme.Border : Color.FromArgb(120, KioskTheme.Border))) g.DrawPath(border, path);
            KioskIcons.Draw(g, new Rectangle(bounds.Left + S(10), bounds.Top, S(24), bounds.Height), GlyphFor(item), null, available ? KioskTheme.AccentLine : KioskTheme.Muted);
            TextRenderer.DrawText(g, item.Name, font, new Rectangle(bounds.Left + S(42), bounds.Top, bounds.Width - S(48), bounds.Height), available ? KioskTheme.Text : KioskTheme.Muted,
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
        // One bar: ← → ⟳, the page's name and a large close button (no second bar above the browser's).
        var close = new Button { Text = windowsAccount ? "Wróć do Kiosku" : "Zamknij" };
        KioskTheme.StyleButton(close, primary: true);
        close.Click += (_, _) => Close();
        browser.SetPageBar(title + (windowsAccount ? "" : "  ·  okno gościa: po zamknięciu nic nie zostaje zapisane"), close);
        countdown.BackColor = KioskTheme.ShellBar;
        countdown.Expired += Close;
        Controls.Add(browser);
        Controls.Add(countdown);
        // Nobody uses the page (click, touch, key, scroll) or this window for the time set in Konfiguracja → Szybka pomoc: it closes.
        // Counted here rather than from Windows' last input, which other programs and remote sessions keep fresh.
        browser.Activity += Touched;
        HelpCountdown.Watch(this, Touched);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private void Touched() => countdown.Restart();

    private bool started;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Start();
    }

    /// <summary>Starts the page and its countdown (once). Inside the Kiosk's window (TopLevel = false) the Kiosk calls it.</summary>
    internal async void Start()
    {
        if (started) return;
        started = true;
        countdown.Start(idleSeconds);
        try { await browser.InitializeAsync(url); }
        catch (Exception ex)
        {
            Audit.Write("help_browser_error", ex.Message);
            // In the page's place, no message window: the countdown or the close button returns to the Kiosk.
            var error = new Label
            {
                Text = "Nie udało się otworzyć strony: " + ex.Message, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13), BackColor = KioskTheme.Background, ForeColor = KioskTheme.Text
            };
            Controls.Add(error);
            error.BringToFront();
            browser.SendToBack();
        }
    }

}

/// <summary>
/// A site's own sign-in (login and password) when the Windows account's sign-in did not get the person in, like the
/// prompt a normal Edge shows. A panel over the page in the same window (no window of its own that jumps into place);
/// nobody types for the help time: it cancels by itself. The password is handed to the page only, never kept anywhere.
/// </summary>
internal sealed class HelpSignIn : Panel
{
    private readonly HelpCountdown countdown = new();
    private readonly TextBox user = new() { BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox password = new() { BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true };
    private readonly Label heading = new() { Text = "Zaloguj się", AutoSize = false, Font = new Font("Segoe UI", 16, FontStyle.Bold) };
    private readonly Label message = new() { AutoSize = false, ForeColor = KioskTheme.Muted, Font = new Font("Segoe UI", 11) };
    private readonly Label userCaption = new() { Text = "Użytkownik", AutoSize = false, Font = new Font("Segoe UI", 11), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label passwordCaption = new() { Text = "Hasło", AutoSize = false, Font = new Font("Segoe UI", 11), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button ok = new() { Text = "Zaloguj" }, cancel = new() { Text = "Anuluj" };
    private readonly TaskCompletionSource<(string User, string Password)?> answer = new();

    private HelpSignIn(string host, string userName)
    {
        BackColor = KioskTheme.Surface;
        ForeColor = KioskTheme.Text;
        message.Text = "Strona " + host + " prosi o login i hasło Windows.";
        foreach (var box in new[] { user, password })
        {
            box.BackColor = KioskTheme.Background;
            box.ForeColor = KioskTheme.Text;
            box.Font = new Font("Segoe UI", 13f);
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Finish(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(false); }
            };
        }
        user.Text = userName;
        KioskTheme.StyleButton(cancel);
        KioskTheme.StyleButton(ok, primary: true);
        ok.Click += (_, _) => Finish(true);
        cancel.Click += (_, _) => Finish(false);
        countdown.BackColor = KioskTheme.Surface;
        countdown.Expired += () => Finish(false);
        Controls.AddRange(new Control[] { heading, message, userCaption, user, passwordCaption, password, ok, cancel, countdown });
        Paint += (_, e) => { using var border = new Pen(KioskTheme.Border); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); };
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private void Arrange()
    {
        // Sizes for 96 DPI, scaled to the screen like the fonts (at 240 DPI fixed sizes cut the text off).
        if (Parent == null) return;
        int width = Math.Min(Scale(600), Parent.ClientSize.Width - Scale(24)), height = Math.Min(Scale(400), Parent.ClientSize.Height - Scale(24));
        SetBounds((Parent.ClientSize.Width - width) / 2, (Parent.ClientSize.Height - height) / 2, width, height);
        int margin = Scale(28), inner = width - 2 * margin, caption = Scale(120), field = inner - caption;
        heading.SetBounds(margin, Scale(22), inner, Scale(40));
        message.SetBounds(margin, Scale(66), inner, Scale(52));
        userCaption.SetBounds(margin, Scale(130), caption, Scale(36));
        user.SetBounds(margin + caption, Scale(130), field, Scale(36));
        passwordCaption.SetBounds(margin, Scale(182), caption, Scale(36));
        password.SetBounds(margin + caption, Scale(182), field, Scale(36));
        int button = (inner - Scale(12)) / 2;
        ok.SetBounds(margin, Scale(250), button, Scale(46));
        cancel.SetBounds(margin + button + Scale(12), Scale(250), button, Scale(46));
        countdown.SetBounds(margin, Scale(316), inner, Scale(30));
    }

    private void Finish(bool accepted)
    {
        if (answer.Task.IsCompleted) return;
        var typed = accepted && user.TextLength > 0 ? (user.Text.Trim(), password.Text) : ((string, string)?)null;
        password.Clear();
        countdown.Stop();
        var parent = Parent;
        if (parent != null) { parent.Resize -= OnParentResize; parent.Controls.Remove(this); }
        answer.TrySetResult(typed);
        Dispose();
    }

    private void OnParentResize(object? sender, EventArgs e) => Arrange();

    /// <summary>The login and password typed, or null when cancelled.</summary>
    internal static Task<(string User, string Password)?> AskAsync(Control parent, string host, string userName, int idleSeconds)
    {
        var panel = new HelpSignIn(host, userName);
        parent.Controls.Add(panel);
        panel.BringToFront();
        panel.Arrange();
        parent.Resize += panel.OnParentResize;
        HelpCountdown.Watch(panel, panel.countdown.Restart);
        panel.countdown.Start(idleSeconds);
        (panel.user.TextLength == 0 ? panel.user : panel.password).Focus();
        return panel.answer.Task;
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
