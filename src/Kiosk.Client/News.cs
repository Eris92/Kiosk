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
    /// <summary>Optional: shown only from / until this moment: "yyyy-MM-dd HH:mm", or "yyyy-MM-dd" for the whole day.</summary>
    public string From { get; init; } = "";
    public string Until { get; init; } = "";
    /// <summary>"Pasek": a bar across the top of the desktop (e.g. "Nie działa system XXX, trwa naprawa") instead of a slide.</summary>
    public string Kind { get; init; } = "";
    /// <summary>The bar's colours: a name (red, orange, green…) or #RRGGBB; default red with white text.</summary>
    public string Color { get; init; } = "";
    public string TextColor { get; init; } = "";
    /// <summary>false: the entry stays in the file (ready for next time) but is not shown.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>A film (MP4) next to kiosk-news.json, a full path or https://…: the slide stays until it ends.</summary>
    public string Video { get; init; } = "";
    /// <summary>true: the film plays with sound (default silent).</summary>
    public bool Sound { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsVideo => VideoSource.Length > 0;
    /// <summary>The film's full path or https address (empty: no film or the file is missing).</summary>
    [System.Text.Json.Serialization.JsonIgnore] internal string VideoSource { get; init; } = "";

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
            if (item == null || !item.Enabled || item.Title.Length == 0 && item.Text.Length == 0 && item.Image.Length == 0 && item.Video.Length == 0) continue;
            if (!Shown(item, DateTime.Now)) continue;
            Image? picture = null;
            if (item.Image.Length > 0)
            {
                var path = Path.IsPathFullyQualified(item.Image) ? item.Image : Path.Combine(folder, item.Image);
                // Read into memory so the share's file is not kept open.
                try { if (File.Exists(path)) picture = System.Drawing.Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); }
                catch (Exception) { picture = null; }
            }
            items.Add(item with { Picture = picture, Action = ResolveAction(item.Action, folder), VideoSource = VideoFor(item.Video, folder) });
        }
        return items;
    }

    /// <summary>A film: https://… as it is, a file (next to the json or a full path) only when it exists.</summary>
    internal static string VideoFor(string video, string folder)
    {
        video = video.Trim().Trim('"');
        if (video.Length == 0) return "";
        if (video.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || video.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return video;
        var path = Path.IsPathFullyQualified(video) ? video : Path.Combine(folder, video);
        try { return File.Exists(path) ? Path.GetFullPath(path) : ""; }
        catch (Exception) { return ""; }
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

    private static readonly string[] Formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd"];

    /// <summary>A date and time from From / Until; a day alone means its start (From) or its end (Until). Unreadable = no limit.</summary>
    internal static DateTime? ParseMoment(string text, bool end)
    {
        text ??= "";
        if (!DateTime.TryParseExact(text.Trim(), Formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var moment)) return null;
        return end && text.Trim().Length == 10 ? moment.Date.AddDays(1).AddTicks(-1) : moment;
    }

    /// <summary>Within From – Until (both optional).</summary>
    internal static bool Shown(NewsItem item, DateTime now) =>
        !(ParseMoment(item.From, false) is { } from && now < from) && !(ParseMoment(item.Until, true) is { } until && now > until);

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
    // A film slide: played in a small browser over the slide; the slide moves on when the film ends.
    private NewsVideo? player;
    private bool videoFailed;
    private static readonly TimeSpan LongestVideo = TimeSpan.FromMinutes(30);

    internal int Seconds { get; set; } = 8;
    /// <summary>The screensaver (Ekrany → kiosk:komunikaty): each slide fills the whole control, the dots lie on it.</summary>
    internal bool FullScreen { get; init; }
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
            if (progress >= 1) { animation.Stop(); previous = -1; UpdateVideo(); }
            Invalidate();
        };
        rotation.Tick += (_, _) =>
        {
            if (hover || items.Count < 2 || !Visible) return;
            // Pictures and text: the time from Konfiguracja; a film: until it ends (a broken film: the same time).
            var wait = items[index].IsVideo && !videoFailed ? LongestVideo : TimeSpan.FromSeconds(Math.Max(3, Seconds));
            if (DateTime.UtcNow - shownAt > wait) Step(1);
        };
        rotation.Start();
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateVideo(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateVideo(); }

    /// <summary>Shows the film of the current slide (once the slide has stopped moving), or stops it.</summary>
    private void UpdateVideo()
    {
        if (IsDisposed) return;
        var item = items.Count > 0 && index < items.Count ? items[index] : null;
        if (item is not { IsVideo: true } || videoFailed || !Visible || progress < 1 || Width < 20 || Height < 20 || !IsHandleCreated)
        {
            player?.Stop();
            return;
        }
        if (player == null)
        {
            player = new NewsVideo();
            player.Ended += () => { if (items.Count > 1) Step(1); };
            player.Failed += message =>
            {
                // The slide then stays as long as a picture would, with the black placeholder.
                videoFailed = true; shownAt = DateTime.UtcNow;
                player?.Stop();
                Audit.Write("news_video_failed", message);
            };
            Controls.Add(player.View);
        }
        player.View.Bounds = VideoBounds(item);
        player.Play(item.VideoSource, item.Sound, loop: items.Count < 2);
    }

    /// <summary>Where the film plays: the whole screensaver, or the picture's place on the banner (16:9).</summary>
    private Rectangle VideoBounds(NewsItem item)
    {
        if (FullScreen) return ClientRectangle;
        var area = SlideArea(0);
        return new Rectangle(area.Left, area.Top, MediaWidth(item, area), area.Height);
    }

    private Rectangle SlideArea(int offset)
    {
        int side = items.Count > 1 ? S(44) : S(22);
        return new Rectangle(offset + side, S(14), Width - side * 2, Height - S(36));
    }

    private int MediaWidth(NewsItem item, Rectangle area)
    {
        int h = area.Height;
        if (item.IsVideo) return Math.Min(h * 16 / 9, area.Width / 2);
        return item.Picture is { } picture ? Math.Min(h * 2, Math.Max(S(40), h * picture.Width / Math.Max(1, picture.Height))) : 0;
    }

    internal void SetItems(List<NewsItem> news)
    {
        // The pictures are shared (the desktop, the screensaver on every screen), so no carousel disposes them:
        // disposing here broke the others' drawing (red X) after a reload, e.g. saving Konfiguracja.
        items = news;
        if (index >= items.Count) index = 0;
        previous = -1; progress = 1; shownAt = DateTime.UtcNow; videoFailed = false;
        UpdateVideo();
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
        progress = 0; shownAt = DateTime.UtcNow; videoFailed = false;
        player?.Stop();
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
        // A broken picture must not leave the red X and the .NET error window on the kiosk.
        try { PaintSlides(e.Graphics); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            if (!paintFailed) { paintFailed = true; Audit.Write("news_paint_failed", ex.Message); }
        }
    }

    private bool paintFailed;

    private void PaintSlides(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = FullScreen ? new GraphicsPath() : KioskTheme.RoundedRectangle(box, S(18));
        if (FullScreen) path.AddRectangle(new Rectangle(0, 0, Width, Height));
        using (var back = new SolidBrush(FullScreen ? KioskTheme.Background : Color.FromArgb(235, KioskTheme.Surface))) g.FillPath(back, path);
        if (!FullScreen) using (var border = new Pen(hover ? KioskTheme.AccentLine : KioskTheme.Border)) g.DrawPath(border, path);

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
            int dot = S(FullScreen ? 10 : 7), gap = S(FullScreen ? 12 : 8), total = items.Count * dot + (items.Count - 1) * gap;
            int x = (Width - total) / 2, y = Height - S(FullScreen ? 40 : 16);
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rectangle(x + i * (dot + gap), y, dot, dot);
                dots.Add(r);
                using var brush = new SolidBrush(i == index ? KioskTheme.AccentLine : FullScreen ? Color.FromArgb(170, Color.White) : Color.FromArgb(90, KioskTheme.Muted));
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
        if (FullScreen && item.IsVideo)
        {
            // Black under the film (also while the slide comes in, before the film starts).
            using var black = new SolidBrush(Color.Black);
            g.FillRectangle(black, new Rectangle(offset, 0, Width, Height));
            if (current) buttonBounds = Rectangle.Empty;
            return;
        }
        if (FullScreen && item.Picture != null) { DrawFullSlide(g, item, offset, current); return; }
        int pad = S(22);
        var area = SlideArea(offset);
        int textLeft = area.Left;
        if (item.IsVideo)
        {
            var videoBounds = new Rectangle(area.Left, area.Top, MediaWidth(item, area), area.Height);
            using var clip = KioskTheme.RoundedRectangle(videoBounds, S(10));
            using var black = new SolidBrush(Color.Black);
            g.FillPath(black, clip);
            KioskIcons.Draw(g, videoBounds, "\uE768", null, Color.FromArgb(200, Color.White)); // play
            textLeft = videoBounds.Right + pad;
        }
        else if (item.Picture is { } picture)
        {
            int h = area.Height, w = MediaWidth(item, area);
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
        // The screensaver has no buttons: any touch closes it before the button could be used.
        if (!FullScreen && item.Button.Length > 0 && item.Action.Length > 0)
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

    /// <summary>The screensaver: the picture covers the whole slide, the title and text at its bottom (no shade).</summary>
    private void DrawFullSlide(Graphics g, NewsItem item, int offset, bool current)
    {
        var picture = item.Picture!;
        var slide = new Rectangle(offset, 0, Width, Height);
        // Covers the slide, cut evenly at the sides or top and bottom (no stretching).
        double scale = Math.Max((double)Width / picture.Width, (double)Height / picture.Height);
        int w = (int)Math.Ceiling(picture.Width * scale), h = (int)Math.Ceiling(picture.Height * scale);
        var state = g.Save();
        g.SetClip(slide, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(picture, new Rectangle(slide.Left + (Width - w) / 2, (Height - h) / 2, w, h));
        if (current) buttonBounds = Rectangle.Empty;
        if (item.Title.Length > 0 || item.Text.Length > 0)
        {
            int shadeHeight = Height / 3;
            int margin = S(48);
            var textArea = new Rectangle(slide.Left + margin, Height - shadeHeight + S(24), Width - margin * 2, shadeHeight - S(24) - S(64));
            using var titleFont = new Font("Segoe UI", 30, FontStyle.Bold);
            using var bodyFont = new Font("Segoe UI", 16);
            int titleHeight = item.Title.Length > 0 ? Math.Min(textArea.Height / 2, TextRenderer.MeasureText(g, item.Title, titleFont, new Size(textArea.Width, int.MaxValue), TextFormatFlags.WordBreak).Height) : 0;
            if (titleHeight > 0) TextRenderer.DrawText(g, item.Title, titleFont, new Rectangle(textArea.Left, textArea.Top, textArea.Width, titleHeight), Color.White, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (item.Text.Length > 0)
                TextRenderer.DrawText(g, item.Text, bodyFont, new Rectangle(textArea.Left, textArea.Top + titleHeight + S(6), textArea.Width, Math.Max(0, textArea.Height - titleHeight - S(6))),
                    Color.FromArgb(225, 255, 255, 255), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { animation.Dispose(); rotation.Dispose(); player?.Dispose(); }
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
