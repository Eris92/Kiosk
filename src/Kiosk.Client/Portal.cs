using System.Drawing.Drawing2D;

namespace Kiosk.Client;

/// <summary>
/// Icons and colours of the Punkt samoobsługowy tiles: Windows' icon font by a plain name from Konfiguracja.
/// </summary>
internal static class TileIcons
{
    private static readonly (string[] Names, string Glyph)[] all =
    [
        (["zegar", "czas"], ""), (["kalendarz", "grafik", "urlop"], ""), (["dokument", "wniosek"], ""),
        (["megafon", "komunikat"], ""), (["szkolenie", "nauka"], ""), (["tarcza", "bezpieczenstwo"], ""),
        (["ostrzezenie", "incydent", "bhp"], ""), (["monitor", "it", "komputer"], ""), (["klodka", "rodo"], ""),
        (["klucz", "pin"], ""), (["osoba", "konto"], ""), (["ludzie", "zespol"], ""), (["poczta", "mail"], ""),
        (["telefon"], ""), (["dom", "start"], ""), (["czat", "sygnalista"], ""), (["folder"], ""),
        (["ustawienia"], ""), (["serce", "zdrowie"], ""), (["informacja", "info"], ""), (["swiat", "strona"], ""),
        (["reset", "odswiez"], ""), (["pomoc"], ""), (["narzedzia", "naprawa"], ""), (["drukarka"], ""),
        (["samochod"], ""), (["sklep", "zakupy"], ""), (["karta"], "")
    ];

    /// <summary>The names Konfiguracja accepts (the first of each icon).</summary>
    internal static string Names => string.Join(", ", all.Select(i => i.Names[0]));

    private static string Plain(string value) => new string(value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).Replace('ł', 'l');

    private static string? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var plain = Plain(name);
        return all.FirstOrDefault(i => i.Names.Contains(plain)).Glyph;
    }

    internal static string For(HelpAction item) => ByName(item.Icon) ?? item.Action.ToLowerInvariant() switch
    {
        HelpAction.ChangePin => "",
        HelpAction.Reenroll or HelpAction.FirstSignIn => "",
        HelpAction.ResetCard => "",
        var a when a.Contains("mfa") || a.Contains("security") => "",
        _ => ""
    };

    private static Dictionary<string, string> categoryIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Menu → Kategorie: the icons chosen there, by the category's path and by its own name.</summary>
    internal static void SetCategories(MenuCategory[] categories)
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
            if (ByName(category.Icon) is { } glyph)
            {
                icons[category.Path] = glyph;
                icons.TryAdd(category.Path.Split('/')[^1], glyph);
            }
        categoryIcons = icons;
    }

    /// <summary>A category's icon: chosen in Kategorie, its name when it is one of the icons, otherwise its first tile's.</summary>
    internal static string ForCategory(string name, IEnumerable<HelpAction> inside) =>
        categoryIcons.GetValueOrDefault(name) ?? ByName(name) ?? Plain(name) switch
        {
            var n when n.Contains("pomoc") => "",
            var n when n.Contains("incydent") || n.Contains("zglos") => "",
            var n when n.Contains("pracownik") => "",
            _ => inside.Select(For).FirstOrDefault() ?? ""
        };

    private static readonly Color[] palette =
    [
        Color.FromArgb(59, 130, 246), Color.FromArgb(34, 197, 94), Color.FromArgb(245, 158, 11), Color.FromArgb(139, 92, 246),
        Color.FromArgb(239, 68, 68), Color.FromArgb(14, 165, 233), Color.FromArgb(236, 72, 153), Color.FromArgb(20, 184, 166)
    ];

    internal static Color ColorFor(HelpAction item, int index) =>
        KioskTheme.IsColor(item.Color) ? ColorTranslator.FromHtml(item.Color) : palette[index % palette.Length];
}

/// <summary>Punkt samoobsługowy: the categories at the left ("Strona główna" first), one of them selected.</summary>
internal sealed class NavBar : Control
{
    private (string Path, string Text, string Glyph, HelpAction? Link)[] entries = [];
    private readonly List<Rectangle> rows = [];
    private int hover = -1;
    internal string Selected { get; private set; } = "";
    internal event Action<string>? SelectionRequested;
    /// <summary>A category with its own action and no entries: the click opens it.</summary>
    internal event Action<HelpAction>? LinkRequested;

    internal NavBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private int RowHeight => S(46);

    internal void SetItems(HelpAction[] items)
    {
        var list = new List<(string, string, string, HelpAction?)> { ("", "Strona główna", "", null) };
        foreach (var top in items.Select(i => (i.LinkPath ?? i.CategoryPath).Split('/')[0]).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inside = items.Where(i => TileBoard.InCategory(i, top)).ToList();
            var link = inside.Count == 0 ? items.FirstOrDefault(i => top.Equals(i.LinkPath, StringComparison.OrdinalIgnoreCase)) : null;
            list.Add((top, top, link != null ? TileIcons.ForCategory(top, [link]) : TileIcons.ForCategory(top, inside), link));
        }
        entries = list.ToArray();
        if (!entries.Any(e => e.Path.Equals(Selected, StringComparison.OrdinalIgnoreCase))) Selected = "";
        Invalidate();
    }

    internal void Select(string path) { Selected = path; Invalidate(); }

    internal int Needed => entries.Length * (RowHeight + S(4));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = rows.FindIndex(r => r.Contains(e.Location));
        if (index != hover) { hover = index; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = rows.FindIndex(r => r.Contains(e.Location));
        if (index < 0) return;
        if (entries[index].Link is { } link) LinkRequested?.Invoke(link);
        else SelectionRequested?.Invoke(entries[index].Path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        rows.Clear();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 11);
        using var bold = new Font("Segoe UI Semibold", 11);
        int y = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var bounds = new Rectangle(0, y, Width - 1, RowHeight);
            rows.Add(bounds);
            bool selected = entry.Path.Equals(Selected, StringComparison.OrdinalIgnoreCase);
            if (selected || i == hover)
            {
                using var path = KioskTheme.RoundedRectangle(bounds, S(10));
                using var fill = new SolidBrush(selected ? KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.28) : KioskTheme.Surface);
                g.FillPath(fill, path);
            }
            KioskIcons.Draw(g, new Rectangle(bounds.Left + S(12), bounds.Top, S(26), bounds.Height), entry.Glyph, null, selected ? KioskTheme.Text : KioskTheme.AccentLine);
            TextRenderer.DrawText(g, entry.Text, selected ? bold : font, new Rectangle(bounds.Left + S(50), bounds.Top, bounds.Width - S(56), bounds.Height),
                KioskTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            y += RowHeight + S(4);
        }
    }
}

/// <summary>
/// Punkt samoobsługowy: the tiles of the chosen category in panels (one panel per subcategory), or on "Strona główna"
/// one panel per category. A tile: coloured circle with the icon, name, description and an arrow.
/// </summary>
internal sealed class TileBoard : Panel
{
    private readonly Canvas canvas;
    private HelpAction[] items = [];
    private string category = "";
    private bool signedIn = true, busy;
    internal event Action<HelpAction>? ActionRequested;
    /// <summary>A tile that needs the card sign-in was chosen before signing in.</summary>
    internal event Action<HelpAction>? SignInNeeded;

    // Windows' own scroll bar is white: a thin one in the Kiosk's colours instead, plus the wheel and dragging the tiles.
    private readonly ScrollStrip strip;
    private int offset;

    internal TileBoard()
    {
        BackColor = KioskTheme.Background;
        canvas = new Canvas(this) { Location = Point.Empty };
        strip = new ScrollStrip(this) { Visible = false };
        Controls.Add(strip);
        Controls.Add(canvas);
    }

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private int Overflow => Math.Max(0, canvas.Height - ClientSize.Height);

    /// <summary>Scrolls to <paramref name="value"/> pixels from the top (kept within the content).</summary>
    private void ScrollTo(int value)
    {
        int clamped = Math.Clamp(value, 0, Overflow);
        if (clamped == offset && canvas.Top == -offset) return;
        offset = clamped;
        canvas.Top = -offset;
        strip.Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollTo(offset - e.Delta * S(120) / 120); }

    internal static bool InCategory(HelpAction item, string path) =>
        item.CategoryPath.Equals(path, StringComparison.OrdinalIgnoreCase) || item.CategoryPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);

    internal void SetItems(HelpAction[] actions) { items = actions; Rebuild(); }

    /// <summary>Opens an entry as a click on its tile would (the left menu's categories with their own action).</summary>
    internal void Run(HelpAction item)
    {
        if (busy) return;
        if (Available(item)) ActionRequested?.Invoke(item);
        else if (item.NeedsSignIn) SignInNeeded?.Invoke(item);
    }
    internal void ShowCategory(string path) { category = path; offset = 0; Rebuild(); }
    internal bool SignedIn { get => signedIn; set { if (signedIn != value) { signedIn = value; canvas.Invalidate(); } } }
    internal bool Busy { get => busy; set { if (busy != value) { busy = value; canvas.Invalidate(); } } }
    private bool Available(HelpAction item) => !busy && (signedIn || !item.NeedsSignIn);

    /// <summary>The panels on screen: a title and its tiles, in the order of the list in Konfiguracja.</summary>
    private List<(string Title, string Glyph, List<HelpAction> Tiles)> Panels()
    {
        var panels = new List<(string, string, List<HelpAction>)>();
        void Add(string title, string glyph, HelpAction item)
        {
            var panel = panels.FirstOrDefault(p => p.Item1.Equals(title, StringComparison.OrdinalIgnoreCase));
            if (panel.Item3 == null) { panel = (title, glyph, []); panels.Add(panel); }
            panel.Item3.Add(item);
        }
        foreach (var item in items)
        {
            var path = item.CategoryPath;
            if (category.Length == 0)
            {
                var top = path.Split('/')[0];
                if (top.Length == 0) Add("Na skróty", "", item);
                else Add(top, TileIcons.ForCategory(top, items.Where(i => InCategory(i, top))), item);
            }
            else if (InCategory(item, category))
            {
                var rest = path.Length > category.Length ? path[(category.Length + 1)..] : "";
                var title = rest.Length == 0 ? category.Split('/')[^1] : rest.Replace("/", " › ");
                Add(title, rest.Length == 0 ? TileIcons.ForCategory(category, items.Where(i => InCategory(i, category)))
                    : TileIcons.ForCategory(rest.Split('/')[0], items.Where(i => InCategory(i, category + "/" + rest))), item);
            }
        }
        return panels;
    }

    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); Rebuild(); }

    private void Rebuild()
    {
        if (canvas == null) return;
        var content = Panels();
        canvas.Arrange(content, ClientSize.Width);
        // Taller than the place: room at the right for the strip.
        bool scroll = canvas.Height > ClientSize.Height;
        if (scroll) canvas.Arrange(content, ClientSize.Width - S(16));
        strip.SetBounds(ClientSize.Width - S(10), S(4), S(8), Math.Max(0, ClientSize.Height - S(8)));
        strip.Visible = scroll;
        offset = Math.Clamp(offset, 0, Overflow);
        canvas.Top = -offset;
        canvas.Invalidate();
        strip.Invalidate();
    }

    /// <summary>The thin scroll bar: the thumb in the cards' border colour, the accent while dragged; a click on the track pages.</summary>
    private sealed class ScrollStrip : Control
    {
        private readonly TileBoard board;
        private bool hover, dragging;
        private int grabY, grabOffset;

        internal ScrollStrip(TileBoard board)
        {
            this.board = board;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = KioskTheme.Background;
        }

        private Rectangle Thumb
        {
            get
            {
                int total = Math.Max(1, board.canvas.Height), view = board.ClientSize.Height;
                int height = Math.Max(Width * 4, Height * view / total);
                int top = board.Overflow == 0 ? 0 : (Height - height) * board.offset / board.Overflow;
                return new Rectangle(0, top, Width, Math.Min(Height, height));
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); board.ScrollTo(board.offset - e.Delta * board.S(120) / 120); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var thumb = Thumb;
            if (thumb.Contains(e.Location)) { dragging = true; grabY = e.Y; grabOffset = board.offset; Capture = true; Invalidate(); }
            else board.ScrollTo(board.offset + (e.Y < thumb.Top ? -1 : 1) * board.ClientSize.Height * 9 / 10);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            int track = Math.Max(1, Height - Thumb.Height);
            board.ScrollTo(grabOffset + (e.Y - grabY) * board.Overflow / track);
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(KioskTheme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Width / 2))
            using (var fill = new SolidBrush(KioskTheme.Surface)) g.FillPath(fill, track);
            var thumb = Thumb;
            using var path = KioskTheme.RoundedRectangle(new Rectangle(thumb.X, thumb.Y, thumb.Width - 1, thumb.Height - 1), Width / 2);
            using var brush = new SolidBrush(dragging ? KioskTheme.AccentLine : hover ? KioskTheme.Blend(KioskTheme.Border, KioskTheme.Text, 0.25) : KioskTheme.Border);
            g.FillPath(brush, path);
        }
    }

    private sealed class Canvas : Control
    {
        private readonly TileBoard board;
        private readonly List<(Rectangle Bounds, string Title, string Glyph)> panels = [];
        private readonly List<(Rectangle Bounds, HelpAction Item, int Index)> tiles = [];
        private int hover = -1;

        internal Canvas(TileBoard board)
        {
            this.board = board;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = KioskTheme.Background;
        }

        private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);

        internal void Arrange(List<(string Title, string Glyph, List<HelpAction> Tiles)> content, int width)
        {
            panels.Clear();
            tiles.Clear();
            hover = -1;
            width = Math.Max(S(300), width - S(4));
            int pad = S(20), gap = S(12), header = S(64), tileHeight = S(112), y = 0, index = 0;
            // As many columns as fit tiles of at least 300 px (96 DPI), at most four.
            int columns = Math.Clamp((width - 2 * pad + gap) / (S(300) + gap), 1, 4);
            int tileWidth = (width - 2 * pad - (columns - 1) * gap) / columns;
            foreach (var panel in content)
            {
                int rowsNeeded = (panel.Tiles.Count + columns - 1) / columns;
                int height = header + rowsNeeded * tileHeight + (rowsNeeded - 1) * gap + pad;
                panels.Add((new Rectangle(0, y, width, height), panel.Title, panel.Glyph));
                for (int i = 0; i < panel.Tiles.Count; i++)
                {
                    int col = i % columns, row = i / columns;
                    tiles.Add((new Rectangle(pad + col * (tileWidth + gap), y + header + row * (tileHeight + gap), tileWidth, tileHeight), panel.Tiles[i], index++));
                }
                y += height + S(18);
            }
            Size = new Size(width, Math.Max(y, 1));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pressed && board.Overflow > 0)
            {
                int dy = Cursor.Position.Y - pressY;
                if (!moved && Math.Abs(dy) > S(10)) moved = true;
                if (moved) { board.ScrollTo(pressOffset - dy); return; }
            }
            int index = tiles.FindIndex(t => t.Bounds.Contains(e.Location));
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            if (index != hover) { hover = index; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        // A finger (or the mouse) moves the tiles up and down; a tile is opened only by a tap that did not move.
        private int pressY, pressOffset;
        private bool pressed, moved;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            pressed = e.Button == MouseButtons.Left;
            moved = false;
            pressY = Cursor.Position.Y;
            pressOffset = board.offset;
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; }

        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); board.ScrollTo(board.offset - e.Delta * S(120) / 120); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (moved) return;
            int index = tiles.FindIndex(t => t.Bounds.Contains(e.Location));
            if (index >= 0) board.Run(tiles[index].Item);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(KioskTheme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var titleFont = new Font("Segoe UI Semibold", 14);
            using var nameFont = new Font("Segoe UI Semibold", 13);
            using var textFont = new Font("Segoe UI", 10);
            foreach (var panel in panels)
            {
                using var path = KioskTheme.RoundedRectangle(panel.Bounds, S(16));
                using (var fill = new SolidBrush(KioskTheme.Blend(KioskTheme.Background, KioskTheme.Surface, 0.6))) g.FillPath(fill, path);
                using (var border = new Pen(KioskTheme.Border)) g.DrawPath(border, path);
                KioskIcons.Draw(g, new Rectangle(panel.Bounds.Left + S(20), panel.Bounds.Top + S(14), S(34), S(36)), panel.Glyph, null, KioskTheme.AccentLine);
                TextRenderer.DrawText(g, panel.Title, titleFont, new Rectangle(panel.Bounds.Left + S(66), panel.Bounds.Top + S(10), panel.Bounds.Width - S(86), S(44)),
                    KioskTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            for (int i = 0; i < tiles.Count; i++)
            {
                var (bounds, item, index) = tiles[i];
                bool available = board.Available(item);
                bool hot = i == hover && (available || item.NeedsSignIn);
                using var path = KioskTheme.RoundedRectangle(bounds, S(14));
                using (var fill = new SolidBrush(hot ? KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.12) : KioskTheme.Surface)) g.FillPath(fill, path);
                using (var border = new Pen(hot ? KioskTheme.AccentLine : KioskTheme.Border)) g.DrawPath(border, path);
                var color = TileIcons.ColorFor(item, index);
                if (!available) color = KioskTheme.Blend(KioskTheme.Surface, color, 0.35);
                int diameter = S(64);
                var circle = new Rectangle(bounds.Left + S(18), bounds.Top + (bounds.Height - diameter) / 2, diameter, diameter);
                using (var dot = new SolidBrush(color)) g.FillEllipse(dot, circle);
                KioskIcons.Draw(g, Rectangle.Inflate(circle, -S(16), -S(16)), TileIcons.For(item), null, available ? Color.White : KioskTheme.Muted);
                int textLeft = circle.Right + S(18), textWidth = bounds.Right - S(40) - textLeft;
                bool hasText = item.Description.Length > 0 || !available;
                var nameBounds = new Rectangle(textLeft, bounds.Top + (hasText ? S(18) : 0), textWidth, hasText ? S(30) : bounds.Height);
                TextRenderer.DrawText(g, item.Name, nameFont, nameBounds, available ? KioskTheme.Text : KioskTheme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                if (hasText)
                {
                    // Before signing in: says why it is grey; a tap opens the card sign-in.
                    var text = available || !item.NeedsSignIn ? item.Description : "Zaloguj się kartą, aby otworzyć";
                    TextRenderer.DrawText(g, text, textFont, new Rectangle(textLeft, bounds.Top + S(50), textWidth, bounds.Height - S(58)), KioskTheme.Muted,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                }
                KioskIcons.Draw(g, new Rectangle(bounds.Right - S(36), bounds.Top, S(22), bounds.Height), "", null, available ? KioskTheme.AccentLine : KioskTheme.Muted);
            }
        }
    }
}

/// <summary>
/// Punkt samoobsługowy as a portal: the categories at the left (logo, name, time at the bottom), a bar on top with the
/// page's title and "Zaloguj kartą" / "Zakończ sesję", and the tiles, the card sign-in or a page in the middle.
/// </summary>
internal sealed partial class DesktopView
{
    internal TileBoard Tiles { get; } = new();
    private readonly NavBar nav = new();
    private readonly Label portalTitle = new() { AutoSize = false, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true };
    private readonly Label portalLine = new() { AutoSize = false, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true };
    private readonly Label portalPerson = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
    private readonly Button account = new() { Text = "Zaloguj kartą" };
    private bool portal, signInWanted, portalSignedIn;
    private string? portalNotice;
    private bool portalWarning;
    /// <summary>"Zaloguj kartą" / "Zakończ sesję" pressed.</summary>
    internal event Action? AccountRequested;
    /// <summary>A category was chosen while a page is open: the page closes.</summary>
    internal event Action? PageCloseRequested;

    private void InitPortal()
    {
        portalTitle.Font = new Font("Segoe UI Semibold", 16);
        portalLine.Font = new Font("Segoe UI", 10);
        portalPerson.Font = new Font("Segoe UI", 11);
        foreach (var label in new[] { portalTitle, portalLine, portalPerson }) { label.BackColor = Color.Transparent; label.Visible = false; }
        portalTitle.ForeColor = portalPerson.ForeColor = KioskTheme.Text;
        portalLine.ForeColor = KioskTheme.Muted;
        nav.Visible = Tiles.Visible = account.Visible = false;
        KioskTheme.StyleButton(account, primary: true);
        account.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        account.Click += (_, _) => AccountRequested?.Invoke();
        nav.SelectionRequested += path =>
        {
            nav.Select(path);
            Tiles.ShowCategory(path);
            if (pageContent != null) PageCloseRequested?.Invoke();
            if (signInWanted) SignInWanted = false;
            UpdatePortalHeader();
        };
        Tiles.SignInNeeded += _ => SignInWanted = true;
        nav.LinkRequested += item => Tiles.Run(item);
        Controls.AddRange(new Control[] { Tiles, nav, portalTitle, portalLine, portalPerson, account });
    }

    /// <summary>Punkt samoobsługowy: the portal layout instead of the greeting card with Szybka pomoc beside it.</summary>
    internal bool Portal
    {
        get => portal;
        set
        {
            if (portal == value) return;
            portal = value;
            clock.TextAlign = date.TextAlign = value ? ContentAlignment.TopLeft : ContentAlignment.TopRight;
            nav.Visible = portalTitle.Visible = portalLine.Visible = portalPerson.Visible = account.Visible = value;
            ApplyVisibility();
            ArrangeControls();
        }
    }

    /// <summary>The card sign-in is on screen (after "Zaloguj kartą" or a tile that needs it).</summary>
    internal bool SignInWanted
    {
        get => signInWanted;
        set
        {
            if (signInWanted == value) return;
            signInWanted = value;
            if (value) SignInWantedAt = DateTime.UtcNow;
            ApplyVisibility();
            ArrangeControls();
            UpdatePortalHeader();
        }
    }
    /// <summary>When the sign-in was asked for: it goes away again when nobody signs in for a while.</summary>
    internal DateTime SignInWantedAt { get; private set; }

    /// <summary>The bar on top: who is signed in and a notice (card taken away, a result) in place of the page's line.</summary>
    internal void SetPortalState(string? personName, string? notice, bool warning = false)
    {
        // Called on every tick of the shell: only a change is drawn.
        if (portalSignedIn == (personName != null) && portalPerson.Text == (personName ?? "") && portalNotice == notice && portalWarning == warning) return;
        portalSignedIn = personName != null;
        portalNotice = notice;
        portalWarning = warning;
        portalPerson.Text = personName ?? "";
        account.Text = personName != null ? "Zakończ sesję" : signInWanted ? "Wróć" : "Zaloguj kartą";
        KioskTheme.StyleButton(account, primary: personName == null && !signInWanted);
        account.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        Tiles.SignedIn = personName != null;
        if (personName != null && signInWanted) SignInWanted = false;
        UpdatePortalHeader();
        ArrangeControls();
    }

    private void UpdatePortalHeader()
    {
        var name = nav.Selected.Length == 0 ? "Strona główna" : nav.Selected;
        portalTitle.Text = signInWanted && signingIn ? "Logowanie kartą" : Asking || AskingPin ? "Karta" : pageContent != null ? pageTitleText ?? name : name;
        portalLine.ForeColor = portalNotice != null && portalWarning ? Color.FromArgb(255, 107, 107) : KioskTheme.Muted;
        portalLine.Text = portalNotice ?? (signInWanted && signingIn ? "Przyłóż kartę do czytnika i wpisz PIN."
            : portalSignedIn ? "Wybierz kafelek – strony otworzą się na Twoim koncie."
            : "Wybierz kafelek. Szare kafelki działają po zalogowaniu kartą.");
        account.Text = portalSignedIn ? "Zakończ sesję" : signInWanted ? "Wróć" : "Zaloguj kartą";
    }

    private string? pageTitleText;
    /// <summary>The title of the page open in the middle (shown in the bar on top).</summary>
    internal void SetPageTitle(string? title) { pageTitleText = title; UpdatePortalHeader(); }

    /// <summary>In the portal the card is on screen only for the sign-in someone asked for, a question or the PIN.</summary>
    private bool PortalCard => signingIn && signInWanted || AskingPin || Asking;

    private void ApplyPortalVisibility(bool page)
    {
        bool cardOn = page || PortalCard;
        card.Visible = cardOn;
        Tiles.Visible = !cardOn;
        Tiles.Busy = Asking || AskingPin;
        if (Help.Visible) Help.Visible = false;
        if (News.Visible) News.Visible = false;
        UpdatePortalHeader();
    }

    /// <summary>The portal's places; returns the card's bounds (a page fills the middle, the card is centred in it).</summary>
    private Rectangle ArrangePortal(int bar, int cardWidth, int cardHeight, bool page)
    {
        int margin = Scale(24), side = Math.Min(Scale(270), Math.Max(Scale(200), Width / 5));
        // The left column: logo and name, the categories, the time at the bottom.
        int y = bar + Scale(22);
        if (logo.Image != null)
        {
            int logoSize = Scale(56);
            var image = logo.Image!;
            int logoWidth = Math.Min(side - margin, Math.Max(Scale(20), logoSize * image.Width / Math.Max(1, image.Height)));
            logo.SetBounds(margin, y, logoWidth, logoSize);
            y += logoSize + Scale(8);
        }
        brand.SetBounds(margin, y, side - margin, Scale(44));
        if (brand.Visible) y += Scale(44);
        subtitle.SetBounds(margin + Scale(2), y, side - margin, Scale(22));
        if (subtitle.Visible) y += Scale(22);
        y += Scale(20);
        clock.SetBounds(margin, Height - margin - Scale(84), side - margin, Scale(50));
        date.SetBounds(margin, Height - margin - Scale(32), side - margin, Scale(28));
        nav.SetBounds(Scale(14), y, side - Scale(28), Math.Max(0, Math.Min(nav.Needed, clock.Top - Scale(16) - y)));
        // The bar on top of the middle: title and line at the left, the person and the button at the right.
        int left = side + Scale(8), right = Width - margin, barTop = bar + Scale(14), barHeight = Scale(64);
        int buttonWidth = Math.Max(Scale(190), TextRenderer.MeasureText("Zakończ sesję", account.Font).Width + Scale(48));
        account.SetBounds(right - buttonWidth, barTop + (barHeight - Scale(48)) / 2, buttonWidth, Scale(48));
        int personWidth = portalSignedIn ? Math.Min(Scale(320), TextRenderer.MeasureText(portalPerson.Text, portalPerson.Font).Width + Scale(16)) : 0;
        portalPerson.SetBounds(account.Left - Scale(12) - personWidth, barTop, personWidth, barHeight);
        int titleWidth = Math.Max(0, portalPerson.Left - Scale(12) - left);
        portalTitle.SetBounds(left, barTop, titleWidth, Scale(36));
        portalLine.SetBounds(left, barTop + Scale(36), titleWidth, Scale(26));
        var middle = new Rectangle(left, barTop + barHeight + Scale(16), Math.Max(Scale(200), right - left), 0);
        middle.Height = Math.Max(Scale(200), Height - margin - middle.Top);
        Tiles.Bounds = middle;
        if (page) return middle;
        cardWidth = Math.Min(cardWidth, middle.Width);
        return new Rectangle(middle.Left + (middle.Width - cardWidth) / 2, middle.Top + Math.Max(0, (middle.Height - cardHeight) / 3), cardWidth, cardHeight);
    }
}
