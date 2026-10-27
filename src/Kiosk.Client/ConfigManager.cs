using System.Diagnostics;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Konfiguracje Kiosków (KioskKonfiguracje.exe, or Kiosk.Client.exe --configs [folder]): a main folder whose subfolders
/// are configurations, the folder's name being the configuration's name (e.g. \\serwer\Kiosk\Recepcja\client.json, the
/// folder a PC's "Konfiguracja z serwera" points at). Lists them, creates new ones and edits a chosen one in the same
/// Konfiguracja as the Kiosk's. Runs as whoever started it: the share's own permissions decide who may change what.
/// </summary>
internal static class ConfigManager
{
    internal const string Argument = "--configs";
    private static string Remembered => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kiosk", "KioskKonfiguracje.json");

    internal static int Run(string? root)
    {
        root = string.IsNullOrWhiteSpace(root) ? Recall() : root.Trim().Trim('"');
        Application.Run(new ManagerForm(root));
        return 0;
    }

    private static string Recall()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Remembered))?.Root ?? ""; }
        catch (Exception) { return ""; }
    }

    private static void Remember(string root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Remembered)!);
            File.WriteAllText(Remembered, JsonSerializer.Serialize(new Settings { Root = root }));
        }
        catch (Exception) { }
    }

    private sealed class Settings { public string Root { get; set; } = ""; }

#if KIOSK_SELF_TEST
    internal static void SelfTest() => ManagerForm.SelfTest();
#endif

    private sealed class ManagerForm : Form
    {
        private readonly TextBox rootBox = new() { Width = 520 };
        private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 28, ForeColor = Color.DimGray, Padding = new Padding(8, 6, 8, 0) };

        internal ManagerForm(string root)
        {
            Text = "Konfiguracje Kiosków";
            Font = new Font("Segoe UI", 10);
            Size = new Size(900, 600);
            MinimumSize = new Size(700, 420);
            StartPosition = FormStartPosition.CenterScreen;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
            top.Controls.Add(new Label { Text = "Folder główny:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            rootBox.Text = root;
            rootBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Reload(); } };
            top.Controls.Add(rootBox);
            top.Controls.Add(Small("Wybierz…", PickRoot));
            top.Controls.Add(Small("Odśwież", () => Reload()));

            list.Columns.Add("Konfiguracja", 240);
            list.Columns.Add("Zmieniona", 160);
            list.Columns.Add("Uwagi", 360);
            list.DoubleClick += (_, _) => Edit();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
            var edit = Small("Edytuj", Edit);
            buttons.Controls.AddRange([edit, Small("Nowa…", () => Create(copy: false)), Small("Kopiuj jako nową…", () => Create(copy: true)), Small("Otwórz folder", OpenFolder)]);

            Controls.Add(list);
            Controls.Add(buttons);
            Controls.Add(status);
            Controls.Add(top);
            AcceptButton = edit;
            Shown += (_, _) => { if (rootBox.Text.Length > 0) Reload(); else PickRoot(); };
        }

        private static Button Small(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            button.Click += (_, _) => action();
            return button;
        }

        private string Root => rootBox.Text.Trim().Trim('"');
        private string? Selected => list.SelectedItems.Count == 1 ? (string)list.SelectedItems[0].Tag! : null;

        private void PickRoot()
        {
            using var dialog = new FolderBrowserDialog { Description = "Folder główny konfiguracji (w nim folder dla każdej konfiguracji)", UseDescriptionForTitle = true };
            if (Directory.Exists(Root)) dialog.InitialDirectory = Root;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            rootBox.Text = dialog.SelectedPath;
            Reload();
        }

        private void Reload(string? select = null)
        {
            list.Items.Clear();
            if (!Directory.Exists(Root)) { status.Text = Root.Length == 0 ? "Wybierz folder główny." : "Nie ma folderu " + Root + "."; return; }
            Remember(Root);
            try
            {
                var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var folder in Directory.EnumerateDirectories(Root, "*", options).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                {
                    var name = Path.GetFileName(folder);
                    if (name.StartsWith(".kiosk-config-", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParseExact(name[".kiosk-config-".Length..], "N", out _)) continue;
                    var file = Path.Combine(folder, CentralConfig.FileName);
                    if (!File.Exists(file)) continue;   // Other folders (pictures, messages, statistics) are not configurations.
                    string note;
                    try { var config = Configuration.Load(file); note = Describe(config); }
                    catch (Exception ex) { note = "Błąd w pliku: " + ex.Message; }
                    var item = new ListViewItem([Path.GetFileName(folder), File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm"), note]) { Tag = folder };
                    list.Items.Add(item);
                    if (string.Equals(folder, select, StringComparison.OrdinalIgnoreCase)) { item.Selected = true; item.EnsureVisible(); }
                }
                status.Text = list.Items.Count == 0 ? "W tym folderze nie ma jeszcze konfiguracji – dodaj nową." : list.Items.Count + " konfiguracji. Dwuklik otwiera do edycji.";
            }
            catch (Exception ex) { status.Text = "Nie można odczytać folderu: " + ex.Message; }
        }

        private static string Describe(Config config)
        {
            var parts = new List<string> { config.SessionMode.ToString() };
            if (config.RdpConnections.Length > 0) parts.Add(config.RdpConnections.Length + " RDP");
            var apps = config.Applications.Count(a => !a.IsBuiltIn);
            if (apps > 0) parts.Add(apps + " aplikacji");
            if (config.TestMode) parts.Add("TRYB TESTOWY");
            return string.Join(", ", parts);
        }

        private void Create(bool copy)
        {
            if (!Directory.Exists(Root)) { PickRoot(); if (!Directory.Exists(Root)) return; }
            var source = copy ? Selected : null;
            if (copy && source == null) { status.Text = "Zaznacz konfigurację do skopiowania."; return; }
            var name = Ask(copy ? "Nazwa nowej konfiguracji (kopia „" + Path.GetFileName(source) + "”):" : "Nazwa nowej konfiguracji (nazwa folderu):");
            if (name == null) return;
            var folder = Path.Combine(Root, name);
            try
            {
                CreateFolder(folder, source);
                Audit.Write("config_created", folder);
                Reload(folder);
                Edit();
            }
            catch (Exception ex) { MessageBox.Show(this, "Nie udało się utworzyć konfiguracji: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private static void CreateFolder(string destination, string? source)
        {
            var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Configuration folder must have a parent.", nameof(destination));
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Konfiguracja „" + Path.GetFileName(destination) + "” już jest.");
            var staging = Path.Combine(parent, ".kiosk-config-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (source != null) CopyFolder(source, staging);
                else
                {
                    Directory.CreateDirectory(staging);
                    // The example shipped with the Kiosk, else the built-in defaults.
                    var example = Path.Combine(AppContext.BaseDirectory, CentralConfig.FileName);
                    var config = File.Exists(example) ? Configuration.Load(example) : new Config();
                    Configuration.Save(Path.Combine(staging, CentralConfig.FileName), config with { ConfigSource = "" });
                }
                Directory.Move(staging, destination);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }

        private static void CopyFolder(string from, string to)
        {
            if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Nie można kopiować konfiguracji przez dowiązanie folderu.");
            var options = new EnumerationOptions { AttributesToSkip = 0 };
            var pending = new Stack<(string Source, string Target)>();
            pending.Push((from, to));
            while (pending.TryPop(out var item))
            {
                Directory.CreateDirectory(item.Target);
                foreach (var child in new DirectoryInfo(item.Source).EnumerateFileSystemInfos("*", options))
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Nie można skopiować konfiguracji z dowiązaniem: " + child.FullName);
                    if (child is DirectoryInfo) pending.Push((child.FullName, Path.Combine(item.Target, child.Name)));
                    else if (child is FileInfo) File.Copy(child.FullName, Path.Combine(item.Target, child.Name));
                }
            }
        }

#if KIOSK_SELF_TEST
        internal static void SelfTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "Kiosk.ConfigManager.Test-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "source");
            var outside = Path.Combine(root, "outside");
            var copied = Path.Combine(root, "copied");
            try
            {
                Directory.CreateDirectory(Path.Combine(source, "assets"));
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(source, CentralConfig.FileName), "{}");
                File.WriteAllText(Path.Combine(source, "assets", "logo.txt"), "logo");
                File.WriteAllText(Path.Combine(outside, "private.txt"), "outside");
                var fresh = Path.Combine(root, "fresh");
                CreateFolder(fresh, null);
                if (!File.Exists(Path.Combine(fresh, CentralConfig.FileName)) ||
                    Configuration.Load(Path.Combine(fresh, CentralConfig.FileName)).ConfigSource.Length != 0)
                    throw new InvalidOperationException("Fresh configuration was not published or kept a server source.");
                CreateFolder(copied, source);
                if (File.ReadAllText(Path.Combine(copied, "assets", "logo.txt")) != "logo")
                    throw new InvalidOperationException("Configuration copy lost an asset.");

                var link = Path.Combine(source, "linked");
                var linked = false;
                try { Directory.CreateSymbolicLink(link, outside); linked = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
                if (linked)
                {
                    var rejected = false;
                    try { CreateFolder(Path.Combine(root, "linked-copy"), source); }
                    catch (IOException) { rejected = true; }
                    if (!rejected || Directory.Exists(Path.Combine(root, "linked-copy")))
                        throw new InvalidOperationException("Configuration copy followed or silently skipped a directory link.");
                    Directory.Delete(link);
                }

                var locked = Path.Combine(source, "locked.txt");
                File.WriteAllText(locked, "in use");
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var failed = false;
                    try { CreateFolder(Path.Combine(root, "partial"), source); }
                    catch (IOException) { failed = true; }
                    if (!failed || Directory.Exists(Path.Combine(root, "partial")) ||
                        Directory.EnumerateDirectories(root, ".kiosk-config-*").Any())
                        throw new InvalidOperationException("Failed configuration copy left a partial or staging folder.");
                }
            }
            finally
            {
                if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("Kiosk.ConfigManager.Test-", StringComparison.Ordinal))
                    Directory.Delete(root, recursive: true);
            }
        }
#endif

        private string? Ask(string question)
        {
            using var dialog = new Form
            {
                Text = Text, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(460, 130), Font = Font
            };
            var box = new TextBox { Location = new Point(12, 44), Width = 436 };
            var ok = new Button { Text = "Utwórz", DialogResult = DialogResult.OK, Location = new Point(260, 86), AutoSize = true };
            var cancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, Location = new Point(360, 86), AutoSize = true };
            dialog.Controls.AddRange([new Label { Text = question, AutoSize = true, Location = new Point(12, 14) }, box, ok, cancel]);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            while (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var name = box.Text.Trim();
                if (name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.Trim('.').Length > 0) return name;
                MessageBox.Show(dialog, "Nazwa jest nazwą folderu: bez znaków \\ / : * ? \" < > |.", Text);
            }
            return null;
        }

        private void OpenFolder()
        {
            var folder = Selected ?? Root;
            if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }

        private void Edit()
        {
            if (Selected is not { } folder) { status.Text = "Zaznacz konfigurację."; return; }
            var file = Path.Combine(folder, CentralConfig.FileName);
            Config config;
            DateTime opened;
            try { opened = File.GetLastWriteTimeUtc(file); config = Configuration.Load(file); }
            catch (Exception ex) { MessageBox.Show(this, "Nie można otworzyć " + file + ": " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            using var form = new Form
            {
                Text = "Konfiguracja „" + Path.GetFileName(folder) + "” – " + file, Size = new Size(1280, 860),
                StartPosition = FormStartPosition.CenterParent, WindowState = FormWindowState.Maximized
            };
            var view = new SettingsView(config, fileEditor: true);
            view.SaveRequested += updated =>
            {
                try
                {
                    // Someone else saved it meanwhile (as in Komunikaty): ask before overwriting their change.
                    if (File.GetLastWriteTimeUtc(file) != opened && MessageBox.Show(form,
                            "Ktoś zmienił tę konfigurację, odkąd ją otworzyłeś. Zapisać mimo to (jego zmiany przepadną)?", form.Text,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    File.Copy(file, file + ".bak", overwrite: true);
                    Configuration.Save(file, updated);
                    Audit.Write("config_edited", folder);
                    form.Close();
                }
                catch (Exception ex) { MessageBox.Show(form, "Nie zapisano: " + ex.Message, form.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            view.CancelRequested += form.Close;
            form.Controls.Add(view);
            form.ShowDialog(this);
            Reload(folder);
        }
    }
}
