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
        using var third = new ApplicationView(scope);
        surface.Controls.Add(first); surface.Controls.Add(second); surface.Controls.Add(third);
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
        ShowView(third);
        await third.StartAsync(entry).WaitAsync(TimeSpan.FromSeconds(20));
        AppNative.GetWindowThreadProcessId(third.HostedWindow, out var thirdPid);
        if (thirdPid != pid || third.HostedWindow == second.HostedWindow || AppNative.GetParent(third.HostedWindow) != third.Handle)
            throw new InvalidOperationException("A new window did not open after the first hosted window closed.");

        // The same fixture executable under a different path asks the running copy for a
        // window. It must not be adopted as if it belonged to this configured program.
        var alias = Path.Combine(AppContext.BaseDirectory, "Kiosk.Alias." + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Environment.ProcessPath!, alias);
        var beforeUnrelatedLaunch = TopLevelWindows(pid);
        try
        {
            using var unrelated = new ApplicationView(scope);
            surface.Controls.Add(unrelated);
            bool rejected = false;
            try { await unrelated.StartAsync(entry with { Path = alias }).WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("przekazał pracę", StringComparison.Ordinal)) { rejected = true; }
            var newWindows = TopLevelWindows(pid).Except(beforeUnrelatedLaunch).ToArray();
            if (!rejected || newWindows.Length == 0)
                throw new InvalidOperationException("A different program was treated as an owned same-program handover.");
            foreach (var window in newWindows) AppNative.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            try { File.Delete(alias); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        second.Dispose();
        await Task.Delay(300);
        if (process.HasExited || !AppNative.IsWindow(third.HostedWindow))
            throw new InvalidOperationException("Closing the second window killed the third window.");
        third.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        ShowView(desktop);

        static HashSet<IntPtr> TopLevelWindows(uint processId)
        {
            var windows = new HashSet<IntPtr>();
            AppNative.EnumWindows((window, _) =>
            {
                AppNative.GetWindowThreadProcessId(window, out var pid);
                if (pid == processId) windows.Add(window);
                return true;
            }, IntPtr.Zero);
            return windows;
        }
    }
}
