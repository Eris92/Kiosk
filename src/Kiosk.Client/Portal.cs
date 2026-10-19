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
    private static HashSet<string> bottom = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Kategorie → "Dolna sekcja": the category is in the left menu's group under the line.</summary>
    internal static bool InBottomGroup(string path) => bottom.Contains(path);

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
        bottom = new HashSet<string>(categories.Where(c => c.Bottom).Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
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

    internal static Color ColorFor(HelpAction item, int index) =>
        KioskTheme.IsColor(item.Color) ? ColorTranslator.FromHtml(item.Color) : KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.85);
}

/// <summary>Punkt samoobsługowy: the categories at the left ("Strona główna" first), one of them selected.</summary>
internal sealed class NavBar : Control
{
    private (string Path, string Text, string Glyph, HelpAction? Link, bool NeedsSignIn)[] entries = [];
    private bool signedIn = true;
    /// <summary>Before signing in, categories that only work after it are grey with a padlock.</summary>
    internal bool SignedIn { get => signedIn; set { if (signedIn != value) { signedIn = value; Invalidate(); } } }
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
        var list = new List<(string, string, string, HelpAction?, bool)> { ("", "Strona główna", "", null, false) };
        foreach (var top in items.Select(i => (i.LinkPath ?? i.CategoryPath).Split('/')[0]).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inside = items.Where(i => TileBoard.InCategory(i, top)).ToList();
            var link = inside.Count == 0 ? items.FirstOrDefault(i => top.Equals(i.LinkPath, StringComparison.OrdinalIgnoreCase)) : null;
            // Its own page that works without signing in keeps the category open, even when its tiles need it.
            var own = items.FirstOrDefault(i => top.Equals(i.LinkPath, StringComparison.OrdinalIgnoreCase));
            bool needsSignIn = (own == null || own.NeedsSignIn) && (inside.Count > 0 ? inside.All(i => i.NeedsSignIn) : own != null);
            list.Add((top, top, link != null ? TileIcons.ForCategory(top, [link]) : TileIcons.ForCategory(top, inside), link, needsSignIn));
        }
        // "Dolna sekcja": those categories after the others, under a line.
        list = [.. list.Where(e => !TileIcons.InBottomGroup(e.Item1)), .. list.Where(e => TileIcons.InBottomGroup(e.Item1))];
        firstBottom = list.FindIndex(e => TileIcons.InBottomGroup(e.Item1));
        entries = list.ToArray();
        if (!entries.Any(e => e.Path.Equals(Selected, StringComparison.OrdinalIgnoreCase))) Selected = "";
        Invalidate();
    }

    internal void Select(string path) { Selected = path; Invalidate(); }

    private int firstBottom = -1;
    private int SeparatorHeight => S(25);
    internal int Needed => entries.Length * (RowHeight + S(4)) + (firstBottom > 0 ? SeparatorHeight : 0);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = rows.FindIndex(r => r.Contains(e.Location));
        if (index >= 0 && Locked(index)) index = -1;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (index != hover) { hover = index; Invalidate(); }
    }

    /// <summary>Signed out: what needs the card sign-in does nothing (grey with a padlock) until someone signs in.</summary>
    private bool Locked(int index) => entries[index].NeedsSignIn && !signedIn;
    internal bool SelectedLocked => Array.FindIndex(entries, e => e.Path.Equals(Selected, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 && Locked(i);

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = rows.FindIndex(r => r.Contains(e.Location));
        if (index < 0 || Locked(index)) return;
        if (entries[index].Link is { } link)
        {
            // After the old page closed (that selects "Strona główna").
            LinkRequested?.Invoke(link);
            Select(entries[index].Path);
        }
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
            if (i == firstBottom && i > 0)
            {
                using var line = new Pen(KioskTheme.Border);
                // As wide as the entries' highlight, so everything in the column keeps the same distance from its edges.
                g.DrawLine(line, 0, y + SeparatorHeight / 2 - S(2), Width - 1, y + SeparatorHeight / 2 - S(2));
                y += SeparatorHeight;
            }
            var bounds = new Rectangle(0, y, Width - 1, RowHeight);
            rows.Add(bounds);
            bool selected = entry.Path.Equals(Selected, StringComparison.OrdinalIgnoreCase);
            if (selected || i == hover)
            {
                using var path = KioskTheme.RoundedRectangle(bounds, S(10));
                using var fill = new SolidBrush(selected ? KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.28) : KioskTheme.Surface);
                g.FillPath(fill, path);
            }
            bool locked = entry.NeedsSignIn && !signedIn;
            KioskIcons.Draw(g, new Rectangle(bounds.Left + S(12), bounds.Top, S(26), bounds.Height), entry.Glyph, null,
                locked ? KioskTheme.Muted : selected ? KioskTheme.Text : KioskTheme.AccentLine);
            TextRenderer.DrawText(g, entry.Text, selected ? bold : font, new Rectangle(bounds.Left + S(50), bounds.Top, bounds.Width - S(56) - (locked ? S(28) : 0), bounds.Height),
                locked ? KioskTheme.Muted : KioskTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            // Works only after the card sign-in: a padlock, so nobody wonders why nothing opens.
            if (locked) KioskIcons.Draw(g, new Rectangle(bounds.Right - S(34), bounds.Top, S(18), bounds.Height), "", null, KioskTheme.Muted);
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

    protected override void OnPaintBackground(PaintEventArgs e) => DesktopView.PaintBackdrop(e.Graphics, this);

    private int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private int Overflow => Math.Max(0, canvas.Height - ClientSize.Height);

    /// <summary>Scrolls to <paramref name="value"/> pixels from the top (kept within the content).</summary>
    private void ScrollTo(int value)
    {
        int clamped = Math.Clamp(value, 0, Overflow);
        if (clamped == offset && canvas.Top == -offset) return;
        offset = clamped;
        canvas.Top = -offset;
        // The background stays put while the tiles move over it.
        canvas.Invalidate();
        strip.Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollTo(offset - e.Delta * S(120) / 120); }

    internal static bool InCategory(HelpAction item, string path) =>
        item.CategoryPath.Equals(path, StringComparison.OrdinalIgnoreCase) || item.CategoryPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);

    internal void SetItems(HelpAction[] actions) { items = actions; Rebuild(); }

    /// <summary>Opens an entry as a click on its tile would (the left menu's categories with their own action).</summary>
    internal void Run(HelpAction item)
    {
        // Signed out, a tile that needs the card does nothing: "Zaloguj kartą" at the top signs in.
        if (!busy && Available(item)) ActionRequested?.Invoke(item);
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
            DesktopView.PaintBackdrop(g, this);
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
            int pad = 0, gap = S(14), tileHeight = S(104), y = 0, index = 0;
            // As many columns as fit tiles of at least 320 px (96 DPI), at most three, each at most 460 px wide.
            int columns = Math.Clamp((width - 2 * pad + gap) / (S(320) + gap), 1, 3);
            int tileWidth = Math.Min(S(460), (width - 2 * pad - (columns - 1) * gap) / columns);
            foreach (var panel in content)
            {
                // The category's own tiles need no heading (the left column marks it); a subcategory gets one.
                int header = panel.Title.Equals(board.category.Split('/')[^1], StringComparison.OrdinalIgnoreCase) ? 0 : S(52);
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
            if (index >= 0 && !board.Available(tiles[index].Item)) index = -1;
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
            DesktopView.PaintBackdrop(g, this);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var titleFont = new Font("Segoe UI Semibold", 14);
            using var nameFont = new Font("Segoe UI Semibold", 13);
            using var textFont = new Font("Segoe UI", 10);
            foreach (var panel in panels)
            {
                // Only a heading on the background (no frame around the tiles); none for the category's own tiles.
                if (panel.Title.Equals(board.category.Split('/')[^1], StringComparison.OrdinalIgnoreCase)) continue;
                TextRenderer.DrawText(g, panel.Title, titleFont, new Rectangle(panel.Bounds.Left + S(2), panel.Bounds.Top + S(6), panel.Bounds.Width - S(4), S(38)),
                    KioskTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            for (int i = 0; i < tiles.Count; i++)
            {
                var (bounds, item, index) = tiles[i];
                bool available = board.Available(item);
                bool hot = i == hover && available;
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
                    var text = available || !item.NeedsSignIn ? item.Description : "Po zalogowaniu kartą";
                    TextRenderer.DrawText(g, text, textFont, new Rectangle(textLeft, bounds.Top + S(50), textWidth, bounds.Height - S(58)), KioskTheme.Muted,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                }
                // A padlock instead of the arrow while the tile waits for the card sign-in.
                bool locked = !available && item.NeedsSignIn && !board.signedIn;
                KioskIcons.Draw(g, new Rectangle(bounds.Right - S(36), bounds.Top, S(22), bounds.Height), locked ? "" : "", null,
                    available ? KioskTheme.AccentLine : KioskTheme.Muted);
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
    /// <summary>The left column's width (a line is drawn at its edge on the plain background).</summary>
    private int portalSide;
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
            // The open page first: closing it goes back to "Strona główna".
            if (pageContent != null) PageCloseRequested?.Invoke();
            nav.Select(path);
            Tiles.ShowCategory(path);
            if (signInWanted) SignInWanted = false;
            // To or from "Strona główna": the card and the messages, or the tiles.
            ApplyVisibility();
            ArrangeControls();
        };
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
            nav.Visible = value;
            portalTitle.Visible = portalLine.Visible = false;
            portalPerson.Visible = account.Visible = value && portalSignedIn;
            ApplyVisibility();
            ArrangeControls();
            Invalidate();
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
        if (portalSignedIn == (personName != null) && portalPerson.Text == (personName ?? "") && portalNotice == notice && portalWarning == warning &&
            Tiles.SignedIn == (personName != null) && nav.SignedIn == (personName != null)) return;
        portalSignedIn = personName != null;
        portalNotice = notice;
        portalWarning = warning;
        portalPerson.Text = personName ?? "";
        account.Text = personName != null ? "Zakończ sesję" : signInWanted ? "Wróć" : "Zaloguj kartą";
        KioskTheme.StyleButton(account, primary: personName == null && !signInWanted);
        account.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        Tiles.SignedIn = nav.SignedIn = personName != null;
        // Signed out inside a category that needs the card: back to the start page.
        if (personName == null && nav.SelectedLocked) { nav.Select(""); Tiles.ShowCategory(""); ApplyVisibility(); }
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
            : "Pozycje z kłódką otworzysz po zalogowaniu kartą.");
        account.Text = portalSignedIn ? "Zakończ sesję" : signInWanted ? "Wróć" : "Zaloguj kartą";
        // Signed out, the card sign-in is on "Strona główna": the button only ends a session.
        if (account.Visible != (portal && portalSignedIn)) account.Visible = portal && portalSignedIn;
    }

    private string? pageTitleText;
    /// <summary>The title of the page open in the middle (shown in the bar on top).</summary>
    internal void SetPageTitle(string? title) { pageTitleText = title; UpdatePortalHeader(); }

    /// <summary>In the portal the card is on screen only for the sign-in someone asked for, a question or the PIN.</summary>
    private bool PortalCard => signingIn && signInWanted || AskingPin || Asking || PortalHome;
    /// <summary>"Strona główna": the start screen as before, the card (sign-in, PIN, greeting) in the middle and the messages under it.</summary>
    private bool PortalHome => portal && nav.Selected.Length == 0;

    private void ApplyPortalVisibility(bool page)
    {
        bool cardOn = page || PortalCard;
        card.Visible = cardOn;
        Tiles.Visible = !cardOn;
        Tiles.Busy = Asking || AskingPin;
        if (Help.Visible) Help.Visible = false;
        if (!PortalHome && News.Visible) News.Visible = false;
        UpdatePortalHeader();
    }

    /// <summary>The portal's places; returns the card's bounds (a page fills the middle, the card is centred in it).</summary>
    private Rectangle ArrangePortal(int bar, int cardWidth, int cardHeight, bool page)
    {
        int margin = Scale(24), side = Math.Min(Scale(270), Math.Max(Scale(200), Width / 5));
        portalSide = side;
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
        // Signed in: the person and "Zakończ sesję" at the bottom of the left column, above the time.
        int bottom = clock.Top - Scale(12);
        account.SetBounds(Scale(14), bottom - Scale(48), side - Scale(28), Scale(48));
        portalPerson.SetBounds(Scale(18), account.Top - Scale(34), side - Scale(36), Scale(30));
        portalPerson.TextAlign = ContentAlignment.MiddleLeft;
        if (portalSignedIn) bottom = portalPerson.Top - Scale(8);
        nav.SetBounds(Scale(14), y, side - Scale(28), Math.Max(0, Math.Min(nav.Needed, bottom - Scale(8) - y)));
        // No bar on top: the chosen category is marked in the left column, a page has its own bar.
        foreach (var c in new Control[] { portalTitle, portalLine }) if (c.Visible) c.Visible = false;
        if (portalPerson.Visible != portalSignedIn) portalPerson.Visible = portalSignedIn;
        if (account.Visible != portalSignedIn) account.Visible = portalSignedIn;
        // The tiles as far from the line as the left column's entries are.
        int left = side + Scale(14), right = Width - margin;
        var middle = new Rectangle(left, bar + margin, Math.Max(Scale(200), right - left), 0);
        middle.Height = Math.Max(Scale(200), Height - margin - middle.Top);
        Tiles.Bounds = middle;
        // A page starts right after the left column's line: no gap, no second line.
        if (page) return new Rectangle(side + 1, bar, Width - side - 1, Height - bar);
        cardWidth = Math.Min(cardWidth, middle.Width);
        bool news = false;
        if (PortalHome)
        {
            // The messages at the bottom of the middle when they fit under the card (the size from Konfiguracja, or smaller).
            int[] heights = [132, 220, 320], widths = [980, 1200, 1400];
            int size = (int)banner, gap = Scale(20);
            while (size > 0 && middle.Height - cardHeight - Scale(heights[size]) - gap * 2 < 0) size--;
            int newsHeight = Scale(heights[size]);
            news = News.Count > 0 && !AskingPin && !Asking && middle.Height - cardHeight - newsHeight - gap * 2 >= 0;
            if (news)
            {
                int newsWidth = Math.Min(Scale(widths[size]), middle.Width);
                News.SetBounds(middle.Left + (middle.Width - newsWidth) / 2, middle.Bottom - newsHeight, newsWidth, newsHeight);
                int room = News.Top - gap - middle.Top;
                if (News.Visible != news) News.Visible = news;
                return new Rectangle(middle.Left + (middle.Width - cardWidth) / 2, middle.Top + Math.Max(0, (room - cardHeight) / 2), cardWidth, cardHeight);
            }
        }
        if (News.Visible != news) News.Visible = news;
        return new Rectangle(middle.Left + (middle.Width - cardWidth) / 2, middle.Top + Math.Max(0, (middle.Height - cardHeight) / 3), cardWidth, cardHeight);
    }
}
