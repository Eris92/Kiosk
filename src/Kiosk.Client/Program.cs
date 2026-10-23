using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kiosk.Client;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The Windows service has no desktop; dispatch it before WinForms setup.
        if (args.Length == 1 && args[0] == CardService.Argument) return CardService.Run();
        ApplicationConfiguration.Initialize();
        if (args.Length == 4 && args[0] == CardPin.Argument && long.TryParse(args[2], out var owner))
            return CardPin.Run(args[1], new IntPtr(owner), args[3]);
        if (args.Length == 2 && args[0] == RdpUserHost.Argument)
            return RdpUserHost.Run(args[1]);
#if KIOSK_SELF_TEST
        if (SelfTestEntry.TryRun(args, out var testResult)) return testResult;
#endif
        try
        {
            var path = args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "client.json") : Path.GetFullPath(args[0]);
            var config = Configuration.Load(path);
            KioskTheme.SetAccent(config.AccentColor);
            using var instance = new Mutex(true, "Local\\Kiosk.Client", out var first);
            // "Uruchom ponownie": the previous copy may still be closing — wait for it a moment.
            if (!first)
            {
                try { first = instance.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first) throw new InvalidOperationException("Kiosk is already running in this session.");
            Application.Run(new KioskForm(config, path));
            return 0;
        }
        catch (Exception ex)
        {
            Audit.Write("startup_error", ex.Message);
            MessageBox.Show(ex.Message, "Kiosk startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal static class Audit
{
    internal static void Write(string action, string detail = "")
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"),
                JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, action, detail }) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class RdpHost : AxHost
{
    // MsRdpClient9NotSafeForScripting, provided by Windows mstscax.dll.
    internal RdpHost() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD") { }
    internal dynamic Client => GetOcx()!;
}

