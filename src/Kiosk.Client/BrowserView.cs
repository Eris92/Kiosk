using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kiosk.Client;

/// <summary>
/// Embedded Edge (WebView2) with tabs and a dark toolbar that matches the Kiosk shell.
/// Tabs share a profile. Shared-account card sessions use separate temporary InPrivate profiles;
/// WindowsShell uses the signed-in Windows user's persistent profile for SSO.
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
    internal const string ProfileMarkerName = ".kiosk-browser-profile";
    internal const string ProfileMarkerText = "KioskBrowserProfile-v1";
    private static readonly ConcurrentDictionary<string, byte> liveProfiles = new(StringComparer.OrdinalIgnoreCase);
    private static int profileCleanupRunning;
    private static readonly HashSet<string> AllowedMenuItems = new(StringComparer.Ordinal)
    {
        "copyLinkLocation", "copy", "cut", "paste", "pasteAndMatchStyle", "selectAll", "undo", "redo", "back", "forward", "reload"
    };
    private readonly List<Tab> tabs = new();
    private Tab? active;
    private CoreWebView2Environment? environment;
    private uint browserProcessId;
    private Task? initialization;
    private bool disposed;
    private string homeUrl = "about:blank";
    private readonly string userDataFolder;
    private FileStream? profileMarker;
    /// <summary>Windows shell mode: the person is signed in to Windows, so the browser uses their Windows (Entra ID)
    /// account for SSO and keeps a normal profile in their own AppData instead of a throw-away InPrivate one.</summary>
    private readonly bool windowsAccount;

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
    private readonly Func<IReadOnlyCollection<string>> cardCertificates;
    private readonly string? expectedCertificateUpn;

    /// <summary>The page in the active tab.</summary>
    internal WebView2 Web => active?.Web ?? throw new InvalidOperationException("Przeglądarka nie jest gotowa.");
    internal int TabCount => tabs.Count;
#if KIOSK_SELF_TEST
    internal string ProfileFolder => userDataFolder;
#endif

    internal BrowserView(IEnumerable<Bookmark>? bookmarks = null, SitePolicy? policy = null,
        Func<IReadOnlyCollection<string>>? cardCertificates = null, bool windowsAccount = false, string? expectedCertificateUpn = null)
    {
        this.policy = policy ?? new SitePolicy(false, [], []);
        this.windowsAccount = windowsAccount;
        this.cardCertificates = cardCertificates ?? (() => []);
        this.expectedCertificateUpn = expectedCertificateUpn;
        if (windowsAccount)
            userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Browser");
        else
        {
            var root = Path.Combine(Path.GetTempPath(), "KioskBrowser");
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Katalog profili przeglądarki jest przekierowaniem.");
            userDataFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataFolder);
            profileMarker = new FileStream(Path.Combine(userDataFolder, ProfileMarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var marker = System.Text.Encoding.UTF8.GetBytes(ProfileMarkerText);
            profileMarker.Write(marker);
            profileMarker.Flush();
            liveProfiles.TryAdd(userDataFolder, 0);
            QueueAbandonedProfileCleanup(root);
        }
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
        newTab.Click += async (_, _) => await OpenTabFromUiAsync(homeUrl);
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

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

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

    internal Task InitializeAsync(string url)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // A second caller must await the same environment/tab startup, not create another browser process.
        return initialization ??= InitializeCoreAsync(url);
    }

    private async Task InitializeCoreAsync(string url)
    {
        homeUrl = url == "about:blank" || policy.Allows(url) ? url : "about:blank";
        // Per-session profile folder, separate from the user's installed browser; tabs share it.
        // Single sign-on with the Windows account (Entra ID PRT) only when that account is the person's own.
        var environmentOptions = new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = windowsAccount };
        environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, environmentOptions);
        if (disposed) return;
        await AddTabAsync(homeUrl);
        if (!disposed) message.Text = "";
    }

    /// <summary>Opens a tab. <paramref name="request"/> is a page asking for a new window (target=_blank, window.open).</summary>
    private async Task<Tab?> AddTabAsync(string? url, CoreWebView2NewWindowRequestedEventArgs? request = null)
    {
        if (environment == null || disposed) return null;
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
        options.IsInPrivateModeEnabled = !windowsAccount;
        try
        {
            await web.EnsureCoreWebView2Async(environment, options);
            if (disposed || web.IsDisposed || !tabs.Contains(tab)) return null;
            var core = web.CoreWebView2;
            browserProcessId = core.BrowserProcessId;
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
                    open.CustomItemSelected += async (_, _) => await OpenTabFromUiAsync(link);
                    items.Insert(0, open);
                }
                if (items.Count == 0) e.Handled = true; // Nothing left to show.
            };
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NewWindowRequested += async (_, e) =>
            {
                // Links "in a new window" become a tab; window.opener keeps working (sign-in pop-ups).
                e.Handled = true; // A failed or cancelled startup must never fall back to an external browser.
                if (!IsWebAddress(e.Uri) && e.Uri != "about:blank") { e.Handled = true; return; }
                var deferral = e.GetDeferral();
                try
                {
                    var opened = await AddTabAsync(null, e);
                    if (!disposed && !web.IsDisposed && opened?.Web.CoreWebView2 != null) e.NewWindow = opened.Web.CoreWebView2;
                    e.Handled = true;
                }
                catch (Exception ex) { ReportError(ex); }
                finally
                {
                    // The requesting tab can close while its new tab is initializing.
                    try { deferral.Complete(); }
                    catch (Exception ex) { ReportError(ex); }
                }
            };
            core.WindowCloseRequested += (_, _) => CloseTab(tab);
            // Ask the session for the currently inserted card only when a site
            // requests a certificate. A removed or changed card cannot use a
            // thumbprint captured when this browser window was opened.
            core.ClientCertificateRequested += (_, e) =>
            {
                try
                {
                    var allowed = cardCertificates();
                    foreach (var candidate in e.MutuallyTrustedCertificates)
                    {
                        using var certificate = candidate.ToX509Certificate2();
                        if (!AllowsClientCertificate(certificate, allowed, expectedCertificateUpn)) continue;
                        e.SelectedCertificate = candidate;
                        break;
                    }
                }
                catch (Exception ex) { Audit.Write("browser_certificate_error", ex.Message); }
                finally
                {
                    // No eligible card means no certificate, never an unfiltered
                    // system picker that could offer another user's credential.
                    e.Handled = true;
                }
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
            if (request == null && url != null && url != "about:blank" && policy.Allows(url))
                core.Navigate(url);
            return tab;
        }
        catch (Exception) when (disposed || web.IsDisposed || !tabs.Contains(tab)) { return null; }
        catch
        {
            RemoveTab(tab, reopen: false);
            throw;
        }
    }

    private void SelectTab(Tab tab)
    {
        if (disposed) return;
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
        if (!disposed) RemoveTab(tab, reopen: true);
    }

    private void RemoveTab(Tab tab, bool reopen)
    {
        int index = tabs.IndexOf(tab);
        if (index < 0) return;
        tabs.RemoveAt(index);
        tabStrip.Controls.Remove(tab.Button);
        pages.Controls.Remove(tab.Web);
        tab.Button.Dispose(); tab.Web.Dispose();
        if (tabs.Count == 0)
        {
            active = null;
            if (reopen && !disposed) _ = OpenTabFromUiAsync(homeUrl); // The browser always keeps one tab.
        }
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

    private async Task OpenTabFromUiAsync(string url)
    {
        try { await AddTabAsync(url); }
        catch (Exception ex) { ReportError(ex); }
    }

    private void ReportError(Exception exception)
    {
        if (disposed) return;
        message.Text = "Nie udało się otworzyć karty: " + exception.Message;
        Audit.Write("browser_tab_error", exception.Message);
    }

    private static bool AllowsClientCertificate(X509Certificate2 certificate, IReadOnlyCollection<string> allowed, string? expectedUpn) =>
        allowed.Contains(certificate.Thumbprint, StringComparer.OrdinalIgnoreCase) &&
        (expectedUpn == null || expectedUpn.Length > 0 &&
            string.Equals(certificate.GetNameInfo(X509NameType.UpnName, false), expectedUpn, StringComparison.OrdinalIgnoreCase));

#if KIOSK_SELF_TEST
    internal bool AllowsClientCertificateForTest(X509Certificate2 certificate) =>
        AllowsClientCertificate(certificate, cardCertificates(), expectedCertificateUpn);
#endif

    private static void QueueAbandonedProfileCleanup(string root)
    {
        if (Interlocked.CompareExchange(ref profileCleanupRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { CleanupAbandonedProfiles(root); }
            finally { Interlocked.Exchange(ref profileCleanupRunning, 0); }
        });
    }

    internal static void CleanupAbandonedProfiles(string root)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _) || liveProfiles.ContainsKey(folder)) continue;
                try
                {
                    DeleteOwnedProfile(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Audit.Write("browser_profile_cleanup_error", ex.Message);
        }
    }

    private static bool DeleteOwnedProfile(string folder)
    {
        // The marker is the ownership proof and the cross-process lock. Keep it until
        // every browser file has gone: recursive deletion can fail halfway through
        // while WebView2 still holds a file, and a later pass must recognize the folder.
        if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _) ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return false;
        var markerPath = Path.Combine(folder, ProfileMarkerName);
        if (!File.Exists(markerPath)) return false;
        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0) return false;
        using (var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var reader = new StreamReader(marker, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
            if (reader.ReadToEnd() != ProfileMarkerText) return false;
            DeleteProfileContents(folder, markerPath);
        }
        File.Delete(markerPath);
        Directory.Delete(folder);
        return true;
    }

    private static void DeleteProfileContents(string directory, string markerPath)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(path, markerPath, StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Katalog profilu przeglądarki zawiera przekierowanie.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteProfileContents(path, markerPath);
                Directory.Delete(path);
            }
            else File.Delete(path);
        }
    }

    private static bool BrowserProcessRunning(uint processId)
    {
        if (processId > int.MaxValue) return true;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && disposed) return;
        if (disposing) disposed = true; // Stop async continuations before disposing their WebView controls.
        var knownBrowserProcessId = browserProcessId;
        Task? browserExited = null;
        if (disposing && !windowsAccount && environment != null)
        {
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.BrowserProcessExited += (_, e) => { if (knownBrowserProcessId == 0 || e.BrowserProcessId == knownBrowserProcessId) exited.TrySetResult(true); };
            browserExited = exited.Task;
        }
        base.Dispose(disposing);
        if (!disposing || windowsAccount) return; // A Windows user's own profile stays, like a normal browser.
        // The person's cookies and sign-ins go with their session. The browser process may hold the folder briefly.
        var folder = userDataFolder;
        var pendingInitialization = initialization;
        var marker = profileMarker;
        profileMarker = null;
        _ = Task.Run(async () =>
        {
            // Environment creation may not have created the directory yet when the view was closed.
            // Wait for it before cleanup, including when startup itself failed.
            if (pendingInitialization != null)
            {
                try { await pendingInitialization; }
                catch (Exception) { }
            }
            try
            {
                // Keep the marker locked until WebView2 releases its folder. If the
                // notification is missed, the OS process state supplies the fallback.
                if (browserExited != null && knownBrowserProcessId != 0)
                {
                    while (!browserExited.IsCompleted && BrowserProcessRunning(knownBrowserProcessId))
                        await Task.WhenAny(browserExited, Task.Delay(5000));
                }
                else if (browserExited != null) await Task.WhenAny(browserExited, Task.Delay(5000));
                if (browserExited == null && pendingInitialization != null) await Task.Delay(5000);
                marker?.Dispose();
                for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
                {
                    try { if (!DeleteOwnedProfile(folder)) break; }
                    catch (IOException) { await Task.Delay(1000); }
                    catch (UnauthorizedAccessException) { await Task.Delay(1000); }
                }
                if (Directory.Exists(folder)) Audit.Write("browser_profile_cleanup_deferred", Path.GetFileName(folder));
            }
            finally { marker?.Dispose(); liveProfiles.TryRemove(folder, out _); }
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
