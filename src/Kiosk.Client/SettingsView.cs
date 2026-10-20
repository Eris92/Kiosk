namespace Kiosk.Client;

internal sealed class SettingsView : UserControl
{
    private static readonly (RdpAuthentication Value, string Label)[] AuthenticationOptions =
    [
        (RdpAuthentication.SmartCard, "Karta inteligentna (PIN)"),
        (RdpAuthentication.WindowsCurrentUser, "Windows Hello / bieżący użytkownik"),
        (RdpAuthentication.EntraId, "Microsoft Entra ID")
    ];
    private const string NoAutoConnect = "(nie łącz automatycznie)";

    private readonly Config original;
    private const string AnyReader = "(wszystkie czytniki, bez czytnika SIM)";
    private readonly ComboBox reader = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly TextBox home = new() { Dock = DockStyle.Fill };
    private readonly TextBox brandTitle = new() { Dock = DockStyle.Fill, MaxLength = 60 };
    private readonly TextBox brandSubtitle = new() { Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly TextBox brandLogo = new() { Dock = DockStyle.Fill };
    private readonly TextBox newsFolder = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown newsSeconds = new() { Minimum = 3, Maximum = 300, Width = 140 };
    private readonly NumericUpDown newsRefresh = new() { Minimum = 1, Maximum = 1440, Width = 140 };
    private readonly ComboBox newsShow = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, Items = { "Paski i komunikaty", "Tylko paski na górze", "Wyłączone" } };
    private readonly TextBox newsSharePoint = new() { Dock = DockStyle.Fill, PlaceholderText = "https://firma.sharepoint.com/sites/intranet" };
    private readonly TextBox newsSharePointAccount = new() { Dock = DockStyle.Fill, PlaceholderText = "nazwa konta z accounts.kiosk (puste = login i hasło poniżej)" };
    private readonly TextBox newsUser = new() { Dock = DockStyle.Fill, PlaceholderText = "login, np. kiosk@firma.pl (zapisany: zostaw puste, aby nie zmieniać)" };
    private readonly TextBox newsPassword = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "hasło" };
    private readonly CheckBox removeNewsAccount = new() { Text = "Usuń zapisany login i hasło", AutoSize = true };

    /// <summary>Komunikaty → login and password for SharePoint typed in (or "remove"); null = leave it as it is.</summary>
    internal CentralConfig.AccountChange? NewsAccountChange =>
        removeNewsAccount.Checked ? new("", "") : newsUser.Text.Trim().Length > 0 ? new(newsUser.Text.Trim(), newsPassword.Text) : null;
    private readonly NumericUpDown newsSharePointCount = new() { Minimum = 1, Maximum = 20, Width = 140 };
    private readonly ComboBox newsOrder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, Items = { "Na przemian (SharePoint, moje, SharePoint…)", "Najpierw moje z folderu", "Najpierw SharePoint" } };
    private readonly ComboBox newsSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, Items = { "Standardowy", "Duży", "Bardzo duży" } };
    private readonly CheckBox newsOnScreens = new() { Text = "Paski komunikatów także na ekranach wygaszacza (ekrany, ekran blokady)", AutoSize = true };
    private readonly NumericUpDown lockAfter = new() { Minimum = 10, Maximum = 86400, Width = 140 };
    private readonly NumericUpDown changeUserAfter = new() { Minimum = 0, Maximum = 86400, Width = 140 };
    private readonly NumericUpDown connectTimeout = new() { Minimum = 10, Maximum = 300, Width = 140 };
    private readonly ComboBox sessionMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620, Items = { "Kiosk: karta + PIN w Kiosku, aplikacje i przeglądarka jako ta osoba (SSO)", "Powłoka Windows: bez karty w Kiosku, liczy się konto zalogowane do Windows", "Punkt samoobsługowy: tylko ekran startowy z Menu i Komunikatami, karta loguje raz (FIDO2), dopóki leży na czytniku" } };
    private readonly CheckBox showHelp = new() { Text = "Pokazuj Menu na pulpicie i na ekranie blokady", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
    private readonly NumericUpDown cardRemovedSeconds = new() { Minimum = 1, Maximum = 300, Width = 100, Margin = new Padding(8, 0, 0, 0) };
    private readonly NumericUpDown helpCloseSeconds = new() { Minimum = 5, Maximum = 3600, Width = 100, Margin = new Padding(8, 0, 0, 0) };
    private readonly CheckBox autoUpdate = new() { Text = "Aktualizuj automatycznie przy uruchomieniu Kiosku", AutoSize = true, Margin = new Padding(4, 0, 4, 12) };
    private readonly TextBox configSource = new() { Width = 620, PlaceholderText = @"\\serwer\kiosk  albo  C:\KioskCentral", Margin = new Padding(4, 0, 4, 4) };
    private readonly Button sourceTest;
    private readonly TextBox sourceUser = new() { Width = 300, PlaceholderText = @"DOMENA\użytkownik  albo  serwer\użytkownik", Margin = new Padding(4, 0, 4, 4) };
    private readonly TextBox sourcePassword = new() { Width = 300, UseSystemPasswordChar = true, PlaceholderText = "hasło", Margin = new Padding(4, 0, 4, 4) };
    private readonly CheckBox removeAccount = new() { Text = "Usuń zapisane konto (łącz jako komputer)", AutoSize = true, Margin = new Padding(4, 4, 4, 4) };

    /// <summary>The share's account typed in (or "remove") for the save step; null = leave it as it is.</summary>
    internal CentralConfig.AccountChange? AccountChange =>
        removeAccount.Checked ? new("", "") : sourceUser.Text.Trim().Length > 0 ? new(sourceUser.Text.Trim(), sourcePassword.Text) : null;
    private readonly TextBox accountsKey = new() { Width = 300, UseSystemPasswordChar = true, PlaceholderText = "klucz kont wspólnych", Margin = new Padding(4, 0, 4, 4) };
    private readonly CheckBox removeKey = new() { Text = "Usuń zapisany klucz", AutoSize = true, Margin = new Padding(4, 4, 4, 4) };

    /// <summary>The shared accounts' key typed in ("" = remove) for the save step; null = leave it as it is.</summary>
    internal string? AccountsKey => removeKey.Checked ? "" : accountsKey.Text.Length > 0 ? accountsKey.Text : null;
    private readonly CheckBox disconnectOnRemoval = new() { Text = "Powłoka Windows: wyjęcie karty rozłącza sesję (programy działają dalej)", AutoSize = true };
    private readonly ComboBox unlock = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, Items = { "Tylko karta", "Karta + Windows Hello (PIN / biometria)", "Karta + PIN karty" } };
    private readonly ComboBox accent = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26
    };
    private readonly ComboBox taskbarAlign = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, Items = { "Na środku (jak Windows 11)", "Po lewej" } };
    private string accentHex = KioskTheme.DefaultAccent;
    // Windows 11 Settings, dark: neutral background, lighter controls, the accent only for the selection.
    private static readonly Color Back = Color.FromArgb(32, 32, 32);
    private static readonly Color InputBack = Color.FromArgb(45, 45, 45);
    private static readonly Color ControlBorder = Color.FromArgb(68, 68, 68);
    private static readonly Color HelpText = Color.FromArgb(80, 92, 110);   // marks help labels, which turn Muted in the dark theme
    private static readonly Color Card = Color.FromArgb(43, 43, 43);
    private const int RowHeight = 54;
    private const string CustomAccent = "Własny kolor…";
    private readonly CheckBox onlyBookmarks = new() { Text = "Pozwalaj otwierać tylko strony z listy: zakładki, dozwolone domeny i strony z Menu (także linki na nich)", AutoSize = true, Padding = new Padding(4, 6, 4, 6) };
    private readonly TextBox allowedDomains = new() { Dock = DockStyle.Fill, PlaceholderText = "login.microsoftonline.com, accounts.google.com" };
    private readonly CheckBox signInAsUser = new() { Text = "„Zaloguj” kartą i jej PIN-em (bez loginu i hasła): aplikacje, Edge i RDP działają jako ta osoba", AutoSize = true };
    private readonly CheckBox requirePin = new() { Text = "Wymagaj PIN-u / poświadczeń przy każdym połączeniu RDP", AutoSize = true };
    private readonly ComboBox autoConnect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly DataGridView connections = Grid();
    private readonly DataGridView apps = Grid();
    private readonly DataGridView bookmarks = Grid();
    private readonly DataGridView adminCards = Grid();
    private readonly DataGridView idleScreens = Grid();
    private readonly DataGridView commands = Grid();
    private readonly DataGridView helpActions = Grid();
    private readonly DataGridView menuCategories = Grid();
    private readonly DataGridView pageActions = Grid();
    private readonly NumericUpDown screenRefresh = new() { Minimum = 0, Maximum = 86400, Width = 120 };
    private bool scaled;
    /// <summary>Self-test pictures: lays the view out as on a screen with this scale.</summary>
    internal static float? ScaleOverride;
    /// <summary>Shows each screen's number on the screen itself.</summary>
    internal event Action? IdentifyScreensRequested;
    /// <summary>A new version is downloaded (folder with its Kiosk.Client.exe); Kiosk closes and the new copy installs itself.</summary>
    internal event Action<string>? UpdateRequested;
    /// <summary>The administrator's window: the running Kiosk downloads and installs, so this window does not download too.</summary>
    internal bool UpdateByKiosk { get; init; }
    private readonly NumericUpDown idleAfter = new() { Minimum = 3, Maximum = 86400, Width = 120 };
    private readonly CheckBox allowWindowsAdmins = new() { Text = "Administratorzy Windows tego komputera też mają dostęp (sprawdzane po koncie z certyfikatu karty)", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 6, 4, 6) };
    private readonly Func<Config, bool>? keepsAccess;
    private readonly Label error = new() { Dock = DockStyle.Bottom, Height = 40, ForeColor = Color.FromArgb(255, 153, 164), Padding = new Padding(16, 8, 16, 0) };
    internal event Action<Config>? SaveRequested;
    internal event Action? CancelRequested;

    internal SettingsView(Config config, CardIdentity? me = null, Func<Config, bool>? keepsAccess = null)
    {
        original = config;
        this.keepsAccess = keepsAccess;
        Dock = DockStyle.Fill;
        BackColor = Back;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Back };
        header.Controls.Add(new Label
        {
            Text = "Konfiguracja Kiosku", ForeColor = KioskTheme.Text, Font = new Font("Segoe UI Semibold", 20),
            AutoSize = true, Location = new Point(24, 18), BackColor = Color.Transparent
        });

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 6) };
        var remote = new TabPage("Pulpity zdalne") { Padding = new Padding(12) };
        var session = new TabPage("Sesja i blokada") { Padding = new Padding(16) };
        var web = new TabPage("Zakładki przeglądarki") { Padding = new Padding(12) };
        var applications = new TabPage("Aplikacje") { Padding = new Padding(12) };
        tabs.TabPages.AddRange([remote, session, web, applications]);
        var admins = new TabPage("Administratorzy") { Padding = new Padding(12) };
        tabs.TabPages.Add(admins);
        adminCards.Columns.Add("Name", "Nazwa (dla Ciebie)");
        adminCards.Columns.Add("Id", "Identyfikator karty");
        AddEnabledColumn(adminCards);
        foreach (var card in config.AdminCards) SetEnabled(adminCards, adminCards.Rows.Add(card.Name, card.Id), card.Enabled);
        SetupGridTab(admins, adminCards, "Tylko te karty (oraz, jeśli zaznaczone, administratorzy Windows tego komputera) widzą Konfigurację w Menu. " +
            "Gdy lista jest pusta, w trybie testowym Konfigurację widzi każdy.");
        allowWindowsAdmins.Checked = config.AllowWindowsAdministrators;
        admins.Controls.Add(allowWindowsAdmins);
        if (me != null)
        {
            var addMe = new Button { Text = "Dodaj moją kartę (" + me.Name + ")", AutoSize = true, Dock = DockStyle.Bottom };
            addMe.Click += (_, _) =>
            {
                if (!adminCards.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow && Cell(r, "Id") == me.Id)) adminCards.Rows.Add(me.Name, me.Id);
            };
            admins.Controls.Add(addMe);
        }

        connections.Columns.Add("Name", "Nazwa w menu");
        connections.Columns.Add("Server", "Komputer / serwer (DNS)");
        connections.Columns.Add("Port", "Port");
        connections.Columns.Add("UserName", "Użytkownik (opcjonalnie)");
        var authentication = new DataGridViewComboBoxColumn { Name = "Authentication", HeaderText = "Logowanie", FlatStyle = FlatStyle.Flat };
        authentication.Items.AddRange(AuthenticationOptions.Select(o => (object)o.Label).ToArray());
        connections.Columns.Add(authentication);
        connections.Columns["Port"]!.FillWeight = 40;
        AddEnabledColumn(connections);
        foreach (var c in config.RdpConnections)
            SetEnabled(connections, connections.Rows.Add(c.Name, c.Server, c.Port.ToString(), c.UserName, AuthLabel(c.Authentication)), c.Enabled);
        connections.DefaultValuesNeeded += (_, e) => { e.Row.Cells["Port"].Value = "3389"; e.Row.Cells["Authentication"].Value = AuthLabel(RdpAuthentication.SmartCard); };
        connections.DataError += (_, e) => e.ThrowException = false;
        SetupGridTab(remote, connections,
            "Każde połączenie pojawia się w Menu. Logowanie Entra ID wymaga pełnej nazwy DNS komputera. " +
            "Karta i Windows Hello logują użytkownika przypisanego do karty.");

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.Paint += (_, e) => PaintCards(fields, e.Graphics);
        fields.Padding = new Padding(0, 0, 12, 0);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, "Czytnik karty", reader);
        AddField(fields, "Strona startowa przeglądarki", home);
        AddField(fields, "Blokada po bezczynności (s, karta w czytniku)", lockAfter);
        AddField(fields, "Przechowywanie sesji po wyjęciu karty (s, 0 = do wylogowania)", changeUserAfter);
        AddField(fields, "Tryb sesji", sessionMode);
        AddField(fields, "Logowanie i odblokowanie", unlock);
        AddField(fields, "Kolor przewodni", accent);
        AddField(fields, "Położenie Start i ikon na pasku", taskbarAlign);
        AddField(fields, "Napis na pulpicie (puste = brak)", brandTitle);
        AddField(fields, "Tekst pod napisem (puste = brak)", brandSubtitle);
        // Logo: a path field with a picker; the file must be readable for everybody (e.g. C:\ProgramData\Kiosk\logo.png).
        var logoPicker = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = 34, Margin = new Padding(0), BackColor = Color.Transparent };
        logoPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logoPicker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logoPicker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var chooseLogo = new Button { Text = "Wybierz…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        var clearLogo = new Button { Text = "Usuń", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        chooseLogo.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Obrazy (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dialog.ShowDialog(this) == DialogResult.OK) brandLogo.Text = dialog.FileName;
        };
        clearLogo.Click += (_, _) => brandLogo.Text = "";
        brandLogo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        logoPicker.Controls.Add(brandLogo, 0, 0);
        logoPicker.Controls.Add(chooseLogo, 1, 0);
        logoPicker.Controls.Add(clearLogo, 2, 0);
        AddField(fields, "Logo na pulpicie (PNG/JPG, dostępne dla wszystkich)", logoPicker);
        logoPicker.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        logoPicker.MinimumSize = Size.Empty;
        AddField(fields, "Połącz automatycznie po włożeniu karty", autoConnect);
        AddField(fields, "Limit czasu łączenia RDP (sekundy)", connectTimeout);
        var pinRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        fields.Controls.Add(requirePin, 1, pinRow);
        var signInRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        fields.Controls.Add(signInAsUser, 1, signInRow);
        var removalRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        fields.Controls.Add(disconnectOnRemoval, 1, removalRow);
        // Scrolls on small screens (1280×720, 125 % scale), so the last options stay reachable.
        var sessionScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        sessionScroll.Controls.Add(new Label
        {
            Dock = DockStyle.Top, Height = 110, Padding = new Padding(4, 12, 4, 4), ForeColor = HelpText,
            Text = "Każda karta ma własną sesję (to nie jest blokada Windows). Wyjęcie karty chowa sesję, inna karta od razu otwiera swoją, " +
                   "a powrót karty przywraca jej okna. Po czasie przechowywania nieużywana sesja jest zamykana. " +
                   "Na komputerze dla wielu osób wybierz „Karta + PIN karty”: Windows Hello potwierdza konto Windows, nie osobę z karty.\n" +
                   "Bez wymagania PIN-u Kiosk łączy od razu tożsamością z karty / Windows Hello / Entra, jeśli Windows ją udostępnia; w przeciwnym razie i tak zapyta."
        });
        sessionScroll.Controls.Add(fields);   // added last = docked first: the fields on top, the note under them
        session.Controls.Add(sessionScroll);
        reader.Items.Add(AnyReader);
        foreach (var name in CardReader.ListReaders()) reader.Items.Add(name);
        // A configured reader that is not connected right now stays selectable.
        if (config.ReaderName.Length > 0 && !reader.Items.Contains(config.ReaderName)) reader.Items.Add(config.ReaderName);
        reader.SelectedItem = config.ReaderName.Length > 0 ? config.ReaderName : AnyReader;
        home.Text = config.BrowserUrl;
        brandTitle.Text = config.BrandTitle;
        brandSubtitle.Text = config.BrandSubtitle;
        brandLogo.Text = config.BrandLogo;
        lockAfter.Value = config.LockAfterSeconds;
        changeUserAfter.Value = config.ChangeUserAfterSeconds;
        connectTimeout.Value = config.ConnectTimeoutSeconds;
        requirePin.Checked = config.RequireCredentialPrompt;
        signInAsUser.Checked = config.SignInAsUser;
        unlock.SelectedIndex = (int)config.UnlockMethod;
        sessionMode.SelectedIndex = (int)config.SessionMode;
        disconnectOnRemoval.Checked = config.DisconnectOnCardRemoval;
        SetupAccent(config.AccentColor);
        taskbarAlign.SelectedIndex = (int)config.TaskbarAlignment;
        RefreshAutoConnect(config.AutoConnectRdpName);
        tabs.Selected += (_, _) => RefreshAutoConnect(autoConnect.SelectedItem as string);

        apps.Columns.Add("Name", "Nazwa w menu");
        apps.Columns.Add("Path", "Pełna ścieżka do EXE");
        apps.Columns.Add("Arguments", "Argumenty");
        apps.Columns.Add("WorkingDirectory", "Katalog roboczy (opcjonalny)");
        apps.Columns.Add("RunAs", "Konto wspólne do sieci (opcjonalne)");
        AddEnabledColumn(apps);
        foreach (var app in config.Applications) SetEnabled(apps, apps.Rows.Add(app.Name, app.Path, app.Arguments, app.WorkingDirectory, app.RunAs), app.Enabled);
        bookmarks.Columns.Add("Name", "Nazwa zakładki");
        bookmarks.Columns.Add("Url", "Adres HTTP/HTTPS");
        AddEnabledColumn(bookmarks);
        foreach (var bookmark in config.Bookmarks) SetEnabled(bookmarks, bookmarks.Rows.Add(bookmark.Name, bookmark.Url), bookmark.Enabled);
        SetupGridTab(web, bookmarks, "Dodaj zakładkę w pustym wierszu. Zaznacz wiersz, aby go usunąć.");
        var webOptions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 8) };
        webOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        webOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        webOptions.Controls.Add(onlyBookmarks, 0, 0);
        webOptions.SetColumnSpan(onlyBookmarks, 2);
        webOptions.Controls.Add(new Label { Text = "Dodatkowe dozwolone domeny (np. strona logowania)", AutoSize = true, Padding = new Padding(4, 8, 4, 4) }, 0, 1);
        webOptions.Controls.Add(allowedDomains, 1, 1);
        web.Controls.Add(webOptions);
        onlyBookmarks.Checked = config.BrowserOnlyBookmarks;
        allowedDomains.Text = string.Join(", ", config.BrowserAllowedDomains);
        allowedDomains.Enabled = onlyBookmarks.Checked;
        onlyBookmarks.CheckedChanged += (_, _) => allowedDomains.Enabled = onlyBookmarks.Checked;
        SetupGridTab(applications, apps, "Każda pozycja pojawia się w Menu. Przeglądarka (" + ApplicationEntry.BuiltInBrowser + "), Notatnik (" + ApplicationEntry.BuiltInNotes + ") i Pliki (" + ApplicationEntry.BuiltInFiles + ") to aplikacje wbudowane: usuń wiersz, aby je wyłączyć. Osadzanie obsługuje zgodne aplikacje Win32. " +
            "„Konto wspólne do sieci”: nazwa konta z " + SharedAccounts.FileName + " – program działa na tym komputerze jak zwykle, a serwery i udziały (np. EXE na serwerze plików) otwiera jako to konto.", helpHeight: 64);

        var pagesTab = new TabPage("Akcje stron") { Padding = new Padding(12) };
        tabs.TabPages.Insert(tabs.TabPages.IndexOf(applications), pagesTab);
        pageActions.Columns.Add("Url", "Adres lub początek adresu");
        pageActions.Columns.Add("Click", "Kliknij przyciski (napisy, oddziel ;)");
        pageActions.Columns.Add("Account", "Zaloguj kontem wspólnym");
        pageActions.Columns.Add("Submit", "Napis przycisku logowania (opcjonalny)");
        var scope = new DataGridViewComboBoxColumn { Name = "Scope", HeaderText = "Dla", FlatStyle = FlatStyle.Flat, FillWeight = 60 };
        scope.Items.AddRange(ScopeOptions.Select(o => (object)o.Label).ToArray());
        pageActions.Columns.Add(scope);
        AddEnabledColumn(pageActions);
        foreach (var a in config.PageActions) SetEnabled(pageActions, pageActions.Rows.Add(a.Url, a.Click, a.Account, a.Submit, ScopeLabel(a.Scope)), a.Enabled);
        SetupGridTab(pagesTab, pageActions, "Kiosk sam klika na stronach (strona startowa, ekrany, przeglądarka Kiosku). Adres: portal.firma.pl (z subdomenami) " +
            "albo https://portal.firma.pl/login (początek adresu). „Kliknij”: np. Akceptuj wszystkie;Accept all – przycisk z tym napisem jest klikany, gdy się pojawi. " +
            "„Zaloguj kontem wspólnym”: nazwa konta z " + SharedAccounts.FileName + " (obok client.json, skrypt Set-KioskSharedAccount.ps1) – Kiosk wpisuje login i hasło i klika przycisk logowania. " +
            "„Dla”: Wszyscy, Kiosk (ekrany i ekran blokady, Menu – gdy nikt nie pracuje) albo Użytkownicy (przeglądarka zalogowanej osoby).", helpHeight:  96);
        var choose = new Button { Text = "Wybierz plik EXE…", AutoSize = true, Dock = DockStyle.Bottom };
        choose.Click += (_, _) => ChooseExecutable();
        applications.Controls.Add(choose);
        var addNotes = new Button { Text = "Dodaj wbudowany Notatnik", AutoSize = true, Dock = DockStyle.Bottom };
        addNotes.Click += (_, _) =>
        {
            if (apps.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow && string.Equals(Cell(r, "Path"), ApplicationEntry.BuiltInNotes, StringComparison.OrdinalIgnoreCase))) return;
            apps.Rows.Add(ApplicationEntry.Notes.Name, ApplicationEntry.BuiltInNotes, "", "");
        };
        applications.Controls.Add(addNotes);
        var addFiles = new Button { Text = "Dodaj wbudowane Pliki", AutoSize = true, Dock = DockStyle.Bottom };
        addFiles.Click += (_, _) =>
        {
            if (apps.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow && string.Equals(Cell(r, "Path"), ApplicationEntry.BuiltInFiles, StringComparison.OrdinalIgnoreCase))) return;
            apps.Rows.Add(ApplicationEntry.Files.Name, ApplicationEntry.BuiltInFiles, "", "");
        };
        applications.Controls.Add(addFiles);
        var addBrowser = new Button { Text = "Dodaj wbudowaną przeglądarkę", AutoSize = true, Dock = DockStyle.Bottom };
        addBrowser.Click += (_, _) =>
        {
            if (apps.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow && string.Equals(Cell(r, "Path"), ApplicationEntry.BuiltInBrowser, StringComparison.OrdinalIgnoreCase))) return;
            apps.Rows.Insert(0, ApplicationEntry.Browser.Name, ApplicationEntry.BuiltInBrowser, "", "");
        };
        applications.Controls.Add(addBrowser);

        var screens = new TabPage("Ekrany") { Padding = new Padding(12) };
        tabs.TabPages.Insert(tabs.TabPages.IndexOf(admins), screens);
        idleScreens.Columns.Add("Screen", "Ekran (0 = wszystkie, 1 = główny)");
        idleScreens.Columns.Add("Url", "Strona WWW");
        idleScreens.Columns.Add("Path", "albo aplikacja (pełna ścieżka do EXE)");
        idleScreens.Columns.Add("Arguments", "Argumenty");
        idleScreens.Columns["Screen"]!.FillWeight = 45;
        AddEnabledColumn(idleScreens);
        foreach (var s in config.IdleScreens) SetEnabled(idleScreens, idleScreens.Rows.Add(s.Screen.ToString(), s.Url, s.Path, s.Arguments), s.Enabled);
        idleScreens.DefaultValuesNeeded += (_, e) => e.Row.Cells["Screen"].Value = "0";
        SetupGridTab(screens, idleScreens,
            "Gdy nikt nie pracuje, ekrany pokazują tę treść tylko do oglądania (bez klawiatury i myszy). Na głównym ekranie ruch myszy, " +
            "klawisz albo karta pokazuje logowanie. Strona kiosk:komunikaty (przycisk „Dodaj komunikaty jako wygaszacz”) pokazuje Komunikaty jako slajdy, zmieniane co tyle sekund, ile ustawiono w Komunikatach. Ekrany: " + string.Join(", ", Screen.AllScreens.Select((s, i) => (i + 1) + " = " + s.Bounds.Width + "×" + s.Bounds.Height + (s.Primary ? " (główny)" : ""))) + ".");
        var idleRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 8) };
        idleRow.Controls.Add(new Label { Text = "Ekran blokady na głównym ekranie po (s bez ruchu, gdy sesja zablokowana lub nikt nie jest zalogowany)", AutoSize = true, Padding = new Padding(4, 8, 8, 4) });
        idleRow.Controls.Add(idleAfter);
        idleAfter.Value = Math.Clamp(config.IdleAfterSeconds, (int)idleAfter.Minimum, (int)idleAfter.Maximum);
        idleRow.Controls.Add(new Label { Text = "Odświeżaj strony ekranów co (s, 0 = nie)", AutoSize = true, Padding = new Padding(24, 8, 8, 4) });
        idleRow.Controls.Add(screenRefresh);
        screenRefresh.Value = Math.Clamp(config.ScreenRefreshSeconds, 0, 86400);
        var identify = new Button { Text = "Pokaż numery ekranów", AutoSize = true, Margin = new Padding(24, 2, 4, 2) };
        identify.Click += (_, _) => IdentifyScreensRequested?.Invoke();
        idleRow.Controls.Add(identify);
        // Komunikaty as the screensaver: the Kiosk's own slides on the main screen (other screens keep their content).
        var addNews = new Button { Text = "Dodaj komunikaty jako wygaszacz", AutoSize = true, Dock = DockStyle.Bottom };
        addNews.Click += (_, _) =>
        {
            if (idleScreens.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow && NewsSlides.IsAddress(Cell(r, "Url")))) return;
            SetEnabled(idleScreens, idleScreens.Rows.Add("1", NewsSlides.Address, "", ""), true);
        };
        screens.Controls.Add(addNews);
        screens.Controls.Add(idleRow);
        screens.Controls.Add(new ScreenMap { Dock = DockStyle.Top, Height = 150 });

        var commandsTab = new TabPage("Polecenia") { Padding = new Padding(12) };
        tabs.TabPages.Insert(tabs.TabPages.IndexOf(admins), commandsTab);
        commands.Columns.Add("Name", "Nazwa w menu");
        commands.Columns.Add("Path", "Program lub skrypt (.exe, .cmd, .bat, .ps1)");
        commands.Columns.Add("Arguments", "Argumenty");
        commands.Columns.Add(new DataGridViewCheckBoxColumn { Name = "AsUser", HeaderText = "Jako zalogowana osoba", FillWeight = 45 });
        commands.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Confirm", HeaderText = "Pytaj przed", FillWeight = 35 });
        AddEnabledColumn(commands);
        foreach (var c in config.Commands) SetEnabled(commands, commands.Rows.Add(c.Name, c.Path, c.Arguments, c.AsUser, c.Confirm), c.Enabled);
        commands.DefaultValuesNeeded += (_, e) => { e.Row.Cells["AsUser"].Value = true; e.Row.Cells["Confirm"].Value = false; };
        SetupGridTab(commandsTab, commands, "Polecenia pojawiają się w Menu w sekcji „Polecenia” i działają w tle (np. kopiowanie plików skryptem). " +
            "„Jako zalogowana osoba” używa konta z „Zaloguj”, inaczej konta Kiosku. Wynik (kod zakończenia) pokazuje pulpit.");

        var helpTab = new TabPage("Menu") { Padding = new Padding(12) };
        tabs.TabPages.Add(helpTab);
        helpActions.Columns.Add("Name", "Nazwa");
        helpActions.Columns.Add("Action", "Akcja");
        // Chosen from the Kategorie tab: the left menu shows categories, a click opens their entries and subcategories.
        var categoryColumn = new DataGridViewComboBoxColumn
        {
            Name = "Category", HeaderText = "Kategoria", ToolTipText = "Kategoria z zakładki „Kategorie”. Pusta: na stronie głównej, „Na skróty”.",
            SortMode = DataGridViewColumnSortMode.NotSortable, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        };
        helpActions.Columns.Add(categoryColumn);
        // A value missing from the list (it is refreshed when the tab opens) must not stop the grid.
        helpActions.DataError += (_, e) => e.ThrowException = false;
        // The tile in the self-service layout: the line under its name, its icon and the circle's colour.
        helpActions.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Description", HeaderText = "Opis", ToolTipText = "Kafelek: linia pod nazwą, np. „Naruszenie danych osobowych”",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        helpActions.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Icon", HeaderText = "Ikona", ToolTipText = "Kafelek: " + TileIcons.Names + ". Puste: dobrana do akcji.",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        helpActions.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Color", HeaderText = "Kolor", ToolTipText = "Kafelek: kolor koła. Kliknij, aby wybrać z palety albo wpisać #RRGGBB. Domyślny: kolor kategorii albo Kiosku.",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        helpActions.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Side", HeaderText = "Obok (strona lub PDF)", ToolTipText = "Otwiera się po prawej stronie obok strony z „Akcji”: druga strona https://… albo plik, np. instrukcja PDF " +
                "(pełna ścieżka, \\\\serwer\\… albo ścieżka względem folderu konfiguracji, np. instrukcje\\mfa.pdf). Puste: tylko strona.",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        AddEnabledColumn(helpActions);
        // Punkt samoobsługowy: greyed out until someone signs in with the card.
        helpActions.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "SignedInOnly", HeaderText = "Po zalogowaniu", ToolTipText = "Punkt samoobsługowy: przycisk działa dopiero po zalogowaniu kartą, wcześniej jest wyszarzony",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 110, MinimumWidth = 80, Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        // The same order and widths as Kategorie: name, category, action, description, icon, colour, sign-in.
        LayOut(helpActions, "Enabled", "Name", "Category", "Action", "Side", "Description", "Icon", "Color", "SignedInOnly");
        void AddHelpRow(HelpAction h)
        {
            if (h.CategoryPath.Length > 0 && !categoryColumn.Items.Contains(h.CategoryPath)) categoryColumn.Items.Add(h.CategoryPath);
            int row = helpActions.Rows.Add(h.Name, h.Action, h.CategoryPath, h.Description, h.Icon, h.Color);
            helpActions.Rows[row].Cells["Side"].Value = h.Side;
            SetEnabled(helpActions, row, h.Enabled);
            helpActions.Rows[row].Cells["SignedInOnly"].Value = h.NeedsSignIn;
        }
        categoryColumn.Items.Add("");
        foreach (var h in config.HelpActions) AddHelpRow(h);
        SetupGridTab(helpTab, helpActions, "Przyciski „Menu” na pulpicie działają też na ekranie blokady i bez logowania. Akcja: adres https://… " +
            "(bez logowania otwiera się okno gościa, tylko strony logowania Microsoft), " + HelpAction.ChangePin + " (zmiana PIN-u karty), " +
            HelpAction.Reenroll + " (Kiosk zapomina konto karty: login, hasło i PIN od nowa), " + HelpAction.ResetCard +
            " (reset FIDO2: usuwa PIN i wszystkie klucze z karty, potem nowy PIN), " + HelpAction.FirstSignIn +
            " (jedno logowanie do Windows: profil i SSO, potem automatyczny powrót do Kiosku). Odznacz „Włączony”, aby ukryć jeden przycisk. " +
            "Kategorię wybierasz z listy (tworzysz je w zakładce „Kategorie”). Kolejność w Kiosku: jak na tej liście, zmieniasz ją przyciskami „Wyżej” / „Niżej”. " +
            "W punkcie samoobsługowym każdy przycisk jest kafelkiem: kategorie są w lewym menu, opis, ikona i kolor zmieniają kafelek.", helpHeight: 110, order: true);
        var helpDefaults = new Button { Text = "Przywróć domyślne przyciski", AutoSize = true, Dock = DockStyle.Bottom };
        helpDefaults.Click += (_, _) =>
        {
            helpActions.Rows.Clear();
            foreach (var h in HelpAction.Defaults) AddHelpRow(h);
            SyncCategories();
        };
        helpTab.Controls.Add(helpDefaults);

        // Kategorie: created here, chosen in Menu; the order of this list is the order of the left menu.
        var categoriesTab = new TabPage("Kategorie") { Padding = new Padding(12) };
        tabs.TabPages.Insert(tabs.TabPages.IndexOf(helpTab) + 1, categoriesTab);
        menuCategories.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name", HeaderText = "Kategoria", ToolTipText = "Np. Pomoc, albo Pomoc/Konto dla podkategorii kategorii Pomoc",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        menuCategories.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Icon", HeaderText = "Ikona", ToolTipText = TileIcons.Names + ". Puste: dobrana do nazwy albo pierwszego kafelka.",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        // Like an entry of Menu: a category without entries opens its action at once.
        menuCategories.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Action", HeaderText = "Akcja", ToolTipText = "Puste: kategoria pokazuje swoje kafelki. Adres https://… albo kiosk:…: kliknięcie kategorii bez wpisów od razu otwiera stronę.",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        menuCategories.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "Opis", ToolTipText = "Kategoria z akcją: linia pod nazwą jej kafelka", SortMode = DataGridViewColumnSortMode.NotSortable });
        menuCategories.Columns.Add(new DataGridViewTextBoxColumn { Name = "Side", HeaderText = "Obok (strona lub PDF)", ToolTipText = "Kategoria z akcją: po prawej obok jej strony druga strona https://… albo plik PDF (pełna ścieżka albo względem folderu konfiguracji).", SortMode = DataGridViewColumnSortMode.NotSortable });
        menuCategories.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", HeaderText = "Kolor", ToolTipText = "Kolor ikony kategorii w lewym menu i kafelków bez własnego koloru. Kliknij, aby wybrać z palety albo wpisać #RRGGBB.", SortMode = DataGridViewColumnSortMode.NotSortable });
        menuCategories.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "SignedInOnly", HeaderText = "Po zalogowaniu", ToolTipText = "Kategoria z akcją: działa dopiero po zalogowaniu kartą",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 110, MinimumWidth = 80, Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        menuCategories.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Bottom", HeaderText = "Dolna sekcja", ToolTipText = "Punkt samoobsługowy: kategoria w lewym menu w drugiej grupie, pod kreską (np. Szybka pomoc, Zgłoś incydent)",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        menuCategories.Columns["Name"].HeaderText = "Nazwa";
        LayOut(menuCategories, "Name", "Action", "Side", "Description", "Icon", "Color", "SignedInOnly", "Bottom");
        foreach (var c in MenuCategory.Complete(config.MenuCategories, config.HelpActions))
        {
            // By name: the columns were added Name, Icon, Action, Description, Side, Color (the colour landed in "Side" and was lost).
            int row = menuCategories.Rows.Add();
            var cells = menuCategories.Rows[row].Cells;
            cells["Name"].Value = c.Path; cells["Icon"].Value = c.Icon; cells["Action"].Value = c.Action;
            cells["Description"].Value = c.Description; cells["Color"].Value = c.Color;
            menuCategories.Rows[row].Cells["SignedInOnly"].Value = c.SignedInOnly ?? HelpAction.DefaultNeedsSignIn(c.Action ?? "");
            menuCategories.Rows[row].Cells["Bottom"].Value = c.Bottom;
            menuCategories.Rows[row].Cells["Side"].Value = c.Side;
        }
        List<string> CategoryPaths() => menuCategories.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
            .Select(r => MenuCategory.Normalize(Cell(r, "Name"))).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool Under(string path, string category) => path.Equals(category, StringComparison.OrdinalIgnoreCase) || path.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase);
        // The entries' categories: the ones of this list, plus any an entry uses (added here so nothing is lost).
        void SyncCategories()
        {
            var paths = CategoryPaths();
            var used = helpActions.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new HelpAction { Category = Cell(r, "Category") });
            foreach (var missing in MenuCategory.Complete(paths.Select(p => new MenuCategory { Name = p }).ToArray(), used).Skip(paths.Count))
            {
                menuCategories.Rows.Add(missing.Path, "", "", "", "");
                paths.Add(missing.Path);
            }
            categoryColumn.Items.Clear();
            categoryColumn.Items.Add("");
            foreach (var p in paths) categoryColumn.Items.Add(p);
        }
        // A renamed category takes its entries and subcategories with it.
        string? renaming = null;
        menuCategories.CellBeginEdit += (_, e) => renaming = e.ColumnIndex == menuCategories.Columns["Name"].Index
            ? MenuCategory.Normalize(Convert.ToString(menuCategories.Rows[e.RowIndex].Cells["Name"].Value)) : null;
        menuCategories.CellEndEdit += (_, e) =>
        {
            if (renaming is not { Length: > 0 } old || e.ColumnIndex != menuCategories.Columns["Name"].Index) return;
            renaming = null;
            var now = MenuCategory.Normalize(Convert.ToString(menuCategories.Rows[e.RowIndex].Cells["Name"].Value));
            if (now.Length == 0 || now.Equals(old, StringComparison.Ordinal)) return;
            string Moved(string path) => now + path[old.Length..];
            foreach (DataGridViewRow r in menuCategories.Rows)
                if (!r.IsNewRow && r.Index != e.RowIndex && Under(MenuCategory.Normalize(Cell(r, "Name")), old)) r.Cells["Name"].Value = Moved(MenuCategory.Normalize(Cell(r, "Name")));
            var entries = helpActions.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && Under(Cell(r, "Category"), old)).ToList();
            var paths = CategoryPaths();
            categoryColumn.Items.Clear();
            categoryColumn.Items.Add("");
            foreach (var p in paths) categoryColumn.Items.Add(p);
            foreach (var r in entries) r.Cells["Category"].Value = Moved(Cell(r, "Category"));
        };
        // A removed category: its subcategories go too, their entries move to the start page ("Na skróty").
        SetupGridTab(categoriesTab, menuCategories, "Kategorie lewego menu w punkcie samoobsługowym i folderów Menu. Podkategoria: nazwa kategorii, ukośnik i nazwa, " +
            "np. „Pomoc/Konto”. Kolejność na liście to kolejność w menu – zmieniasz ją przyciskami „Wyżej” / „Niżej”. Zmiana nazwy przenosi wpisy, " +
            "usunięcie kategorii przenosi jej wpisy na stronę główną. Akcja (jak w Menu): kategoria bez wpisów od razu otwiera tę stronę. Ikona: " + TileIcons.Names + ".", helpHeight: 96, order: true, removing: row =>
        {
            var path = MenuCategory.Normalize(Cell(row, "Name"));
            if (path.Length == 0) return;
            foreach (DataGridViewRow r in helpActions.Rows) if (!r.IsNewRow && Under(Cell(r, "Category"), path)) r.Cells["Category"].Value = "";
            BeginInvoke(() =>
            {
                foreach (var r in menuCategories.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && Under(MenuCategory.Normalize(Cell(r, "Name")), path)).ToList())
                    menuCategories.Rows.Remove(r);
                SyncCategories();
            });
        });
        SyncCategories();
        tabs.Selected += (_, e) => { if (e.TabPage == helpTab) SyncCategories(); };
        showHelp.Checked = config.ShowHelp;
        showHelp.CheckedChanged += (_, _) => helpActions.Enabled = helpDefaults.Enabled = showHelp.Checked;
        helpActions.Enabled = helpDefaults.Enabled = showHelp.Checked;
        // The help page (opened without signing in) closes on its own when nobody uses it.
        var helpClose = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8), BackColor = Color.Transparent };
        helpClose.Controls.Add(new Label { Text = "Zamknij okno pomocy po (sekundy bez aktywności)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        helpClose.Controls.Add(helpCloseSeconds);
        helpCloseSeconds.Value = Math.Clamp(config.HelpCloseSeconds, 5, 3600);
        helpTab.Controls.Add(helpClose);
        // Punkt samoobsługowy: how soon taking the card away signs the person out.
        var cardRemoved = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8), BackColor = Color.Transparent };
        cardRemoved.Controls.Add(new Label { Text = "Wyloguj po zdjęciu karty z czytnika po (sekundy)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        cardRemoved.Controls.Add(cardRemovedSeconds);
        cardRemovedSeconds.Value = Math.Clamp(config.CardRemovedSeconds, 1, 300);
        helpTab.Controls.Add(cardRemoved);
        helpTab.Controls.Add(showHelp);

        var newsTab = new TabPage("Komunikaty") { Padding = new Padding(16) };
        tabs.TabPages.Add(newsTab);
        var newsFields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 12, 0) };
        newsFields.Paint += (_, e) => PaintCards(newsFields, e.Graphics);
        newsFields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        newsFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var folderPicker = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = 34, Margin = new Padding(0), BackColor = Color.Transparent };
        folderPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderPicker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderPicker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // The example installed with the program (Aktualizacja A, Zgłoś problem…), for a quick test without a share.
        var useExample = new Button { Text = "Użyj przykładu", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        useExample.Click += (_, _) => newsFolder.Text = Path.Combine(AppContext.BaseDirectory, "news-example");
        var chooseFolder = new Button { Text = "Wybierz folder…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        chooseFolder.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Folder z plikiem " + News.FileName, UseDescriptionForTitle = true };
            if (newsFolder.Text.Length > 0 && Directory.Exists(newsFolder.Text)) dialog.InitialDirectory = newsFolder.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) newsFolder.Text = dialog.SelectedPath;
        };
        newsFolder.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        folderPicker.Controls.Add(newsFolder, 0, 0);
        folderPicker.Controls.Add(chooseFolder, 1, 0);
        folderPicker.Controls.Add(useExample, 2, 0);
        AddField(newsFields, "Pokazuj komunikaty", newsShow);
        newsShow.SelectedIndex = (int)config.NewsShow;
        AddField(newsFields, "Folder lub udział z komunikatami (puste = brak)", folderPicker);
        folderPicker.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        folderPicker.MinimumSize = Size.Empty;
        AddField(newsFields, "Aktualności ze strony SharePoint (puste = brak)", newsSharePoint);
        AddField(newsFields, "Konto do SharePointa", newsSharePointAccount);
        AddField(newsFields, "albo login do SharePointa" + (NewsAccount.Saved ? " (zapisany)" : ""), newsUser);
        AddField(newsFields, "Hasło do SharePointa", newsPassword);
        AddField(newsFields, "", removeNewsAccount);
        removeNewsAccount.Visible = NewsAccount.Saved;
        removeNewsAccount.CheckedChanged += (_, _) => newsUser.Enabled = newsPassword.Enabled = !removeNewsAccount.Checked;
        AddField(newsFields, "Ile aktualności z SharePointa", newsSharePointCount);
        AddField(newsFields, "Kolejność", newsOrder);
        AddField(newsFields, "Zmiana komunikatu co (sekundy)", newsSeconds);
        AddField(newsFields, "Pobieranie z sieci co (minuty)", newsRefresh);
        AddField(newsFields, "Rozmiar banera", newsSize);
        AddField(newsFields, "Ekrany wygaszacza", newsOnScreens);
        newsOnScreens.Checked = config.NewsBarsOnScreens;
        newsSharePoint.Text = config.NewsSharePoint;
        newsSharePointAccount.Text = config.NewsSharePointAccount;
        newsSharePointCount.Value = Math.Clamp(config.NewsSharePointCount, 1, 20);
        newsOrder.SelectedIndex = (int)config.NewsOrder;
        newsSize.SelectedIndex = (int)config.NewsSize;
        newsFolder.Text = config.NewsFolder;
        newsSeconds.Value = Math.Clamp(config.NewsSeconds, 3, 300);
        newsRefresh.Value = Math.Clamp(config.NewsRefreshMinutes, 1, 1440);
        newsTab.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, ForeColor = HelpText, Padding = new Padding(4, 12, 4, 4),
            Text = "Komunikaty przewijają się na dole pulpitu (np. „Zgłoś problem”), a paski („Kind”: „Pasek”) świecą u góry (np. „Nie działa system XXX”). " +
                   "Folder (np. \\\\serwer\\kiosk\\komunikaty) zawiera plik " + News.FileName + ", Kiosk pobiera go co ustawioną liczbę minut; musi być do odczytu dla wszystkich kont. " +
                   "Film (Video, plik MP4): slajd stoi, aż film się skończy, pozostałe zmieniają się co ustawione sekundy. " +
                   "Opis wszystkich pól (Title, Text, Image, Video, Sound, Button, Action, Color, TextColor, Enabled, From / Until) jest na początku przykładowego pliku: " +
                   "„Użyj przykładu” albo config\\news w paczce. SharePoint: najnowsze aktualności witryny, czytane kontem Windows Kiosku albo kontem podanym niżej."
        });
        newsTab.Controls.Add(newsFields);

        var updateTab = new TabPage("Aktualizacja") { Padding = new Padding(16) };
        tabs.TabPages.Add(updateTab);
        var updatePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        updatePanel.Controls.Add(new Label { Text = "Zainstalowana wersja: " + Updater.CurrentVersion, AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), Margin = new Padding(4, 4, 4, 8) });
        updatePanel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = HelpText, Margin = new Padding(4, 0, 4, 12),
            Text = "Pobiera najnowsze wydanie z GitHub (github.com/Eris92/Kiosk, zakładka Releases), zamyka Kiosk, podmienia pliki programu " +
                   "i uruchamia nową wersję. Konfiguracja zostaje bez zmian. Gdy Kiosk jest zainstalowany w Program Files, Windows zapyta o administratora. " +
                   "Niezapisane zmiany w tej Konfiguracji przepadną. Automatyczna aktualizacja działa po zapisaniu konfiguracji: sprawdza przy starcie Kiosku i potem co 24 godziny; " +
                   "nowszą wersję instaluje, gdy nikt nie ma otwartej pracy (do tego czasu próbuje co godzinę), i uruchamia Kiosk ponownie."
        });
        var updateButton = new Button { Text = "Sprawdź i zaktualizuj", Size = new Size(240, 38), Margin = new Padding(4) };
        KioskTheme.StyleButton(updateButton, primary: true);
        var updateStatus = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(4, 12, 4, 4) };
        updateButton.Click += async (_, _) =>
        {
            updateButton.Enabled = false;
            try
            {
                updateStatus.Text = "Sprawdzam najnowsze wydanie…";
                var release = await Updater.LatestAsync();
                if (release.Tag == Updater.CurrentVersion)
                {
                    updateStatus.Text = "Masz najnowszą wersję (" + release.Tag + ").";
                    return;
                }
                if (UpdateByKiosk) { updateStatus.Text = "Kiosk pobiera i instaluje " + release.Tag + "…"; UpdateRequested?.Invoke(""); return; }
                updateStatus.Text = "Pobieram " + release.Tag + "…";
                var folder = await Updater.DownloadAsync(release, new Progress<int>(p =>
                    updateStatus.Text = p < 100 ? "Pobieram " + release.Tag + "… " + p + " %" : "Rozpakowuję " + release.Tag + "…"));
                updateStatus.Text = "Instaluję " + release.Tag + ", Kiosk uruchomi się ponownie.";
                UpdateRequested?.Invoke(folder);
            }
            catch (Exception ex) { updateStatus.Text = "Aktualizacja nie powiodła się: " + ex.Message; }
            finally { if (!IsDisposed) updateButton.Enabled = true; }
        };
        autoUpdate.Checked = config.AutoUpdate;
        updatePanel.Controls.Add(autoUpdate);
        updatePanel.Controls.Add(updateButton);
        updatePanel.Controls.Add(updateStatus);
        updatePanel.Controls.Add(new Label { Text = "Konfiguracja z serwera", AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), Margin = new Padding(4, 24, 4, 4) });
        updatePanel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = HelpText, Margin = new Padding(4, 0, 4, 8),
            Text = "Folder lub udział z plikiem client.json (i np. logo, kiosk-news.json). Przy każdym uruchomieniu Kiosk pobiera cały folder i stosuje go na tym komputerze; " +
                   "ścieżki w client.json mogą być względne, np. \"BrandLogo\": \"logo.jpg\". Bez połączenia używa ostatnio pobranej kopii. " +
                   "Tutaj Konfiguracja jest wtedy tylko podglądem – zmienia się ją na serwerze. Puste = konfiguracja tego komputera."
        });
        configSource.Text = config.ConfigSource;
        updatePanel.Controls.Add(configSource);
        updatePanel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = HelpText, Margin = new Padding(4, 12, 4, 4),
            Text = "Konto do serwera (opcjonalnie). Bez niego Kiosk łączy się jako komputer (DOMENA\\KOMPUTER$) – to działa tylko na komputerze w domenie " +
                   "i gdy udział ma odczyt dla „Komputery domeny”; „Wszyscy” nie obejmuje komputera spoza domeny. Podaj konto tylko do odczytu, może być wspólne. " +
                   "Hasło jest szyfrowane dla tego komputera i czytelne tylko dla usługi Kiosku i administratorów. " +
                   (CentralConfig.AccountSaved ? "Konto jest zapisane – wpisz nowe, aby je zmienić." : "Żadne konto nie jest zapisane.")
        });
        var accountRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        accountRow.Controls.Add(sourceUser);
        accountRow.Controls.Add(sourcePassword);
        updatePanel.Controls.Add(accountRow);
        removeAccount.Visible = CentralConfig.AccountSaved;
        removeAccount.CheckedChanged += (_, _) => { sourceUser.Enabled = sourcePassword.Enabled = !removeAccount.Checked; };
        updatePanel.Controls.Add(removeAccount);
        sourceTest = new Button { Text = "Test", Size = new Size(120, 34), Margin = new Padding(4, 8, 4, 4) };
        KioskTheme.StyleButton(sourceTest);
        var sourceResult = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(4, 8, 4, 4) };
        sourceTest.Click += async (_, _) =>
        {
            var source = configSource.Text;
            sourceTest.Enabled = false;
            sourceResult.Text = "Sprawdzam dostęp do " + source.Trim() + "…";
            try { sourceResult.Text = await CentralConfig.DescribeTestAsync(source, removeAccount.Checked ? null : AccountChange); }
            finally { if (!IsDisposed) sourceTest.Enabled = true; }
        };
        updatePanel.Controls.Add(sourceTest);
        updatePanel.Controls.Add(sourceResult);
        updatePanel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = HelpText, Margin = new Padding(4, 16, 4, 4),
            Text = "Klucz kont wspólnych. Hasła kont do stron i aplikacji są w pliku " + SharedAccounts.FileName + " obok client.json (na serwerze: jeden plik dla wszystkich komputerów), " +
                   "zaszyfrowane tym kluczem (scripts\\Set-KioskSharedAccount.ps1). Ten komputer trzyma klucz zaszyfrowany dla siebie, czytelny tylko dla usługi Kiosku i administratorów. " +
                   (SharedAccounts.KeySaved ? "Klucz jest zapisany – wpisz nowy, aby go zmienić." : "Klucz nie jest zapisany.")
        });
        updatePanel.Controls.Add(accountsKey);
        removeKey.Visible = SharedAccounts.KeySaved;
        removeKey.CheckedChanged += (_, _) => accountsKey.Enabled = !removeKey.Checked;
        updatePanel.Controls.Add(removeKey);
        updateTab.Controls.Add(updatePanel);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 10) };
        var save = new Button { Text = "Zapisz konfigurację", Size = new Size(200, 38) };
        var cancel = new Button { Text = "Anuluj", Size = new Size(120, 38) };
        KioskTheme.StyleButton(save, primary: true);
        KioskTheme.StyleButton(cancel);
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        actions.Paint += (_, e) => { using var line = new Pen(ControlBorder); e.Graphics.DrawLine(line, 0, 0, actions.Width, 0); };

        // Windows 11 Settings: the sections are a list on the left; the tab strip itself is hidden.
        tabs.Appearance = TabAppearance.Buttons;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(0, 1);
        var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Back, Padding = new Padding(0, 0, 16, 0) };
        tabs.Dock = DockStyle.None;
        pageHost.Controls.Add(tabs);
        pageHost.Resize += (_, _) => tabs.Bounds = new Rectangle(-4, -6, Math.Max(0, pageHost.ClientSize.Width - pageHost.Padding.Right + 8), pageHost.ClientSize.Height + 10);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 260, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 4, 8, 4), BackColor = Back };
        foreach (TabPage page in tabs.TabPages)
        {
            var item = new NavItem(page.Text) { Width = 236, Height = 40, Margin = new Padding(0, 0, 0, 4) };
            var target = page;
            item.Click += (_, _) => tabs.SelectedTab = target;
            nav.Controls.Add(item);
        }
        void MarkSelected() { foreach (NavItem item in nav.Controls) item.Selected = item.Text == tabs.SelectedTab?.Text; }
        tabs.SelectedIndexChanged += (_, _) => MarkSelected();
        MarkSelected();
        var banner = new Label
        {
            Dock = DockStyle.Top, Height = 64, Padding = new Padding(24, 6, 24, 6), ForeColor = KioskTheme.Text, BackColor = Card, Visible = Central,
            Text = "Podgląd – konfiguracja z serwera " + config.ConfigSource + ". Zmiany wprowadza się w plikach na serwerze; " +
                   "tutaj można zmienić tylko adres serwera (Aktualizacja → Konfiguracja z serwera) i login do SharePointa (Komunikaty)."
        };
        if (Central)
        {
            foreach (TabPage page in tabs.TabPages) Preview(page);
            autoUpdate.Enabled = false;
            configSource.ReadOnly = false;
            sourceUser.ReadOnly = sourcePassword.ReadOnly = false;
            removeAccount.Enabled = true;
            accountsKey.ReadOnly = false;
            removeKey.Enabled = true;
            // This PC's own login to SharePoint (Komunikaty) is saved here also with a server's configuration.
            newsUser.ReadOnly = newsPassword.ReadOnly = false;
            removeNewsAccount.Enabled = true;
            sourceTest.Enabled = true;
            updateButton.Enabled = true;
        }
        Controls.Add(pageHost); Controls.Add(nav); Controls.Add(error); Controls.Add(actions); Controls.Add(banner); Controls.Add(header);
        ApplyDark(this);
    }

    /// <summary>The configuration comes from a server (ConfigSource): everything here is a preview.</summary>
    private bool Central => original.ConfigSource.Length > 0;

    /// <summary>Read-only view of a section: values stay visible (and scrollable), nothing can be changed.</summary>
    private static void Preview(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBoxBase box: box.ReadOnly = true; break;
                case NumericUpDown number: number.ReadOnly = true; number.Increment = 0; break;
                case DataGridView grid: grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; break;
                case CheckBox or ComboBox or Button: c.Enabled = false; break;
            }
            Preview(c);
        }
    }

    /// <summary>Dark Windows 11 colours for everything inside (inputs, lists, buttons, help text).</summary>
    private static void ApplyDark(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TabPage page: page.UseVisualStyleBackColor = false; page.BackColor = Back; page.ForeColor = KioskTheme.Text; break;
                // Windows' own dark control styles (as in Windows 11 Settings): rounded fields, drop-downs and scroll bars.
                case TextBox box: box.BackColor = InputBack; box.ForeColor = KioskTheme.Text; box.BorderStyle = BorderStyle.FixedSingle; DarkTheme(box, "DarkMode_CFD"); break;
                case NumericUpDown number:
                    number.BackColor = InputBack; number.ForeColor = KioskTheme.Text; number.BorderStyle = BorderStyle.FixedSingle;
                    DarkTheme(number, "DarkMode_CFD");
                    foreach (Control part in number.Controls) DarkTheme(part, "DarkMode_CFD");
                    break;
                case ComboBox combo: combo.FlatStyle = FlatStyle.Standard; combo.BackColor = InputBack; combo.ForeColor = KioskTheme.Text; DarkTheme(combo, "DarkMode_CFD"); break;
                case CheckBox check: check.ForeColor = KioskTheme.Text; check.BackColor = Color.Transparent; break;
                case DataGridView grid: StyleGrid(grid); foreach (Control bar in grid.Controls) DarkTheme(bar, "DarkMode_Explorer"); break;
                case Button button: RoundButton(button, button.BackColor == KioskTheme.Accent); break;
                case ScrollableControl scroll when scroll is not TabPage: DarkTheme(scroll, "DarkMode_Explorer"); break;
                case Label label when label.ForeColor == HelpText: label.ForeColor = KioskTheme.Muted; break;
            }
            ApplyDark(c);
        }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subApp, string? idList);

    private static void DarkTheme(Control control, string theme)
    {
        if (control.IsHandleCreated) _ = SetWindowTheme(control.Handle, theme, null);
        control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, theme, null);
    }

    /// <summary>Each setting sits on its own rounded card, like a row in Windows 11 Settings.</summary>
    private static void PaintCards(TableLayoutPanel table, Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int gap = Math.Max(2, table.DeviceDpi / 48), radius = Math.Max(4, table.DeviceDpi / 16), y = 0;
        using var fill = new SolidBrush(Card);
        foreach (var height in table.GetRowHeights())
        {
            using var path = KioskTheme.RoundedRectangle(new Rectangle(0, y + gap, table.ClientSize.Width - 1, height - gap * 2), radius);
            g.FillPath(fill, path);
            y += height;
        }
    }

    /// <summary>Rounded buttons: the accent for the main action, a soft grey for the others.</summary>
    private static void RoundButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
        bool hovered = false, pressed = false;
        button.MouseEnter += (_, _) => { hovered = true; button.Invalidate(); };
        button.MouseLeave += (_, _) => { hovered = pressed = false; button.Invalidate(); };
        button.MouseDown += (_, _) => { pressed = true; button.Invalidate(); };
        button.MouseUp += (_, _) => { pressed = false; button.Invalidate(); };
        button.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(button.Parent?.BackColor ?? Back);
            var color = primary
                ? (pressed ? KioskTheme.AccentPressed : hovered ? KioskTheme.AccentHover : KioskTheme.Accent)
                : (pressed ? Color.FromArgb(50, 50, 50) : hovered ? Color.FromArgb(62, 62, 62) : Color.FromArgb(55, 55, 55));
            if (!button.Enabled) color = KioskTheme.Blend(color, Back, 0.5);
            int radius = Math.Max(4, button.DeviceDpi / 16);
            using var path = KioskTheme.RoundedRectangle(new Rectangle(0, 0, button.Width - 1, button.Height - 1), radius);
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);
            if (!primary) { using var line = new Pen(Color.FromArgb(70, 70, 70)); g.DrawPath(line, path); }
            TextRenderer.DrawText(g, button.Text, button.Font, button.ClientRectangle, primary ? KioskTheme.OnAccent : button.Enabled ? KioskTheme.Text : KioskTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Back;
        grid.GridColor = ControlBorder;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.DefaultCellStyle.BackColor = Color.FromArgb(39, 39, 39);
        grid.DefaultCellStyle.ForeColor = KioskTheme.Text;
        grid.DefaultCellStyle.SelectionBackColor = KioskTheme.Blend(Color.FromArgb(39, 39, 39), KioskTheme.AccentLine, 0.35);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 28, 28);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = KioskTheme.Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(28, 28, 28);
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 28, 28);
        grid.RowHeadersDefaultCellStyle.SelectionBackColor = KioskTheme.Blend(Color.FromArgb(28, 28, 28), KioskTheme.AccentLine, 0.35);
        grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        foreach (DataGridViewColumn column in grid.Columns)
            if (column is DataGridViewComboBoxColumn combo) { combo.FlatStyle = FlatStyle.Flat; combo.DefaultCellStyle.BackColor = Color.FromArgb(39, 39, 39); }
    }


    private void SetupAccent(string hex)
    {
        accentHex = KioskTheme.IsColor(hex) ? hex.ToUpperInvariant() : KioskTheme.DefaultAccent;
        foreach (var preset in KioskTheme.Presets) accent.Items.Add(preset.Name);
        accent.Items.Add(CustomAccent);
        var index = Array.FindIndex(KioskTheme.Presets, p => p.Hex.Equals(accentHex, StringComparison.OrdinalIgnoreCase));
        accent.SelectedIndex = index >= 0 ? index : accent.Items.Count - 1;
        accent.DrawItem += (_, e) =>
        {
            e.DrawBackground();
            if (e.Index < 0) return;
            var name = (string)accent.Items[e.Index]!;
            var hexForItem = e.Index < KioskTheme.Presets.Length ? KioskTheme.Presets[e.Index].Hex : accentHex;
            var swatch = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 4, e.Bounds.Height - 8, e.Bounds.Height - 8);
            using (var fill = new SolidBrush(ColorTranslator.FromHtml(hexForItem))) e.Graphics.FillRectangle(fill, swatch);
            e.Graphics.DrawRectangle(Pens.Gray, swatch);
            var label = name == CustomAccent && index < 0 ? "Własny (" + accentHex + ")" : name;
            TextRenderer.DrawText(e.Graphics, label, e.Font, new Point(swatch.Right + 8, e.Bounds.Top + 4), e.ForeColor);
            e.DrawFocusRectangle();
        };
        accent.SelectionChangeCommitted += (_, _) =>
        {
            if (accent.SelectedIndex < KioskTheme.Presets.Length) { accentHex = KioskTheme.Presets[accent.SelectedIndex].Hex; index = accent.SelectedIndex; return; }
            using var dialog = new ColorDialog { Color = ColorTranslator.FromHtml(accentHex), FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                accentHex = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
                index = -1;
            }
            else if (index >= 0) accent.SelectedIndex = index;
            accent.Invalidate();
        };
    }
    private static string AuthLabel(RdpAuthentication value) => AuthenticationOptions.First(o => o.Value == value).Label;

    /// <summary>Akcje stron → "Dla": everywhere, the Kiosk's own pages (screens, lock screen, Szybka pomoc) or a person's browser.</summary>
    private static readonly (PageScope Value, string Label)[] ScopeOptions =
        [(PageScope.All, "Wszyscy"), (PageScope.Kiosk, "Kiosk (ekrany, blokada)"), (PageScope.Users, "Użytkownicy")];
    private static string ScopeLabel(PageScope value) => ScopeOptions.First(o => o.Value == value).Label;

    /// <summary>
    /// The layout is written for 100 % (96 DPI). On a screen with another scale everything is enlarged once, so
    /// columns, rows and grids fit the bigger text (fonts are in points and follow the scale by themselves).
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (scaled) return;
        scaled = true;
        float factor = ScaleOverride ?? DeviceDpi / 96f;
        if (Math.Abs(factor - 1) < 0.01) return;
        Scale(new SizeF(factor, factor));
        accent.ItemHeight = (int)(26 * factor);   // owner-drawn: Scale leaves the item height (the colour bar was cut off)
        // A check box that does not fit its row is not drawn at all (Szybka pomoc was missing here: empty "Wł." column).
        foreach (var grid in new[] { connections, apps, bookmarks, adminCards, idleScreens, commands, helpActions })
        {
            if (grid.Columns["Enabled"] is { } enabled) { enabled.MinimumWidth = (int)(40 * factor); enabled.Width = (int)(48 * factor); }
            grid.ColumnHeadersHeight = (int)(34 * factor);
            grid.RowTemplate.Height = (int)(30 * factor);
            grid.RowHeadersWidth = (int)(28 * factor);
            foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
        }
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AllowUserToAddRows = true, AllowUserToDeleteRows = true, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersWidth = 28,
        ColumnHeadersHeight = 34, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        EnableHeadersVisualStyles = false,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(232, 237, 244), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) },
        RowTemplate = { Height = 30 }
    };

    private static void AddField(TableLayoutPanel panel, string text, Control control)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
        // The label wraps inside its column; fixed-width controls keep their width (Dock = Left in a cell collapsed them).
        panel.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 4, 0), BackColor = Color.Transparent }, 0, row);
        // A table cell shrinks a too-wide control to fit before the table has its real width (at 250 % it stayed 0 px wide):
        // the minimum width keeps number fields visible; drop-downs take the column's width, so long choices are not cut off.
        if (control is ComboBox) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        else if (control is TextBox) { control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Left | AnchorStyles.Right; }
        else if (control.Dock == DockStyle.None) { control.Anchor = AnchorStyles.Left; control.MinimumSize = new Size(control.Width, 0); }
        panel.Controls.Add(control, 1, row);
    }

    /// <param name="order">"Wyżej" / "Niżej" buttons: the list's order is the order on screen.</param>
    /// <param name="removing">Called before a row is removed (the button or the Delete key).</param>
    private static void SetupGridTab(TabPage page, DataGridView grid, string help, int helpHeight = 48, bool order = false, Action<DataGridViewRow>? removing = null)
    {
        var remove = new Button { Text = "Usuń wybrany wiersz", AutoSize = true, Dock = DockStyle.Bottom };
        remove.Click += (_, _) => { if (grid.CurrentRow is { IsNewRow: false } row) { removing?.Invoke(row); grid.Rows.Remove(row); } };
        if (removing != null) grid.UserDeletingRow += (_, e) => { if (e.Row is { IsNewRow: false } row) removing(row); };
        page.Controls.Add(grid);
        page.Controls.Add(new Label { Text = help, Dock = DockStyle.Top, Height = helpHeight, ForeColor = HelpText });
        page.Controls.Add(remove);
        if (!order) return;
        var moves = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent };
        var up = new Button { Text = "↑ Wyżej", AutoSize = true, MinimumSize = new Size(120, 0) };
        var down = new Button { Text = "↓ Niżej", AutoSize = true, MinimumSize = new Size(120, 0) };
        up.Click += (_, _) => MoveRow(grid, -1);
        down.Click += (_, _) => MoveRow(grid, 1);
        moves.Controls.AddRange(new Control[] { up, down });
        page.Controls.Add(moves);
    }

    /// <summary>
    /// Menu and Kategorie look alike: the columns in this order, names and texts share the width, icon, colour and the
    /// switches are narrow and fixed (wide enough for their headers).
    /// </summary>
    private static void LayOut(DataGridView grid, params string[] order)
    {
        for (int i = 0; i < order.Length; i++)
        {
            var column = grid.Columns[order[i]];
            column.DisplayIndex = i;
            int? width = order[i] switch { "Enabled" => 48, "Icon" or "Color" => 120, "SignedInOnly" => 130, "Bottom" => 120, _ => null };
            if (width is { } fixedWidth)
            {
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                column.Width = column.MinimumWidth = fixedWidth;
                column.Resizable = DataGridViewTriState.False;
            }
            else column.FillWeight = order[i] is "Action" or "Description" ? 150 : 100;
        }
        // A swatch and a palette instead of typing #RRGGBB.
        if (order.Contains("Color")) ColorPicker.Attach(grid, "Color");
    }

    /// <summary>Moves the selected row one place up (-1) or down (1); the new-row line stays last.</summary>
    private static void MoveRow(DataGridView grid, int by)
    {
        if (grid.CurrentRow is not { IsNewRow: false } row) return;
        grid.EndEdit();
        int from = row.Index, to = from + by, last = grid.Rows.Count - (grid.AllowUserToAddRows ? 2 : 1);
        if (to < 0 || to > last) return;
        int column = grid.CurrentCell?.ColumnIndex ?? 0;
        grid.Rows.RemoveAt(from);
        grid.Rows.Insert(to, row);
        grid.CurrentCell = row.Cells[column].Visible ? row.Cells[column] : row.Cells.Cast<DataGridViewCell>().First(c => c.Visible);
    }

    /// <summary>"Włączony": unticked entries stay in the list (so they need not be typed again) but Kiosk does not use them.</summary>
    private static void AddEnabledColumn(DataGridView grid)
    {
        var column = new DataGridViewCheckBoxColumn
        {
            Name = "Enabled", HeaderText = "Wł.", ToolTipText = "Włączony: odznaczony wpis zostaje na liście, ale Kiosk go nie używa",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 48, MinimumWidth = 40, Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        grid.Columns.Add(column);
        column.DisplayIndex = 0;
        // New rows (typed or added with a button) start ticked.
        grid.RowsAdded += (_, e) =>
        {
            for (int i = e.RowIndex; i < e.RowIndex + e.RowCount && i < grid.Rows.Count; i++)
                if (!grid.Rows[i].IsNewRow && grid.Rows[i].Cells["Enabled"].Value == null) grid.Rows[i].Cells["Enabled"].Value = true;
        };
        grid.DefaultValuesNeeded += (_, e) => e.Row.Cells["Enabled"].Value = true;
    }

    private static void SetEnabled(DataGridView grid, int row, bool enabled) => grid.Rows[row].Cells["Enabled"].Value = enabled;
    private static bool IsEnabled(DataGridViewRow row) => row.Cells["Enabled"].Value is not false;

    private static string Cell(DataGridViewRow row, string name) => Convert.ToString(row.Cells[name].Value)?.Trim() ?? "";
    // "Copy as path" in Explorer wraps paths in quotes.
    private static string PathCell(DataGridViewRow row, string name) => Cell(row, name).Trim('"').Trim();

    private RdpConnection[] ReadConnections() => connections.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r =>
    {
        var name = Cell(r, "Name");
        if (!int.TryParse(Cell(r, "Port"), out var port)) throw new InvalidDataException("Port połączenia „" + name + "” musi być liczbą.");
        var auth = AuthenticationOptions.FirstOrDefault(o => o.Label == Cell(r, "Authentication"));
        return new RdpConnection
        {
            Name = name, Server = Cell(r, "Server"), Port = port, UserName = Cell(r, "UserName"),
            Authentication = auth.Label == null ? RdpAuthentication.SmartCard : auth.Value, Enabled = IsEnabled(r)
        };
    }).ToArray();

    private void RefreshAutoConnect(string? selected)
    {
        string[] names;
        try { connections.EndEdit(); names = ReadConnections().Select(c => c.Name).Where(n => n.Length > 0).ToArray(); }
        catch (InvalidDataException) { names = original.RdpConnections.Select(c => c.Name).ToArray(); }
        autoConnect.Items.Clear();
        autoConnect.Items.Add(NoAutoConnect);
        autoConnect.Items.AddRange(names);
        var match = names.FirstOrDefault(n => n.Equals(selected, StringComparison.OrdinalIgnoreCase));
        autoConnect.SelectedItem = match ?? NoAutoConnect;
    }

    private void ChooseExecutable()
    {
        using var dialog = new OpenFileDialog { Filter = "Aplikacje Windows (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var row = apps.CurrentRow;
        if (row == null || row.IsNewRow) row = apps.Rows[apps.Rows.Add()];
        row.Cells["Path"].Value = dialog.FileName;
        if (string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["Name"].Value))) row.Cells["Name"].Value = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    /// <summary>Builds and validates the edited configuration without saving it.</summary>
    internal Config BuildConfig()
    {
        connections.EndEdit(); apps.EndEdit(); bookmarks.EndEdit(); adminCards.EndEdit(); idleScreens.EndEdit(); commands.EndEdit(); helpActions.EndEdit(); pageActions.EndEdit();
        var selected = autoConnect.SelectedItem as string;
        var result = original with
        {
            RdpConnections = ReadConnections(),
            ReaderName = reader.SelectedItem is string name && name != AnyReader ? name : "",
            BrowserUrl = home.Text.Trim(),
            BrandTitle = brandTitle.Text.Trim(),
            BrandSubtitle = brandSubtitle.Text.Trim(),
            BrandLogo = brandLogo.Text.Trim().Trim('"'),
            NewsFolder = newsFolder.Text.Trim().Trim('"'),
            NewsShow = (NewsDisplay)Math.Max(0, newsShow.SelectedIndex),
            HelpActions = helpActions.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && (Cell(r, "Name").Length > 0 || Cell(r, "Action").Length > 0))
                .Select(r => new HelpAction
                {
                    Name = Cell(r, "Name"), Action = Cell(r, "Action").Trim(), Enabled = IsEnabled(r),
                    Category = string.Join("/", Cell(r, "Category").Split('/').Select(s => s.Trim()).Where(s => s.Length > 0)),
                    Description = Cell(r, "Description").Trim(), Icon = Cell(r, "Icon").Trim(), Color = Cell(r, "Color").Trim(),
                    Side = Cell(r, "Side").Trim().Trim('"'),
                    SignedInOnly = r.Cells["SignedInOnly"].Value is bool only ? only : HelpAction.DefaultNeedsSignIn(Cell(r, "Action"))
                }).ToArray(),
            MenuCategories = menuCategories.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && MenuCategory.Normalize(Cell(r, "Name")).Length > 0)
                .Select(r => new MenuCategory
                {
                    Name = MenuCategory.Normalize(Cell(r, "Name")), Icon = Cell(r, "Icon"), Action = Cell(r, "Action"), Description = Cell(r, "Description"),
                    Color = Cell(r, "Color"), SignedInOnly = Cell(r, "Action").Length == 0 ? null : r.Cells["SignedInOnly"].Value is bool only ? only : null,
                    Bottom = r.Cells["Bottom"].Value is true, Side = Cell(r, "Side").Trim().Trim('"')
                }).ToArray(),
            NewsSeconds = (int)newsSeconds.Value,
            NewsRefreshMinutes = (int)newsRefresh.Value,
            NewsSharePoint = newsSharePoint.Text.Trim(),
            NewsSharePointAccount = newsSharePointAccount.Text.Trim(),
            NewsSharePointCount = (int)newsSharePointCount.Value,
            NewsOrder = (NewsOrdering)Math.Max(0, newsOrder.SelectedIndex),
            NewsSize = (NewsBanner)Math.Max(0, newsSize.SelectedIndex),
            NewsBarsOnScreens = newsOnScreens.Checked,
            LockAfterSeconds = (int)lockAfter.Value,
            ChangeUserAfterSeconds = (int)changeUserAfter.Value,
            ConnectTimeoutSeconds = (int)connectTimeout.Value,
            RequireCredentialPrompt = requirePin.Checked,
            SignInAsUser = signInAsUser.Checked,
            UnlockMethod = (UnlockMethod)Math.Max(0, unlock.SelectedIndex),
            SessionMode = (SessionMode)Math.Max(0, sessionMode.SelectedIndex),
            DisconnectOnCardRemoval = disconnectOnRemoval.Checked,
            AccentColor = accentHex,
            TaskbarAlignment = (TaskbarAlignment)Math.Max(0, taskbarAlign.SelectedIndex),
            BrowserOnlyBookmarks = onlyBookmarks.Checked,
            AdminCards = adminCards.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new AdminCard { Name = Cell(r, "Name"), Id = Cell(r, "Id"), Enabled = IsEnabled(r) }).ToArray(),
            AllowWindowsAdministrators = allowWindowsAdmins.Checked,
            AutoUpdate = autoUpdate.Checked,
            ConfigSource = configSource.Text.Trim().Trim('"'),
            ShowHelp = showHelp.Checked,
            HelpCloseSeconds = (int)helpCloseSeconds.Value,
            CardRemovedSeconds = (int)cardRemovedSeconds.Value,
            BrowserAllowedDomains = allowedDomains.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AutoConnectRdpName = selected == null || selected == NoAutoConnect ? "" : selected,
            Applications = apps.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new ApplicationEntry
            {
                Name = Cell(r, "Name"), Path = PathCell(r, "Path"), Arguments = Cell(r, "Arguments"), WorkingDirectory = PathCell(r, "WorkingDirectory"),
                RunAs = Cell(r, "RunAs"), Enabled = IsEnabled(r)
            }).ToArray(),
            PageActions = pageActions.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && Cell(r, "Url").Length > 0).Select(r => new PageAction
            {
                Url = Cell(r, "Url"), Click = Cell(r, "Click"), Account = Cell(r, "Account"), Submit = Cell(r, "Submit"), Enabled = IsEnabled(r),
                Scope = ScopeOptions.FirstOrDefault(o => o.Label == Cell(r, "Scope")).Value
            }).ToArray(),
            Bookmarks = bookmarks.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new Bookmark { Name = Cell(r, "Name"), Url = Cell(r, "Url"), Enabled = IsEnabled(r) }).ToArray(),
            IdleAfterSeconds = (int)idleAfter.Value,
            ScreenRefreshSeconds = (int)screenRefresh.Value,
            Commands = commands.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && Cell(r, "Name").Length > 0).Select(r => new CommandEntry
            {
                Name = Cell(r, "Name"), Path = PathCell(r, "Path"), Arguments = Cell(r, "Arguments"),
                AsUser = r.Cells["AsUser"].Value is true, Confirm = r.Cells["Confirm"].Value is true, Enabled = IsEnabled(r)
            }).ToArray(),
            IdleScreens = idleScreens.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && (Cell(r, "Url").Length > 0 || Cell(r, "Path").Length > 0)).Select(r => new ScreenContent
            {
                Screen = int.TryParse(Cell(r, "Screen"), out var screen) ? screen : throw new InvalidDataException("Numer ekranu musi być liczbą (0 = wszystkie)."),
                Url = Cell(r, "Url"), Path = PathCell(r, "Path"), Arguments = Cell(r, "Arguments"), Enabled = IsEnabled(r)
            }).ToArray()
        };
        result = result with { EnableBrowser = result.Applications.Any(a => a.IsBuiltInBrowser) };
        result.Validate();
        return result;
    }

    private void Save()
    {
        try
        {
            if (Central)
            {
                // Only the server's address can change here; the PC's own file keeps everything else.
                var source = original with { ConfigSource = configSource.Text.Trim().Trim('"') };
                source.Validate();
                SaveRequested?.Invoke(source);
                return;
            }
            var result = BuildConfig();
            if (keepsAccess != null && !keepsAccess(result))
                throw new InvalidDataException("Po zapisie nie miałbyś dostępu do Konfiguracji. Dodaj swoją kartę na liście administratorów.");
            foreach (var app in result.Applications)
            {
                if (app.IsBuiltIn || app.RunAs.Length > 0) continue;   // A shared account's share is not visible from here.
                if (!File.Exists(app.Path)) throw new InvalidDataException("Nie znaleziono pliku: " + app.Path);
                if (app.WorkingDirectory.Length > 0 && !Directory.Exists(app.WorkingDirectory))
                    throw new InvalidDataException("Nie znaleziono katalogu: " + app.WorkingDirectory);
            }
            SaveRequested?.Invoke(result);
        }
        catch (Exception ex) { error.Text = ex.Message; }
    }
}

/// <summary>A small map of the screens as Windows arranges them, with the numbers used in Kiosk (1 = main).</summary>
internal sealed class ScreenMap : Control
{
    internal ScreenMap() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var screens = Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();
        if (screens.Length == 0 || Width < 20 || Height < 20) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var all = screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        float scale = Math.Min((Width - 20f) / all.Width, (Height - 20f) / all.Height);
        float offsetX = (Width - all.Width * scale) / 2, offsetY = (Height - all.Height * scale) / 2;
        using var font = new Font("Segoe UI", 14, FontStyle.Bold);
        using var small = new Font("Segoe UI", 8);
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            var r = new RectangleF(offsetX + (b.X - all.X) * scale, offsetY + (b.Y - all.Y) * scale, b.Width * scale - 4, b.Height * scale - 4);
            using var fill = new SolidBrush(i == 0 ? KioskTheme.Blend(Color.FromArgb(45, 45, 45), KioskTheme.AccentLine, 0.3) : Color.FromArgb(45, 45, 45));
            g.FillRectangle(fill, r);
            using var frame = new Pen(i == 0 ? KioskTheme.AccentLine : Color.FromArgb(80, 80, 80));
            g.DrawRectangle(frame, r.X, r.Y, r.Width, r.Height);
            var text = (i + 1).ToString();
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, Brushes.White, r.X + (r.Width - size.Width) / 2, r.Y + (r.Height - size.Height) / 2 - 6);
            var label = b.Width + "×" + b.Height + (i == 0 ? " (główny)" : "");
            var labelSize = g.MeasureString(label, small);
            g.DrawString(label, small, Brushes.Silver, r.X + (r.Width - labelSize.Width) / 2, r.Bottom - labelSize.Height - 2);
        }
    }
}

/// <summary>A section in the left list of Konfiguracja, like Windows 11 Settings: a soft square and an accent pill when selected.</summary>
internal sealed class NavItem : Control
{
    private bool hovered, selected;

    internal NavItem(string text)
    {
        Text = text;
        Font = new Font("Segoe UI", 10);
        ForeColor = KioskTheme.Text;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    internal bool Selected
    {
        get => selected;
        set { if (selected == value) return; selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int S(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        g.Clear(Parent?.BackColor ?? Color.FromArgb(32, 32, 32));
        if (selected || hovered)
        {
            using var back = KioskTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), S(5));
            using var fill = new SolidBrush(selected ? Color.FromArgb(45, 45, 45) : Color.FromArgb(40, 40, 40));
            g.FillPath(fill, back);
        }
        if (selected)
        {
            using var pill = KioskTheme.RoundedRectangle(new Rectangle(0, (Height - S(16)) / 2, S(3), S(16)), S(1));
            using var accent = new SolidBrush(KioskTheme.AccentLine);
            g.FillPath(accent, pill);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(S(16), 0, Width - S(20), Height), KioskTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var focus = KioskTheme.RoundedRectangle(new Rectangle(S(1), S(1), Width - S(3), Height - S(3)), S(5));
            using var pen = new Pen(KioskTheme.Text, S(2));
            g.DrawPath(pen, focus);
        }
    }
}
