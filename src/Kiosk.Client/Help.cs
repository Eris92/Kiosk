using System.Drawing.Drawing2D;

namespace Kiosk.Client;

/// <summary>
/// Szybka pomoc on the desktop: buttons that work locked and unlocked (MFA, password reset, card PIN…).
/// A column at the left when there is room beside the card, otherwise a row under it.
/// </summary>
internal sealed class HelpBar : Control
{
    private HelpAction[] items = [];
    /// <summary>One line of the column: a button, a category (folder) or "Wstecz".</summary>
    private sealed record Entry(HelpAction? Item, string? Folder, string Text, bool Back = false);
    private Entry[] entries = [];
    /// <summary>The open category ("" = the top: categories and buttons without one).</summary>
    private string folder = "";
    private readonly List<Rectangle> buttons = [];
    private int hover = -1;

    internal bool Vertical { get; set; } = true;
    internal int Count => items.Length;
    internal HelpAction[] Items => items;
    internal event Action<HelpAction>? ActionRequested;
    /// <summary>A category opened or closed: the column has another size.</summary>
    internal event Action? ContentChanged;
    private bool signedIn = true;
    /// <summary>Punkt samoobsługowy: nobody signed in with the card yet; buttons that need it are greyed out.</summary>
    internal bool SignedIn
    {
        get => signedIn;
        set
        {
            if (signedIn == value) return;
            signedIn = value;
            hover = -1;
            // Someone else comes to the kiosk: the column starts from the top again.
            if (folder.Length > 0) OpenFolder("");
            Invalidate();
        }
    }
    private bool busy;
    /// <summary>A question or the PIN field waits in the card: every button is greyed out until it is answered.</summary>
    internal bool Busy
    {
        get => busy;
        set { if (busy == value) return; busy = value; hover = -1; Invalidate(); }
    }
    private bool Available(HelpAction item) => !busy && (signedIn || !item.NeedsSignIn);
    private bool Available(Entry entry) => entry.Item != null ? Available(entry.Item)
        : !busy && (entry.Back || items.Any(i => InFolder(i, entry.Folder!) && Available(i)));

    private static bool InFolder(HelpAction item, string path) =>
        item.CategoryPath.Equals(path, StringComparison.OrdinalIgnoreCase) || item.CategoryPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);

    internal HelpBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    internal void SetItems(HelpAction[] actions)
    {
        items = actions;
        // The open category may be gone after Konfiguracja changed.
        if (folder.Length > 0 && !items.Any(i => InFolder(i, folder))) folder = "";
        Build();
        Invalidate();
    }

    /// <summary>The lines of the open category, in the order of the list in Konfiguracja.</summary>
    private void Build()
    {
        var list = new List<Entry>();
        if (folder.Length > 0) list.Add(new Entry(null, folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "", "Wstecz", Back: true));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var path = item.CategoryPath;
            if (path.Equals(folder, StringComparison.OrdinalIgnoreCase)) { list.Add(new Entry(item, null, item.Name)); continue; }
            if (folder.Length > 0 && !path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = folder.Length > 0 ? path[(folder.Length + 1)..] : path;
            var name = rest.Split('/')[0];
            if (seen.Add(name)) list.Add(new Entry(null, folder.Length > 0 ? folder + "/" + name : name, name));
        }
        entries = list.ToArray();
    }

    private void OpenFolder(string path)
    {
        folder = path;
        hover = -1;
        Build();
        Invalidate();
        ContentChanged?.Invoke();
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private int HeadingHeight => S(30);
    private int ButtonHeight => S(40);
    private int Gap => S(8);
    private string Heading => folder.Length == 0 ? "Menu" : folder.Replace("/", " › ");

    private static string GlyphFor(Entry entry)
    {
        if (entry.Back) return "\uE72B";        // back arrow
        if (entry.Item == null) return "\uE8B7";  // folder
        return entry.Item.Action.ToLowerInvariant() switch
        {
            HelpAction.ChangePin => "\uE8D7",    // key
            HelpAction.Reenroll => "\uE77B",     // person
            HelpAction.FirstSignIn => "\uE77B",  // contact
            HelpAction.ResetCard => "\uE777",    // restore
            var a when a.Contains("mfa") || a.Contains("security") => "\uE72E", // lock
            _ => "\uE774"                         // globe
        };
    }

    private int TextWidth(Entry entry)
    {
        using var font = new Font("Segoe UI", 10);
        return TextRenderer.MeasureText(entry.Text, font).Width;
    }

    // Icon, text and, for a category, the arrow at the right.
    private int ButtonWidth(Entry entry) => S(52) + TextWidth(entry) + (entry.Folder != null && !entry.Back ? S(24) : 0);

    /// <summary>The size the bar needs: the widest button in a column, or all buttons side by side.</summary>
    internal Size Needed(bool vertical)
    {
        if (entries.Length == 0) return Size.Empty;
        return vertical
            ? new Size(ColumnWidth(), HeadingHeight + entries.Length * ButtonHeight + (entries.Length - 1) * Gap)
            : new Size(entries.Sum(ButtonWidth) + (entries.Length - 1) * Gap, ButtonHeight);
    }

    /// <summary>
    /// The same width in every category (the widest of them all), so the page beside the column does not move when
    /// a category opens.
    /// </summary>
    private int ColumnWidth()
    {
        using var bold = new Font("Segoe UI", 10, FontStyle.Bold);
        int width = ButtonWidth(new Entry(null, "", "Wstecz", Back: true));
        foreach (var item in items)
        {
            width = Math.Max(width, ButtonWidth(new Entry(item, null, item.Name)));
            var parts = item.CategoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                width = Math.Max(width, ButtonWidth(new Entry(null, parts[i], parts[i])));
                width = Math.Max(width, TextRenderer.MeasureText(string.Join(" › ", parts[..(i + 1)]), bold).Width + S(8));
            }
        }
        return Math.Max(width, TextRenderer.MeasureText("Menu", bold).Width + S(8));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = buttons.FindIndex(b => b.Contains(e.Location));
        if (index >= 0 && index < entries.Length && !Available(entries[index])) index = -1;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (index != hover) { hover = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = buttons.FindIndex(b => b.Contains(e.Location));
        if (index < 0 || index >= entries.Length || !Available(entries[index])) return;
        var entry = entries[index];
        if (entry.Item != null) ActionRequested?.Invoke(entry.Item);
        else OpenFolder(entry.Folder!);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        buttons.Clear();
        if (entries.Length == 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 10);
        int x = 0, y = 0;
        if (Vertical)
        {
            using var heading = new Font("Segoe UI", 10, FontStyle.Bold);
            TextRenderer.DrawText(g, Heading, heading, new Rectangle(S(4), 0, Width, HeadingHeight), KioskTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            y = HeadingHeight;
        }
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            bool available = Available(entry);
            int width = Vertical ? Width - 1 : ButtonWidth(entry);
            var bounds = new Rectangle(x, y, width, ButtonHeight - 1);
            buttons.Add(bounds);
            using var path = KioskTheme.RoundedRectangle(bounds, S(10));
            // Greyed out until someone signs in with the card: dimmer tile, muted icon and text, no hover.
            using (var fill = new SolidBrush(Color.FromArgb(i == hover ? 255 : available ? 215 : 110, i == hover ? KioskTheme.AccentSoft : KioskTheme.Surface)))
                g.FillPath(fill, path);
            using (var border = new Pen(i == hover ? KioskTheme.AccentLine : available ? KioskTheme.Border : Color.FromArgb(120, KioskTheme.Border))) g.DrawPath(border, path);
            KioskIcons.Draw(g, new Rectangle(bounds.Left + S(10), bounds.Top, S(24), bounds.Height), GlyphFor(entry), null, available ? KioskTheme.AccentLine : KioskTheme.Muted);
            bool category = entry.Folder != null && !entry.Back;
            TextRenderer.DrawText(g, entry.Text, font, new Rectangle(bounds.Left + S(42), bounds.Top, bounds.Width - S(category ? 72 : 48), bounds.Height),
                available ? KioskTheme.Text : KioskTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (category)
                KioskIcons.Draw(g, new Rectangle(bounds.Right - S(30), bounds.Top, S(20), bounds.Height), "\uE76C", null, KioskTheme.Muted);   // chevron
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
    /// <param name="onlyListed">Konfiguracja → Przeglądarka "tylko strony z listy": links to other sites are blocked;
    /// off, links on the page (a SharePoint page pointing elsewhere) open any site.</param>
    internal HelpBrowserForm(string title, string url, IEnumerable<string> extraDomains, int idleSeconds = 60, bool windowsAccount = false, string? profileFolder = null, bool onlyListed = true)
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
        var policy = new SitePolicy(onlyListed, [], SignInDomains.Concat(extraDomains).Append(host));
        policy.Trust(url);
        browser = new BrowserView([], policy, null, windowsAccount: windowsAccount, singlePage: true, profileFolder: profileFolder) { Dock = DockStyle.Fill };
        // One bar: ← → ⟳, the page's name and a large close button (no second bar above the browser's).
        var close = new Button { Text = "Zamknij" };   // The same word everywhere (signed in or guest).
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
    /// <summary>Someone reads the element beside the page: the page's countdown starts again.</summary>
    internal void Touch() => countdown.Restart();

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
/// Menu → "Obok": a second page or a file (a PDF with instructions, shown by the browser itself) at the right of the
/// page. A small bar with its name and "Ukryj"; reading it counts as using the page (<see cref="Activity"/>).
/// </summary>
internal sealed class SidePage : Panel
{
    private readonly BrowserView browser;
    private readonly string url;
    private readonly Button show = new() { Text = "◀\nPokaż", Dock = DockStyle.Fill, Visible = false };
    internal event Action? Activity;
    /// <summary>"Ukryj" or "Pokaż" pressed: the page's frame gives the strip or the half back.</summary>
    internal event Action? Toggled;
    /// <summary>Folded to a narrow strip at the right with "Pokaż"; the page takes the width.</summary>
    internal bool Collapsed { get; private set; }

    internal SidePage(string title, string url, IEnumerable<string> domains, bool onlyListed)
    {
        this.url = url;
        BackColor = KioskTheme.Background;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.Host : "";
        var policy = new SitePolicy(onlyListed, [], HelpBrowserForm.SignInDomains.Concat(domains).Append(host));
        policy.Trust(url);
        browser = new BrowserView([], policy, null, singlePage: true) { Dock = DockStyle.Fill };
        var hide = new Button { Text = "Ukryj" };
        KioskTheme.StyleButton(hide);
        hide.Click += (_, _) => SetCollapsed(true);
        browser.SetPageBar(title, hide);
        browser.Activity += () => Activity?.Invoke();
        KioskTheme.StyleButton(show, primary: true);
        show.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        show.Click += (_, _) => SetCollapsed(false);
        Controls.Add(browser);
        Controls.Add(show);
    }

    private void SetCollapsed(bool value)
    {
        Collapsed = value;
        browser.Visible = !value;
        show.Visible = value;
        Activity?.Invoke();
        Toggled?.Invoke();
    }

    internal async void Start()
    {
        try { await browser.InitializeAsync(url); }
        catch (Exception ex)
        {
            Audit.Write("side_page_error", ex.Message);
            var error = new Label
            {
                Text = "Nie udało się otworzyć: " + ex.Message, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12), BackColor = KioskTheme.Background, ForeColor = KioskTheme.Text
            };
            Controls.Add(error);
            error.BringToFront();
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
