using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kiosk.Client;

/// <summary>
/// Embedded Edge (WebView2) with tabs and a dark toolbar that matches the Kiosk shell.
/// All tabs of one person share one InPrivate profile (one sign-in covers every tab);
/// each person's browser has its own profile, removed when their session closes.
/// </summary>
internal sealed class BrowserView : UserControl
{
    private sealed class Tab
    {
        internal required WebView2 Web { get; init; }
        internal required TabButton Button { get; init; }
        internal bool Blocked { get; set; }
    }

    private const string ProfileName = "kiosk";
    private static readonly HashSet<string> AllowedMenuItems = new(StringComparer.Ordinal)
    {
        "copyLinkLocation", "copy", "cut", "paste", "pasteAndMatchStyle", "selectAll", "undo", "redo", "back", "forward", "reload"
    };
    private readonly List<Tab> tabs = new();
    private Tab? active;
    private CoreWebView2Environment? environment;
    private string homeUrl = "about:blank";
    private readonly string userDataFolder = Path.Combine(Path.GetTempPath(), "KioskBrowser", Guid.NewGuid().ToString("N"));

    private readonly TextBox address = new()
    {
        BorderStyle = BorderStyle.None, BackColor = KioskTheme.Surface, ForeColor = KioskTheme.Text, Font = new Font("Segoe UI", 11)
    };
    private readonly Panel addressBox = new() { BackColor = KioskTheme.Surface };
    private readonly TaskButton back = new("←"), forward = new("→"), reload = new("⟳"), go = new("Przejdź", primary: true);
    private readonly TaskButton newTab = new("+");
    private readonly Panel tabStrip = new() { Dock = DockStyle.Top, BackColor = KioskTheme.Taskbar };
    private readonly Panel toolbar = new() { Dock = DockStyle.Top, BackColor = KioskTheme.Taskbar };
    private readonly Panel pages = new() { Dock = DockStyle.Fill, BackColor = KioskTheme.Background };
    private readonly FlowLayoutPanel favorites = new() { Dock = DockStyle.Top, AutoScroll = true, WrapContents = false, BackColor = KioskTheme.Taskbar };
    private readonly Label message = new()
    {
        Dock = DockStyle.Bottom, Height = 26, Text = "Uruchamianie przeglądarki…", BackColor = KioskTheme.Taskbar,
        ForeColor = KioskTheme.Muted, Padding = new Padding(10, 5, 10, 0)
    };
    private readonly SitePolicy policy;
    private readonly HashSet<string> cardCertificates;

    /// <summary>The page in the active tab.</summary>
    internal WebView2 Web => active?.Web ?? throw new InvalidOperationException("Przeglądarka nie jest gotowa.");
    internal int TabCount => tabs.Count;

    internal BrowserView(IEnumerable<Bookmark>? bookmarks = null, SitePolicy? policy = null, IReadOnlyCollection<string>? cardCertificates = null)
    {
        this.policy = policy ?? new SitePolicy(false, [], []);
        this.cardCertificates = new HashSet<string>(cardCertificates ?? [], StringComparer.OrdinalIgnoreCase);
        Dock = DockStyle.Fill;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        addressBox.Controls.Add(address);
        addressBox.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.Clear(KioskTheme.Taskbar);
            using var path = KioskTheme.RoundedRectangle(new Rectangle(0, 0, addressBox.Width - 1, addressBox.Height - 1), Scale(8));
            using var fill = new SolidBrush(KioskTheme.Surface);
            using var stroke = new Pen(address.Focused ? KioskTheme.AccentLine : KioskTheme.Border);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(stroke, path);
        };
        address.GotFocus += (_, _) => { addressBox.Invalidate(); address.SelectAll(); };
        address.LostFocus += (_, _) => addressBox.Invalidate();
        toolbar.Controls.AddRange(new Control[] { back, forward, reload, addressBox, go });
        if (this.policy.Restricted)
        {
            // Only bookmarked sites: no address bar; the bookmarks sit in the toolbar next to the navigation buttons.
            addressBox.Visible = go.Visible = false;
            favorites.Dock = DockStyle.None;
            favorites.Padding = Padding.Empty;
            toolbar.Controls.Add(favorites);
        }
        toolbar.Resize += (_, _) => ArrangeToolbar();
        tabStrip.Controls.Add(newTab);
        tabStrip.Resize += (_, _) => ArrangeTabs();

        back.Click += (_, _) => { if (active?.Web.CanGoBack == true) active.Web.GoBack(); };
        forward.Click += (_, _) => { if (active?.Web.CanGoForward == true) active.Web.GoForward(); };
        reload.Click += (_, _) => { if (active?.Web.CoreWebView2 != null) active.Web.Reload(); };
        go.Click += (_, _) => NavigateAddress();
        newTab.Click += async (_, _) => await AddTabAsync(homeUrl);
        address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; NavigateAddress(); } };
        foreach (var bookmark in bookmarks ?? [])
        {
            var button = new Button { Text = bookmark.Name, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
            KioskTheme.StyleButton(button);
            button.Font = new Font("Segoe UI", 9.5f);
            button.Click += (_, _) => active?.Web.CoreWebView2?.Navigate(bookmark.Url);
            favorites.Controls.Add(button);
        }
        favorites.Visible = favorites.Controls.Count > 0;
        if (!this.policy.Restricted) favorites.Padding = new Padding(6, 0, 6, 6);
        Controls.Add(pages); Controls.Add(message);
        if (!this.policy.Restricted) Controls.Add(favorites);
        Controls.Add(toolbar); Controls.Add(tabStrip);
    }

    private new int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        tabStrip.Height = Scale(44);
        toolbar.Height = Scale(50);
        favorites.Height = Scale(40);
        ArrangeTabs();
        ArrangeToolbar();
    }

    private void ArrangeToolbar()
    {
        int height = Scale(36), top = (toolbar.Height - height) / 2, gap = Scale(6), x = Scale(10);
        foreach (var button in new[] { back, forward, reload })
        {
            button.SetBounds(x, top, Scale(44), height);
            x += Scale(44) + gap;
        }
        if (policy.Restricted)
        {
            favorites.SetBounds(x + gap, top, Math.Max(0, toolbar.Width - x - gap - Scale(10)), height);
            return;
        }
        int goWidth = Scale(100);
        go.SetBounds(toolbar.Width - Scale(10) - goWidth, top, goWidth, height);
        addressBox.SetBounds(x + gap, top, Math.Max(Scale(100), go.Left - gap * 2 - x), height);
        int textHeight = address.PreferredHeight;
        address.SetBounds(Scale(12), (height - textHeight) / 2, Math.Max(0, addressBox.Width - Scale(24)), textHeight);
    }

    /// <summary>Tabs share the strip width like the Edge tab row; each has its own × button.</summary>
    private void ArrangeTabs()
    {
        int height = Scale(34), top = tabStrip.Height - height - Scale(2), gap = Scale(4), x = Scale(10);
        int available = Math.Max(0, tabStrip.Width - x - Scale(60));
        int width = tabs.Count == 0 ? 0 : Math.Min(Scale(240), Math.Max(Scale(110), available / tabs.Count - gap));
        foreach (var tab in tabs)
        {
            tab.Button.SetBounds(x, top, width, height);
            x = tab.Button.Right + gap;
        }
        newTab.SetBounds(x + gap, top, Scale(40), height);
    }

    internal async Task InitializeAsync(string url)
    {
        homeUrl = url;
        // Per-session profile folder, separate from the user's installed browser; tabs share it.
        environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        if (IsDisposed) return;
        await AddTabAsync(url);
        message.Text = "";
    }

    /// <summary>Opens a tab. <paramref name="request"/> is a page asking for a new window (target=_blank, window.open).</summary>
    private async Task<Tab?> AddTabAsync(string? url, CoreWebView2NewWindowRequestedEventArgs? request = null)
    {
        if (environment == null || IsDisposed) return null;
        var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = KioskTheme.Background, Visible = false };
        var tab = new Tab { Web = web, Button = new TabButton("Nowa karta") };
        tab.Button.Selected += () => SelectTab(tab);
        tab.Button.CloseRequested += () => CloseTab(tab);
        tabs.Add(tab);
        pages.Controls.Add(web);
        tabStrip.Controls.Add(tab.Button);
        ArrangeTabs();
        SelectTab(tab);

        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        options.IsInPrivateModeEnabled = true;
        await web.EnsureCoreWebView2Async(environment, options);
        if (web.IsDisposed) return null;
        var core = web.CoreWebView2;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark; // Sites that support it match the shell.
        core.Settings.AreDevToolsEnabled = false;
        // A trimmed right-click menu: open a link in a new tab, copy / paste, navigation. No save, print or inspect.
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.ContextMenuRequested += (_, e) =>
        {
            var items = e.MenuItems;
            for (int i = items.Count - 1; i >= 0; i--)
                if (!AllowedMenuItems.Contains(items[i].Name)) items.RemoveAt(i);
            var target = e.ContextMenuTarget;
            if (target.HasLinkUri && IsWebAddress(target.LinkUri) && environment != null)
            {
                var link = target.LinkUri;
                var open = environment.CreateContextMenuItem("Otwórz w nowej karcie", null, CoreWebView2ContextMenuItemKind.Command);
                open.CustomItemSelected += async (_, _) => await AddTabAsync(link);
                items.Insert(0, open);
            }
            if (items.Count == 0) e.Handled = true; // Nothing left to show.
        };
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NewWindowRequested += async (_, e) =>
        {
            // Links "in a new window" become a tab; window.opener keeps working (sign-in pop-ups).
            if (!IsWebAddress(e.Uri) && e.Uri != "about:blank") { e.Handled = true; return; }
            var deferral = e.GetDeferral();
            try
            {
                var opened = await AddTabAsync(null, e);
                if (opened?.Web.CoreWebView2 != null) e.NewWindow = opened.Web.CoreWebView2;
                e.Handled = true;
            }
            finally { deferral.Complete(); }
        };
        core.WindowCloseRequested += (_, _) => CloseTab(tab);
        // Sites that sign in with a certificate (Entra ID certificate-based auth, ADFS, internal portals) get the
        // certificate from this person's card, so the browser is signed in as the card's user, not as the Windows
        // account the Kiosk runs under. Windows asks for the card PIN. Certificates of other cards are never offered.
        core.ClientCertificateRequested += (_, e) =>
        {
            var fromCard = e.MutuallyTrustedCertificates.Where(c => cardCertificates.Contains(c.ToX509Certificate2().Thumbprint)).ToList();
            if (fromCard.Count > 0) e.SelectedCertificate = fromCard[0];
            // Otherwise continue without a certificate: never offer one that is not on this person's card.
            e.Handled = true;
        };
        core.LaunchingExternalUriScheme += (_, e) => { e.Cancel = true; };
        core.NavigationStarting += (_, e) =>
        {
            // NavigateToString uses an internal data document (also used by the smoke test).
            if (!IsWebAddress(e.Uri) && e.Uri != "about:blank" && !e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            else if (IsWebAddress(e.Uri) && !policy.Allows(e.Uri))
            {
                e.Cancel = true;
                tab.Blocked = true;
                if (tab == active) message.Text = "Ta strona nie jest dozwolona w Kiosku: " + new Uri(e.Uri).Host + ". Korzystaj ze stron z zakładek.";
                Audit.Write("browser_blocked", new Uri(e.Uri).Host);
            }
            else if (tab == active) message.Text = "Wczytywanie…";
        };
        core.SourceChanged += (_, _) => { if (tab == active) address.Text = core.Source; };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Button.Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Nowa karta" : core.DocumentTitle;
        };
        core.HistoryChanged += (_, _) => { if (tab == active) UpdateNavigation(); };
        core.NavigationCompleted += (_, e) =>
        {
            if (tab.Blocked) { tab.Blocked = false; return; } // Keep the "not allowed" message.
            if (tab == active) message.Text = e.IsSuccess ? "" : "Nie udało się wczytać strony: " + e.WebErrorStatus;
        };
        UpdateNavigation();
        if (request == null && url != null && url != "about:blank")
            core.Navigate(policy.Allows(url) ? url : homeUrl);
        return tab;
    }

    private void SelectTab(Tab tab)
    {
        active = tab;
        foreach (var other in tabs)
        {
            other.Web.Visible = other == tab;
            other.Button.Active = other == tab;
        }
        tab.Web.BringToFront();
        address.Text = tab.Web.CoreWebView2?.Source ?? "";
        message.Text = "";
        UpdateNavigation();
    }

    private void CloseTab(Tab tab)
    {
        int index = tabs.IndexOf(tab);
        if (index < 0) return;
        tabs.RemoveAt(index);
        tabStrip.Controls.Remove(tab.Button);
        pages.Controls.Remove(tab.Web);
        tab.Button.Dispose(); tab.Web.Dispose();
        if (tabs.Count == 0) { active = null; _ = AddTabAsync(homeUrl); } // The browser always keeps one tab.
        else if (active == tab) SelectTab(tabs[Math.Min(index, tabs.Count - 1)]);
        ArrangeTabs();
    }

    private void UpdateNavigation()
    {
        back.Enabled = active?.Web.CoreWebView2?.CanGoBack == true;
        forward.Enabled = active?.Web.CoreWebView2?.CanGoForward == true;
    }

    /// <summary>Opens a new tab with the start page (also used by the self-test).</summary>
    internal Task OpenTabAsync() => AddTabAsync(homeUrl);
    internal void CloseActiveTab() { if (active != null) CloseTab(active); }

    private static bool IsWebAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void NavigateAddress()
    {
        if (active?.Web.CoreWebView2 == null) return;
        var url = address.Text.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (IsWebAddress(url)) active.Web.CoreWebView2.Navigate(url);
        else message.Text = "Podaj adres http:// lub https://.";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        // The person's cookies and sign-ins go with their session. The browser process may hold the folder briefly.
        var folder = userDataFolder;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
            {
                try { Directory.Delete(folder, true); }
                catch (IOException) { await Task.Delay(1000); }
                catch (UnauthorizedAccessException) { await Task.Delay(1000); }
            }
        });
    }
}

/// <summary>Which sites the embedded browser may open. Unrestricted unless "only bookmarks" is on.</summary>
internal sealed class SitePolicy
{
    private readonly string[]? hosts;

    internal SitePolicy(bool onlyBookmarks, IEnumerable<Bookmark> bookmarks, IEnumerable<string> extraDomains)
    {
        if (!onlyBookmarks) return;
        // A bookmark for www.example.com also allows example.com and its subdomains (sign-in pages, redirects).
        hosts = bookmarks.Select(b => Uri.TryCreate(b.Url, UriKind.Absolute, out var uri) ? uri.Host : "")
            .Concat(extraDomains)
            .Select(h => h.Trim().TrimEnd('.').ToLowerInvariant())
            .Select(h => h.StartsWith("www.") ? h[4..] : h)
            .Where(h => h.Length > 0).Distinct().ToArray();
    }

    internal bool Restricted => hosts != null;

    internal bool Allows(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        if (hosts == null) return true;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }
}
