using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kiosk.Client;

/// <summary>
/// A screen's content while nobody works at the Kiosk: a web page or an application across the whole screen,
/// view only. The window is disabled, so Windows sends it no keyboard or mouse input (as with the owner of a
/// modal dialog); the page or program keeps running and drawing.
/// </summary>
internal sealed class IdleScreen : Form
{
    private readonly ScreenContent content;
    private readonly System.Windows.Forms.Timer watch = new() { Interval = 5000 };
    private WebView2? web;
    private ApplicationView? app;
    private Label? startError;
    private bool starting, restartingApp;
    private int monitorFailures, appStartFailures;
    private long retryAppAtMilliseconds;

    internal Screen Target { get; }
    private readonly int number;

    /// <summary>Normally view only; true while Konfiguracja is open, so the page can be signed in (read-only account).</summary>
    internal bool Interactive
    {
        get => Enabled;
        set
        {
            if (Enabled != value) Enabled = value;
            // A Chromium program is its own window over this one: Enabled here does not reach it.
            if (app != null) app.InputEnabled = value;
        }
    }
    internal long? ShownAtMilliseconds { get; private set; }
#if KIOSK_SELF_TEST
    internal bool ApplicationStarted => app?.Started == true;
    internal bool ShowingError => startError != null;
#endif

    internal IdleScreen(Screen screen, ScreenContent content, int number)
    {
        Target = screen;
        this.number = number;
        this.content = content;
        Text = "Kiosk";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        ShowInTaskbar = false;
        BackColor = KioskTheme.Background;
        Enabled = false; // View only: Windows sends a disabled window no keyboard or mouse input.
        Load += async (_, _) => await StartAsync();
        VisibleChanged += (_, _) =>
        {
            if (!Visible) return;
            ShownAtMilliseconds = Environment.TickCount64;
            Bounds = Target.Bounds;
        };
        watch.Tick += async (_, _) => await WatchApplicationAsync();
    }

    private readonly List<AlertBar> bars = [];

    /// <summary>Komunikaty → "Ekrany wygaszacza": the bars across the top of this screen too (empty = none).</summary>
    internal void SetBars(IReadOnlyList<NewsItem> items)
    {
        if (items.SequenceEqual(bars.Select(b => b.Item).Reverse())) return;
        foreach (var old in bars) { Controls.Remove(old); old.Dispose(); }
        bars.Clear();
        foreach (var item in items.Reverse())   // Added last = docked first: the first message ends up on top.
        {
            var bar = new AlertBar(item) { Dock = DockStyle.Top, Height = (int)Math.Round(44 * DeviceDpi / 96d) };
            bars.Add(bar);
            Controls.Add(bar);
        }
        foreach (var fill in Controls.Cast<Control>().Where(c => c.Dock == DockStyle.Fill).ToList()) fill.BringToFront();   // The page under the bars.
    }

    private async Task StartAsync()
    {
        if (starting) return;
        starting = true;
        try
        {
            if (NewsSlides.IsAddress(content.Url))
            {
                var slides = new NewsSlides(sound: number == 1) { Dock = DockStyle.Fill };
                Controls.Add(slides);
                slides.BringToFront();   // Below any message bars docked at the top.
            }
            else if (content.Url.Length > 0)
            {
                web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = KioskTheme.Background };
                Controls.Add(web);
                web.BringToFront();   // Below any message bars docked at the top.
                // A lasting profile per screen: the read-only account signed in once stays signed in.
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Screens", "Ekran" + number);
                var environment = await CoreWebView2Environment.CreateAsync(null, folder);
                await web.EnsureCoreWebView2Async(environment);
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;   // No print / save dialogs.
                web.CoreWebView2.DownloadStarting += (_, e) => { e.Handled = true; e.Cancel = true; };
                web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true; // No pop-ups on a view-only screen.
                PageScripts.Attach(web.CoreWebView2, kiosk: true);
                web.CoreWebView2.Navigate(content.Url);
            }
            else await RestartAppAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            if (content.Url.Length == 0) ScheduleAppRetry();
        }
        finally
        {
            starting = false;
            // Web pages and slides have no hosted process to inspect. An application is retried even if its first launch failed.
            if (content.Url.Length == 0 && !IsDisposed) watch.Start();
        }
    }

    private async Task WatchApplicationAsync()
    {
        if (starting || restartingApp || IsDisposed) return;
        if (app is { Started: true } running)
        {
            try
            {
                if (running.RefreshHostedWindow()) { monitorFailures = 0; return; }
                monitorFailures = 0;
            }
            catch (Exception ex)
            {
                if (++monitorFailures < 3)
                {
                    if (monitorFailures == 1) Audit.Write("idle_screen_monitor_error", ex.Message);
                    return;
                }
                Audit.Write("idle_screen_monitor_error", "Restart after repeated errors: " + ex.Message);
                monitorFailures = 0;
            }
        }
        if (Environment.TickCount64 < retryAppAtMilliseconds) return;
        restartingApp = true;
        try
        {
            await RestartAppAsync();
            appStartFailures = 0;
            retryAppAtMilliseconds = 0;
            ClearError();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            ScheduleAppRetry();
        }
        finally { restartingApp = false; }
    }

    private void ShowError(Exception ex)
    {
        Audit.Write("idle_screen_error", ex.Message);
        if (IsDisposed) return;
        startError ??= new Label { Dock = DockStyle.Fill, ForeColor = KioskTheme.Muted, TextAlign = ContentAlignment.MiddleCenter };
        startError.Text = "Nie udało się pokazać treści ekranu: " + ex.Message;
        if (startError.Parent != this) Controls.Add(startError);
        startError.BringToFront();
    }

    private void ScheduleAppRetry()
    {
        retryAppAtMilliseconds = Environment.TickCount64 + Math.Min(30_000, 5_000 << Math.Min(3, appStartFailures));
        appStartFailures = Math.Min(3, appStartFailures + 1);
    }

    private void ClearError()
    {
        if (startError == null) return;
        Controls.Remove(startError);
        startError.Dispose();
        startError = null;
    }

    /// <summary>Reloads the page (timed refresh); programs keep running.</summary>
    internal void Reload() { try { web?.CoreWebView2?.Reload(); } catch (Exception ex) { Audit.Write("idle_screen_reload_error", ex.Message); } }

    private async Task RestartAppAsync()
    {
        if (app != null) { Controls.Remove(app); app.Dispose(); }
        app = new ApplicationView();
        Controls.Add(app);
        app.BringToFront();   // Below any message bars docked at the top.
        app.InputEnabled = Enabled;
        await app.StartAsync(new ApplicationEntry { Name = "Ekran", Path = content.Path, Arguments = content.Arguments });
    }

    /// <summary>Moving to a screen with another scale: Windows suggests a size; the window must still cover its screen.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Bounds = Target.Bounds; }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Bounds = Target.Bounds; }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never takes focus, not in Alt+Tab
            return p;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { watch.Dispose(); web?.Dispose(); app?.Dispose(); }
        base.Dispose(disposing);
    }
}
