using System.Diagnostics;

namespace Kiosk.Client;

internal static class CardPinLifecycleTests
{
    internal static async Task RunAsync()
    {
        var marker = Path.Combine(AppContext.BaseDirectory, "card-pin-helper-" + Guid.NewGuid().ToString("N") + ".pid");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("--command-child");
        start.ArgumentList.Add(marker);
        using var process = Process.Start(start)!;
        try
        {
            using var cancel = new CancellationTokenSource();
            var waiting = CardPin.WaitForHelperAsync(process, cancel.Token);
            for (int attempt = 0; attempt < 250 && !File.Exists(marker); attempt++) await Task.Delay(20);
            if (!File.Exists(marker) || process.HasExited)
                throw new InvalidOperationException("PIN helper lifecycle fixture did not start.");
            cancel.Cancel();
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            if (!process.HasExited)
                throw new InvalidOperationException("Cancelled PIN helper remained alive.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (File.Exists(marker)) File.Delete(marker);
        }
    }
}
