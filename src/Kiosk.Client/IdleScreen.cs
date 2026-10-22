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
    private Label? failure;
    private bool starting;
    private bool restartingApp;
    private bool disposed;

    internal Screen Target { get; }
    private readonly int number;

    /// <summary>Normally view only; true while Konfiguracja is open, so the page can be signed in (read-only account).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Interactive
    {
        get => Enabled;
        set
        {
            if (Enabled == value || disposed) return;
            Enabled = value;
            if (!IsHandleCreated) return;
            // Admins must be able to focus/type into the screen page while configuring its account.
            const long noActivate = 0x08000000;
            long style = AppNative.GetWindowLongPtr(Handle, -20).ToInt64();
            AppNative.SetWindowLongPtr(Handle, -20, new IntPtr(value ? style & ~noActivate : style | noActivate));
        }
    }
    internal DateTime ShownAt { get; private set; }

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
            ShownAt = DateTime.UtcNow;
            Bounds = Target.Bounds;
        };
        // A program that closed by itself (or crashed) is started again.
        watch.Tick += async (_, _) =>
        {
            if (!disposed && content.Url.Length == 0 && (app == null || app.Started && !app.PollLifetime()))
                await RestartAppAsync();
        };
    }

    private async Task StartAsync()
    {
        if (starting || disposed) return;
        starting = true;
        try
        {
            if (content.Url.Length > 0)
            {
                web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = KioskTheme.Background };
                Controls.Add(web);
                // A lasting profile per screen: the read-only account signed in once stays signed in.
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Screens", "Ekran" + number);
                var environment = await CoreWebView2Environment.CreateAsync(null, folder);
                if (disposed) return;
                await web.EnsureCoreWebView2Async(environment);
                if (disposed) return;
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true; // No pop-ups on a view-only screen.
                web.CoreWebView2.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
                web.CoreWebView2.Navigate(content.Url);
            }
            else await RestartAppAsync();
        }
        catch (Exception ex)
        {
            if (disposed) return;
            if (web != null) { Controls.Remove(web); web.Dispose(); web = null; }
            ShowError(ex);
        }
        finally { starting = false; }
    }

    private async Task RestartAppAsync()
    {
        if (restartingApp || disposed) return;
        restartingApp = true;
        watch.Stop();
        try
        {
            if (app != null) { Controls.Remove(app); app.Dispose(); }
            app = new ApplicationView();
            Controls.Add(app);
            await app.StartAsync(new ApplicationEntry { Name = "Ekran", Path = content.Path, Arguments = content.Arguments });
            if (disposed) return;
            if (failure != null) failure.Visible = false;
            watch.Interval = 5000;
        }
        catch (Exception ex)
        {
            if (disposed) return;
            if (app != null) { Controls.Remove(app); app.Dispose(); app = null; }
            ShowError(ex);
            watch.Interval = 30000; // A broken executable must not be relaunched continuously.
        }
        finally
        {
            restartingApp = false;
            if (!disposed) watch.Start();
        }
    }

    private void ShowError(Exception exception)
    {
        failure ??= new Label { Dock = DockStyle.Fill, ForeColor = KioskTheme.Muted, BackColor = KioskTheme.Background, TextAlign = ContentAlignment.MiddleCenter };
        if (failure.Parent == null) Controls.Add(failure);
        failure.Text = "Nie udało się pokazać treści ekranu: " + exception.Message;
        failure.Visible = true;
        failure.BringToFront();
        Audit.Write("idle_screen_error", exception.Message);
    }

    /// <summary>Moving to a screen with another scale: Windows suggests a size; the window must still cover its screen.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Bounds = Target.Bounds; }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Bounds = Target.Bounds; }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: not in Alt+Tab.
            if (!Interactive) p.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE only while the screen is view only.
            return p;
        }
    }

    protected override bool ShowWithoutActivation => !Interactive;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { disposed = true; watch.Dispose(); }
        base.Dispose(disposing);
    }
}
