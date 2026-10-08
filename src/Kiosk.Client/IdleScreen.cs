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
    private bool starting;

    internal Screen Target { get; }
    internal DateTime ShownAt { get; private set; }

    internal IdleScreen(Screen screen, ScreenContent content)
    {
        Target = screen;
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
            ShownAt = DateTime.UtcNow;
            Bounds = Target.Bounds;
        };
        // A program that closed by itself (or crashed) is started again.
        watch.Tick += async (_, _) => { if (app != null && app.Started && !app.Refresh()) await RestartAppAsync(); };
    }

    private async Task StartAsync()
    {
        if (starting) return;
        starting = true;
        try
        {
            if (content.Url.Length > 0)
            {
                web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = KioskTheme.Background };
                Controls.Add(web);
                var folder = Path.Combine(Path.GetTempPath(), "KioskScreens", Guid.NewGuid().ToString("N"));
                var environment = await CoreWebView2Environment.CreateAsync(null, folder);
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                await web.EnsureCoreWebView2Async(environment, options);
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true; // No pop-ups on a view-only screen.
                web.CoreWebView2.Navigate(content.Url);
            }
            else await RestartAppAsync();
            watch.Start();
        }
        catch (Exception ex)
        {
            Controls.Add(new Label { Text = "Nie udało się pokazać treści ekranu: " + ex.Message, Dock = DockStyle.Fill, ForeColor = KioskTheme.Muted, TextAlign = ContentAlignment.MiddleCenter });
            Audit.Write("idle_screen_error", ex.Message);
        }
        finally { starting = false; }
    }

    private async Task RestartAppAsync()
    {
        if (app != null) { Controls.Remove(app); app.Dispose(); }
        app = new ApplicationView();
        Controls.Add(app);
        await app.StartAsync(new ApplicationEntry { Name = "Ekran", Path = content.Path, Arguments = content.Arguments });
    }

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
