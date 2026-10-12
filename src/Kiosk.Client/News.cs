using System.Drawing.Drawing2D;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>One company message on the desktop: title, text, optional picture and a button that does something.</summary>
internal sealed record NewsItem
{
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Picture file next to kiosk-news.json (or a full path).</summary>
    public string Image { get; init; } = "";
    /// <summary>Button text; empty = no button (the whole slide still runs the action).</summary>
    public string Button { get; init; } = "";
    /// <summary>https://… page or a file (opened in the Kiosk browser), app:Name, rdp:Name, cmd:Name; empty = information only.</summary>
    public string Action { get; init; } = "";
    /// <summary>Optional: shown only from / until this day (yyyy-MM-dd).</summary>
    public DateTime? From { get; init; }
    public DateTime? Until { get; init; }
    /// <summary>"Pasek": a bar across the top of the desktop (e.g. "Nie działa system XXX, trwa naprawa") instead of a slide.</summary>
    public string Kind { get; init; } = "";
    /// <summary>The bar's colours: a name (red, orange, green…) or #RRGGBB; default red with white text.</summary>
    public string Color { get; init; } = "";
    public string TextColor { get; init; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsBar => Kind.Equals("pasek", StringComparison.OrdinalIgnoreCase) || Kind.Equals("bar", StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore] internal Image? Picture { get; init; }
}

/// <summary>Reads kiosk-news.json from the folder set in Konfiguracja (a local folder or a share).</summary>
internal static class News
{
    internal const string FileName = "kiosk-news.json";

    /// <summary>The current messages; a missing folder or file gives none. Runs off the UI thread (shares can be slow).</summary>
    internal static List<NewsItem> Load(string folder)
    {
        var items = new List<NewsItem>();
        if (string.IsNullOrWhiteSpace(folder)) return items;
        var file = Path.Combine(folder, FileName);
        if (!File.Exists(file)) return items;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var list = JsonSerializer.Deserialize<NewsItem[]>(File.ReadAllText(file), options) ?? [];
        var today = DateTime.Today;
        foreach (var item in list)
        {
            if (item == null || item.Title.Length == 0 && item.Text.Length == 0 && item.Image.Length == 0) continue;
            if (item.From is { } from && today < from.Date || item.Until is { } until && today > until.Date) continue;
            Image? picture = null;
            if (item.Image.Length > 0)
            {
                var path = Path.IsPathFullyQualified(item.Image) ? item.Image : Path.Combine(folder, item.Image);
                // Read into memory so the share's file is not kept open.
                try { if (File.Exists(path)) picture = System.Drawing.Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); }
                catch (Exception) { picture = null; }
            }
            items.Add(item with { Picture = picture, Action = ResolveAction(item.Action, folder) });
        }
        return items;
    }

    /// <summary>The folder's messages together with the SharePoint news, in the order set in Konfiguracja.</summary>
    internal static List<NewsItem> Merge(List<NewsItem> mine, List<NewsItem> sharePoint, NewsOrdering order)
    {
        if (order == NewsOrdering.MineFirst) return [.. mine, .. sharePoint];
        if (order == NewsOrdering.SharePointFirst) return [.. sharePoint, .. mine];
        var merged = new List<NewsItem>();
        for (int i = 0; i < Math.Max(mine.Count, sharePoint.Count); i++)
        {
            if (i < sharePoint.Count) merged.Add(sharePoint[i]);
            if (i < mine.Count) merged.Add(mine[i]);
        }
        return merged;
    }

    /// <summary>"red", "#C62828" or "198,40,40"; anything else gives the fallback.</summary>
    internal static System.Drawing.Color ParseColor(string text, System.Drawing.Color fallback)
    {
        text = text.Trim();
        if (text.Length == 0) return fallback;
        try
        {
            var parts = text.Split(',');
            if (parts.Length == 3) return System.Drawing.Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            var color = ColorTranslator.FromHtml(text);
            return color.IsEmpty ? fallback : System.Drawing.Color.FromArgb(255, color);
        }
        catch (Exception) { return fallback; }
    }

    /// <summary>A relative file (e.g. zglos-problem.html next to the json) becomes a file:// address for the browser.</summary>
    /// <remarks>"https:// wp.pl" and "wp.pl" both become https://wp.pl.</remarks>
    internal static string ResolveAction(string action, string folder)
    {
        action = action.Trim();
        if (action.Length == 0) return action;
        if (action.Contains("://")) return string.Concat(action.Where(c => !char.IsWhiteSpace(c)));
        if (action.Contains(':') && !Path.IsPathFullyQualified(action)) return action; // app:, rdp:, cmd:, mailto:…
        var path = Path.IsPathFullyQualified(action) ? action : Path.Combine(folder, action);
        if (!Path.IsPathFullyQualified(action) && !File.Exists(path) && !action.Contains('\\') && !action.Contains(' ') && action.Contains('.'))
            return "https://" + action; // A site name without https://
        return new Uri(path).AbsoluteUri;
    }
}

/// <summary>
/// Company messages scrolling one after another at the bottom of the desktop. Arrows and dots move by hand,
/// the mouse over it pauses, the button (or the slide) runs its action.
/// </summary>
internal sealed class NewsCarousel : Control
{
    private List<NewsItem> items = [];
    private int index, previous = -1, direction = 1;
    private double progress = 1;
    private bool hover;
    private DateTime shownAt = DateTime.UtcNow;
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer rotation = new() { Interval = 250 };
    private Rectangle buttonBounds, leftArrow, rightArrow;
    private readonly List<Rectangle> dots = [];

    internal int Seconds { get; set; } = 8;
    internal int Count => items.Count;
    internal int Index => index;
    internal event Action<NewsItem>? ActionRequested;
    internal void RequestAction(NewsItem item) => ActionRequested?.Invoke(item);

    internal NewsCarousel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        animation.Tick += (_, _) =>
        {
            progress = Math.Min(1, progress + 0.07);
            if (progress >= 1) { animation.Stop(); previous = -1; }
            Invalidate();
        };
        rotation.Tick += (_, _) =>
        {
            if (!hover && items.Count > 1 && Visible && DateTime.UtcNow - shownAt > TimeSpan.FromSeconds(Math.Max(3, Seconds))) Step(1);
        };
        rotation.Start();
    }

    internal void SetItems(List<NewsItem> news)
    {
        var old = items;
        items = news;
        if (index >= items.Count) index = 0;
        previous = -1; progress = 1;
        foreach (var item in old) if (!items.Any(n => ReferenceEquals(n.Picture, item.Picture))) item.Picture?.Dispose();
        Invalidate();
    }

    internal void Step(int step)
    {
        if (items.Count < 2) return;
        GoTo((index + step + items.Count) % items.Count, step);
    }

    private void GoTo(int target, int step)
    {
        if (target == index || items.Count == 0) return;
        previous = index; index = target; direction = Math.Sign(step) == 0 ? 1 : Math.Sign(step);
        progress = 0; shownAt = DateTime.UtcNow;
        animation.Start();
        Invalidate();
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; shownAt = DateTime.UtcNow; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (items.Count == 0) return;
        if (items.Count > 1 && leftArrow.Contains(e.Location)) { Step(-1); return; }
        if (items.Count > 1 && rightArrow.Contains(e.Location)) { Step(1); return; }
        for (int i = 0; i < dots.Count; i++)
            if (Rectangle.Inflate(dots[i], S(6), S(6)).Contains(e.Location)) { GoTo(i, i - index); return; }
        var item = items[index];
        if (item.Action.Length > 0) ActionRequested?.Invoke(item);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (items.Count == 0 || Width < 20 || Height < 20) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = KioskTheme.RoundedRectangle(box, S(18));
        using (var back = new SolidBrush(Color.FromArgb(235, KioskTheme.Surface))) g.FillPath(back, path);
        using (var border = new Pen(hover ? KioskTheme.AccentLine : KioskTheme.Border)) g.DrawPath(border, path);

        var state = g.Save();
        g.SetClip(path);
        // Sliding: the old message leaves on one side while the new one comes in from the other.
        double eased = 1 - Math.Pow(1 - progress, 3);
        int shift = (int)Math.Round(Width * eased) * direction;
        if (previous >= 0 && previous < items.Count && progress < 1) DrawSlide(g, items[previous], -shift, false);
        DrawSlide(g, items[index], previous >= 0 && progress < 1 ? Width * direction - shift : 0, true);
        g.Restore(state);

        dots.Clear();
        leftArrow = rightArrow = Rectangle.Empty;
        if (items.Count > 1)
        {
            int dot = S(7), gap = S(8), total = items.Count * dot + (items.Count - 1) * gap;
            int x = (Width - total) / 2, y = Height - S(16);
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rectangle(x + i * (dot + gap), y, dot, dot);
                dots.Add(r);
                using var brush = new SolidBrush(i == index ? KioskTheme.AccentLine : Color.FromArgb(90, KioskTheme.Muted));
                g.FillEllipse(brush, r);
            }
            int arrow = S(30);
            leftArrow = new Rectangle(S(8), (Height - arrow) / 2, arrow, arrow);
            rightArrow = new Rectangle(Width - S(8) - arrow, (Height - arrow) / 2, arrow, arrow);
            if (hover)
                foreach (var (r, glyph) in new[] { (leftArrow, ""), (rightArrow, "") })
                {
                    using var circle = new SolidBrush(Color.FromArgb(160, KioskTheme.Background));
                    g.FillEllipse(circle, r);
                    KioskIcons.Draw(g, r, glyph, null, KioskTheme.Text);
                }
        }
    }

    private void DrawSlide(Graphics g, NewsItem item, int offset, bool current)
    {
        int pad = S(22), side = items.Count > 1 ? S(44) : pad;
        var area = new Rectangle(offset + side, S(14), Width - side * 2, Height - S(36));
        int textLeft = area.Left;
        if (item.Picture is { } picture)
        {
            int h = area.Height, w = Math.Min(h * 2, Math.Max(S(40), h * picture.Width / Math.Max(1, picture.Height)));
            var pictureBounds = new Rectangle(area.Left, area.Top, w, h);
            using var clip = KioskTheme.RoundedRectangle(pictureBounds, S(10));
            var state = g.Save();
            g.SetClip(clip, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(picture, pictureBounds);
            g.Restore(state);
            textLeft = pictureBounds.Right + pad;
        }
        int buttonWidth = 0;
        if (item.Button.Length > 0 && item.Action.Length > 0)
        {
            using var font = new Font("Segoe UI", 10, FontStyle.Bold);
            var size = TextRenderer.MeasureText(item.Button, font);
            buttonWidth = size.Width + S(32);
            var bounds = new Rectangle(area.Right - buttonWidth, area.Top + (area.Height - S(38)) / 2, buttonWidth, S(38));
            using var pill = KioskTheme.RoundedRectangle(bounds, S(19));
            using var fill = new SolidBrush(KioskTheme.Accent);
            g.FillPath(fill, pill);
            TextRenderer.DrawText(g, item.Button, font, bounds, KioskTheme.OnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (current) buttonBounds = bounds;
            buttonWidth += pad;
        }
        else if (current) buttonBounds = Rectangle.Empty;
        var textArea = new Rectangle(textLeft, area.Top, Math.Max(0, area.Right - buttonWidth - textLeft), area.Height);
        // A larger banner (Konfiguracja → Komunikaty → Rozmiar) gets larger letters.
        bool large = Height >= S(200), extra = Height >= S(300);
        using var titleFont = new Font("Segoe UI", extra ? 26 : large ? 20 : 15, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", extra ? 14 : large ? 12.5f : 10.5f);
        int titleHeight = item.Title.Length > 0 ? TextRenderer.MeasureText(g, item.Title, titleFont, new Size(textArea.Width, int.MaxValue), TextFormatFlags.WordBreak).Height : 0;
        titleHeight = Math.Min(titleHeight, textArea.Height / 2);
        var bodySize = TextRenderer.MeasureText(g, item.Text, bodyFont, new Size(textArea.Width, int.MaxValue), TextFormatFlags.WordBreak);
        int blockHeight = Math.Min(textArea.Height, titleHeight + (item.Text.Length > 0 ? S(4) + bodySize.Height : 0));
        int top = textArea.Top + (textArea.Height - blockHeight) / 2;
        if (titleHeight > 0)
            TextRenderer.DrawText(g, item.Title, titleFont, new Rectangle(textArea.Left, top, textArea.Width, titleHeight), KioskTheme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (item.Text.Length > 0)
            TextRenderer.DrawText(g, item.Text, bodyFont, new Rectangle(textArea.Left, top + titleHeight + S(4), textArea.Width, Math.Max(0, textArea.Bottom - top - titleHeight - S(4))),
                KioskTheme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { animation.Dispose(); rotation.Dispose(); foreach (var item in items) item.Picture?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>A bar across the top of the desktop for something everyone must see now ("Nie działa system XXX, trwa naprawa").</summary>
internal sealed class AlertBar : Control
{
    internal NewsItem Item { get; }
    internal event Action<NewsItem>? ActionRequested;

    internal AlertBar(NewsItem item)
    {
        Item = item;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = News.ParseColor(item.Color, Color.FromArgb(198, 40, 40));
        ForeColor = News.ParseColor(item.TextColor, Color.White);
        Cursor = item.Action.Length > 0 ? Cursors.Hand : Cursors.Default;
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (Item.Action.Length > 0) ActionRequested?.Invoke(Item);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var bold = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        using var regular = new Font("Segoe UI", 11.5f);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        string link = Item.Action.Length > 0 ? (Item.Button.Length > 0 ? Item.Button : "Szczegóły") + "  ›" : "";
        string text = Item.Text.Length > 0 ? "  " + Item.Text : "";
        int icon = S(22), gap = S(10);
        int titleWidth = TextRenderer.MeasureText(g, Item.Title, bold, Size.Empty, flags).Width;
        int textWidth = TextRenderer.MeasureText(g, text, regular, Size.Empty, flags).Width;
        int linkWidth = link.Length > 0 ? TextRenderer.MeasureText(g, link, bold, Size.Empty, flags).Width + S(24) : 0;
        int available = Width - S(32) - linkWidth;
        int total = Math.Min(available, icon + gap + titleWidth + textWidth);
        int x = Math.Max(S(16), (Width - linkWidth - total) / 2);
        KioskIcons.Draw(g, new Rectangle(x, 0, icon, Height), "\uE7BA", null, ForeColor); // warning
        x += icon + gap;
        int right = Width - S(16) - linkWidth;
        TextRenderer.DrawText(g, Item.Title, bold, new Rectangle(x, 0, Math.Max(0, Math.Min(titleWidth, right - x)), Height), ForeColor, flags | TextFormatFlags.EndEllipsis);
        x += titleWidth;
        if (text.Length > 0 && x < right)
            TextRenderer.DrawText(g, text, regular, new Rectangle(x, 0, right - x, Height), ForeColor, flags | TextFormatFlags.EndEllipsis);
        if (link.Length > 0)
            TextRenderer.DrawText(g, link, bold, new Rectangle(Width - S(16) - linkWidth + S(24), 0, linkWidth - S(24), Height), ForeColor, flags);
    }
}
