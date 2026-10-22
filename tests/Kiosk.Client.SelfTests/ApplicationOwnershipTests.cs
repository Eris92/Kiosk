namespace Kiosk.Client;

internal sealed partial class KioskForm
{
    internal async Task ApplicationOwnershipTestsAsync()
    {
        var scope = new object();
        var entry = new ApplicationEntry
        {
            Name = "Shared process fixture", Path = Environment.ProcessPath!,
            Arguments = "--multi-window-fixture kiosk-test-" + Guid.NewGuid().ToString("N")
        };
        using var first = new ApplicationView(scope);
        using var second = new ApplicationView(scope);
        surface.Controls.Add(first); surface.Controls.Add(second);
        ShowView(first);
        await first.StartAsync(entry).WaitAsync(TimeSpan.FromSeconds(20));
        AppNative.GetWindowThreadProcessId(first.HostedWindow, out var pid);
        using var process = System.Diagnostics.Process.GetProcessById((int)pid);
        ShowView(second);
        await second.StartAsync(entry).WaitAsync(TimeSpan.FromSeconds(20));
        AppNative.GetWindowThreadProcessId(second.HostedWindow, out var secondPid);
        if (pid != secondPid || second.HostedWindow == first.HostedWindow || AppNative.GetParent(second.HostedWindow) != second.Handle)
            throw new InvalidOperationException("Same-session handover did not embed the second owned window.");
        first.Dispose();
        await Task.Delay(300);
        if (process.HasExited || !AppNative.IsWindow(second.HostedWindow))
            throw new InvalidOperationException("Closing one window killed another window of the same application.");
        second.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        ShowView(desktop);
    }
}
