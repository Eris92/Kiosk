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
    private string folder = "";
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
        Tool("Wklej", Paste, 90);
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
        places.BackColor = KioskTheme.Taskbar; places.ForeColor = KioskTheme.Text;
        places.SelectedIndexChanged += (_, _) => { if (places.SelectedIndex >= 0) Navigate(placeList[places.SelectedIndex].Path); };
        status.BackColor = KioskTheme.Taskbar; status.ForeColor = KioskTheme.Muted;

        Controls.Add(list); Controls.Add(places); Controls.Add(status); Controls.Add(toolbar);
        FillPlaces();
        if (placeList.Count > 0) Navigate(placeList[0].Path);
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
        if (Math.Abs(factor - 1) < 0.01) return;
        Scale(new SizeF(factor, factor));
        foreach (ColumnHeader column in list.Columns) column.Width = (int)(column.Width * factor);
        icons.ImageSize = new Size((int)(16 * factor), (int)(16 * factor));
        icons.Images.Clear();
        if (folder.Length > 0) Navigate(folder, remember: false);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? app, string? idList);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private T? As<T>(Func<T?> work) => (T?)runAs(() => work());

    private void FillPlaces()
    {
        var home = profile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void Add(string name, string? path) { if (!string.IsNullOrEmpty(path) && As(() => Directory.Exists(path))) placeList.Add((name, path)); }
        // Known folders of that person: under their profile (OneDrive may hold Pulpit / Dokumenty / Obrazy).
        var oneDrive = As(() => Directory.Exists(home) ? Directory.GetDirectories(home, "OneDrive*") : []) ?? [];
        string Known(string name) => oneDrive.Select(o => Path.Combine(o, name)).FirstOrDefault(p => As(() => Directory.Exists(p))) ?? Path.Combine(home, name);
        Add("Pulpit", Known("Desktop"));
        Add("Dokumenty", Known("Documents"));
        Add("Pobrane", Path.Combine(home, "Downloads"));
        Add("Obrazy", Known("Pictures"));
        foreach (var o in oneDrive) Add(Path.GetFileName(o), o);
        Add("Folder osobisty", home);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network) Add((drive.VolumeLabel.Length > 0 ? drive.VolumeLabel + " " : "") + "(" + drive.Name.TrimEnd('\\') + ")", drive.Name); }
            catch (IOException) { }
        }
        places.Items.AddRange(placeList.Select(p => (object)p.Name).ToArray());
    }

    private void Navigate(string path, bool remember = true)
    {
        try
        {
            var entries = As(() =>
            {
                var info = new DirectoryInfo(path);
                if (!info.Exists) throw new DirectoryNotFoundException("Nie ma folderu " + path);
                return info.EnumerateFileSystemInfos().Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .OrderBy(i => i is FileInfo).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            })!;
            if (remember && folder.Length > 0 && !string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)) back.Push(folder);
            folder = path;
            address.Text = path;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var entry in entries)
            {
                bool isFolder = entry is DirectoryInfo;
                var item = new ListViewItem(entry.Name) { Tag = entry.FullName, ImageKey = IconKey(entry.FullName, isFolder) };
                item.SubItems.Add(entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(isFolder ? "Folder" : TypeName(entry.FullName));
                item.SubItems.Add(entry is FileInfo f ? Size(f.Length) : "");
                list.Items.Add(item);
            }
            list.EndUpdate();
            status.Text = entries.Count + " elementów" + (clipboard is { } c ? " · w schowku: " + c.Paths.Length + (c.Move ? " (wytnij)" : " (kopiuj)") : "");
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

    private void Paste()
    {
        if (clipboard is not { } c) return;
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
        Navigate(folder, remember: false);
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

    private static string Size(long bytes) =>
        bytes < 1024 ? bytes + " B" : bytes < 1 << 20 ? (bytes / 1024.0).ToString("0") + " KB" : bytes < 1L << 30 ? (bytes / 1048576.0).ToString("0.0") + " MB" : (bytes / 1073741824.0).ToString("0.0") + " GB";

    // ------------------------------------------------------------------ icons and types (Windows' own, by extension)

    private string IconKey(string path, bool isFolder)
    {
        var key = isFolder ? "<folder>" : Path.GetExtension(path).ToLowerInvariant() is var ext && ext is ".exe" or ".lnk" or ".ico" ? path : Path.GetExtension(path).ToLowerInvariant();
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

    private static string TypeName(string path)
    {
        var info = new ShFileInfo();
        return SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero ? info.TypeName : Path.GetExtension(path);
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
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return (path, "");
        if (extension.Length == 0) return null;
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

    private static readonly string[] Viewable = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".pdf", ".txt", ".log", ".csv", ".xml", ".json"];

    private static (string Program, string Arguments)? Viewer(string path)
    {
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        return Viewable.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(edge) ? (edge, "--single-argument \"" + path + "\"") : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string association, string? extra, System.Text.StringBuilder output, ref uint size);
}
