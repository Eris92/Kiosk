using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// One of the other screens while someone works: shows one of their Kiosk windows (an application, the browser,
/// a remote desktop) across the whole screen. Borderless, owned by the Kiosk so it never hides behind it.
/// </summary>
internal sealed class ScreenHost : Form
{
    internal Screen Target { get; }
    internal Control? View { get; private set; }

    internal ScreenHost(Screen screen)
    {
        Target = screen;
        Text = "Kiosk";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        ShowInTaskbar = false;
        BackColor = KioskTheme.Background;
    }

    internal void Host(Control view)
    {
        view.Parent?.Controls.Remove(view);
        view.Dock = DockStyle.Fill;
        Controls.Add(view);
        view.Visible = true;
        View = view;
    }

    /// <summary>Takes the view off this screen (the caller puts it back on the main screen or disposes it).</summary>
    internal Control? Release()
    {
        var view = View;
        if (view != null) Controls.Remove(view);
        View = null;
        return view;
    }
}

/// <summary>Which screen each of a person's windows was put on (by window key), so it opens there again.</summary>
internal static class ScreenLayouts
{
    private static string FileFor(string personId)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Layouts");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(personId))) + ".json");
    }

    internal static Dictionary<string, int> Load(string personId)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FileFor(personId))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    internal static void Save(string personId, Dictionary<string, int> layout)
    {
        try { File.WriteAllText(FileFor(personId), JsonSerializer.Serialize(layout)); }
        catch (IOException ex) { Audit.Write("layout_save_error", ex.Message); }
    }
}
