namespace Kiosk.Client;

/// <summary>
/// First start of a new installation (ShowSetup): where does this PC take its configuration from – its own file,
/// or a server folder / share (Konfiguracja z serwera)? One installation package then fits every site: the server
/// with the right configuration is chosen here. Saving needs a Windows administrator (the configuration is shared).
/// "Później" starts the Kiosk without saving; the question comes back at the next start.
/// </summary>
internal sealed class FirstRunSetup : Form
{
    private readonly string path;
    private readonly Config local;
    private readonly TextBox source = new() { Width = 640, Font = new Font("Segoe UI", 12), PlaceholderText = @"\\serwer\kiosk", Margin = new Padding(0, 4, 0, 8), BackColor = Color.FromArgb(45, 45, 45), ForeColor = KioskTheme.Text, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label result = new() { AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = KioskTheme.Text, Margin = new Padding(0, 4, 0, 8) };
    private readonly Label error = new() { AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = Color.FromArgb(255, 153, 164), Margin = new Padding(0, 12, 0, 0) };

    /// <summary>Shows the question when the PC's own configuration asks for it; true when something was saved.</summary>
    internal static bool RunIfNeeded(string path, Config local)
    {
        if (!local.ShowSetup || local.ConfigSource.Length > 0) return false;
        using var form = new FirstRunSetup(path, local);
        Application.Run(form);
        return form.DialogResult == DialogResult.OK;
    }

    private FirstRunSetup(string path, Config local)
    {
        this.path = path;
        this.local = local;
        Text = "Kiosk – pierwsze uruchomienie";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 11);

        var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = KioskTheme.Surface, Padding = new Padding(40, 32, 40, 32) };
        column.Controls.Add(new Label { Text = "Witaj w Kiosku", AutoSize = true, Font = new Font("Segoe UI Semibold", 24), Margin = new Padding(0, 0, 0, 8) });
        column.Controls.Add(new Label { Text = "Skąd ten komputer ma brać konfigurację?", AutoSize = true, ForeColor = KioskTheme.Muted, Font = new Font("Segoe UI", 13), Margin = new Padding(0, 0, 0, 24) });

        var localButton = Choice("Konfiguracja lokalna", "Ustawienia zapisane na tym komputerze. Zmienisz je później w Konfiguracji (Ctrl+Alt+K).");
        var serverButton = Choice("Konfiguracja z serwera", "Jedna konfiguracja dla wielu komputerów: folder lub udział z client.json (logo, komunikaty…), pobierany przy każdym uruchomieniu.");
        column.Controls.Add(localButton);
        column.Controls.Add(serverButton);

        var server = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Visible = false, Margin = new Padding(0, 16, 0, 0), BackColor = KioskTheme.Surface };
        server.Controls.Add(new Label { Text = "Folder lub udział z plikiem client.json", AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        server.Controls.Add(source);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0), BackColor = KioskTheme.Surface };
        var test = new Button { Text = "Test", AutoSize = true, Padding = new Padding(12, 0, 12, 0), Margin = new Padding(0, 0, 8, 0) };
        var save = new Button { Text = "Zapisz i uruchom", AutoSize = true, Padding = new Padding(12, 0, 12, 0), Margin = new Padding(0) };
        KioskTheme.StyleButton(test);
        KioskTheme.StyleButton(save, primary: true);
        buttons.Controls.Add(test);
        buttons.Controls.Add(save);
        server.Controls.Add(buttons);
        server.Controls.Add(result);
        column.Controls.Add(server);
        column.Controls.Add(error);

        var later = new LinkLabel { Text = "Później (zapytaj przy następnym uruchomieniu)", AutoSize = true, LinkColor = KioskTheme.Muted, ActiveLinkColor = KioskTheme.Text, Margin = new Padding(0, 24, 0, 0) };
        later.LinkClicked += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        column.Controls.Add(later);

        localButton.Click += (_, _) => Save(local with { ShowSetup = false }, "local");
        serverButton.Click += (_, _) => { server.Visible = true; source.Focus(); };
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            result.Text = "Sprawdzam dostęp do " + source.Text.Trim() + "…";
            try { result.Text = await CentralConfig.DescribeTestAsync(source.Text); }
            finally { if (!IsDisposed) test.Enabled = true; }
        };
        save.Click += (_, _) => Save(local with { ConfigSource = source.Text.Trim().Trim('"'), ShowSetup = false }, "server");
        source.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; save.PerformClick(); } };

        Controls.Add(column);
        // Sizes above are for 100 %: enlarged once for the screen's scale (fonts in points follow it by themselves).
        HandleCreated += (_, _) => { float scale = DeviceDpi / 96f; if (scale > 1.01f) column.Scale(new SizeF(scale, scale)); };
        void Center() => column.Location = new Point(Math.Max(0, (ClientSize.Width - column.Width) / 2), Math.Max(0, (ClientSize.Height - column.Height) / 2));
        Resize += (_, _) => Center();
        column.SizeChanged += (_, _) => Center();
        Shown += (_, _) => Center();
    }

    /// <summary>A large choice: the title in bold and a line of explanation under it.</summary>
    private static Button Choice(string title, string detail)
    {
        var button = new Button
        {
            Text = title + "\n" + detail, TextAlign = ContentAlignment.MiddleLeft, AutoSize = false,
            Size = new Size(820, 84), Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0, 0, 0, 12)
        };
        KioskTheme.StyleButton(button);
        return button;
    }

    private void Save(Config chosen, string kind)
    {
        try
        {
            if (kind == "server" && chosen.ConfigSource.Length == 0) throw new InvalidDataException("Podaj folder lub udział, np. \\\\serwer\\kiosk.");
            chosen.Validate();
            Configuration.Save(path, chosen);   // Shared configuration: Windows asks for an administrator.
            Audit.Write("first_run_setup", kind == "server" ? chosen.ConfigSource : "local");
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { error.Text = ex.Message; }
    }
}
