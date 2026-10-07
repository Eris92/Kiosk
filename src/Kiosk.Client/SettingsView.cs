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
    private readonly TextBox reader = new() { Dock = DockStyle.Fill };
    private readonly TextBox home = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown lockAfter = new() { Minimum = 10, Maximum = 86400, Dock = DockStyle.Left, Width = 140 };
    private readonly NumericUpDown changeUserAfter = new() { Minimum = 0, Maximum = 86400, Dock = DockStyle.Left, Width = 140 };
    private readonly NumericUpDown connectTimeout = new() { Minimum = 10, Maximum = 300, Dock = DockStyle.Left, Width = 140 };
    private readonly ComboBox sessionMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 520, Items = { "Kiosk: jedno konto Windows, osoby przełącza Kiosk kartą", "Powłoka Windows: każdy loguje się do Windows kartą/Hello (pełne SSO)" } };
    private readonly CheckBox disconnectOnRemoval = new() { Text = "Powłoka Windows: wyjęcie karty rozłącza sesję (programy działają dalej)", AutoSize = true };
    private readonly ComboBox unlock = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 320, Items = { "Tylko karta", "Karta + Windows Hello (PIN / biometria)", "Karta + PIN karty" } };
    private readonly ComboBox accent = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 260, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26
    };
    private string accentHex = KioskTheme.DefaultAccent;
    private const string CustomAccent = "Własny kolor…";
    private readonly CheckBox enableBrowser = new() { Text = "Przeglądarka włączona (widoczna w Menu)", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 6, 4, 2), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly CheckBox onlyBookmarks = new() { Text = "Pozwalaj otwierać tylko strony z zakładek (i ich subdomeny)", AutoSize = true, Padding = new Padding(4, 6, 4, 6) };
    private readonly TextBox allowedDomains = new() { Dock = DockStyle.Fill, PlaceholderText = "login.microsoftonline.com, accounts.google.com" };
    private readonly CheckBox requirePin = new() { Text = "Wymagaj PIN-u / poświadczeń przy każdym połączeniu RDP", AutoSize = true };
    private readonly ComboBox autoConnect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 320 };
    private readonly DataGridView connections = Grid();
    private readonly DataGridView apps = Grid();
    private readonly DataGridView bookmarks = Grid();
    private readonly DataGridView adminCards = Grid();
    private readonly CheckBox allowWindowsAdmins = new() { Text = "Administratorzy Windows tego komputera też mają dostęp (sprawdzane po koncie z certyfikatu karty)", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 6, 4, 6) };
    private readonly Func<Config, bool>? keepsAccess;
    private readonly Label error = new() { Dock = DockStyle.Bottom, Height = 40, ForeColor = Color.Firebrick, Padding = new Padding(16, 8, 16, 0) };
    internal event Action<Config>? SaveRequested;
    internal event Action? CancelRequested;

    internal SettingsView(Config config, CardIdentity? me = null, Func<Config, bool>? keepsAccess = null)
    {
        original = config;
        this.keepsAccess = keepsAccess;
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(245, 247, 250);
        ForeColor = Color.FromArgb(20, 28, 40);
        Font = new Font("Segoe UI", 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = KioskTheme.Surface };
        header.Controls.Add(new Label
        {
            Text = "Konfiguracja Kiosku", ForeColor = KioskTheme.Text, Font = new Font("Segoe UI", 16, FontStyle.Bold),
            AutoSize = true, Location = new Point(20, 16), BackColor = Color.Transparent
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
        foreach (var card in config.AdminCards) adminCards.Rows.Add(card.Name, card.Id);
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
        foreach (var c in config.RdpConnections)
            connections.Rows.Add(c.Name, c.Server, c.Port.ToString(), c.UserName, AuthLabel(c.Authentication));
        connections.DefaultValuesNeeded += (_, e) => { e.Row.Cells["Port"].Value = "3389"; e.Row.Cells["Authentication"].Value = AuthLabel(RdpAuthentication.SmartCard); };
        connections.DataError += (_, e) => e.ThrowException = false;
        SetupGridTab(remote, connections,
            "Każde połączenie pojawia się w Menu. Logowanie Entra ID wymaga pełnej nazwy DNS komputera. " +
            "Karta i Windows Hello logują użytkownika przypisanego do karty.");

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, "Czytnik karty (pusty = dowolny)", reader);
        AddField(fields, "Strona startowa przeglądarki", home);
        AddField(fields, "Blokada po bezczynności (s, karta w czytniku)", lockAfter);
        AddField(fields, "Przechowywanie sesji po wyjęciu karty (s, 0 = do wylogowania)", changeUserAfter);
        AddField(fields, "Tryb sesji", sessionMode);
        AddField(fields, "Logowanie i odblokowanie", unlock);
        AddField(fields, "Kolor przewodni", accent);
        AddField(fields, "Połącz automatycznie po włożeniu karty", autoConnect);
        AddField(fields, "Limit czasu łączenia RDP (sekundy)", connectTimeout);
        var pinRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        fields.Controls.Add(requirePin, 1, pinRow);
        var removalRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        fields.Controls.Add(disconnectOnRemoval, 1, removalRow);
        session.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom, Height = 90, ForeColor = Color.FromArgb(80, 92, 110),
            Text = "Każda karta ma własną sesję (to nie jest blokada Windows). Wyjęcie karty chowa sesję, inna karta od razu otwiera swoją, " +
                   "a powrót karty przywraca jej okna. Po czasie przechowywania nieużywana sesja jest zamykana. " +
                   "Na komputerze dla wielu osób wybierz „Karta + PIN karty”: Windows Hello potwierdza konto Windows, nie osobę z karty.\n" +
                   "Bez wymagania PIN-u Kiosk łączy od razu tożsamością z karty / Windows Hello / Entra, jeśli Windows ją udostępnia; w przeciwnym razie i tak zapyta."
        });
        session.Controls.Add(fields);
        reader.Text = config.ReaderName;
        home.Text = config.BrowserUrl;
        lockAfter.Value = config.LockAfterSeconds;
        changeUserAfter.Value = config.ChangeUserAfterSeconds;
        connectTimeout.Value = config.ConnectTimeoutSeconds;
        requirePin.Checked = config.RequireCredentialPrompt;
        unlock.SelectedIndex = (int)config.UnlockMethod;
        sessionMode.SelectedIndex = (int)config.SessionMode;
        disconnectOnRemoval.Checked = config.DisconnectOnCardRemoval;
        SetupAccent(config.AccentColor);
        RefreshAutoConnect(config.AutoConnectRdpName);
        tabs.Selected += (_, _) => RefreshAutoConnect(autoConnect.SelectedItem as string);

        apps.Columns.Add("Name", "Nazwa w menu");
        apps.Columns.Add("Path", "Pełna ścieżka do EXE");
        apps.Columns.Add("Arguments", "Argumenty");
        apps.Columns.Add("WorkingDirectory", "Katalog roboczy (opcjonalny)");
        foreach (var app in config.Applications) apps.Rows.Add(app.Name, app.Path, app.Arguments, app.WorkingDirectory);
        bookmarks.Columns.Add("Name", "Nazwa zakładki");
        bookmarks.Columns.Add("Url", "Adres HTTP/HTTPS");
        foreach (var bookmark in config.Bookmarks) bookmarks.Rows.Add(bookmark.Name, bookmark.Url);
        SetupGridTab(web, bookmarks, "Dodaj zakładkę w pustym wierszu. Zaznacz wiersz, aby go usunąć.");
        var webOptions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 8) };
        webOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        webOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        webOptions.Controls.Add(onlyBookmarks, 0, 0);
        webOptions.SetColumnSpan(onlyBookmarks, 2);
        webOptions.Controls.Add(new Label { Text = "Dodatkowe dozwolone domeny (np. strona logowania)", AutoSize = true, Padding = new Padding(4, 8, 4, 4) }, 0, 1);
        webOptions.Controls.Add(allowedDomains, 1, 1);
        web.Controls.Add(webOptions);
        enableBrowser.Checked = config.EnableBrowser;
        web.Controls.Add(enableBrowser);
        onlyBookmarks.Checked = config.BrowserOnlyBookmarks;
        allowedDomains.Text = string.Join(", ", config.BrowserAllowedDomains);
        allowedDomains.Enabled = onlyBookmarks.Checked;
        onlyBookmarks.CheckedChanged += (_, _) => allowedDomains.Enabled = onlyBookmarks.Checked;
        SetupGridTab(applications, apps, "Każda pozycja pojawia się w Menu. Notatnik (" + ApplicationEntry.BuiltInNotes + ") to przykładowa wbudowana aplikacja: usuń wiersz, aby ją wyłączyć. Osadzanie obsługuje zgodne aplikacje Win32.");
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

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 10) };
        var save = new Button { Text = "Zapisz konfigurację", Size = new Size(200, 38) };
        var cancel = new Button { Text = "Anuluj", Size = new Size(120, 38) };
        KioskTheme.StyleButton(save, primary: true);
        KioskTheme.StyleButton(cancel);
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        Controls.Add(tabs); Controls.Add(error); Controls.Add(actions); Controls.Add(header);
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
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new Padding(4, 8, 4, 4) }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void SetupGridTab(TabPage page, DataGridView grid, string help)
    {
        var remove = new Button { Text = "Usuń wybrany wiersz", AutoSize = true, Dock = DockStyle.Bottom };
        remove.Click += (_, _) => { if (grid.CurrentRow is { IsNewRow: false } row) grid.Rows.Remove(row); };
        page.Controls.Add(grid);
        page.Controls.Add(new Label { Text = help, Dock = DockStyle.Top, Height = 48, ForeColor = Color.FromArgb(80, 92, 110) });
        page.Controls.Add(remove);
    }

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
            Authentication = auth.Label == null ? RdpAuthentication.SmartCard : auth.Value
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
        connections.EndEdit(); apps.EndEdit(); bookmarks.EndEdit(); adminCards.EndEdit();
        var selected = autoConnect.SelectedItem as string;
        var result = original with
        {
            RdpConnections = ReadConnections(),
            ReaderName = reader.Text.Trim(),
            BrowserUrl = home.Text.Trim(),
            LockAfterSeconds = (int)lockAfter.Value,
            ChangeUserAfterSeconds = (int)changeUserAfter.Value,
            ConnectTimeoutSeconds = (int)connectTimeout.Value,
            RequireCredentialPrompt = requirePin.Checked,
            UnlockMethod = (UnlockMethod)Math.Max(0, unlock.SelectedIndex),
            SessionMode = (SessionMode)Math.Max(0, sessionMode.SelectedIndex),
            DisconnectOnCardRemoval = disconnectOnRemoval.Checked,
            AccentColor = accentHex,
            EnableBrowser = enableBrowser.Checked,
            BrowserOnlyBookmarks = onlyBookmarks.Checked,
            AdminCards = adminCards.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new AdminCard { Name = Cell(r, "Name"), Id = Cell(r, "Id") }).ToArray(),
            AllowWindowsAdministrators = allowWindowsAdmins.Checked,
            BrowserAllowedDomains = allowedDomains.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AutoConnectRdpName = selected == null || selected == NoAutoConnect ? "" : selected,
            Applications = apps.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new ApplicationEntry
            {
                Name = Cell(r, "Name"), Path = PathCell(r, "Path"), Arguments = Cell(r, "Arguments"), WorkingDirectory = PathCell(r, "WorkingDirectory")
            }).ToArray(),
            Bookmarks = bookmarks.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => new Bookmark { Name = Cell(r, "Name"), Url = Cell(r, "Url") }).ToArray()
        };
        result.Validate();
        return result;
    }

    private void Save()
    {
        try
        {
            var result = BuildConfig();
            if (keepsAccess != null && !keepsAccess(result))
                throw new InvalidDataException("Po zapisie nie miałbyś dostępu do Konfiguracji. Dodaj swoją kartę na liście administratorów.");
            foreach (var app in result.Applications)
            {
                if (app.IsBuiltInNotes) continue;
                if (!File.Exists(app.Path)) throw new InvalidDataException("Nie znaleziono pliku: " + app.Path);
                if (app.WorkingDirectory.Length > 0 && !Directory.Exists(app.WorkingDirectory))
                    throw new InvalidDataException("Nie znaleziono katalogu: " + app.WorkingDirectory);
            }
            SaveRequested?.Invoke(result);
        }
        catch (Exception ex) { error.Text = ex.Message; }
    }
}
