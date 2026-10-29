using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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
    private sealed class MoveManifest
    {
        internal readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Directories = new(StringComparer.Ordinal);
    }
    private static readonly uint[] ZipCrcTable = CreateZipCrcTable();

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
    private HashSet<string> listedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> typeNames = new(StringComparer.OrdinalIgnoreCase);
    private string folder = "";
    private string initialFolder = "";
    private bool placesStarted;
    private Task? placesLoading;
    private readonly CancellationTokenSource placesCancellation = new();
    private int navigationVersion;
    private CancellationTokenSource? navigation;
    private readonly CancellationTokenSource operationStop = new();
    private bool operationBusy;
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
            listedFolders = entries.Where(e => e.IsFolder).Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            if (paths.Length == 1 && listedFolders.Contains(paths[0]))
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

    /// <summary>Moves blocking filesystem work off the UI thread while retaining the signed-in person's token.</summary>
    private async Task<bool> RunWorkAsync(string progress, Func<CancellationToken, string> work)
    {
        if (operationBusy || IsDisposed) { if (!IsDisposed) status.Text = "Poczekaj na zakończenie bieżącej operacji."; return false; }
        operationBusy = true;
        var shownFolder = folder;
        status.Text = progress + "…";
        try
        {
            var result = await Task.Run(() => As(() => work(operationStop.Token))!, operationStop.Token);
            if (IsDisposed || operationStop.IsCancellationRequested) return false;
            if (string.Equals(folder, shownFolder, StringComparison.OrdinalIgnoreCase))
                await NavigateAsync(folder, remember: false);
            if (!IsDisposed) status.Text = result;
            return true;
        }
        catch (OperationCanceledException) when (operationStop.IsCancellationRequested) { return false; }
        catch (Exception ex) { if (!IsDisposed) Problem(ex); return false; }
        finally { operationBusy = false; }
    }

    private void Zip(string[] paths) => _ = ZipAsync(paths);

    private Task<bool> ZipAsync(string[] paths)
    {
        var destination = folder;
        return RunWorkAsync("Kompresowanie plików", cancellation =>
        {
            var name = paths.Length == 1 ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\')) : Path.GetFileName(destination.TrimEnd('\\'));
            var target = Free(Path.Combine(destination, (name.Length > 0 ? name : "Archiwum") + ".zip"));
            var partial = target + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
                {
                    File.SetAttributes(partial, File.GetAttributes(partial) | FileAttributes.Hidden);
                    foreach (var path in paths)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Nie można kompresować dowiązania: " + path);
                        if (File.Exists(path)) { AddZipFile(zip, path, Path.GetFileName(path), cancellation); continue; }
                        var root = Path.GetDirectoryName(path.TrimEnd('\\'))!;
                        ZipFolder(zip, path, root, cancellation);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                File.SetAttributes(partial, File.GetAttributes(partial) & ~FileAttributes.Hidden);
                File.Move(partial, target);
            }
            catch { DeletePartial(partial); throw; }
            return "Utworzono " + Path.GetFileName(target);
        });
    }

    private void Unzip(string path) => _ = UnzipAsync(path);

    private Task<bool> UnzipAsync(string path)
    {
        var destination = folder;
        return RunWorkAsync("Wyodrębnianie archiwum", cancellation =>
        {
            var target = Free(Path.Combine(destination, Path.GetFileNameWithoutExtension(path)));
            var partial = target + ".partial-" + Guid.NewGuid().ToString("N");
            cancellation.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(partial);
                File.SetAttributes(partial, File.GetAttributes(partial) | FileAttributes.Hidden);
                using (var zip = ZipFile.OpenRead(path))
                {
                    EnsureArchiveFits(zip, partial);
                    var root = Path.GetFullPath(partial).TrimEnd('\\') + "\\";
                    foreach (var entry in zip.Entries)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var relative = entry.FullName.Replace('/', '\\');
                        ValidateArchivePath(relative);
                        var output = Path.GetFullPath(Path.Combine(partial, relative));
                        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Archiwum zawiera ścieżkę poza wybranym folderem.");
                        if (entry.Name.Length == 0) { Directory.CreateDirectory(output); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        using (var source = entry.Open())
                        using (var destinationFile = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072))
                        {
                            CopyStream(source, destinationFile, cancellation, entry.Length, entry.Crc32);
                            destinationFile.Flush(flushToDisk: true);
                        }
                        if (entry.LastWriteTime.Year is >= 1980 and <= 2107)
                            File.SetLastWriteTime(output, entry.LastWriteTime.LocalDateTime);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                File.SetAttributes(partial, File.GetAttributes(partial) & ~FileAttributes.Hidden);
                Directory.Move(partial, target);
            }
            catch { DeletePartial(partial, directory: true); throw; }
            return "Wyodrębniono do " + Path.GetFileName(target);
        });
    }

    private static void ZipFolder(ZipArchive zip, string folder, string root, CancellationToken cancellation)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Nie można kompresować dowiązania: " + current);
            zip.CreateEntry(Path.GetRelativePath(root, current).Replace('\\', '/') + "/");
            foreach (var file in Directory.EnumerateFiles(current))
            {
                cancellation.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Nie można kompresować dowiązania: " + file);
                AddZipFile(zip, file, Path.GetRelativePath(root, file), cancellation);
            }
            foreach (var sub in Directory.EnumerateDirectories(current))
            { cancellation.ThrowIfCancellationRequested(); pending.Push(sub); }
        }
    }

    private static void AddZipFile(ZipArchive zip, string path, string name, CancellationToken cancellation)
    {
        var entry = zip.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Optimal);
        var modified = File.GetLastWriteTime(path);
        if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
        using var destination = entry.Open();
        CopyStream(source, destination, cancellation);
    }

    private static void ValidateArchivePath(string relative)
    {
        var parts = relative.TrimEnd('\\').Split('\\');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." ||
            p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedName(p)))
            throw new InvalidDataException("Archiwum zawiera niebezpieczną nazwę pliku.");
    }

    private static bool ReservedName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '1' and <= '9';
    }

    private void Properties(string[] paths) => _ = PropertiesAsync(paths);

    private async Task PropertiesAsync(string[] paths)
    {
        if (operationBusy || IsDisposed) { if (!IsDisposed) status.Text = "Poczekaj na zakończenie bieżącej operacji."; return; }
        operationBusy = true;
        var location = folder;
        var type = paths.Length == 1
            ? list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == paths[0])?.SubItems[2].Text ?? "Folder"
            : "";
        status.Text = "Obliczanie właściwości…";
        try
        {
            var text = await Task.Run(() => As(() =>
            {
                operationStop.Token.ThrowIfCancellationRequested();
                if (paths.Length > 1)
                {
                    long total = 0;
                    foreach (var selected in paths)
                    { operationStop.Token.ThrowIfCancellationRequested(); if (File.Exists(selected)) total += new FileInfo(selected).Length; }
                    return paths.Length + " elementów\nRozmiar plików: " + FormatSize(total) + "\nLokalizacja: " + location;
                }
                var path = paths[0];
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                string size;
                if (info is FileInfo f) size = FormatSize(f.Length);
                else
                {
                    // A folder's size: its files, as far as this account may read them.
                    long bytes = 0; int files = 0;
                    foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    { operationStop.Token.ThrowIfCancellationRequested(); try { bytes += new FileInfo(file).Length; files++; } catch (IOException) { } }
                    size = FormatSize(bytes) + " (" + files + " plików)";
                }
                return "Nazwa: " + info.Name + "\nTyp: " + type + "\nLokalizacja: " + Path.GetDirectoryName(path.TrimEnd('\\')) +
                    "\nRozmiar: " + size + "\nUtworzono: " + info.CreationTime.ToString("yyyy-MM-dd HH:mm") + "\nZmodyfikowano: " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") +
                    ((info.Attributes & FileAttributes.ReadOnly) != 0 ? "\nTylko do odczytu" : "");
            })!, operationStop.Token);
            if (IsDisposed || operationStop.IsCancellationRequested) return;
            MessageBox.Show(this, text, "Właściwości", MessageBoxButtons.OK, MessageBoxIcon.Information);
            status.Text = "Właściwości gotowe.";
        }
        catch (OperationCanceledException) when (operationStop.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) Problem(ex); }
        finally { operationBusy = false; }
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
            if (listedFolders.Contains(path)) { Navigate(path); return; }
            OpenRequested?.Invoke(path);
        }
    }

    private void NewFolder()
    {
        _ = NewFolderAsync();
    }

    private async Task NewFolderAsync()
    {
        var destination = folder;
        string? created = null;
        var succeeded = await RunWorkAsync("Tworzenie folderu", cancellation =>
        {
            var name = "Nowy folder";
            for (int i = 2; Directory.Exists(Path.Combine(destination, name)) || File.Exists(Path.Combine(destination, name)); i++)
            { cancellation.ThrowIfCancellationRequested(); name = "Nowy folder (" + i + ")"; }
            cancellation.ThrowIfCancellationRequested();
            created = Path.Combine(destination, name);
            Directory.CreateDirectory(created);
            return "Utworzono " + name;
        });
        if (succeeded && created != null && string.Equals(folder, destination, StringComparison.OrdinalIgnoreCase))
            list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == created)?.BeginEdit();
    }

    private void Rename(LabelEditEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Label)) { e.CancelEdit = true; return; }
        var from = (string)list.Items[e.Item].Tag!;
        var to = Path.Combine(Path.GetDirectoryName(from)!, e.Label.Trim());
        e.CancelEdit = true; // The operation finishes asynchronously; refresh with the actual filesystem result.
        if (e.Label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Problem(new IOException("Nazwa zawiera niedozwolone znaki.")); return; }
        if (string.Equals(from, to, StringComparison.Ordinal)) return;
        _ = RunWorkAsync("Zmiana nazwy", cancellation =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
            return "Zmieniono nazwę na " + Path.GetFileName(to);
        });
    }

    private void Delete()
    {
        var paths = Selected();
        if (paths.Length == 0) return;
        var what = paths.Length == 1 ? "„" + Path.GetFileName(paths[0]) + "”" : paths.Length + " elementów";
        if (MessageBox.Show(this, "Usunąć " + what + "? Tego nie można cofnąć.", "Pliki", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _ = DeleteAsync(paths);
    }

    private async Task<bool> DeleteAsync(string[] paths)
    {
        var success = await RunWorkAsync("Usuwanie plików", cancellation =>
        {
            foreach (var path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Directory.Exists(path)) Directory.Delete(path, recursive: (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
                else File.Delete(path);
            }
            return "Usunięto " + paths.Length + " elementów";
        });
        if (success) Audit.Write("files_deleted", paths.Length.ToString());
        return success;
    }

    private void Remember(bool move)
    {
        var paths = Selected();
        if (paths.Length == 0) return;
        clipboard = (paths, move);
        status.Text = "W schowku: " + paths.Length + (move ? " (wytnij)" : " (kopiuj)") + " – wybierz folder i „Wklej”.";
    }

    private void Paste(string? into = null) => _ = PasteAsync(into);

    private async Task<bool> PasteAsync(string? into, bool forceCopy = false)
    {
        if (clipboard is not { } c) return false;
        var destination = into ?? folder;
        var success = await RunWorkAsync(c.Move ? "Przenoszenie plików" : "Kopiowanie plików", cancellation =>
        {
            foreach (var from in c.Paths)
            {
                cancellation.ThrowIfCancellationRequested();
                var to = Free(Path.Combine(destination, Path.GetFileName(from.TrimEnd('\\'))));
                if (Directory.Exists(from))
                {
                    var source = Path.GetFullPath(from).TrimEnd('\\') + "\\";
                    var targetFolder = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
                    if (targetFolder.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Nie można wkleić folderu do niego samego.");
                    if (c.Move && !forceCopy && string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                        Directory.Move(from, to);
                    else
                    {
                        var manifest = c.Move ? new MoveManifest() : null;
                        var partial = to + ".partial-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            Directory.CreateDirectory(partial);
                            File.SetAttributes(partial, File.GetAttributes(partial) | FileAttributes.Hidden);
                            CopyFolder(from, partial, cancellation, stagingRoot: true, manifest: manifest);
                            cancellation.ThrowIfCancellationRequested();
                            Directory.SetLastWriteTimeUtc(partial, Directory.GetLastWriteTimeUtc(from));
                            File.SetAttributes(partial, File.GetAttributes(from) & ~FileAttributes.ReparsePoint);
                            Directory.Move(partial, to);
                        }
                        catch { DeletePartial(partial, directory: true); throw; }
                        if (manifest != null)
                        {
#if KIOSK_SELF_TEST
                            BeforeCrossVolumeSourceCleanupForSmokeTest?.Invoke();
#endif
                            DeleteCopiedFolder(from, to, manifest, cancellation);
                        }
                    }
                }
                else if (c.Move && !forceCopy && string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                    File.Move(from, to);
                else
                {
                    var hash = CopyFile(from, to, cancellation, captureHash: c.Move);
                    if (hash != null)
                    {
#if KIOSK_SELF_TEST
                        BeforeCrossVolumeSourceCleanupForSmokeTest?.Invoke();
#endif
                        DeleteCopiedFile(from, to, hash, cancellation);
                    }
                }
            }
            return (c.Move ? "Przeniesiono " : "Skopiowano ") + c.Paths.Length + " elementów";
        });
        if (success && c.Move && clipboard is { } current && ReferenceEquals(current.Paths, c.Paths)) clipboard = null;
        return success;
    }

#if KIOSK_SELF_TEST
    internal Action? BeforeCrossVolumeSourceCleanupForSmokeTest { get; set; }
    internal Task<bool> PasteForSmokeTestAsync(string[] paths, string destination, bool move, bool forceCopy = false)
    { clipboard = (paths, move); return PasteAsync(destination, forceCopy); }
    internal Task<bool> ZipForSmokeTestAsync(string[] paths) => ZipAsync(paths);
    internal Task<bool> UnzipForSmokeTestAsync(string path) => UnzipAsync(path);
    internal Task<bool> DeleteForSmokeTestAsync(string[] paths) => DeleteAsync(paths);
#endif

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

    private static void CopyFolder(string from, string to, CancellationToken cancellation, bool stagingRoot = false,
        MoveManifest? manifest = null)
    {
        var pending = new Stack<(string From, string To, bool Finish, bool StagingRoot, FileAttributes Attributes)>();
        pending.Push((from, to, false, stagingRoot, default));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Finish)
            {
                Directory.SetLastWriteTimeUtc(item.To, Directory.GetLastWriteTimeUtc(item.From));
                File.SetAttributes(item.To, item.Attributes);
                continue;
            }
            var attributes = File.GetAttributes(item.From);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Nie można kopiować dowiązania: " + item.From);
            Directory.CreateDirectory(item.To);
            if (manifest != null && !item.StagingRoot)
                manifest.Directories.Add(Path.GetRelativePath(from, item.From));
            if (!item.StagingRoot) pending.Push((item.From, item.To, true, false, attributes));
            foreach (var file in Directory.EnumerateFiles(item.From))
            {
                cancellation.ThrowIfCancellationRequested();
                var hash = CopyFile(file, Path.Combine(item.To, Path.GetFileName(file)), cancellation, captureHash: manifest != null);
                if (hash != null) manifest!.Files.Add(Path.GetRelativePath(from, file), hash);
            }
            foreach (var sub in Directory.EnumerateDirectories(item.From))
            { cancellation.ThrowIfCancellationRequested(); pending.Push((sub, Path.Combine(item.To, Path.GetFileName(sub)), false, false, default)); }
        }
    }

    private static byte[]? CopyFile(string from, string to, CancellationToken cancellation, bool captureHash = false)
    {
        cancellation.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(from);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Nie można kopiować dowiązania: " + from);
        var partial = to + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using var hash = captureHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            CopyProgressRoutine progress = (_, _, _, _, _, _, _, _, _) =>
                cancellation.IsCancellationRequested ? ProgressCancel : 0u;
            var copied = CopyFileExW(ExtendedWindowsPath(from), ExtendedWindowsPath(partial),
                progress, IntPtr.Zero, IntPtr.Zero, CopyFileFailIfExists);
            GC.KeepAlive(progress);
            if (!copied)
            {
                cancellation.ThrowIfCancellationRequested();
                throw new IOException("Nie można skopiować pliku: " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            cancellation.ThrowIfCancellationRequested();
            File.SetAttributes(partial, File.GetAttributes(partial) | FileAttributes.Hidden);
            if (hash != null)
            {
                using var copiedStream = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
                CopyStream(copiedStream, Stream.Null, cancellation, hash: hash);
            }
            cancellation.ThrowIfCancellationRequested();
            if ((attributes & FileAttributes.Hidden) == 0)
                File.SetAttributes(partial, File.GetAttributes(partial) & ~FileAttributes.Hidden);
            File.Move(partial, to);
            return hash?.GetHashAndReset();
        }
        catch { DeletePartial(partial); throw; }
    }

    private static string ExtendedWindowsPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static void DeleteCopiedFolder(string from, string to, MoveManifest manifest, CancellationToken cancellation)
    {
        var pending = new Stack<string>();
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(from);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Źródło zmieniło się podczas przenoszenia. Kopia i oryginał zostały zachowane.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellation.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Źródło zmieniło się podczas przenoszenia. Kopia i oryginał zostały zachowane.");
                var relative = Path.GetRelativePath(from, entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!manifest.Directories.Contains(relative))
                        throw new IOException("Źródło zmieniło się podczas przenoszenia. Kopia i oryginał zostały zachowane.");
                    directories.Add(relative);
                    pending.Push(entry);
                }
                else
                {
                    if (!manifest.Files.ContainsKey(relative))
                        throw new IOException("Źródło zmieniło się podczas przenoszenia. Kopia i oryginał zostały zachowane.");
                    files.Add(relative);
                }
            }
        }
        if (files.Count != manifest.Files.Count || directories.Count != manifest.Directories.Count)
            throw new IOException("Źródło zmieniło się podczas przenoszenia. Kopia i oryginał zostały zachowane.");

        foreach (var (relative, hash) in manifest.Files)
            DeleteCopiedFile(Path.Combine(from, relative), Path.Combine(to, relative), hash, cancellation);
        foreach (var relative in manifest.Directories.OrderByDescending(path => path.Length))
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.Delete(Path.Combine(from, relative), recursive: false);
        }
        cancellation.ThrowIfCancellationRequested();
        Directory.Delete(from, recursive: false);
    }

    private static void DeleteCopiedFile(string path, string targetPath, byte[] copiedHash, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Kopia docelowa zmieniła się podczas przenoszenia. Plik źródłowy został zachowany.");
        using var target = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
        using var targetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CopyStream(target, Stream.Null, cancellation, hash: targetHash);
        if (!CryptographicOperations.FixedTimeEquals(targetHash.GetHashAndReset(), copiedHash))
            throw new IOException("Kopia docelowa zmieniła się podczas przenoszenia. Plik źródłowy został zachowany.");
        using var sourceHandle = CreateFileW(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (sourceHandle.IsInvalid)
            throw new IOException("Nie można zablokować pliku źródłowego: " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (!GetFileInformationByHandleEx(sourceHandle, 9, out var sourceInfo, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new IOException("Nie można sprawdzić pliku źródłowego: " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        if ((sourceInfo.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("Plik źródłowy zmienił się podczas przenoszenia. Skopiowane dane pozostały w miejscu docelowym.");
        using var source = new FileStream(sourceHandle, FileAccess.Read, 131072);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CopyStream(source, Stream.Null, cancellation, hash: hash);
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), copiedHash))
            throw new IOException("Plik źródłowy zmienił się podczas przenoszenia. Skopiowane dane pozostały w miejscu docelowym.");
        cancellation.ThrowIfCancellationRequested();
        uint disposition = 0x00000011; // FILE_DISPOSITION_DELETE | FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE
        if (!SetFileInformationByHandle(sourceHandle, 21, ref disposition, sizeof(uint)))
            throw new IOException("Nie można usunąć potwierdzonego pliku źródłowego: " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private static void EnsureArchiveFits(ZipArchive zip, string destination)
    {
        long required = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > long.MaxValue - required)
                throw new InvalidDataException("Archiwum jest za duże do rozpakowania.");
            required += entry.Length;
        }

        long? available = null;
        try
        {
            var root = Path.GetPathRoot(destination);
            if (root != null) available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (available is long free && required > free)
            throw new IOException("Za mało wolnego miejsca na rozpakowanie archiwum.");
    }

    private static uint[] CreateZipCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
            table[i] = value;
        }
        return table;
    }

    private static void CopyStream(Stream source, Stream destination, CancellationToken cancellation,
        long? expectedBytes = null, uint? expectedCrc = null, IncrementalHash? hash = null)
    {
        var size = source is FileStream file ? (int)Math.Clamp(file.Length - file.Position, 4096, 131072) : 131072;
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            long copied = 0;
            uint crc = uint.MaxValue;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = source.Read(buffer);
                if (read == 0) break;
                if (expectedBytes is long expected && read > expected - copied)
                    throw new InvalidDataException("Archiwum zawiera więcej danych niż zadeklarowano.");
                if (expectedCrc != null)
                    foreach (var value in buffer.AsSpan(0, read)) crc = ZipCrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
                hash?.AppendData(buffer.AsSpan(0, read));
                destination.Write(buffer, 0, read);
                copied += read;
            }
            if (expectedBytes is long total && copied != total)
                throw new InvalidDataException("Archiwum zawiera mniej danych niż zadeklarowano.");
            if (expectedCrc is uint correct && ~crc != correct)
                throw new InvalidDataException("Archiwum zawiera uszkodzone dane.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static void DeletePartial(string path, bool directory = false)
    {
        try
        {
            if (directory && Directory.Exists(path))
            {
                var options = new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                    File.SetAttributes(file, FileAttributes.Normal);
                foreach (var sub in Directory.EnumerateDirectories(path, "*", options).OrderByDescending(p => p.Length))
                    File.SetAttributes(sub, FileAttributes.Directory);
                File.SetAttributes(path, FileAttributes.Directory);
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
        catch (IOException ex) { Audit.Write("files_partial_cleanup_failed", path + ": " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Audit.Write("files_partial_cleanup_failed", path + ": " + ex.Message); }
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
        if (disposing) { navigationVersion++; navigation?.Cancel(); placesCancellation.Cancel(); operationStop.Cancel(); icons.Dispose(); }
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
    private const uint CopyFileFailIfExists = 1, ProgressCancel = 1;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CopyProgressRoutine(long total, long transferred, long streamSize, long streamTransferred,
        uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);
    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFileExW(string source, string destination, CopyProgressRoutine progress,
        IntPtr data, IntPtr cancel, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref uint info, uint size);
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
