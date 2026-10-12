using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// Ctrl+Alt+K: Konfiguracja for an administrator in anyone's session. Windows asks for an administrator (UAC, card
/// or password as the PC allows); only the settings window runs elevated, saves the shared configuration itself and
/// closes. The Kiosk keeps running as the signed-in person and reloads the saved configuration.
/// </summary>
internal static class AdminSettings
{
    internal const string Argument = "--settings";
    private const int HotkeyId = 0x4B53;            // "KS"
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    /// <summary>Works whatever has the keyboard (browser, hosted program), as long as it is this Windows session.</summary>
    internal static void RegisterHotkey(IntPtr window) => RegisterHotKey(window, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.K);
    internal static void UnregisterHotkey(IntPtr window) => UnregisterHotKey(window, HotkeyId);
    internal static bool IsHotkey(Message m) => m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId;

    /// <summary>Starts the elevated settings window; <paramref name="saved"/> runs on the caller's thread after a save.</summary>
    internal static void Open(Control owner, string configPath, Action saved)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
                Arguments = "\"" + Argument + "\" \"" + configPath + "\""
            })!;
        }
        catch (System.ComponentModel.Win32Exception) { Audit.Write("admin_settings_cancelled"); return; }   // UAC cancelled
        Audit.Write("admin_settings_opened");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;
            process.Dispose();
            if (code == 0 && !owner.IsDisposed) owner.BeginInvoke(saved);
        };
    }

    /// <summary>Kiosk.Client.exe --settings &lt;configuration&gt; (elevated): exit code 0 = saved, 1 = cancelled.</summary>
    internal static int Run(string configPath)
    {
        var config = Configuration.Load(configPath);
        KioskTheme.SetAccent(config.AccentColor);
        var result = 1;
        using var form = new Form
        {
            Text = "Konfiguracja Kiosku (administrator)", FormBorderStyle = FormBorderStyle.None,
            WindowState = FormWindowState.Maximized, TopMost = true, ShowInTaskbar = false
        };
        var view = new SettingsView(config);
        view.SaveRequested += updated =>
        {
            Configuration.Save(configPath, updated);
            Audit.Write("settings_saved", "administrator");
            result = 0;
            form.Close();
        };
        view.CancelRequested += form.Close;
        form.Controls.Add(view);
        Application.Run(form);
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
