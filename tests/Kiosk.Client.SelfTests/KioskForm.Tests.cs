using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kiosk.Client;

internal sealed partial class KioskForm
{
    // ---------------------------------------------------------------- self-test

    internal void SmokeTest()
    {
        timer.Stop();
        if (Marshal.SizeOf<Native.ReaderState>() != 64 ||
            Marshal.OffsetOf<Native.ReaderState>(nameof(Native.ReaderState.EventState)).ToInt32() != 20)
            throw new InvalidOperationException("Unexpected PCSC x64 structure layout.");
        if (!CardReader.IsUsableCard(0x122) || !CardReader.IsUsableCard(0xA2) ||
            !CardReader.IsUsableCard(0x422) || CardReader.IsUsableCard(0x222) ||
            CardReader.IsUsableCard(0x12) || CardReader.IsUsableCard(0x28))
            throw new InvalidOperationException("PCSC card-state regression test failed.");
        var physicalAndVirtual = new[]
        {
            new Native.ReaderState { Reader = "ACS", EventState = 0x422, Atr = new byte[36] },
            new Native.ReaderState { Reader = "UICC", EventState = 0x22, Atr = new byte[36] },
            new Native.ReaderState { Reader = "Windows Hello", EventState = 0x422, Atr = new byte[36] }
        };
        if (CardReader.MonitoredReaders(physicalAndVirtual, "ACS").Count(s => CardReader.IsUsableCard(s.EventState)) != 1 ||
            CardReader.MonitoredReaders(physicalAndVirtual, "").Count(s => CardReader.IsUsableCard(s.EventState)) != 3 ||
            CardReader.MonitoredReaders(physicalAndVirtual, "Missing").Length != 0)
            throw new InvalidOperationException("PCSC reader-selection regression test failed.");
        physicalAndVirtual[0].EventState = 0x12;
        if (CardReader.MonitoredReaders(physicalAndVirtual, "ACS").Any(s => CardReader.IsUsableCard(s.EventState)))
            throw new InvalidOperationException("Virtual reader masked physical card removal.");

        // Older single-server files migrate to the connection list and the lock / retention timers.
        var legacy = JsonSerializer.Deserialize<Config>("{\"Server\":\"rds.example.local\",\"Port\":3390,\"IdleSeconds\":120}")!.Migrate();
        legacy.Validate();
        if (legacy.RdpConnections.Single().Server != "rds.example.local" || legacy.RdpConnections[0].Port != 3390 ||
            legacy.LockAfterSeconds != 120 || legacy.ChangeUserAfterSeconds != 600 || legacy.Server != null ||
            !legacy.Applications.Single().IsBuiltInNotes || legacy.ConfigVersion != Config.CurrentVersion || legacy.Migrate().Applications.Length != 1)
            throw new InvalidOperationException("Legacy configuration migration failed.");
        foreach (var invalid in new[]
        {
            legacy with { ChangeUserAfterSeconds = -1 },
            legacy with { LockAfterSeconds = 5 },
            legacy with { AutoConnectRdpName = "Missing" },
            legacy with { UnlockMethod = (UnlockMethod)42 },
            legacy with { AccentColor = "zielony" },
            legacy with { RdpConnections = [legacy.RdpConnections[0], legacy.RdpConnections[0]] },
            legacy with { RdpConnections = [new RdpConnection { Name = "E", Server = "10.0.0.1", Authentication = RdpAuthentication.EntraId }] }
        })
        {
            try { invalid.Validate(); throw new Exception("Invalid configuration was accepted."); }
            catch (InvalidDataException) { }
        }

        // A black accent stays visible: lines switch to light text, labels on it turn white.
        KioskTheme.SetAccent("#000000");
        if (KioskTheme.AccentLine != KioskTheme.Text || KioskTheme.OnAccent.ToArgb() != Color.White.ToArgb()) throw new InvalidOperationException("Black accent is not readable.");
        KioskTheme.SetAccent("#FACC15");
        if (KioskTheme.OnAccent != KioskTheme.Background || KioskTheme.AccentLine != KioskTheme.Accent) throw new InvalidOperationException("Light accent is not readable.");
        KioskTheme.SetAccent(config.AccentColor);

        var sites = new SitePolicy(true, [new Bookmark { Name = "Portal", Url = "https://www.portal.example.pl/start" }], ["login.microsoftonline.com"]);
        if (!sites.Allows("https://portal.example.pl/") || !sites.Allows("https://app.portal.example.pl/x") ||
            !sites.Allows("https://login.microsoftonline.com/common") || sites.Allows("https://www.google.com/") ||
            sites.Allows("https://evilportal.example.pl/") || sites.Allows("file:///C:/") || !new SitePolicy(false, [], []).Allows("https://any.example/"))
            throw new InvalidOperationException("Browser site policy failed.");
        using (var restricted = new BrowserView([new Bookmark { Name = "Portal", Url = "https://portal.example.pl/" }], sites))
            if (restricted.Controls.OfType<FlowLayoutPanel>().Any() || restricted.Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<TextBox>()).Any())
                throw new InvalidOperationException("Address bar visible in bookmarks-only mode.");

        _ = Native.IdleMilliseconds();
        // Validate every dynamic COM setting for each sign-in mode without initiating a connection.
        foreach (var authentication in Enum.GetValues<RdpAuthentication>())
        foreach (var prompt in new[] { true, false })
        {
            var host = CreateRdp(new RdpConnection { Name = "Test", Server = "pc.example.local", Authentication = authentication }, prompt);
            try { if ((int)host.Client.Connected != 0) throw new InvalidOperationException("Unexpected connection in smoke test."); }
            finally { surface.Controls.Remove(host); host.Dispose(); }
        }
    }

    internal async Task EmbeddedSmokeTestAsync()
    {
        var alice = new CardIdentity("cert:A", "Alicja Test");
        var bob = new CardIdentity("cert:B", "Bartek Test");
        identifyCard = () => current?.Identity;
        cardPresent = () => true;
        config = config with { UnlockMethod = UnlockMethod.Card, ChangeUserAfterSeconds = 600 };

        CardInserted(alice);
        var aliceSession = current;
        if (aliceSession?.Identity != alice || aliceSession.Locked) throw new InvalidOperationException("Card did not open a session.");
        ShowNotes();
        var noteView = aliceSession.Notes!;
        const string sample = "Notatka testowa: zażółć gęślą jaźń\r\nDruga linia";
        noteView.Editor.Text = sample;
        var path = Path.Combine(AppContext.BaseDirectory, "self-test-note.txt");
        noteView.SaveFile(path);
        noteView.Editor.Clear();
        noteView.LoadFile(path);
        if (noteView.Editor.Text != sample || noteView.Dirty) throw new InvalidOperationException("Notes file round-trip failed.");

        var browserView = new BrowserView([new Bookmark { Name = "Test bookmark", Url = "https://bookmark.invalid/" }]);
        aliceSession.Browser = browserView;
        AddWindow(aliceSession, BrowserKey, "Przeglądarka", browserView);
        await browserView.InitializeAsync("about:blank").WaitAsync(TimeSpan.FromSeconds(25));
        var navigated = new TaskCompletionSource<bool>();
        string navigationUri = "";
        browserView.Web.CoreWebView2.NavigationStarting += (_, e) => navigationUri = e.Uri;
        browserView.Web.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) navigated.TrySetResult(true);
            else navigated.TrySetException(new InvalidOperationException("Embedded navigation failed: " + e.WebErrorStatus + " / " + navigationUri));
        };
        browserView.Web.NavigateToString("<html><head><title>Kiosk smoke test</title></head><body><h1>Przeglądarka wewnątrz Kiosku</h1></body></html>");
        await navigated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var text = await browserView.Web.ExecuteScriptAsync("document.body.innerText");
        if (!text.Contains("Kiosku") || !browserView.Web.CoreWebView2.Profile.IsInPrivateModeEnabled)
            throw new InvalidOperationException("Embedded page or InPrivate profile failed.");
        // Tabs: a second tab shares the same InPrivate profile (one sign-in), closing it returns to the first.
        var firstPage = browserView.Web;
        await browserView.OpenTabAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if (browserView.TabCount != 2 || browserView.Web == firstPage ||
            browserView.Web.CoreWebView2.Profile.ProfileName != firstPage.CoreWebView2.Profile.ProfileName || !browserView.Web.CoreWebView2.Profile.IsInPrivateModeEnabled)
            throw new InvalidOperationException("Browser tab failed.");
        browserView.CloseActiveTab();
        if (browserView.TabCount != 1 || browserView.Web != firstPage) throw new InvalidOperationException("Closing a tab failed.");

        // Taskbar: one button per window; clicking switches without losing state.
        if (taskbar.TaskCount != 2) throw new InvalidOperationException("Taskbar does not list open windows.");
        ActivateWindow(NotesKey);
        if (!noteView.Visible || browserView.Visible || noteView.Editor.Text != sample)
            throw new InvalidOperationException("Notes switch lost state.");
        ActivateWindow(NotesKey);
        if (!desktop.Visible || noteView.Visible) throw new InvalidOperationException("Clicking the active window did not show the desktop.");
        ActivateWindow(BrowserKey);
        if (!browserView.Visible || noteView.Visible || browserView.Parent != surface)
            throw new InvalidOperationException("Browser view is not embedded.");
        var bookmarkNavigation = new TaskCompletionSource<string>();
        browserView.Web.CoreWebView2.NavigationStarting += (_, e) => { e.Cancel = true; bookmarkNavigation.TrySetResult(e.Uri); };
        var bookmarkButton = browserView.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single();
        bookmarkButton.PerformClick();
        if (await bookmarkNavigation.Task.WaitAsync(TimeSpan.FromSeconds(5)) != "https://bookmark.invalid/")
            throw new InvalidOperationException("Bookmark navigation failed.");

        // Konfiguracja is for admins: a listed card, a Windows administrator, or anyone while the list is empty in test mode.
        var adminTest = config with { TestMode = false, AdminCards = [new AdminCard { Name = "Alicja", Id = alice.Id }] };
        var bobSession = new UserSession { Identity = bob };
        if (!IsAdmin(aliceSession, adminTest) || IsAdmin(bobSession, adminTest) ||
            !IsAdmin(bobSession, config with { TestMode = true, AdminCards = [] }) || IsAdmin(bobSession, config with { TestMode = false, AdminCards = [] }))
            throw new InvalidOperationException("Admin card check failed.");
        bobSession.IsWindowsAdmin = true;
        if (!IsAdmin(bobSession, adminTest) || IsAdmin(bobSession, adminTest with { AllowWindowsAdministrators = false }))
            throw new InvalidOperationException("Windows administrator check failed.");

        menu.Populate("test", BuildMenu());
        if (menu.ItemCount < config.RdpConnections.Length + 3) throw new InvalidOperationException("Menu is incomplete.");

        // In-app lock hides every window; resume with the same card restores the last one.
        Lock("self_test_lock", 0);
        if (!aliceSession.Locked || !desktop.Visible || browserView.Visible || taskbar.TaskCount != 0 || taskbar.MenuButton.Enabled)
            throw new InvalidOperationException("Lock did not hide the session.");
        await VerifyAndUnlockAsync();
        if (aliceSession.Locked || !browserView.Visible || taskbar.TaskCount != 2) throw new InvalidOperationException("Resume did not restore the session.");

        // Shared PC: removing the card keeps the session; another card gets its own empty session.
        CardRemoved();
        if (current != null || browserView.IsDisposed || noteView.IsDisposed || browserView.Visible || !desktop.Visible)
            throw new InvalidOperationException("Card removal did not park the session.");
        CardInserted(bob);
        if (current?.Identity != bob || current.Windows.Count != 0 || taskbar.TaskCount != 0 || sessions.Count != 2 || noteView.Visible)
            throw new InvalidOperationException("Second card did not get its own session.");
        CardRemoved();
        CardInserted(alice);
        if (current != aliceSession || aliceSession.Locked || !browserView.Visible || noteView.Editor.Text != sample || taskbar.TaskCount != 2)
            throw new InvalidOperationException("Returning card did not restore its session.");

        // Parked sessions expire after the retention time.
        sessions[bob.Id].InactiveSince = DateTime.UtcNow.AddHours(-1);
        ExpireSessions();
        if (sessions.ContainsKey(bob.Id) || sessions.Count != 1) throw new InvalidOperationException("Parked session did not expire.");

        // Logout closes only this person's session and asks for the card to be removed.
        Logout("self_test_logout");
        if (!noteView.IsDisposed || !browserView.IsDisposed || sessions.Count != 0 || current != null || !desktop.Visible || taskbar.MenuButton.Enabled)
            throw new InvalidOperationException("Session cleanup failed.");
        requireRemoval = false;
        CardInserted(alice);
        ShowNotes();
        if (current!.Notes!.Editor.TextLength != 0) throw new InvalidOperationException("Notes leaked to the next session.");
        CloseAllSessions("fresh_notes_smoke_test");
        identifyCard = IdentifyPresentCard;
        cardPresent = () => reader.Snapshot(config.ReaderName) != null;
    }

    internal async Task ApplicationSmokeTestAsync()
    {
        // Card PIN unlock runs in a child process; a missing reader must fail cleanly, without a dialog.
        var pin = await CardPin.VerifyAsync("Kiosk self-test missing reader", Handle, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        if (pin.Code != CardPin.NoKey || pin.Message.Length == 0) throw new InvalidOperationException("Card PIN check: " + pin.Code + " " + pin.Message);
        if (CardPin.ReadIdentity("Kiosk self-test missing reader") != null) throw new InvalidOperationException("Identity read from a missing reader.");

        var entry = new ApplicationEntry { Name = "Test", Path = Environment.ProcessPath!, Arguments = "--app-host-fixture" };
        var testConfig = config with
        {
            Applications = [entry],
            Bookmarks = [new Bookmark { Name = "Google", Url = "https://www.google.com" }],
            RdpConnections = [new RdpConnection { Name = "Biuro", Server = "rds.example.local" },
                new RdpConnection { Name = "Entra", Server = "pc.example.local", Authentication = RdpAuthentication.EntraId, UserName = "jan@example.com" }],
            AutoConnectRdpName = "Biuro", RequireCredentialPrompt = false, UnlockMethod = UnlockMethod.CardAndPin,
            ConfigVersion = Config.CurrentVersion, EnableBrowser = false,
            LockAfterSeconds = 60, ChangeUserAfterSeconds = 30, AccentColor = "#3DDC84",
            BrowserOnlyBookmarks = true, BrowserAllowedDomains = ["login.microsoftonline.com"],
            AdminCards = [new AdminCard { Name = "Admin", Id = "cert:ADMIN" }], AllowWindowsAdministrators = false
        };
        var configFile = Path.Combine(AppContext.BaseDirectory, "self-test-config.json");
        Configuration.Save(configFile, testConfig);
        var restored = Configuration.Load(configFile);
        if (restored.Applications.Single() != entry || restored.Bookmarks.Single() != testConfig.Bookmarks.Single() ||
            !restored.RdpConnections.SequenceEqual(testConfig.RdpConnections) || restored.AutoConnectRdpName != "Biuro" ||
            restored.RequireCredentialPrompt || restored.UnlockMethod != UnlockMethod.CardAndPin ||
            restored.LockAfterSeconds != 60 || restored.ChangeUserAfterSeconds != 30 || restored.AccentColor != "#3DDC84" || !restored.BrowserOnlyBookmarks || restored.BrowserAllowedDomains.Single() != "login.microsoftonline.com" || restored.EnableBrowser)
            throw new InvalidOperationException("Configuration round-trip failed.");
        try
        {
            (testConfig with { Bookmarks = [new Bookmark { Name = "Bad", Url = "file:///C:/Windows" }] }).Validate();
            throw new Exception("Invalid bookmark was accepted.");
        }
        catch (InvalidDataException) { }
        using (var settings = new SettingsView(restored))
        {
            surface.Controls.Add(settings);
            var edited = settings.BuildConfig();
            surface.Controls.Remove(settings);
            if (!edited.RdpConnections.SequenceEqual(restored.RdpConnections) || edited.AutoConnectRdpName != "Biuro" ||
                edited.RequireCredentialPrompt || edited.UnlockMethod != UnlockMethod.CardAndPin || edited.LockAfterSeconds != 60 ||
                edited.ChangeUserAfterSeconds != 30 || edited.AccentColor != "#3DDC84" || edited.AdminCards.Single() != restored.AdminCards.Single() || edited.AllowWindowsAdministrators || !edited.BrowserOnlyBookmarks || edited.BrowserAllowedDomains.Single() != "login.microsoftonline.com" || edited.EnableBrowser || edited.ReaderName != restored.ReaderName || !edited.Applications.SequenceEqual(restored.Applications))
                throw new InvalidOperationException("Settings view does not round-trip the configuration.");
        }

        // An existing same-executable window must never be adopted or terminated.
        using var unrelated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.Path, entry.Arguments)
        { UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
        try
        {
            config = config with { UnlockMethod = UnlockMethod.Card };
            CardInserted(new CardIdentity("cert:T", "Test"));
            var session = current!;
            await StartApplicationAsync(session, entry).WaitAsync(TimeSpan.FromSeconds(20));
            var hosted = session.Applications[entry.Name];
            if (hosted.HostedWindow == IntPtr.Zero || AppNative.GetParent(hosted.HostedWindow) != hosted.Handle)
                throw new InvalidOperationException("Application is not embedded.");
            AppNative.GetWindowThreadProcessId(hosted.HostedWindow, out var pid);
            if (pid == unrelated.Id) throw new InvalidOperationException("Adopted an unrelated process.");
            using var owned = System.Diagnostics.Process.GetProcessById((int)pid);
            hosted.Dock = DockStyle.None; hosted.Size = new Size(640, 400);
            if (!AppNative.GetClientRect(hosted.HostedWindow, out var rect) || rect.Right != 640 || rect.Bottom != 400)
                throw new InvalidOperationException("Hosted window resize failed.");
            CardRemoved();
            if (owned.HasExited) throw new InvalidOperationException("Parking a session closed its application.");
            // The application closed (its own × button or exit): its Kiosk window and taskbar button go too.
            owned.Kill(); // Same outcome as the app closing itself: its window is destroyed.
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 40 && session.Applications.Count > 0; i++) { MonitorApplications(); await Task.Delay(100); } // Job accounting lags the exit slightly.
            if (session.Applications.Count != 0 || !hosted.IsDisposed || session.Windows.Count != 0) throw new InvalidOperationException("Closed application left its window behind.");
            CloseAllSessions("application_smoke_test");
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (unrelated.HasExited || !hosted.IsDisposed || sessions.Count != 0)
                throw new InvalidOperationException("Application ownership cleanup failed.");
        }
        finally
        {
            if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
        }

        // Windows shell mode: the signed-in Windows user is the only session, unlocked at once (no card switching).
        var kioskConfig = config;
        config = config with { SessionMode = SessionMode.WindowsShell, AutoConnectRdpName = "" };
        StartWindowsSession();
        if (current == null || current.Locked || !current.Identity.Id.StartsWith("win:") || current.Identity.Name.Length == 0 || sessions.Count != 1)
            throw new InvalidOperationException("Windows shell session failed.");
        _ = WindowsSession.IsDisconnected();
        CloseAllSessions("shell_smoke_test");
        config = kioskConfig;
    }

    internal void FinishSelfTest()
    {
        timer.Stop();
        closeConfirmed = true;
        CloseAllSessions("self_test_finished");
    }
}
