namespace Kiosk.Client;

/// <summary>
/// Komunikaty as a screensaver: a screen whose content (Konfiguracja → Ekrany) is <see cref="Address"/> shows the
/// company messages as full-screen slides while nobody works, instead of a web page or a program.
/// </summary>
internal sealed class NewsSlides : Control
{
    internal const string Address = "kiosk:komunikaty";
    internal static bool IsAddress(string url) => url.Trim().Equals(Address, StringComparison.OrdinalIgnoreCase);

    private static List<NewsItem> current = [];
    private static int seconds = 8;
    private static event Action? Changed;

    /// <summary>The Kiosk hands over every new load of the messages (the bars stay on the desktop).</summary>
    internal static void Publish(List<NewsItem> items, int slideSeconds)
    {
        current = items.Where(i => !i.IsBar).ToList();
        seconds = slideSeconds;
        Changed?.Invoke();
    }

    private readonly NewsCarousel carousel = new() { Dock = DockStyle.Fill };
    private readonly Label empty = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = KioskTheme.Muted,
        Font = new Font("Segoe UI", 20), Text = "Brak komunikatów"
    };

    internal NewsSlides()
    {
        BackColor = KioskTheme.Background;
        Controls.Add(carousel);
        Controls.Add(empty);
        Changed += ShowItems;
        ShowItems();
    }

    private void ShowItems()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(ShowItems)); return; }
        carousel.Seconds = seconds;
        carousel.SetItems([.. current]);
        carousel.Visible = current.Count > 0;
        empty.Visible = current.Count == 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Changed -= ShowItems;
        base.Dispose(disposing);
    }
}
