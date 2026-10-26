using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// Pliki (kiosk:pliki): the Kiosk's own file manager. Explorer cannot run inside the Kiosk – started next to it, it
/// becomes the Windows shell itself (taskbar, Start) or hands the window to the running one. Here the person browses
/// their folders and drives, opens files in their programs (hosted like any application), and copies, moves, renames
/// and deletes. After "Zaloguj" everything happens as that person (their profile and rights).
/// </summary>
internal sealed class FilesView : UserControl
{
    private sealed record FileEntry(string Name, string Path, bool IsFolder, DateTime Modified, long? Bytes);

    private readonly Func<Func<object?>, object?> runAs;
    private readonly string? profile;
    private readonly TextBox address = new() { Font = new Font("Segoe UI", 11), BorderStyle = BorderStyle.FixedSingle };
    private readonly ListView list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false,
        BorderStyle = BorderStyle.None, LabelEdit = true, AllowDrop = false
    };
    private readonly ListBox places = new() { Dock = DockStyle.Left, Width = 240, BorderStyle = BorderStyle.None, IntegralHeight = false, Font = new Font("Segoe UI", 11) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(10, 6, 10, 0) };
    private readonly ImageList icons = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly Stack<string> back = new();
    private readonly List<(string Name, string Path)> placeList = [];
    private readonly Dictionary<string, string> typeNames = new(StringComparer.OrdinalIgnoreCase);
    private string folder = "";
    private string initialFolder = "";
    private bool placesStarted;
    private Task? placesLoading;
    private readonly CancellationTokenSource placesCancellation = new();
    private int navigationVersion;
    private CancellationTokenSource? navigation;
    private (string[] Paths, bool Move)? clipboard;

    /// <summary>A file to open in its program (the Kiosk hosts it like an application).</summary>
    internal event Action<string>? OpenRequested;

    /// <param name="runAs">Runs file work as the person signed in with Zaloguj (impersonation), or as is.</param>
    /// <param name="profile">That person's profile folder; null = the account the Kiosk runs as.</param>
    internal FilesView(Func<Func<object?>, object?> runAs, string? profile)
    {
        this.runAs = runAs;
        this.profile = profile;
        Dock = DockStyle.Fill;
        BackColor = KioskTheme.Background; ForeColor = KioskTheme.Text;
        Font = new Font("Segoe UI", 10);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(8, 9, 8, 0), BackColor = KioskTheme.Taskbar, WrapContents = false };
        Button Tool(string text, Action action, int width = 44)
        {
            var button = new Button { Text = text, Size = new Size(width, 36), Margin = new Padding(0, 0, 6, 0) };
            KioskTheme.StyleButton(button);
            button.Click += (_, _) => action();
            toolbar.Controls.Add(button);
            return button;
        }
        Tool("←", GoBack);
        Tool("↑", GoUp);
        Tool("⟳", () => Navigate(folder, remember: false));
        address.Size = new Size(520, 36);
        address.Margin = new Padding(6, 4, 10, 0);
        address.BackColor = KioskTheme.Surface; address.ForeColor = KioskTheme.Text;
        address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(address.Text.Trim().Trim('"')); } };
        toolbar.Controls.Add(address);
        Tool("Otwórz", OpenSelected, 100);
        Tool("Nowy folder", NewFolder, 120);
        Tool("Kopiuj", () => Remember(move: false), 90);
        Tool("Wytnij", () => Remember(move: true), 90);
        Tool("Wklej", () => Paste(), 90);
        Tool("Zmień nazwę", () => { if (list.SelectedItems.Count == 1) list.SelectedItems[0].BeginEdit(); }, 130);
        Tool("Usuń", Delete, 80);

        list.Columns.Add("Nazwa", 420);
        list.Columns.Add("Zmodyfikowano", 170);
        list.Columns.Add("Typ", 160);
        list.Columns.Add("Rozmiar", 110, HorizontalAlignment.Right);
        list.BackColor = KioskTheme.Surface; list.ForeColor = KioskTheme.Text;
        list.SmallImageList = icons;
        list.ItemActivate += (_, _) => OpenSelected();
        list.AfterLabelEdit += (_, e) => Rename(e);
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) Delete();
            else if (e.KeyCode == Keys.F2 && list.SelectedItems.Count == 1) list.SelectedItems[0].BeginEdit();
            else if (e.KeyCode == Keys.Back) GoBack();
            else if (e.Control && e.KeyCode == Keys.C) Remember(move: false);
            else if (e.Control && e.KeyCode == Keys.X) Remember(move: true);
            else if (e.Control && e.KeyCode == Keys.V) Paste();
            else if (e.KeyCode == Keys.F5) Navigate(folder, remember: false);
        };
        list.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(e.Location); };
        places.BackColor = KioskTheme.Taskbar; places.ForeColor = KioskTheme.Text;
        places.SelectedIndexChanged += (_, _) => { if (places.SelectedIndex >= 0) Navigate(placeList[places.SelectedIndex].Path); };
        status.BackColor = KioskTheme.Taskbar; status.ForeColor = KioskTheme.Muted;

        Controls.Add(list); Controls.Add(places); Controls.Add(status); Controls.Add(toolbar);
        initialFolder = profile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private bool scaled;

    /// <summary>Written for 100 %: enlarged once for the screen's scale; the list and its header in Windows' dark style.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowTheme(list.Handle, "DarkMode_ItemsView", null);
        var header = SendMessage(list.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
        if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
        if (scaled) return;
        scaled = true;
        float factor = DeviceDpi / 96f;
        if (Math.Abs(factor - 1) >= 0.01)
        {
            Scale(new SizeF(factor, factor));
            foreach (ColumnHeader column in list.Columns) column.Width = (int)(column.Width * factor);
            icons.ImageSize = new Size((int)(16 * factor), (int)(16 * factor));
            icons.Images.Clear();
        }
        if (initialFolder.Length > 0) { var path = initialFolder; initialFolder = ""; Navigate(path); }
        else if (folder.Length > 0) Navigate(folder, remember: false);
        if (!placesStarted) { placesStarted = true; placesLoading = FillPlacesAsync(); }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? app, string? idList);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private T? As<T>(Func<T?> work) => (T?)runAs(() => work());

    private async Task FillPlacesAsync()
    {
        try
        {
            var found = await Task.Run(() => DiscoverPlaces(placesCancellation.Token), placesCancellation.Token);
            if (IsDisposed || placesCancellation.IsCancellationRequested) return;
            placeList.Clear();
            placeList.AddRange(found);
            places.Items.Clear();
            places.Items.AddRange(found.Select(p => (object)p.Name).ToArray());
        }
        catch (OperationCanceledException) when (placesCancellation.IsCancellationRequested) { }
        catch (Exception ex) { Audit.Write("file_places_error", ex.Message); }
    }

    private List<(string Name, string Path)> DiscoverPlaces(CancellationToken cancellation)
    {
        var found = new List<(string Name, string Path)>();
        var home = profile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void Add(string name, string? path)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(path) && As(() => Directory.Exists(path))) found.Add((name, path));
        }
        // Known folders of that person: under their profile (OneDrive may hold Pulpit / Dokumenty / Obrazy).
        string[] oneDrive;
        try { oneDrive = As(() => Directory.Exists(home) ? Directory.GetDirectories(home, "OneDrive*") : []) ?? []; }
        catch (IOException) { oneDrive = []; }
        catch (UnauthorizedAccessException) { oneDrive = []; }
        string Known(string name) => oneDrive.Select(o => Path.Combine(o, name)).FirstOrDefault(p => As(() => Directory.Exists(p))) ?? Path.Combine(home, name);
        Add("Pulpit", Known("Desktop"));
        Add("Dokumenty", Known("Documents"));
        Add("Pobrane", Path.Combine(home, "Downloads"));
        Add("Obrazy", Known("Pictures"));
        foreach (var o in oneDrive) Add(Path.GetFileName(o), o);
        Add("Folder osobisty", home);
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                var label = drive.VolumeLabel;
                Add((label.Length > 0 ? label + " " : "") + "(" + drive.Name.TrimEnd('\\') + ")", drive.Name);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    private void Navigate(string path, bool remember = true) => _ = NavigateAsync(path, remember);

    private async Task NavigateAsync(string path, bool remember)
    {
        navigation?.Cancel();
        var cancellation = new CancellationTokenSource();
        navigation = cancellation;
        var version = ++navigationVersion;
        status.Text = "Wczytywanie folderu…";
        list.Enabled = false;
        try
        {
            // Read names and metadata under the person's token, away from the WinForms thread. A slow
            // network folder must not freeze the whole Kiosk; a later navigation supersedes this one.
            var entries = await Task.Run(() => As(() => ReadDirectory(path, cancellation.Token))!, cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested || version != navigationVersion) return;
            if (remember && folder.Length > 0 && !string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)) back.Push(folder);
            folder = path;
            address.Text = path;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                var items = new ListViewItem[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    var item = new ListViewItem(entry.Name) { Tag = entry.Path, ImageKey = IconKey(entry.Path, entry.IsFolder) };
                    item.SubItems.Add(entry.Modified.ToString("yyyy-MM-dd HH:mm"));
                    item.SubItems.Add(entry.IsFolder ? "Folder" : TypeName(entry.Path));
                    item.SubItems.Add(entry.Bytes is { } size ? FormatSize(size) : "");
                    items[i] = item;
                }
                list.Items.AddRange(items);
            }
            finally { list.EndUpdate(); }
            status.Text = entries.Length + " elementów" + (clipboard is { } c ? " · w schowku: " + c.Paths.Length + (c.Move ? " (wytnij)" : " (kopiuj)") : "");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed && version == navigationVersion) Problem(ex); }
        finally
        {
            if (!IsDisposed && version == navigationVersion) { list.Enabled = true; navigation = null; }
            cancellation.Dispose();
        }
    }

    private static FileEntry[] ReadDirectory(string path, CancellationToken cancellation)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException("Nie ma folderu " + path);
        var entries = new List<FileEntry>();
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                var isFolder = entry is DirectoryInfo;
                entries.Add(new FileEntry(entry.Name, entry.FullName, isFolder, entry.LastWriteTime,
                    entry is FileInfo file ? file.Length : null));
            }
            catch (IOException) { } // The item was removed while this directory was being read.
            catch (UnauthorizedAccessException) { }
        }
        return entries.OrderBy(e => !e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

#if KIOSK_SELF_TEST
    internal Task NavigateForSmokeTestAsync(string path) => NavigateAsync(path, remember: false);
    internal Task PlacesForSmokeTestAsync => placesLoading ?? Task.CompletedTask;
    internal string FolderForSmokeTest => folder;
    internal string[] ItemsForSmokeTest => list.Items.Cast<ListViewItem>().Select(i => i.Text).ToArray();
#endif

    /// <summary>Right click: what can be done with the clicked files, or with this folder when nothing is under the mouse.</summary>
    private void ShowMenu(Point point)
    {
        var hit = list.GetItemAt(point.X, point.Y);
        if (hit == null) list.SelectedItems.Clear();
        else if (!hit.Selected) { list.SelectedItems.Clear(); hit.Selected = true; }
        var paths = Selected();
        var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 10.5f) };
        void Item(string text, Action action, bool enabled = true, string keys = "")
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled, ShortcutKeyDisplayString = keys };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (paths.Length > 0)
        {
            Item("Otwórz", OpenSelected, keys: "Enter");
            menu.Items.Add(new ToolStripSeparator());
            Item("Kopiuj", () => Remember(move: false), keys: "Ctrl+C");
            Item("Wytnij", () => Remember(move: true), keys: "Ctrl+X");
            if (paths.Length == 1 && As(() => Directory.Exists(paths[0])))
                Item("Wklej do tego folderu", () => Paste(paths[0]), clipboard != null);
            Item("Zmień nazwę", () => list.SelectedItems[0].BeginEdit(), paths.Length == 1, "F2");
            Item("Usuń", Delete, keys: "Delete");
            menu.Items.Add(new ToolStripSeparator());
            Item("Kompresuj do pliku ZIP", () => Zip(paths));
            if (paths.Length == 1 && string.Equals(Path.GetExtension(paths[0]), ".zip", StringComparison.OrdinalIgnoreCase))
                Item("Wyodrębnij tutaj", () => Unzip(paths[0]));
            Item("Kopiuj ścieżkę", () => CopyText(string.Join(Environment.NewLine, paths)));
            Item("Właściwości", () => Properties(paths));
        }
        else
        {
            Item("Wklej", () => Paste(), clipboard != null, "Ctrl+V");
            Item("Nowy folder", NewFolder);
            Item("Odśwież", () => Navigate(folder, remember: false), keys: "F5");
            menu.Items.Add(new ToolStripSeparator());
            Item("Kopiuj ścieżkę folderu", () => CopyText(folder));
            Item("Właściwości folderu", () => Properties([folder]));
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(list, point);
    }

    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); status.Text = "Skopiowano: " + text; }
        catch (Exception ex) { Problem(ex); }
    }

    private void Zip(string[] paths)
    {
        try
        {
            var name = paths.Length == 1 ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\')) : Path.GetFileName(folder.TrimEnd('\\'));
            var target = Free(Path.Combine(folder, (name.Length > 0 ? name : "Archiwum") + ".zip"));
            As<object?>(() =>
            {
                using var zip = System.IO.Compression.ZipFile.Open(target, System.IO.Compression.ZipArchiveMode.Create);
                foreach (var path in paths)
                {
                    if (File.Exists(path)) { zip.CreateEntryFromFile(path, Path.GetFileName(path)); continue; }
                    var root = Path.GetDirectoryName(path.TrimEnd('\\'))!;
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file));
                }
                return null;
            });
            Navigate(folder, remember: false);
            status.Text = "Utworzono " + Path.GetFileName(target);
        }
        catch (Exception ex) { Problem(ex); }
    }

    private void Unzip(string path)
    {
        try
        {
            var target = Free(Path.Combine(folder, Path.GetFileNameWithoutExtension(path)));
            As<object?>(() => { System.IO.Compression.ZipFile.ExtractToDirectory(path, target); return null; });
            Navigate(folder, remember: false);
            status.Text = "Wyodrębniono do " + Path.GetFileName(target);
        }
        catch (Exception ex) { Problem(ex); }
    }

    private void Properties(string[] paths)
    {
        try
        {
            var text = As(() =>
            {
                if (paths.Length > 1)
                {
                    long total = paths.Where(File.Exists).Sum(p => new FileInfo(p).Length);
                    return paths.Length + " elementów\nRozmiar plików: " + FormatSize(total) + "\nLokalizacja: " + folder;
                }
                var path = paths[0];
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                string size;
                if (info is FileInfo f) size = FormatSize(f.Length);
                else
                {
                    // A folder's size: its files, as far as this account may read them.
                    long bytes = 0; int files = 0;
                    foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    { try { bytes += new FileInfo(file).Length; files++; } catch (IOException) { } }
                    size = FormatSize(bytes) + " (" + files + " plików)";
                }
                return "Nazwa: " + info.Name + "\nTyp: " + (info is DirectoryInfo ? "Folder" : TypeName(path)) + "\nLokalizacja: " + Path.GetDirectoryName(path.TrimEnd('\\')) +
                    "\nRozmiar: " + size + "\nUtworzono: " + info.CreationTime.ToString("yyyy-MM-dd HH:mm") + "\nZmodyfikowano: " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") +
                    ((info.Attributes & FileAttributes.ReadOnly) != 0 ? "\nTylko do odczytu" : "");
            });
            MessageBox.Show(this, text, "Właściwości", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Problem(ex); }
    }

    private void GoBack() { if (back.Count > 0) Navigate(back.Pop(), remember: false); }

    private void GoUp()
    {
        var parent = Path.GetDirectoryName(folder.TrimEnd('\\'));
        if (!string.IsNullOrEmpty(parent)) Navigate(parent.Length == 2 ? parent + "\\" : parent);
    }

    private string[] Selected() => list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToArray();

    private void OpenSelected()
    {
        foreach (var path in Selected())
        {
            if (As(() => Directory.Exists(path))) { Navigate(path); return; }
            OpenRequested?.Invoke(path);
        }
    }

    private void NewFolder()
    {
        try
        {
            var name = "Nowy folder";
            for (int i = 2; As(() => Directory.Exists(Path.Combine(folder, name)) || File.Exists(Path.Combine(folder, name))); i++) name = "Nowy folder (" + i + ")";
            var path = Path.Combine(folder, name);
            As<object?>(() => Directory.CreateDirectory(path));
            Navigate(folder, remember: false);
            list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == path)?.BeginEdit();
        }
        catch (Exception ex) { Problem(ex); }
    }

    private void Rename(LabelEditEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Label)) { e.CancelEdit = true; return; }
        var from = (string)list.Items[e.Item].Tag!;
        var to = Path.Combine(Path.GetDirectoryName(from)!, e.Label.Trim());
        try
        {
            if (e.Label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("Nazwa zawiera niedozwolone znaki.");
            As<object?>(() => { if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to); return null; });
            list.Items[e.Item].Tag = to;
        }
        catch (Exception ex) { e.CancelEdit = true; Problem(ex); }
    }

    private void Delete()
    {
        var paths = Selected();
        if (paths.Length == 0) return;
        var what = paths.Length == 1 ? "„" + Path.GetFileName(paths[0]) + "”" : paths.Length + " elementów";
        if (MessageBox.Show(this, "Usunąć " + what + "? Tego nie można cofnąć.", "Pliki", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            As<object?>(() => { foreach (var p in paths) { if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); } return null; });
            Audit.Write("files_deleted", paths.Length.ToString());
        }
        catch (Exception ex) { Problem(ex); }
        Navigate(folder, remember: false);
    }

    private void Remember(bool move)
    {
        var paths = Selected();
        if (paths.Length == 0) return;
        clipboard = (paths, move);
        status.Text = "W schowku: " + paths.Length + (move ? " (wytnij)" : " (kopiuj)") + " – wybierz folder i „Wklej”.";
    }

    private void Paste(string? into = null)
    {
        if (clipboard is not { } c) return;
        var folder = into ?? this.folder;
        try
        {
            As<object?>(() =>
            {
                foreach (var from in c.Paths)
                {
                    var to = Free(Path.Combine(folder, Path.GetFileName(from.TrimEnd('\\'))));
                    if (Directory.Exists(from))
                    {
                        if ((folder + "\\").StartsWith(from.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Nie można wkleić folderu do niego samego.");
                        if (c.Move && string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase)) Directory.Move(from, to);
                        else { CopyFolder(from, to); if (c.Move) Directory.Delete(from, true); }
                    }
                    else if (c.Move) File.Move(from, to);
                    else File.Copy(from, to);
                }
                return null;
            });
            if (c.Move) clipboard = null;
        }
        catch (Exception ex) { Problem(ex); }
        Navigate(this.folder, remember: false);
    }

    /// <summary>"Raport.docx" → "Raport (2).docx" when the name is taken.</summary>
    private static string Free(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var sub in Directory.GetDirectories(from)) CopyFolder(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    private void Problem(Exception ex)
    {
        status.Text = "⚠ " + ex.Message;
        Audit.Write("files_error", ex.Message);
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? bytes + " B" : bytes < 1 << 20 ? (bytes / 1024.0).ToString("0") + " KB" : bytes < 1L << 30 ? (bytes / 1048576.0).ToString("0.0") + " MB" : (bytes / 1073741824.0).ToString("0.0") + " GB";

    protected override void Dispose(bool disposing)
    {
        if (disposing) { navigationVersion++; navigation?.Cancel(); placesCancellation.Cancel(); icons.Dispose(); }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ icons and types (Windows' own, by extension)

    private string IconKey(string path, bool isFolder)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        // These three types can have per-file icons. Bound the cache across a long-lived session;
        // after that the standard icon for the extension is enough for another executable.
        bool customIcon = (extension is ".exe" or ".lnk" or ".ico") && icons.Images.Count < 256;
        var key = isFolder ? "<folder>" : customIcon ? path : extension;
        if (icons.Images.ContainsKey(key)) return key;
        var info = new ShFileInfo();
        uint flags = SHGFI_ICON | (icons.ImageSize.Width > 24 ? 0 : SHGFI_SMALLICON) | SHGFI_USEFILEATTRIBUTES;   // Large icons on a big scale: sharper.
        if (SHGetFileInfo(isFolder ? "folder" : path, isFolder ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) != IntPtr.Zero && info.Icon != IntPtr.Zero)
        {
            using (var icon = Icon.FromHandle(info.Icon)) icons.Images.Add(key, icon.ToBitmap());
            DestroyIcon(info.Icon);
        }
        return key;
    }

    private string TypeName(string path)
    {
        var extension = Path.GetExtension(path);
        if (typeNames.TryGetValue(extension, out var cached)) return cached;
        var info = new ShFileInfo();
        var name = SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero
            ? info.TypeName : extension;
        typeNames[extension] = name;
        return name;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>The program and arguments Windows uses to open a file (its "open" command), for hosting it in the Kiosk.</summary>
internal static class FileAssociation
{
    /// <summary>Null when Windows has no desktop program for the file (none at all, or only a Store app).</summary>
    internal static (string Program, string Arguments)? Command(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0 || IsProgram(path)) return null;
        uint size = 2048;
        var text = new System.Text.StringBuilder((int)size);
        if (AssocQueryString(0x400, 1, extension, "open", text, ref size) != 0) return Viewer(path);   // ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_COMMAND
        var template = Environment.ExpandEnvironmentVariables(text.ToString()).Trim();
        string program, rest;
        if (template.StartsWith('"'))
        {
            int end = template.IndexOf('"', 1);
            if (end < 0) return null;
            program = template[1..end]; rest = template[(end + 1)..];
        }
        else
        {
            int exe = template.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int end = exe >= 0 ? exe + 4 : template.IndexOf(' ') is var space and >= 0 ? space : template.Length;
            program = template[..end]; rest = template[end..];
        }
        // Store apps (WindowsApps) cannot be started or hosted from here: pictures, PDF and text then open in Edge.
        if (!File.Exists(program) || program.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return Viewer(path);
        // Placeholders other than the file (Word: /o "%u") have no value here: dropped with their switch.
        rest = System.Text.RegularExpressions.Regex.Replace(rest, @"(\s+/\w+)?\s*""?%[^1Ll*\s""]""?", "");
        var quoted = "\"" + path + "\"";
        bool named = rest.Contains("%1") || rest.Contains("%L", StringComparison.OrdinalIgnoreCase);
        rest = rest.Replace("\"%1\"", quoted).Replace("\"%L\"", quoted).Replace("\"%l\"", quoted)
                   .Replace("%1", quoted).Replace("%L", quoted).Replace("%l", quoted).Replace("%*", "").Trim();
        return (program, named ? rest : (rest + " " + quoted).Trim());
    }

    /// <summary>Programs and scripts: Pliki opens documents, it never starts a program (e.g. one brought on a USB stick).</summary>
    internal static bool IsProgram(string path) => Programs.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static readonly string[] Programs = [".exe", ".com", ".scr", ".pif", ".msi", ".msix", ".appx", ".bat", ".cmd", ".ps1", ".psm1",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".reg", ".cpl", ".msc", ".jar", ".application", ".appref-ms",
        // Files Windows opens in Explorer, a shell window or an installer: a way out of the Kiosk just the same.
        ".zip", ".cab", ".iso", ".img", ".vhd", ".vhdx", ".library-ms", ".search-ms", ".searchconnector-ms", ".chm", ".settingcontent-ms",
        ".rdp", ".diagcab", ".msu", ".msp", ".xll", ".appinstaller", ".theme", ".themepack", ".ms-settings", ".website", ".scf", ".inf"];

    private static readonly string[] Viewable = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".pdf", ".txt", ".log", ".csv", ".xml", ".json"];

    private static (string Program, string Arguments)? Viewer(string path)
    {
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        return Viewable.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(edge) ? (edge, "--single-argument \"" + path + "\"") : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string association, string? extra, System.Text.StringBuilder output, ref uint size);
}
