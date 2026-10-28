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

    private BrowserView? browser;
    private string url = "";
    private int idleSeconds;
    private readonly HelpCountdown countdown = new() { Dock = DockStyle.Bottom };
    private readonly bool windowsAccount;
    private readonly string? profileFolder;
    /// <summary>
    /// Punkt samoobsługowy: one window for the whole card session (<see cref="CardPageHost"/>). "Zamknij" and the
    /// countdown only park it (<see cref="Park"/>); the next page opens in it at once, without starting this program again.
    /// </summary>
    private readonly bool reusable;
    /// <summary>A blank page that keeps the browser running between pages (a reusable window only).</summary>
    private BrowserView? keeper;

    /// <param name="windowsAccount">Punkt samoobsługowy, signed in with the card: this program runs as the person
    /// (<see cref="CardPageHost"/>), the page signs in with their Windows account (SSO) and keeps <paramref name="profileFolder"/>.</param>
    /// <param name="onlyListed">Konfiguracja → Przeglądarka "tylko strony z listy": links to other sites are blocked;
    /// off, links on the page (a SharePoint page pointing elsewhere) open any site.</param>
    internal HelpBrowserForm(string title, string url, IEnumerable<string> extraDomains, int idleSeconds = 60, bool windowsAccount = false, string? profileFolder = null, bool onlyListed = true)
        : this(windowsAccount, profileFolder, reusable: false)
    {
        LoadPage(title, url, extraDomains, idleSeconds, onlyListed);
    }

    /// <summary>The reusable window of a card session, without a page until <see cref="Open"/>.</summary>
    internal HelpBrowserForm(bool windowsAccount, string? profileFolder, bool reusable)
    {
        this.windowsAccount = windowsAccount;
        this.profileFolder = profileFolder;
        this.reusable = reusable;
        Text = "Kiosk";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        // Hosted by the Kiosk (windowsAccount): an unowned window the Kiosk finds and keeps over its own screen.
        ShowInTaskbar = windowsAccount;
        TopMost = !windowsAccount;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        countdown.BackColor = KioskTheme.ShellBar;
        countdown.Expired += Finish;
        Controls.Add(countdown);
        // Nobody uses the page (click, touch, key, scroll) or this window for the time set in Konfiguracja → Szybka pomoc: it closes.
        // Counted here rather than from Windows' last input, which other programs and remote sessions keep fresh.
        HelpCountdown.Watch(this, Touched);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(); };
    }

    /// <summary>"Zamknij", Esc or the countdown: closes the window, or parks a reusable one.</summary>
    private void Finish() { if (reusable) Park(); else Close(); }

    /// <summary>The page <paramref name="url"/> in this window, in place of the one shown before.</summary>
    private void LoadPage(string title, string url, IEnumerable<string> extraDomains, int idleSeconds, bool onlyListed)
    {
        this.idleSeconds = idleSeconds;
        this.url = url;
        Text = title;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        var policy = new SitePolicy(onlyListed, [], SignInDomains.Concat(extraDomains).Append(host));
        policy.Trust(url);
        var old = browser;
        browser = new BrowserView([], policy, null, windowsAccount: windowsAccount, singlePage: true, profileFolder: profileFolder) { Dock = DockStyle.Fill };
        // One bar: ← → ⟳, the page's name and a large close button (no second bar above the browser's).
        var close = new Button { Text = "Zamknij" };   // The same word everywhere (signed in or guest).
        KioskTheme.StyleButton(close, primary: true);
        close.Click += (_, _) => Finish();
        browser.SetPageBar(title + (windowsAccount ? "" : "  ·  okno gościa: po zamknięciu nic nie zostaje zapisane"), close);
        browser.Activity += Touched;
        Controls.Add(browser);
        browser.BringToFront();   // Docked Fill after the countdown at the bottom.
        if (sideInset > 0) browser.SetRightInset(sideInset);
        if (old != null) { Controls.Remove(old); old.Dispose(); }
    }

    /// <summary>A reusable window: the next page opens here (the Kiosk asks, <see cref="CardPageHost"/>).</summary>
    internal void Open(string title, string url, IEnumerable<string> domains, int idleSeconds, bool onlyListed)
    {
        RemoveProp(Handle, ParkedProperty);
        started = false;
        LoadPage(title, url, domains, idleSeconds, onlyListed);
        Start();
    }

    /// <summary>The Kiosk reads this mark on a reusable window: its page was closed ("Zamknij", Esc, the countdown).</summary>
    internal const string ParkedProperty = "KioskPageParked";

    /// <summary>
    /// A reusable window back to waiting: the page ends (a film or a sound stops with it), the browser stays running
    /// for the next page, and the window is marked for the Kiosk, which takes it off the screen.
    /// </summary>
    internal void Park()
    {
        countdown.Stop();
        if (browser != null) { Controls.Remove(browser); browser.Dispose(); browser = null; }
        SetProp(Handle, ParkedProperty, (IntPtr)1);
        Audit.Write("card_page_parked");
    }

    /// <summary>A reusable window starts the browser at once (a blank page), so the first page opens without that wait.</summary>
    internal async void Warm()
    {
        keeper = new BrowserView([], new SitePolicy(true, [], []), null, windowsAccount: windowsAccount, singlePage: true, profileFolder: profileFolder)
        {
            // Shown (a hidden control gets no window for the browser), one pixel behind the page.
            Dock = DockStyle.None, Bounds = new Rectangle(0, 0, 1, 1)
        };
        Controls.Add(keeper);
        keeper.SendToBack();
        try { await keeper.InitializeAsync("about:blank"); }
        catch (Exception ex) { Audit.Write("card_page_warm_failed", ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr window, string name);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern IntPtr GetProp(IntPtr window, string name);

    private void Touched() => countdown.Restart();
    /// <summary>Someone reads the element beside the page: the page's countdown starts again.</summary>
    internal void Touch() => countdown.Restart();

    /// <summary>The Kiosk tells the person's page (another program) how wide the element beside it is: wParam = px.</summary>
    internal const int SideInsetMessage = 0x8051;   // WM_APP + 0x51
    private int sideInset;

    /// <summary>
    /// Menu → "Obok": the bar (Zamknij) and the countdown stay across the whole width; the page itself leaves the
    /// right part free for the element beside it. Over the Kiosk (the person's page) that part is cut out of this
    /// window, so the Kiosk's element under it shows and takes the clicks.
    /// </summary>
    internal void SetSideInset(int width)
    {
        // The Kiosk repeats it whenever it lays out the card: a new window shape (a repaint over the page) only on a change.
        if (Math.Max(0, width) == sideInset && IsHandleCreated) return;
        sideInset = Math.Max(0, width);
        browser?.SetRightInset(sideInset);
        UpdateSideRegion();
    }

    /// <summary>Where the element beside a page goes in the page's <paramref name="area"/>: under the bar, over the lines at the bottom.</summary>
    internal static Rectangle SideSlot(Rectangle area, int width, int dpi)
    {
        int S(int v) => (int)Math.Round(v * dpi / 96d);
        int top = S(64), bottom = S(30) + S(30);   // The page bar; the browser's message line and the countdown.
        return new Rectangle(area.Right - width, area.Top + top, width, Math.Max(0, area.Height - top - bottom));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The Kiosk runs under another account (and maybe another integrity level): let its message through.
        if (TopLevel) ChangeWindowMessageFilterEx(Handle, SideInsetMessage, 1, IntPtr.Zero); // MSGFLT_ALLOW
        UpdateSideRegion();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, int message, int action, IntPtr status);

    private void UpdateSideRegion()
    {
        if (!TopLevel || !IsHandleCreated) return;
        if (sideInset <= 0) { Region = null; return; }
        var slot = SideSlot(new Rectangle(Point.Empty, ClientSize), sideInset, DeviceDpi);
        var region = new Region(new Rectangle(Point.Empty, ClientSize));
        region.Exclude(slot);
        Region = region;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateSideRegion(); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == SideInsetMessage) { SetSideInset((int)m.WParam); return; }
        base.WndProc(ref m);
    }

    private bool started;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Start();
    }

    /// <summary>Starts the page and its countdown (once). Inside the Kiosk's window (TopLevel = false) the Kiosk calls it.</summary>
    internal async void Start()
    {
        if (started || browser is not { } page) return;
        started = true;
        countdown.Start(idleSeconds);
        try { await page.InitializeAsync(url); }
        catch (Exception ex)
        {
            Audit.Write("help_browser_error", ex.Message);
            if (page != browser) return;   // Already replaced by the next page.
            // In the page's place, no message window: the countdown or the close button returns to the Kiosk.
            var error = new Label
            {
                Text = "Nie udało się otworzyć strony: " + ex.Message, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13), BackColor = KioskTheme.Background, ForeColor = KioskTheme.Text
            };
            // The next page (a reusable window) removes the message together with this page.
            page.Controls.Add(error);
            error.BringToFront();
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
    private readonly SideTab tab = new() { Dock = DockStyle.Left };
    internal event Action? Activity;
    /// <summary>The tab was clicked (or, folded, grew under the pointer): the page's frame gives the strip or the half back.</summary>
    internal event Action? Toggled;
    /// <summary>Folded to the tab at the right edge; the page takes the width.</summary>
    internal bool Collapsed { get; private set; }
    /// <summary>The width while folded: the tab.</summary>
    internal int StripWidth => tab.Width;

    internal SidePage(string title, string url, IEnumerable<string> domains, bool onlyListed)
    {
        this.url = url;
        BackColor = KioskTheme.Background;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.Host : "";
        var policy = new SitePolicy(onlyListed, [], HelpBrowserForm.SignInDomains.Concat(domains).Append(host));
        policy.Trust(url);
        // No bar of its own: the document is the right part of the page's frame, with a tab on its edge.
        browser = new BrowserView([], policy, null, singlePage: true) { Dock = DockStyle.Fill };
        browser.HideBars();
        browser.Activity += () => Activity?.Invoke();
        tab.Title = title;
        tab.Click += (_, _) => SetCollapsed(!Collapsed);
        tab.Resized += () => Toggled?.Invoke();
        Controls.Add(browser);
        Controls.Add(tab);
    }

    private void SetCollapsed(bool value)
    {
        Collapsed = value;
        tab.Collapsed = value;
        browser.Visible = !value;
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
    private readonly TaskCompletionSource<(string User, string Password)?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            countdown.Stop();
            password.Clear();
            if (Parent is { } parent) parent.Resize -= OnParentResize;
            answer.TrySetResult(null);
        }
        base.Dispose(disposing);
    }

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
    private readonly Font captionFont = new("Segoe UI", 9.5f);
    private long lastMilliseconds = Environment.TickCount64;
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

    private double SecondsLeft => seconds - (Environment.TickCount64 - lastMilliseconds) / 1000d;
    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(30) + Padding.Vertical; }

    internal void Start(int idleSeconds) { seconds = Math.Max(1, idleSeconds); lastMilliseconds = Environment.TickCount64; timer.Start(); Invalidate(); }
    internal void Stop() => timer.Stop();
    internal void Restart() { lastMilliseconds = Environment.TickCount64; Invalidate(); }

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
        TextRenderer.DrawText(g, "Bez aktywności zamknie się za " + (int)Math.Ceiling(left) + " s", captionFont,
            new Rectangle(area.Left, area.Top, area.Width, area.Height - S(8)), KioskTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        var track = new Rectangle(area.Left, area.Bottom - S(5), area.Width, S(5));
        using (var back = new SolidBrush(KioskTheme.Border)) g.FillRectangle(back, track);
        using var fill = new SolidBrush(KioskTheme.AccentLine);
        g.FillRectangle(fill, new Rectangle(track.Left, track.Top, (int)(track.Width * left / seconds), track.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); captionFont.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The ribbon on the left edge of the element beside a page: a thin accent strip that widens under the pointer and
/// says "Schowaj" (or "Pokaż" when folded); a click folds or unfolds the element.
/// </summary>
internal sealed class SideTab : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private bool hover, collapsed;
    private int target;
    internal string Title { get; set; } = "";
    /// <summary>Folded, it grew or shrank under the pointer.</summary>
    internal event Action? Resized;
    /// <summary>Takes its new width at once, so the page's frame is laid out with it.</summary>
    internal bool Collapsed
    {
        get => collapsed;
        set { collapsed = value; timer.Stop(); target = hover ? Wide : Rest; Width = target; Invalidate(); }
    }

    internal SideTab()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        timer.Tick += (_, _) =>
        {
            int step = Math.Max(1, Math.Abs(target - Width) / 3);
            Width = Width < target ? Math.Min(target, Width + step) : Math.Max(target, Width - step);
            if (Width == target) timer.Stop();
        };
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    // Thin at rest, folded or not; under the pointer wide enough for its text.
    private int Rest => Scale(8);
    private int Wide => Scale(collapsed ? 40 : 44);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Width = target = Rest; }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Grow(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Grow(); }

    private void Grow()
    {
        target = hover ? Wide : Rest;
        if (!IsHandleCreated) return;
        // Folded, the page's frame makes room for it: at once, so the page is laid out twice, not at every step.
        if (collapsed) { Width = target; Resized?.Invoke(); }
        else if (Width != target) timer.Start();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(KioskTheme.Background);
        using (var fill = new SolidBrush(hover ? KioskTheme.Accent : Color.FromArgb(collapsed ? 210 : 150, KioskTheme.Accent)))
            g.FillRectangle(fill, 0, 0, Width, Height);
        if (Width < Scale(24)) return;
        // Vertical text, read from the bottom up, like a ribbon on a binder.
        string text = hover ? (collapsed ? "◀  Pokaż" : "Schowaj  ▶") : "";
        if (text.Length == 0) return;
        using var font = new Font("Segoe UI Semibold", 11);
        var color = KioskTheme.Luminance(KioskTheme.Accent) > 0.5 ? Color.FromArgb(20, 20, 20) : Color.White;
        var state = g.Save();
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.RotateTransform(-90);
        var size = g.MeasureString(text, font);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, -size.Width / 2, -size.Height / 2);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
