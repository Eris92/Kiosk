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
    private const int UpdateHotkeyId = 0x4B55;      // "KU"
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    /// <summary>Works whatever has the keyboard (browser, hosted program), as long as it is this Windows session.</summary>
    internal static void RegisterHotkey(IntPtr window)
    {
        // Fails when another program in this session already owns Ctrl+Alt+K (graphics or keyboard utilities).
        if (!RegisterHotKey(window, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.K))
            Audit.Write("admin_hotkey_unavailable", "Win32 " + Marshal.GetLastWin32Error());
        // Ctrl+Alt+U: anyone may install the latest official release (the Kiosk Card service copies it, no administrator).
        if (!RegisterHotKey(window, UpdateHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.U))
            Audit.Write("update_hotkey_unavailable", "Win32 " + Marshal.GetLastWin32Error());
    }
    internal static void UnregisterHotkey(IntPtr window) { UnregisterHotKey(window, HotkeyId); UnregisterHotKey(window, UpdateHotkeyId); }
    internal static bool IsHotkey(Message m) => m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId;
    internal static bool IsUpdateHotkey(Message m) => m.Msg == WM_HOTKEY && m.WParam.ToInt32() == UpdateHotkeyId;

    /// <summary>Exit code of the settings window when Aktualizacja was pressed: the Kiosk that owns the files installs it.</summary>
    internal const int UpdateExitCode = 2;

    /// <summary>Starts the elevated settings window; <paramref name="saved"/> runs on the caller's thread after a save,
    /// <paramref name="update"/> after "Sprawdź i zaktualizuj".</summary>
    internal static void Open(Control owner, string configPath, Action saved, Action update)
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
        catch (System.ComponentModel.Win32Exception ex)
        {
            Audit.Write("admin_settings_cancelled", "Win32 " + ex.NativeErrorCode);
            // 1223: someone pressed No / closed the prompt. Anything else: Windows refused to ask (e.g. Intune
            // "Automatically deny elevation requests" for standard users) – say so instead of doing nothing.
            if (ex.NativeErrorCode != 1223 && !owner.IsDisposed)
                MessageBox.Show(owner, "Windows nie pozwolił poprosić o administratora (" + ex.Message + ").\n" +
                    "Na tym komputerze zwykły użytkownik nie dostaje okna zgody (UAC). Przyłóż kartę administratora " +
                    "albo zmień konfigurację z konta administratora.", "Konfiguracja Kiosku", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Audit.Write("admin_settings_opened");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;
            process.Dispose();
            if (owner.IsDisposed) return;
            if (code == 0) owner.BeginInvoke(saved);
            else if (code == UpdateExitCode) owner.BeginInvoke(update);
        };
    }

    /// <summary>Kiosk.Client.exe --settings &lt;configuration&gt; (elevated): exit code 0 = saved, 1 = cancelled, 2 = update.</summary>
    internal static int Run(string configPath)
    {
        var loaded = CentralConfig.Load(configPath);
        var config = loaded.Effective;
        KioskTheme.SetAccent(config.AccentColor);
        var result = 1;
        using var form = new Form
        {
            Text = "Konfiguracja Kiosku (administrator)", FormBorderStyle = FormBorderStyle.None,
            WindowState = FormWindowState.Maximized, TopMost = true, ShowInTaskbar = false
        };
        var view = new SettingsView(config) { UpdateByKiosk = true };
        view.SaveRequested += updated =>
        {
            // From a server's configuration only its address is saved here; the rest is edited on the server.
            Configuration.Save(configPath, loaded.Central ? loaded.Local with { ConfigSource = updated.ConfigSource } : updated, new CentralConfig.Secrets(view.AccountChange, view.AccountsKey, view.NewsAccountChange));
            Audit.Write("settings_saved", "administrator");
            result = 0;
            form.Close();
        };
        view.CancelRequested += form.Close;
        // The running Kiosk holds the program files: it closes and installs the update itself.
        view.UpdateRequested += _ => { result = UpdateExitCode; form.Close(); };
        form.Controls.Add(view);
        Application.Run(form);
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
