using System.Diagnostics;

namespace Kiosk.ConfigTool;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        var here = AppContext.BaseDirectory.TrimEnd('\\');
        // The Kiosk next to this file (its own build folder), else the installed one.
        var kiosk = new[] { Path.Combine(here, "Kiosk.Client.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kiosk", "Kiosk.Client.exe") }
            .FirstOrDefault(File.Exists);
        if (kiosk == null)
        {
            MessageBox.Show("Nie znaleziono Kiosku (Kiosk.Client.exe) obok tego pliku ani w Program Files\\Kiosk. " +
                "Zainstaluj go na tym komputerze: scripts\\Install-KioskConfig.ps1.", "Konfiguracje Kiosków", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        // Copied next to the configurations: this folder is the main one. Elsewhere the manager asks or remembers.
        var start = new ProcessStartInfo(kiosk) { UseShellExecute = false };
        start.ArgumentList.Add("--configs");
        if (IsConfigRoot(here)) start.ArgumentList.Add(here);
        Process.Start(start)?.Dispose();
        return 0;
    }

    private static bool IsConfigRoot(string folder)
    {
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateDirectories(folder, "*", options).Any(d => File.Exists(Path.Combine(d, "client.json")));
        }
        catch (Exception) { return false; }
    }
}
