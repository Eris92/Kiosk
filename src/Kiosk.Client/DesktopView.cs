using System.Drawing.Drawing2D;

namespace Kiosk.Client;

internal static class KioskTheme
{
    internal static readonly Color Background = Color.FromArgb(13, 20, 35);
    internal static readonly Color Surface = Color.FromArgb(24, 35, 54);
    internal static readonly Color Border = Color.FromArgb(44, 60, 81);
    internal static readonly Color Text = Color.FromArgb(240, 245, 252);
    internal static readonly Color Muted = Color.FromArgb(157, 174, 195);
    internal static readonly Color Taskbar = Color.FromArgb(17, 26, 42);
    internal const string DefaultAccent = "#5AB5FF";
    // Windows 11 (26H2) dark shell: neutral taskbar and Start menu, light hover, accent only for the indicators.
    internal static readonly Color ShellBar = Color.FromArgb(32, 32, 32);
    internal static readonly Color ShellHover = Color.FromArgb(52, 52, 52);
    internal static readonly Color ShellActive = Color.FromArgb(44, 44, 44);
    internal static readonly Color ShellBorder = Color.FromArgb(56, 56, 56);
    internal static readonly Color MenuBack = Color.FromArgb(38, 38, 38);
    internal static readonly Color MenuFooter = Color.FromArgb(30, 30, 30);
    internal static readonly Color MenuHover = Color.FromArgb(54, 54, 54);

    /// <summary>Accent presets offered in Konfiguracja (any #RRGGBB also works).</summary>
    internal static readonly (string Name, string Hex)[] Presets =
    [
        ("Niebieski", "#5AB5FF"), ("Zielony", "#3DDC84"), ("Turkusowy", "#2DD4BF"), ("Fioletowy", "#A78BFA"),
        ("Różowy", "#F472B6"), ("Pomarańczowy", "#FB923C"), ("Czerwony", "#F87171"), ("Żółty", "#FACC15"),
        ("Czarny", "#000000"), ("Biały", "#E5E7EB")
    ];

    /// <summary>Fill colour of primary buttons, the Menu button and highlights; chosen in Konfiguracja.</summary>
    internal static Color Accent { get; private set; } = ColorTranslator.FromHtml(DefaultAccent);
    /// <summary>Accent for thin lines and icons: a near-black accent would vanish on the dark background.</summary>
    internal static Color AccentLine => Luminance(Accent) < 0.12 ? Text : Accent;
    internal static Color AccentHover => Luminance(Accent) < 0.12 ? Color.FromArgb(40, 40, 46) : Blend(Accent, Color.White, 0.2);
    internal static Color AccentPressed => Luminance(Accent) < 0.12 ? Color.FromArgb(25, 25, 30) : Blend(Accent, Color.Black, 0.15);
    internal static Color AccentSoft => Blend(Surface, AccentLine, 0.18);
    /// <summary>Readable text on an accent fill.</summary>
    internal static Color OnAccent => Luminance(Accent) > 0.45 ? Background : Color.White;
    internal static event Action? Changed;

    internal static bool IsColor(string? value) =>
        value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");

    internal static void SetAccent(string? hex)
    {
        Accent = ColorTranslator.FromHtml(IsColor(hex) ? hex! : DefaultAccent);
        Changed?.Invoke();
    }

    internal static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount), (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    internal static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    internal static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? AccentLine : Border;
        button.FlatAppearance.BorderSize = primary && Luminance(Accent) >= 0.12 ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Color.FromArgb(38, 54, 78);
        button.FlatAppearance.MouseDownBackColor = primary ? AccentPressed : Color.FromArgb(49, 67, 93);
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? OnAccent : Text;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>The desktop shown between apps and while the card session is locked.</summary>
internal sealed partial class DesktopView : UserControl
{
    private readonly Label brand = new() { Text = "Kiosk", AutoSize = false };
    private readonly Label subtitle = new() { Text = "Twój pulpit pracy", AutoSize = false };
    private readonly PictureBox logo = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Visible = false };
    private string logoPath = "";
    private readonly Label clock = new() { AutoSize = false, TextAlign = ContentAlignment.TopRight };
    private readonly Label date = new() { AutoSize = false, TextAlign = ContentAlignment.TopRight };
    private readonly StatusCard card = new();
    /// <summary>Company messages at the bottom (Konfiguracja → Komunikaty).</summary>
    internal NewsCarousel News { get; } = new();
    /// <summary>Szybka pomoc (Konfiguracja → Szybka pomoc), shown locked and unlocked.</summary>
    internal HelpBar Help { get; } = new();
    private readonly Label heading = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
    private readonly Label detail = new() { AutoSize = false, TextAlign = ContentAlignment.TopCenter };
    private readonly Label hint = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button resume = new() { Text = "Wznów sesję", Visible = false };
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 1000 };
    // Card PIN asked right on the lock card (FIDO2 cards), instead of a separate window.
    private readonly Label pinPrompt = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private readonly TextBox pinBox = new() { UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center, MaxLength = 63, Visible = false };
    private readonly Button pinOk = new() { Text = "Zaloguj", Visible = false };
    private readonly Button pinCancel = new() { Text = "Anuluj", Visible = false };
    private readonly System.Windows.Forms.Timer pinWatch = new() { Interval = 250 };
    private TaskCompletionSource<string?>? pinRequest;
    private Func<bool>? pinCardPresent;
    // Szybka pomoc: the PIN field gives up when nobody types for the help time, with the same countdown as everywhere.
    private readonly HelpCountdown pinCountdown = new() { Visible = false };
    private bool locked;
    internal bool AskingPin => pinRequest != null;
    // Szybka pomoc questions (Resetuj kartę…) asked right in the card, instead of a separate window over the Kiosk.
    private readonly Label askTitle = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    private readonly Label askText = new() { AutoSize = false, TextAlign = ContentAlignment.TopCenter, Visible = false };
    private readonly Button askYes = new() { Visible = false }, askNo = new() { Visible = false };
    private TaskCompletionSource<bool>? askRequest;
    private bool askWarning;
    internal bool Asking => askRequest != null;
    /// <summary>A Szybka pomoc page shown in the middle frame (the card grows to the free space beside Szybka pomoc).</summary>
    private Control? pageContent;
    internal Control? Page => pageContent;
    /// <summary>Menu → "Obok": a second page or a PDF at the page's right; closed together with the page.</summary>
    private Control? sideContent;

    /// <summary>Puts <paramref name="content"/> beside the page (null = the page alone); the previous one is disposed.</summary>
    internal void ShowSide(Control? content)
    {
        if (sideContent == content) return;
        var old = sideContent;
        sideContent = content;
        if (content != null)
        {
            content.Visible = false;
            card.Controls.Add(content);
            content.BringToFront();
        }
        if (old != null && !old.IsDisposed) { card.Controls.Remove(old); old.Dispose(); }
        ArrangeControls();
        ApplyVisibility();
    }

    /// <summary>The element beside the page was folded or unfolded.</summary>
    internal void RefreshSide() => ArrangeControls();

    /// <summary>The page in <paramref name="area"/> of the card; with a second element the page takes 55 % at the left.</summary>
    private void PlacePage(Rectangle area)
    {
        pageContent!.Bounds = area;
        // The page keeps its bar (Zamknij) and countdown across the whole width; the element beside it sits between them,
        // in the right part the page leaves free. Folded: only its ribbon at the right edge.
        int width = sideContent == null ? 0 : sideContent is SidePage { Collapsed: true } folded ? folded.StripWidth : area.Width * 45 / 100;
        if (pageContent is HelpBrowserForm form) form.SetSideInset(width);
        else if (pageContent is ApplicationView { HostedWindow: var window } && window != IntPtr.Zero)
            AppNative.PostMessage(window, HelpBrowserForm.SideInsetMessage, (IntPtr)width, IntPtr.Zero);
        if (sideContent == null) return;
        sideContent.Bounds = HelpBrowserForm.SideSlot(area, width, DeviceDpi);
        sideContent.BringToFront();
    }
    /// <summary>The card sign-in (Windows' card + PIN, see <see cref="FidoSignIn"/>) is drawn inside this place of the card.</summary>
    private readonly Panel signInPlace = new() { Visible = false };
    private bool signingIn;
    /// <summary>The window the card sign-in screen becomes part of (created on first use, on the UI thread).</summary>
    internal IntPtr SignInPlace => signInPlace.Handle;

    internal event Action? ResumeRequested;

    internal DesktopView()
    {
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        brand.Font = new Font("Segoe UI", 24, FontStyle.Bold);
        subtitle.Font = new Font("Segoe UI", 10);
        subtitle.ForeColor = KioskTheme.Muted;
        clock.Font = new Font("Segoe UI", 25, FontStyle.Regular);
        date.Font = new Font("Segoe UI", 10);
        date.ForeColor = KioskTheme.Muted;
        heading.Font = new Font("Segoe UI", 22, FontStyle.Bold);
        detail.Font = new Font("Segoe UI", 11);
        detail.ForeColor = KioskTheme.Muted;
        hint.Font = new Font("Segoe UI", 10);
        hint.ForeColor = KioskTheme.Muted;

        foreach (var label in new[] { brand, subtitle, clock, date }) label.BackColor = Color.Transparent;
        foreach (var label in new[] { heading, detail, hint, pinPrompt }) label.BackColor = KioskTheme.Surface;
        signInPlace.BackColor = KioskTheme.Surface;
        pinPrompt.Font = new Font("Segoe UI", 11);
        pinPrompt.ForeColor = KioskTheme.Muted;
        pinPrompt.Visible = false;
        pinBox.Font = new Font("Segoe UI", 20);
        pinBox.BackColor = KioskTheme.Background;
        pinBox.ForeColor = KioskTheme.Text;
        KioskTheme.StyleButton(resume, primary: true);
        KioskTheme.StyleButton(pinOk, primary: true);
        KioskTheme.StyleButton(pinCancel);
        pinOk.Click += (_, _) => FinishPin(true);
        pinCancel.Click += (_, _) => FinishPin(false);
        pinBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FinishPin(true); }
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; FinishPin(false); }
        };
        pinWatch.Tick += (_, _) =>
        {
            if (pinCardPresent?.Invoke() == false) FinishPin(false); // Card taken away.
        };
        pinBox.TextChanged += (_, _) => pinCountdown.Restart();
        pinCountdown.Expired += () => { FinishPin(false); FinishAsk(false); };
        askTitle.Font = new Font("Segoe UI", 18, FontStyle.Bold);
        askText.Font = new Font("Segoe UI", 11);
        askText.ForeColor = KioskTheme.Muted;
        foreach (var label in new[] { askTitle, askText }) label.BackColor = KioskTheme.Surface;
        askYes.Click += (_, _) => FinishAsk(true);
        askNo.Click += (_, _) => FinishAsk(false);
        foreach (var button in new[] { askYes, askNo })
            button.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; FinishAsk(false); } };
        // Like the other help windows: a real mouse move, click or key anywhere on the desktop gives more time.
        HelpCountdown.Watch(this, () => { if (pinCountdown.Visible) pinCountdown.Restart(); });
        Help.ContentChanged += ArrangeControls;   // A category opened: the column has another height.
        KioskTheme.Changed += OnThemeChanged;
        resume.Click += (_, _) => ResumeRequested?.Invoke();
        card.Controls.AddRange(new Control[] { heading, detail, hint, resume, pinPrompt, pinBox, pinOk, pinCancel, pinCountdown, askTitle, askText, askYes, askNo, signInPlace });
        Controls.AddRange(new Control[] { card, News, Help, logo, brand, subtitle, clock, date });
        InitPortal();
        clockTimer.Tick += (_, _) => UpdateClock();
        clockTimer.Start();
        UpdateClock();
        UpdateStatus("Witaj w Kiosku", "Przyłóż kartę, aby rozpocząć pracę.");
    }

    private void OnThemeChanged()
    {
        KioskTheme.StyleButton(resume, primary: true);
        KioskTheme.StyleButton(pinOk, primary: true);
        KioskTheme.StyleButton(pinCancel);
        KioskTheme.StyleButton(askYes, primary: !askWarning);
        KioskTheme.StyleButton(askNo, primary: askWarning);
        Invalidate(true);
    }

    private string? person;

    /// <summary><paramref name="person"/>: a signed-in person's name, shown with their initials in a circle instead of a greeting.</summary>
    internal void UpdateStatus(string title, string description, bool locked = false, string buttonText = "Wznów sesję", string? person = null)
    {
        // Called on every shell tick; repaint only when something changed.
        if (heading.Text == title && detail.Text == description && this.locked == locked && resume.Text == buttonText && this.person == person) return;
        this.person = person;
        card.Initials = person == null ? "" : People.Initials(person);
        resume.Text = buttonText;
        this.locked = locked;
        heading.Text = title;
        detail.Text = description;
        hint.Text = locked ? "Twoje aplikacje czekają na wznowienie sesji." : "Aplikacje i połączenia znajdziesz w menu na dole.";
        card.Locked = locked;
        ApplyVisibility();
        ArrangeControls();
        card.Invalidate();
    }

    /// <summary>Shows the card sign-in screen in the card instead of the greeting (or back).</summary>
    internal void ShowSignIn(bool show)
    {
        if (signingIn == show) return;
        signingIn = show;
        ApplyVisibility();
        ArrangeControls();
        card.Invalidate();
    }

    /// <summary>
    /// One screen at a time in the card: a question, the PIN field, the card sign-in or the greeting. A PIN asked
    /// while the sign-in waits for a tap (a new PIN after a card reset) hides the sign-in instead of drawing over it.
    /// </summary>
    private void ApplyVisibility()
    {
        // Only one of them is in the frame; nothing of another one stays under it (the icon showed through a question).
        bool page = pageContent != null && !AskingPin && !Asking;
        // In the portal the greeting is never shown and the sign-in only when asked for: computed once, so nothing is
        // shown and hidden again in one pass (that made other elements blink).
        bool home = portal && nav.Selected.Length == 0;
        // The card sign-in as in build-176: shown whenever it is ready and nothing else (page, question, PIN) is in the card.
        bool signIn = signingIn && !AskingPin && !Asking && !page;
        bool status = !signIn && !page && !Asking && (!portal || home);
        card.SuspendLayout();
        if (pageContent != null) pageContent.Visible = page;
        if (sideContent != null) sideContent.Visible = page;
        signInPlace.Visible = signIn;
        card.SignIn = !status || AskingPin;
        heading.Visible = status;
        detail.Visible = status && !AskingPin;   // The PIN's own text takes its place.
        resume.Visible = status && locked && !AskingPin && !signingIn;
        hint.Visible = status && !AskingPin && !signingIn;
        foreach (var c in new Control[] { askTitle, askText, askYes, askNo }) c.Visible = Asking;
        if (Asking) pinCountdown.Visible = true;
        // Szybka pomoc stays where it is; while a question or the PIN waits it is greyed out.
        Help.Busy = Asking || AskingPin;
        if (portal) ApplyPortalVisibility(page);
        else { card.Visible = true; Tiles.Visible = false; }
        card.ResumeLayout(false);
    }

    /// <summary>
    /// Shows <paramref name="content"/> (a help page) in the middle frame instead of opening a window over the screen;
    /// null = back to the card. The previous content is only taken out: its owner closes it.
    /// </summary>
    internal void ShowPage(Control? content)
    {
        if (pageContent == content) return;
        if (portal) Atomically(() => SwapPage(content));
        else SwapPage(content);
    }

    private void SwapPage(Control? content)
    {
        if (pageContent != null && !pageContent.IsDisposed) card.Controls.Remove(pageContent);
        pageContent = content;
        if (content != null)
        {
            content.Visible = false;
            card.Controls.Add(content);
            content.BringToFront();
        }
        else
        {
            // The second element goes with its page.
            if (sideContent != null) { var side = sideContent; sideContent = null; card.Controls.Remove(side); side.Dispose(); }
            // A page closed (Zamknij, its time ran out, signing out): back to "Strona główna".
            if (portal && nav.Selected.Length > 0) { nav.Select(""); Tiles.ShowCategory(""); }
        }
        ArrangeControls();
        ApplyVisibility();
        card.Invalidate();
    }

    /// <summary>The middle frame's page area on the screen (for a page drawn by another program over it).</summary>
    internal Rectangle PageScreenArea => pageContent != null ? pageContent.RectangleToScreen(pageContent.ClientRectangle) : card.RectangleToScreen(card.ClientRectangle);

    /// <summary>
    /// A yes / no question in the card (Szybka pomoc): no separate window. Nobody answers for
    /// <paramref name="idleSeconds"/>: "no". With <paramref name="warning"/> "no" is the default and drawn in the accent.
    /// </summary>
    internal Task<bool> AskAsync(string title, string text, int idleSeconds, bool warning = false)
    {
        askRequest?.TrySetResult(false);
        askRequest = new TaskCompletionSource<bool>();
        askWarning = warning;
        askTitle.Text = title;
        askText.Text = text;
        askYes.Text = warning ? "Tak, wykonaj" : "Tak";
        askNo.Text = "Nie";
        KioskTheme.StyleButton(askYes, primary: !warning);
        KioskTheme.StyleButton(askNo, primary: warning);
        pinCountdown.Start(Math.Max(5, idleSeconds));
        ApplyVisibility();
        ArrangeControls();
        card.Invalidate();
        (warning ? askNo : askYes).Focus();   // A dangerous step is never the default.
        return askRequest.Task;
    }

    private void FinishAsk(bool yes)
    {
        if (askRequest == null) return;
        var request = askRequest;
        askRequest = null;
        if (!AskingPin) { pinCountdown.Stop(); pinCountdown.Visible = false; }
        ApplyVisibility();
        ArrangeControls();
        card.Invalidate();
        request.TrySetResult(yes);
    }

    /// <summary>The desktop corner from Konfiguracja: name, line under it and logo; empty ones are hidden.</summary>
    internal void SetBranding(string title, string line, string logoFile)
    {
        brand.Text = title;
        subtitle.Text = line;
        if (logoFile != logoPath)
        {
            logoPath = logoFile;
            var old = logo.Image;
            logo.Image = null;
            old?.Dispose();
            // Read into memory, so the file is not kept locked; a missing or broken file simply shows no logo.
            try
            {
                if (logoFile.Length > 0 && File.Exists(logoFile)) logo.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(logoFile)));
                else if (logoFile.Length > 0) Audit.Write("logo_missing", logoFile);
            }
            catch (Exception ex) { logo.Image = null; Audit.Write("logo_missing", logoFile + ": " + ex.Message); }
        }
        logo.Visible = logo.Image != null;
        brand.Visible = title.Length > 0;
        subtitle.Visible = line.Length > 0;
        ArrangeControls();
    }

    internal void SetHelp(HelpAction[] actions)
    {
        if (ReferenceEquals(Help.Items, actions)) return;
        Help.SetItems(actions);
        nav.SetItems(actions);
        Tiles.SetItems(actions);
        Tiles.ShowCategory(nav.Selected);
        ApplyVisibility();
        ArrangeControls();
    }

    private NewsBanner banner;
    private readonly List<AlertBar> bars = [];
    internal int BarCount => bars.Count;

    internal void SetNews(List<NewsItem> items, int seconds, NewsBanner size = NewsBanner.Standard)
    {
        News.Seconds = seconds;
        banner = size;
        // Bars ("Kind": "Pasek") go across the top, at most three; the rest scroll at the bottom.
        var wanted = items.Where(i => i.IsBar).Take(3).ToList();
        if (!wanted.SequenceEqual(bars.Select(b => b.Item)))
        {
            foreach (var old in bars) { Controls.Remove(old); old.Dispose(); }
            bars.Clear();
            foreach (var item in wanted)
            {
                var bar = new AlertBar(item);
                bar.ActionRequested += News.RequestAction;
                bars.Add(bar);
                Controls.Add(bar);
                bar.BringToFront();
                if (IsHandleCreated) _ = bar.Handle; // Also while the desktop is hidden (an app in front), so it is ready when it shows.
            }
        }
        News.SetItems(items.Where(i => !i.IsBar).ToList());
        ArrangeControls();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        clock.Text = now.ToString("HH:mm");
        date.Text = now.ToString("dddd, d MMMM", System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ArrangeControls();
    }

    private void ArrangeControls()
    {
        if (card == null) return;
        int margin = Scale(36);
        // Bars across the top (Komunikaty, "Kind": "Pasek"); everything else moves down under them.
        int bar = 0;
        foreach (var alert in bars) { alert.SetBounds(0, bar, Width, Scale(44)); bar += Scale(44); }
        int clockWidth = Math.Min(Scale(290), Math.Max(Scale(150), Width / 3));
        int logoSize = Scale(76), textLeft = margin;
        // Visible reads false while the desktop itself is hidden, so the layout looks at the content.
        // The portal places these itself: moving them here first and back there made them blink on every change.
        if (!portal && logo.Image != null)
        {
            // Wide logos keep their proportions up to three times their height.
            var image = logo.Image!;
            int logoWidth = Math.Min(logoSize * 3, Math.Max(Scale(20), logoSize * image.Width / Math.Max(1, image.Height)));
            logo.SetBounds(margin, bar + Scale(24), logoWidth, logoSize);
            textLeft = margin + logoWidth + Scale(16);
        }
        int textWidth0 = Math.Max(0, Width - clockWidth - margin - textLeft);
        // Only a line under it: the line moves up to where the name would be.
        if (!portal)
        {
            brand.SetBounds(textLeft, bar + Scale(24), textWidth0, Scale(47));
            subtitle.SetBounds(textLeft + Scale(2), bar + (brand.Text.Length > 0 ? Scale(75) : Scale(30)), textWidth0, Scale(25));
            clock.SetBounds(Width - clockWidth - margin, bar + Scale(25), clockWidth, Scale(47));
            date.SetBounds(Width - clockWidth - margin, bar + Scale(77), clockWidth, Scale(25));
        }

        int cardWidth = Math.Min(Scale(660), Math.Max(Scale(240), Width - margin * 2));
        bool personRow = person != null && !locked && !AskingPin && !Asking;
        int cardHeight = Scale(Asking ? 430 : AskingPin ? 470 : signingIn ? 440 : locked ? 370 : personRow ? 270 : 324);
        bool page = pageContent != null && !AskingPin && !Asking;
        if (portal)
        {
            var place = ArrangePortal(bar, cardWidth, cardHeight, page);
            cardWidth = place.Width;
            cardHeight = place.Height;
            card.Bounds = place;
            if (page) PlacePage(new Rectangle(0, 0, cardWidth, cardHeight));
            if (Help.Visible) Help.Visible = false;
        }
        else ArrangeDesktop(bar, margin, ref cardWidth, ref cardHeight, page);
        ArrangeCardInside(cardWidth, cardHeight, personRow);
    }

    /// <summary>The desktop with the card in the middle, Szybka pomoc beside or under it and the messages at the bottom.</summary>
    private void ArrangeDesktop(int bar, int margin, ref int cardWidth, ref int cardHeight, bool page)
    {
        // Szybka pomoc: a column at the left when it fits beside the card, otherwise a row under the card.
        bool help = Help.Count > 0;
        var column = Help.Needed(true);
        bool helpColumn = help && (Width - cardWidth) / 2 - margin - Scale(16) >= column.Width && Height - (bar + Scale(125)) >= column.Height;
        int helpRow = help && !helpColumn ? Scale(56) : 0;   // gap + row height
        Help.Vertical = helpColumn;
        // The messages take the bottom of the desktop when they fit under the card; the card is centred above them.
        // The size set in Konfiguracja, or the largest smaller one that fits.
        int[] heights = [132, 220, 320], widths = [980, 1200, 1400];
        int size = (int)banner, newsGap = Scale(20), bottom = Height;
        while (size > 0 && Height - (bar + Scale(125)) - cardHeight - helpRow - Scale(heights[size]) - newsGap * 2 < 0) size--;
        int newsHeight = Scale(heights[size]);
        bool news = News.Count > 0 && !AskingPin && !Asking && !page && Height - (bar + Scale(125)) - cardHeight - helpRow - newsHeight - newsGap * 2 >= 0;
        News.Visible = news;
        if (news)
        {
            int newsWidth = Math.Min(Scale(widths[size]), Math.Max(Scale(240), Width - margin * 2));
            News.SetBounds((Width - newsWidth) / 2, Height - newsHeight - newsGap, newsWidth, newsHeight);
            bottom = News.Top - newsGap;
        }
        int top = Math.Max((bar + Scale(125)), (bottom - cardHeight - helpRow) / 2 + Scale(12));
        if (news) top = Math.Min(top, bottom - cardHeight - helpRow);
        if (page)
        {
            // The page takes the whole free space: beside the Szybka pomoc column, or above its row.
            int left = helpColumn ? margin + column.Width + Scale(24) : margin;
            top = bar + Scale(125);
            cardWidth = Math.Max(Scale(240), Width - margin - left);
            cardHeight = Math.Max(Scale(200), Height - top - margin - helpRow);
            card.SetBounds(left, top, cardWidth, cardHeight);
            int inset = Scale(10);
            PlacePage(new Rectangle(inset, inset, Math.Max(0, cardWidth - 2 * inset), Math.Max(0, cardHeight - 2 * inset)));
        }
        else card.SetBounds((Width - cardWidth) / 2, top, cardWidth, cardHeight);
        Help.Visible = help;
        if (helpColumn) Help.SetBounds(margin, (bar + Scale(125)), column.Width, column.Height);
        else if (help)
        {
            var row = Help.Needed(false);
            int rowWidth = Math.Min(row.Width, Width - margin * 2);
            Help.SetBounds((Width - rowWidth) / 2, card.Bottom + Scale(16), rowWidth, row.Height);
        }
    }

    /// <summary>What is inside the card: the sign-in, the greeting, the PIN field or a question.</summary>
    private void ArrangeCardInside(int cardWidth, int cardHeight, bool personRow)
    {
        int innerMargin = Scale(30);
        signInPlace.SetBounds(innerMargin, Scale(24), Math.Max(0, cardWidth - innerMargin * 2), Math.Max(0, cardHeight - Scale(48)));
        int textWidth = Math.Max(0, cardWidth - innerMargin * 2);
        if (personRow)
        {
            // The person: initials in a circle and the name beside it, the rest under them.
            int diameter = Scale(62), gap = Scale(18);
            int nameWidth = Math.Max(0, Math.Min(textWidth - diameter - gap, TextRenderer.MeasureText(heading.Text, heading.Font).Width + Scale(8)));
            int left = (cardWidth - diameter - gap - nameWidth) / 2;
            card.Avatar = new Rectangle(left, Scale(36), diameter, diameter);
            heading.TextAlign = ContentAlignment.MiddleLeft;
            heading.SetBounds(left + diameter + gap, Scale(32), nameWidth, diameter + Scale(8));
            detail.SetBounds(innerMargin, Scale(124), textWidth, Scale(64));
            hint.SetBounds(innerMargin, Scale(214), textWidth, Scale(35));
        }
        else
        {
            card.Avatar = Rectangle.Empty;
            heading.TextAlign = ContentAlignment.MiddleCenter;
            heading.SetBounds(innerMargin, Scale(110), textWidth, Scale(70));
            detail.SetBounds(innerMargin, Scale(191), textWidth, Scale(64));
            hint.SetBounds(innerMargin, Scale(locked ? 320 : 270), textWidth, Scale(35));
        }
        int buttonWidth = Math.Min(Scale(220), textWidth);
        resume.SetBounds((cardWidth - buttonWidth) / 2, Scale(265), buttonWidth, Scale(43));
        int pinWidth = Math.Min(Scale(360), textWidth);
        int pinLeft = (cardWidth - pinWidth) / 2;
        // In the place of the text under the heading, wrapped (a long one was cut off on one line).
        pinPrompt.SetBounds(innerMargin, Scale(186), textWidth, Scale(104));
        pinBox.SetBounds(pinLeft, Scale(300), pinWidth, pinBox.PreferredHeight);
        int half = (pinWidth - Scale(12)) / 2;
        pinOk.SetBounds(pinLeft, Scale(385), half, Scale(46));
        pinCancel.SetBounds(pinLeft + half + Scale(12), Scale(385), half, Scale(46));
        pinCountdown.SetBounds(pinLeft, Scale(436), pinWidth, Scale(30));
        if (Asking)
        {
            int askWidth = Math.Min(Scale(440), textWidth), askLeft = (cardWidth - askWidth) / 2, askHalf = (askWidth - Scale(12)) / 2;
            askTitle.SetBounds(innerMargin, Scale(36), textWidth, Scale(60));
            // As high as its text: the buttons follow right under it.
            int textHeight = Math.Min(Scale(196), TextRenderer.MeasureText(askText.Text, askText.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height + Scale(8));
            askText.SetBounds(innerMargin, Scale(104), textWidth, textHeight);
            int buttonsTop = askText.Bottom + Scale(24);
            // "Nie" on the right, as before; the default one is the accent button.
            askYes.SetBounds(askLeft, buttonsTop, askHalf, Scale(46));
            askNo.SetBounds(askLeft + askHalf + Scale(12), buttonsTop, askHalf, Scale(46));
            pinCountdown.SetBounds(askLeft, buttonsTop + Scale(58), askWidth, Scale(30));
            card.Height = Math.Min(card.Height, pinCountdown.Bottom + Scale(30));
        }
    }

    /// <summary>
    /// Shows the PIN field on the lock card and waits for it; null when cancelled or the card is taken away.
    /// The typed text is handed over once and the field is cleared.
    /// </summary>
    internal Task<string?> AskPinAsync(string message, Func<bool>? cardPresent, int idleSeconds = 0)
    {
        pinCountdown.Visible = idleSeconds > 0;
        if (idleSeconds > 0) pinCountdown.Start(idleSeconds); else pinCountdown.Stop();
        pinRequest?.TrySetResult(null);
        pinRequest = new TaskCompletionSource<string?>();
        pinCardPresent = cardPresent;
        pinPrompt.Text = message;
        foreach (var c in new Control[] { pinPrompt, pinBox, pinOk, pinCancel }) c.Visible = true;
        ApplyVisibility();
        ArrangeControls();
        card.Invalidate();
        pinBox.Clear();
        pinBox.Focus();
        pinWatch.Start();
        return pinRequest.Task;
    }

    private void FinishPin(bool accepted)
    {
        if (pinRequest == null || accepted && pinBox.TextLength == 0) return;
        var request = pinRequest;
        var value = accepted ? pinBox.Text : null;
        pinBox.Clear();
        pinWatch.Stop();
        pinRequest = null;
        foreach (var c in new Control[] { pinPrompt, pinBox, pinOk, pinCancel, pinCountdown }) c.Visible = false;
        pinCountdown.Stop();
        ApplyVisibility();
        ArrangeControls();
        card.Invalidate();
        request.TrySetResult(value);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        PaintBackdrop(e.Graphics, ClientSize, DeviceDpi);
        // The portal: the same background everywhere (the tiles lie on it too), a line after the left column.
        if (portal && portalSide > 0)
        {
            using var line = new Pen(KioskTheme.Border);
            e.Graphics.DrawLine(line, portalSide, 0, portalSide, Height);
        }
    }

    /// <summary>The Kiosk's background (a navy gradient with two faint rings) for a window of <paramref name="size"/>.</summary>
    private static void PaintBackdrop(Graphics g, Size size, int dpi)
    {
        var area = new Rectangle(Point.Empty, size);
        using var background = new LinearGradientBrush(area, Color.FromArgb(21, 36, 59), KioskTheme.Background, 70f);
        g.FillRectangle(background, area);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var accent = new Pen(Color.FromArgb(20, KioskTheme.AccentLine), Math.Max(1, dpi / 96f));
        int ringSize = (int)Math.Round(580 * dpi / 96d);
        g.DrawEllipse(accent, size.Width - ringSize / 2, -ringSize / 2, ringSize, ringSize);
        g.DrawEllipse(accent, -ringSize / 2, size.Height - ringSize / 3, ringSize, ringSize);
        g.SmoothingMode = smoothing;
    }

    /// <summary>Paints the window's background under <paramref name="control"/> (one of its children), so it lies on the same background.</summary>
    internal static void PaintBackdrop(Graphics g, Control control)
    {
        Control? view = control.Parent;
        while (view != null && view is not DesktopView) view = view.Parent;
        if (view == null || view.Width <= 0 || view.Height <= 0) { g.Clear(KioskTheme.Background); return; }
        var at = view.PointToClient(control.PointToScreen(Point.Empty));
        var state = g.Save();
        g.TranslateTransform(-at.X, -at.Y);
        PaintBackdrop(g, view.ClientSize, view.DeviceDpi);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { clockTimer.Dispose(); pinWatch.Dispose(); pinRequest?.TrySetResult(null); askRequest?.TrySetResult(false); KioskTheme.Changed -= OnThemeChanged; }
        base.Dispose(disposing);
    }

    private sealed class StatusCard : Panel
    {
        internal bool Locked { get; set; }
        internal string Initials { get; set; } = "";
        internal Rectangle Avatar { get; set; }
        /// <summary>Something else fills the card (sign-in, question, page): no icon.</summary>
        internal bool SignIn { get; set; }

        internal StatusCard()
        {
            BackColor = KioskTheme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 1 || Height <= 1) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
            using var cardPath = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(20));
            using var border = new Pen(KioskTheme.Border);
            g.DrawPath(border, cardPath);

            if (SignIn) return;
            if (Initials.Length > 0 && !Avatar.IsEmpty)
            {
                using var fill = new SolidBrush(KioskTheme.AccentSoft);
                g.FillEllipse(fill, Avatar);
                using var ring = new Pen(KioskTheme.AccentLine, S(2));
                g.DrawEllipse(ring, Avatar);
                using var letters = new Font("Segoe UI", 18, FontStyle.Bold);
                TextRenderer.DrawText(g, Initials, letters, Avatar, KioskTheme.AccentLine,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                return;
            }
            int diameter = S(62);
            var iconBounds = new Rectangle((Width - diameter) / 2, S(32), diameter, diameter);
            using var circle = new SolidBrush(KioskTheme.AccentSoft);
            g.FillEllipse(circle, iconBounds);
            using var line = new Pen(KioskTheme.AccentLine, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int centerX = Width / 2;
            int centerY = iconBounds.Top + diameter / 2;
            if (Locked)
            {
                g.DrawArc(line, centerX - S(9), centerY - S(15), S(18), S(23), 180, 180);
                using var lockPath = KioskTheme.RoundedRectangle(new Rectangle(centerX - S(14), centerY - S(2), S(28), S(21)), S(3));
                g.DrawPath(line, lockPath);
                g.DrawLine(line, centerX, centerY + S(6), centerX, centerY + S(11));
            }
            else
            {
                using var display = KioskTheme.RoundedRectangle(new Rectangle(centerX - S(17), centerY - S(12), S(34), S(23)), S(3));
                g.DrawPath(line, display);
                g.DrawLine(line, centerX, centerY + S(12), centerX, centerY + S(18));
                g.DrawLine(line, centerX - S(8), centerY + S(18), centerX + S(8), centerY + S(18));
            }
        }
    }
}

/// <summary>A keyboard-accessible launcher tile for the desktop's application menu.</summary>
internal sealed class MenuTile : Button
{
    private bool hovered;
    private bool pressed;
    private string category;

    internal MenuTile(string title, string category = "")
    {
        Text = title;
        this.category = category;
        Size = new Size(220, 84);
        Margin = new Padding(6);
        Padding = new Padding(16);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = KioskTheme.Surface;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 11, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleLeft;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Icon on the left of the tile: the program's own icon, or a glyph of the Windows icon font.</summary>
    internal Image? IconImage { get; init; }
    internal string? Glyph { get; init; }

    internal string Category
    {
        get => category;
        set { category = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        g.Clear(Parent?.BackColor ?? KioskTheme.Surface); // Rounded corners show the menu, not black.
        using var tile = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(12));
        using var fill = new SolidBrush(pressed ? KioskTheme.Blend(KioskTheme.Surface, KioskTheme.AccentLine, 0.22) : hovered ? Color.FromArgb(32, 48, 69) : KioskTheme.Surface);
        using var stroke = new Pen(hovered || Focused && ShowFocusCues ? KioskTheme.AccentLine : KioskTheme.Border);
        g.FillPath(fill, tile);
        g.DrawPath(stroke, tile);
        var textColor = Enabled ? KioskTheme.Text : KioskTheme.Muted;
        int left = S(17);
        if (IconImage != null || Glyph != null)
        {
            int icon = S(30);
            KioskIcons.Draw(g, new Rectangle(left, (Height - icon) / 2, icon, icon), Glyph, IconImage, Enabled ? KioskTheme.AccentLine : KioskTheme.Muted);
            left += icon + S(14);
        }
        int right = S(17);
        int titleHeight = Math.Max(Font.Height + S(5), S(28));
        int titleTop = category.Length == 0 ? (Height - titleHeight) / 2 : S(17);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(left, titleTop, Math.Max(0, Width - left - right), titleHeight), textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (category.Length > 0)
        {
            using var smallFont = new Font("Segoe UI", 9);
            TextRenderer.DrawText(g, category, smallFont, new Rectangle(left, S(49), Math.Max(0, Width - left - right), S(22)), KioskTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -S(6), -S(6)), KioskTheme.AccentLine, BackColor);
    }
}
