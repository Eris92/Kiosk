namespace Kiosk.Client;

internal static class BrowserLifecycleTests
{
    internal static async Task RunAsync(Form owner)
    {
        using (var browser = new BrowserView())
        {
            owner.Controls.Add(browser);
            browser.BringToFront();
            var first = browser.InitializeAsync("about:blank");
            var second = browser.InitializeAsync("about:blank");
            if (!ReferenceEquals(first, second)) throw new InvalidOperationException("Browser startup was allocated twice.");
            await first.WaitAsync(TimeSpan.FromSeconds(25));
            if (browser.TabCount != 1) throw new InvalidOperationException("Repeated browser initialization created duplicate tabs.");

            var original = browser.Web;
            var opening = browser.OpenTabAsync();
            browser.CloseActiveTab();
            await opening.WaitAsync(TimeSpan.FromSeconds(20));
            if (browser.TabCount != 1 || browser.Web != original)
                throw new InvalidOperationException("Closing a starting tab damaged the remaining browser tab.");

            opening = browser.OpenTabAsync();
            browser.Dispose();
            await opening.WaitAsync(TimeSpan.FromSeconds(20));
        }

        using (var starting = new BrowserView())
        {
            owner.Controls.Add(starting);
            var initializing = starting.InitializeAsync("about:blank");
            starting.Dispose();
            await initializing.WaitAsync(TimeSpan.FromSeconds(25));
            if (starting.Controls.Count != 0) throw new InvalidOperationException("Browser startup added controls after disposal.");
        }

        using var idle = new IdleScreen(Screen.PrimaryScreen ?? throw new InvalidOperationException("Missing primary screen."),
            new ScreenContent { Url = "https://example.invalid" }, 99);
        _ = idle.Handle; // Do not show it: this test must not start an idle web page or touch its stored profile.
        const long noActivate = 0x08000000;
        if ((AppNative.GetWindowLongPtr(idle.Handle, -20).ToInt64() & noActivate) == 0)
            throw new InvalidOperationException("View-only screen can take focus.");
        idle.Interactive = true;
        if ((AppNative.GetWindowLongPtr(idle.Handle, -20).ToInt64() & noActivate) != 0)
            throw new InvalidOperationException("Administrator cannot focus the screen page.");
        idle.Interactive = false;
        if ((AppNative.GetWindowLongPtr(idle.Handle, -20).ToInt64() & noActivate) == 0)
            throw new InvalidOperationException("View-only screen remained interactive.");

        // Only marked, inactive GUID profiles belong to the cleanup routine. A profile
        // locked by another live browser must survive until that browser releases it.
        var root = Path.Combine(Path.GetTempPath(), "KioskBrowserCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stale = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var active = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var unrelated = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(stale, BrowserView.ProfileMarkerName), BrowserView.ProfileMarkerText);
        var activeMarker = Path.Combine(active, BrowserView.ProfileMarkerName);
        File.WriteAllText(activeMarker, BrowserView.ProfileMarkerText);
        using (var lockFile = new FileStream(activeMarker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            BrowserView.CleanupAbandonedProfiles(root);
            if (Directory.Exists(stale) || !Directory.Exists(active) || !Directory.Exists(unrelated))
                throw new InvalidOperationException("Browser cleanup removed a live or unowned profile, or left an orphan.");
        }
        BrowserView.CleanupAbandonedProfiles(root);
        if (Directory.Exists(active)) throw new InvalidOperationException("Browser cleanup did not remove the released profile.");
        Directory.Delete(unrelated);
        Directory.Delete(root);
    }
}
