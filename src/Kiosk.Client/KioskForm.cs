using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Security.Credentials.UI;

namespace Kiosk.Client;

/// <summary>
/// The kiosk shell for a shared PC: a desktop, one Menu button on the bottom taskbar and embedded
/// windows (remote desktops, browser, notes, applications). Every card gets its own session.
/// Removing the card parks the session (windows keep running, hidden); another card gets its own
/// session at once; reinserting a card brings its session back. Sessions expire after
/// ChangeUserAfterSeconds of inactivity. Nothing here touches the Windows lock screen.
/// </summary>
internal sealed partial class KioskForm : Form
{
    private sealed class RdpSession
    {
        internal required RdpConnection Connection { get; init; }
        internal required RdpHost Host { get; init; }
        internal DateTime ConnectingSince { get; init; }
        internal bool WasConnected { get; set; }
    }

    private sealed record Window(string Key, string Title, Control View);

    private sealed class UserSession
    {
        internal required CardIdentity Identity { get; init; }
        internal List<Window> Windows { get; } = new();
        internal Dictionary<string, RdpSession> Rdp { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, ApplicationView> Applications { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal BrowserView? Browser { get; set; }
        internal NotesView? Notes { get; set; }
        internal Control? LastView { get; set; }
        internal bool Locked { get; set; } = true;
        internal bool Verified { get; set; }
        internal bool AutoConnected { get; set; }
        internal DateTime InactiveSince { get; set; } = DateTime.UtcNow;
        internal string? Notice { get; set; }
        internal bool HasWork => Notes?.Dirty == true || Applications.Count > 0 || Rdp.Count > 0 || CommandJobs.Count > 0;
        /// <summary>True only for the authenticated Windows shell identity, never a claimed certificate UPN.</summary>
        internal bool IsWindowsAdmin { get; set; }
        /// <summary>The person's own Windows / Entra account after "Zaloguj" (SignInAsUser); their programs run as it.</summary>
        internal UserLogon? Logon { get; set; }
        internal string Name => Logon?.DisplayName ?? Identity.Name;
        /// <summary>Which screen each window goes on (window key → 1 = main, 2… = others); remembered per card.</summary>
        internal Dictionary<string, int>? Layout { get; set; }
        /// <summary>The screens' content was opened as this person (editable) after their first unlock.</summary>
        internal bool ScreensOpened { get; set; }
        internal bool ScreensOpening { get; set; }
        internal bool ScreensRetryOnUnlock { get; set; }
        internal bool Closed { get; set; }
        internal CancellationTokenSource Lifetime { get; } = new();
        internal HashSet<SafeFileHandle> CommandJobs { get; } = new();
        internal bool AdminCertificateVerified { get; set; }
    }

    private const string BrowserKey = "browser", NotesKey = "notes", SettingsKey = "settings";
    private Config config;
    private readonly string? configPath;
    private readonly Panel surface = new() { Dock = DockStyle.Fill };
    private readonly DesktopView desktop = new();
    private readonly Taskbar taskbar = new();
    private readonly StartMenu menu = new();
    private readonly Dictionary<string, UserSession> sessions = new(StringComparer.Ordinal);
    private UserSession? current;
    private SettingsView? settingsView;
    private Control? visibleView;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly CardReader reader = new();
    private Func<CardIdentity?> identifyCard;
    private Func<bool> cardPresent;
    private string? cardKey;
    private bool requireRemoval, inTick, closeConfirmed, verifying;
    private CancellationTokenSource? pinCheck;
    private string? shellCardKey;
    private DateTime? disconnectedSince;
    private DateTime? shellLockRequestedAt;
    private bool shellUnlockNotified;
    private IntPtr shellNotificationHandle;
    private DateTime nextShellNotificationAttempt;
    private bool ShellMode => config.SessionMode == SessionMode.WindowsShell;
    private string? notice;
    private ChromeState? lastChrome;
    private readonly record struct ChromeState(UserSession? Current, Control? View, bool Locked, bool Verified,
        bool Verifying, bool RequireRemoval, int Sessions, int Windows, string? Notice, string? SessionNotice,
        string Name, string ReaderStatus);

    private bool CanUseApps => current is { Locked: false };

    internal KioskForm(Config config, string? configPath = null)
    {
        this.config = config;
        this.configPath = configPath;
        identifyCard = IdentifyPresentCard;
        cardPresent = () => reader.Snapshot(this.config.ReaderName) != null;
        Text = "Kiosk";
        // Full-screen shell: no title bar with minimize / maximize / close, in test mode too.
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        BackColor = KioskTheme.Background;
        ForeColor = KioskTheme.Text;
        surface.BackColor = KioskTheme.Background;
        surface.Controls.Add(desktop);
        Controls.Add(surface);
        Controls.Add(taskbar);
        visibleView = desktop;

        taskbar.MenuButton.Click += (_, _) => ToggleMenu();
        taskbar.TaskClicked += ActivateWindow;
        taskbar.TaskCloseRequested += key => { if (ConfirmClose(key)) CloseWindow(key, "window_closed"); };
        taskbar.TaskMenuRequested += ShowTaskMenu;
        taskbar.LockButton.Click += (_, _) => Lock("manual_lock", 0);
        taskbar.LogoutButton.Click += (_, _) => { if (current != null && ConfirmEnd([current])) Logout("manual_logout"); };
        taskbar.ExitButton.Click += (_, _) => Close();
        desktop.ResumeRequested += () => _ = VerifyAndUnlockAsync();

        timer.Tick += (_, _) => TickState();
        Shown += (_, _) => { timer.Start(); StartIdleScreens(); };
        FormClosing += (_, e) =>
        {
            if (!closeConfirmed && !ConfirmEnd(sessions.Values)) { e.Cancel = true; return; }
            timer.Stop(); CloseAllSessions("client_exit"); reader.Dispose();
        };
        FormClosed += (_, _) => { menu.Dispose(); StopIdleScreens(); foreach (var host in hosts.Values) host.Dispose(); };
        if (ShellMode) StartWindowsSession();
        RefreshChrome();
    }

    // ---------------------------------------------------------------- card and session state

    private void TickState()
    {
        if (inTick) return;
        inTick = true;
        try
        {
            if (ShellMode)
            {
                ShellTick();
                MonitorRdp();
                MonitorApplications();
                return;
            }
            // While a PIN check runs the card is reset on purpose; presence changes are evaluated afterwards.
            if (!verifying)
            {
                var key = reader.Snapshot(config.ReaderName);
                if (key != cardKey)
                {
                    cardKey = key;
                    if (key == null) CardRemoved();
                    else
                    {
                        var identity = identifyCard() ?? UncertifiedCard();
                        if (identity != null) CardInserted(identity);
                        else
                        {
                            if (current != null) Park(current, "card_identity_unavailable");
                            notice = "Nie można ustalić identyfikatora karty. Wyjmij ją i spróbuj ponownie.";
                        }
                    }
                }
            }
            if (current is { Locked: false })
            {
                var idle = Native.IdleMilliseconds();
                if (idle >= (uint)config.LockAfterSeconds * 1000u) Lock("idle_lock", idle);
            }
            ExpireSessions();
            MonitorRdp();
            MonitorApplications();
            UpdateMainIdle();
        }
        catch (Exception ex)
        {
            notice = "Błąd czytnika: " + ex.Message;
            Audit.Write("monitor_error", ex.Message);
            if (current != null) Park(current, "monitor_error");
        }
        finally
        {
            inTick = false;
            RefreshChrome();
        }
    }

    private CardIdentity? IdentifyPresentCard()
    {
        try { return CardPin.ReadIdentity(reader.ActiveReader); }
        catch (Exception ex) { Audit.Write("card_identity_error", ex.Message); return null; }
    }

    /// <summary>A card without certificates (e.g. FIDO2): its UID keeps cards of the same model in separate sessions.</summary>
    private CardIdentity? UncertifiedCard()
    {
        var uid = CardPin.ReadUid(reader.ActiveReader);
        // ATR identifies a card model, not a person. Never merge two cards into
        // one parked session just because the reader/model happen to match.
        return string.IsNullOrWhiteSpace(uid) ? null : new CardIdentity("uid:" + uid, "Karta");
    }

    private void CardRemoved()
    {
        requireRemoval = false;
        pinCheck?.Cancel();
        if (current != null) Park(current, "card_removed");
    }

    private void CardInserted(CardIdentity identity)
    {
        if (requireRemoval) return;
        // This method runs only for a new PC/SC insertion generation. Even the
        // same card must be locked and re-verified after a rapid remove/reinsert.
        if (current != null) Park(current, "card_changed");
        if (!sessions.TryGetValue(identity.Id, out var session))
        {
            session = new UserSession { Identity = identity };
            sessions[identity.Id] = session;
            Audit.Write("session_started", identity.Name);
        }
        else Audit.Write("session_returned", identity.Name);
        current = session;
        notice = null;
        ShowView(desktop);
        if (config.UnlockMethod == UnlockMethod.Card) Unlock(session);
        else _ = VerifyAndUnlockAsync(); // Ask for Hello / the card PIN straight away.
    }

    /// <summary>Hides a session without closing it: its windows keep running for its owner.</summary>
    private void Park(UserSession session, string reason)
    {
        menu.HideMenu();
        if (settingsView != null && session.Windows.Any(w => w.Key == SettingsKey)) CloseWindow(SettingsKey, "settings_closed", session);
        if (!session.Locked) session.InactiveSince = DateTime.UtcNow;
        session.Locked = true;
        session.AdminCertificateVerified = false;
        if (current == session) current = null;
        ShowView(desktop);
        Audit.Write(reason, session.Identity.Name);
    }

    /// <summary>In-app idle or manual lock; the card is still in the reader.</summary>
    private void Lock(string reason, uint idleMilliseconds)
    {
        if (ShellMode) { DisconnectWindows(reason); return; }
        if (current is not { Locked: false } session) return;
        menu.HideMenu();
        session.Locked = true;
        session.AdminCertificateVerified = false;
        session.InactiveSince = DateTime.UtcNow - TimeSpan.FromMilliseconds(idleMilliseconds);
        ShowView(desktop);
        Audit.Write(reason, session.Identity.Name);
        RefreshChrome();
    }

    private void Unlock(UserSession session)
    {
        if (!session.ScreensOpened && !ShellMode && config.IdleScreens.Length > 0)
        {
            if (session.ScreensOpening) session.ScreensRetryOnUnlock = true;
            else { session.ScreensOpening = true; BeginInvoke(() => _ = OpenScreenContentsAsync(session)); }
        }
        session.Locked = false;
        session.Verified = true;
        session.Notice = null;
        Audit.Write("unlocked", session.Identity.Name);
        ShowView(session.LastView != null && session.Windows.Any(w => w.View == session.LastView) ? session.LastView : desktop);
        if (!session.AutoConnected)
        {
            session.AutoConnected = true;
            var connection = config.RdpConnections.FirstOrDefault(c => c.Name.Equals(config.AutoConnectRdpName, StringComparison.OrdinalIgnoreCase));
            if (connection != null) OpenRdp(connection);
        }
        RefreshChrome();
    }

    private async Task VerifyAndUnlockAsync()
    {
        if (current is not { Locked: true } session || verifying) return;
        verifying = true;
        bool ok = false;
        try { ok = await VerifyPersonAsync(session); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            session.Notice = "Nie udało się potwierdzić tożsamości: " + ex.Message;
            Audit.Write("unlock_error", ex.Message);
        }
        finally { verifying = false; }
        if (session.Closed || IsDisposed) { session.Logon?.Dispose(); session.Logon = null; return; }
        if (!ok || current != session) { RefreshChrome(); return; }
        // The card may have been swapped while the dialog was open.
        if (!cardPresent() || (identifyCard() ?? UncertifiedCard())?.Id != session.Identity.Id)
        {
            cardKey = null; // Re-evaluate the card on the next tick.
            RefreshChrome();
            return;
        }
        cardKey = reader.Snapshot(config.ReaderName); // A PIN check resets the card; that is not a new insertion.
        if (config.SignInAsUser && session.Logon == null)
        {
            // Like runas: the person's own account, checked by Windows; asked once per Kiosk session.
            try { session.Logon = UserLogon.Prompt(Handle, "Podaj login (np. jan@firma.pl) i hasło swojego konta. Aplikacje, przeglądarka i pulpity zdalne uruchomią się jako Ty."); }
            catch (Exception ex) { session.Notice = "Nie udało się zalogować: " + ex.Message; Audit.Write("signin_error", ex.Message); }
            if (session.Closed || current != session || !cardPresent())
            {
                session.Logon?.Dispose(); session.Logon = null;
                cardKey = null; return;
            }
            if (session.Logon == null) { session.Notice ??= "Logowanie anulowane."; RefreshChrome(); return; }
            Audit.Write("signed_in_as_user", session.Logon.DisplayName);
            // Remember the account for this card: next time the card and its PIN are enough.
            var logon = session.Logon;
            var id = session.Identity.Id;
            var readerName = reader.ActiveReader;
            verifying = true;
            try
            {
                string message = "Wpisz PIN karty, aby ją zapamiętać. Następnym razem wystarczy sam PIN.";
                while (await desktop.AskPinAsync(message, cardPresent) is { } pin)
                {
                    var saved = await Task.Run(() => CardVault.Enroll(id, logon, readerName, pin));
                    Audit.Write(saved.Code == CardPin.Verified ? "card_enrolled" : "card_enroll_failed", saved.Code + " " + saved.Message);
                    if (saved.Code == CardPin.WrongPin) { message = saved.Message + " Spróbuj ponownie."; continue; }
                    session.Notice = saved.Code == CardPin.Verified ? null : "Karta nie została zapamiętana: " + saved.Message;
                    break;
                }
            }
            catch (Exception ex) { session.Notice = "Karta nie została zapamiętana: " + ex.Message; Audit.Write("card_enroll_failed", ex.Message); }
            finally { verifying = false; cardKey = reader.Snapshot(config.ReaderName); }
        }
        if (session.Closed || IsDisposed || current != session || !cardPresent() ||
            (identifyCard() ?? UncertifiedCard())?.Id != session.Identity.Id)
        {
            cardKey = null; return;
        }
        Unlock(session);
    }

    private async Task<CardPin.Result> VerifyFidoPinAsync()
    {
        var readerName = reader.ActiveReader;
        string message = "Wpisz PIN karty.";
        while (await desktop.AskPinAsync(message, cardPresent) is { } pin)
        {
            var answer = await Task.Run(() => CardService.Call(new CardService.Request { Op = "verify", Reader = readerName, Pin = pin }));
            if (answer.Code == 0) return new(CardPin.Verified, answer.Message);
            if (answer.Code != 0x31) return new(CardPin.Failed, answer.Message);
            message = answer.Message + " Spróbuj ponownie.";
        }
        return new(CardPin.Cancelled, "Anulowano wpisywanie PIN-u.");
    }

    private async Task<bool> VerifyPersonAsync(UserSession session)
    {
        if (config.SignInAsUser && session.Logon == null)
        {
            // A remembered card: only its PIN, typed in Kiosk, opens the account stored for it.
            if (!CardVault.IsEnrolled(session.Identity.Id)) return true; // First use: Zaloguj asks, enrolling checks the PIN.
            var id = session.Identity.Id;
            var readerName = reader.ActiveReader;
            string message = "Wpisz PIN karty, aby się zalogować.";
            while (true)
            {
                var pin = await desktop.AskPinAsync(message, cardPresent);
                if (pin == null) { session.Notice = "Logowanie anulowane."; return false; }
                var opened = await Task.Run(() => CardVault.Open(id, readerName, pin));
                if (opened.Logon != null) { session.Logon = opened.Logon; Audit.Write("card_vault_opened", opened.Logon.DisplayName); return true; }
                Audit.Write("card_vault_failed", opened.Code + " " + opened.Message);
                if (opened.Code == CardPin.WrongPin) { message = opened.Message + " Spróbuj ponownie."; continue; }
                if (opened.Code == CardPin.NoKey) { session.Notice = opened.Message; return true; } // Set the card up again below.
                if (opened.Code != CardPin.Verified) { session.Notice = opened.Message; return false; }
                // The PIN was right but the password changed: ask once and store the new one.
                var logon = UserLogon.Prompt(Handle, opened.Message);
                if (logon == null) { session.Notice = "Logowanie anulowane."; return false; }
                session.Logon = logon;
                var saved = await Task.Run(() => CardVault.Enroll(id, logon, readerName, pin));
                if (saved.Code != CardPin.Verified) session.Notice = "Nowe hasło nie zostało zapamiętane: " + saved.Message;
                return true;
            }
        }
        switch (config.UnlockMethod)
        {
            case UnlockMethod.CardAndWindowsHello:
            {
                // Windows Hello confirms the person at the device; the Windows session itself is never locked.
                var availability = await UserConsentVerifier.CheckAvailabilityAsync();
                if (availability != UserConsentVerifierAvailability.Available)
                {
                    session.Notice = "Windows Hello jest niedostępne (" + availability + "). Zmień sposób logowania w Konfiguracji.";
                    Audit.Write("unlock_hello_unavailable", availability.ToString());
                    return false;
                }
                var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(Handle, "Potwierdź, aby otworzyć sesję Kiosku");
                if (result == UserConsentVerificationResult.Verified) return true;
                session.Notice = "Nie potwierdzono tożsamości (" + result + "). Spróbuj ponownie.";
                Audit.Write("unlock_hello_failed", result.ToString());
                return false;
            }
            case UnlockMethod.CardAndPin:
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                pinCheck = cancel;
                try
                {
                    // A card without certificates is a FIDO2 card: Kiosk asks for its PIN and the card checks it (CTAP2).
                    var result = session.Identity.Id.StartsWith("cert:", StringComparison.OrdinalIgnoreCase)
                        ? await CardPin.VerifyAsync(reader.ActiveReader, Handle, cancel.Token, session.Identity.Id[5..])
                        : await VerifyFidoPinAsync();
                    if (result.Code == CardPin.Verified)
                    {
                        if (session.Identity.Id.StartsWith("cert:", StringComparison.OrdinalIgnoreCase)) session.AdminCertificateVerified = true;
                        return true;
                    }
                    session.Notice = result.Message.Length > 0 ? result.Message : "Nie potwierdzono PIN-u karty.";
                    Audit.Write("unlock_pin_failed", result.Code.ToString());
                    return false;
                }
                finally { pinCheck = null; }
            }
            default:
                return true;
        }
    }

    private void ExpireSessions()
    {
        if (config.ChangeUserAfterSeconds <= 0) return;
        var limit = TimeSpan.FromSeconds(config.ChangeUserAfterSeconds);
        foreach (var session in sessions.Values.ToArray())
        {
            if (!session.Locked || verifying && session == current) continue;
            if (DateTime.UtcNow - session.InactiveSince < limit) continue;
            bool wasCurrent = session == current;
            CloseSession(session, "session_expired");
            if (wasCurrent) requireRemoval = true; // Its card is still inserted: reinserting starts afresh.
        }
    }

    private void Logout(string reason)
    {
        if (ShellMode) { SignOutWindows(reason); return; }
        if (current == null) return;
        CloseSession(current, reason);
        requireRemoval = cardKey != null;
        RefreshChrome();
    }

    private void CloseSession(UserSession session, string reason)
    {
        session.Closed = true;
        session.Lifetime.Cancel();
        foreach (var job in session.CommandJobs.ToArray()) job.Dispose();
        session.CommandJobs.Clear();
        if (session == current) { menu.HideMenu(); ShowView(desktop); current = null; }
        foreach (var window in session.Windows.ToArray()) CloseWindowCore(session, window);
        session.Logon?.Dispose(); // Forget the password with the session.
        session.Logon = null;
        session.Lifetime.Dispose();
        sessions.Remove(session.Identity.Id);
        Audit.Write(reason, session.Identity.Name);
    }

    private void CloseAllSessions(string reason)
    {
        pinCheck?.Cancel();
        foreach (var session in sessions.Values.ToArray()) CloseSession(session, reason);
        RefreshChrome();
    }

    private bool CheckCard()
    {
        if (!CanUseApps) return false;
        if (!ShellMode)
        {
            var observed = reader.Snapshot(config.ReaderName);
            if (observed == null || observed != cardKey)
            {
                // A different card may arrive between timer ticks and a menu click.
                if (current != null) Park(current, "card_changed_before_launch");
                cardKey = null; return false;
            }
        }
        notice = null;
        return true;
    }

    private bool ConfirmEnd(IEnumerable<UserSession> affected) =>
        !affected.Any(s => s.HasWork) ||
        MessageBox.Show(this, "Zakończyć sesję? Niezapisane notatki zostaną usunięte, a pulpity zdalne i aplikacje zamknięte.",
            "Wyloguj", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private bool ConfirmClose(string key)
    {
        string? question = key == NotesKey && current?.Notes?.Dirty == true ? "Zamknąć notatnik? Niezapisany tekst zostanie usunięty." :
            key.StartsWith("app:") ? "Zamknąć aplikację? Niezapisane dane mogą zostać utracone." :
            key.StartsWith("rdp:") ? "Rozłączyć ten pulpit zdalny?" : null;
        return question == null || MessageBox.Show(this, question, "Zamknij okno", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    // ---------------------------------------------------------------- screens while nobody works

    private readonly List<IdleScreen> idleScreens = new();
    private readonly Dictionary<int, IdleScreen> idleByScreen = new();
    private readonly Dictionary<int, ScreenHost> hosts = new();

    /// <summary>1 = the main screen, then the others in Windows' order.</summary>
    private static Screen[] OrderedScreens => Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();

    private ScreenHost? HostOf(Control view) => hosts.Values.FirstOrDefault(h => h.View == view);

    /// <summary>"Excel 2", "Excel 3"…: a name not used by this person's open windows yet.</summary>
    private static string NextInstanceName(UserSession session, string name)
    {
        var baseName = System.Text.RegularExpressions.Regex.Replace(name, @" \d+$", "");
        for (int n = 2; ; n++)
            if (!session.Applications.ContainsKey(baseName + " " + n) && session.Windows.All(w => w.Title != baseName + " " + n)) return baseName + " " + n;
    }

    /// <summary>One more window of the same kind (browser, application) — e.g. to put one on each screen.</summary>
    private async Task OpenAnotherAsync(string key)
    {
        if (current is not { } session) return;
        if (key.StartsWith("app:") && session.Applications.ContainsKey(key[4..]))
        {
            var name = System.Text.RegularExpressions.Regex.Replace(key[4..], @" \d+$", "");
            var entry = config.Applications.FirstOrDefault(a => a.Name == name) ?? (name == EdgeAsUser.Name ? EdgeAsUser : null);
            if (entry != null) await OpenApplicationAsync(entry with { Name = key[4..] }, another: true);
        }
        else if (key == BrowserKey || key.StartsWith("browser:"))
        {
            // The Kiosk browser (no Windows account for the person): another browser window of its own.
            var title = NextInstanceName(session, "Przeglądarka");
            var view = new BrowserView(config.Bookmarks, new SitePolicy(config.BrowserOnlyBookmarks, config.Bookmarks, config.BrowserAllowedDomains), session.Identity.Thumbprints);
            AddWindow(session, "browser:" + title, title, view);
            try { await view.InitializeAsync(config.BrowserUrl); }
            catch (Exception ex) { CloseWindow("browser:" + title, "browser_error", session); notice = "Nie udało się uruchomić przeglądarki: " + ex.Message; }
        }
    }

    private void ShowTaskMenu(string key, Control button, Point point)
    {
        if (!CanUseApps || current!.Windows.FirstOrDefault(w => w.Key == key) is not { } window) return;
        var screens = OrderedScreens;
        var menuStrip = new ContextMenuStrip { Font = new Font("Segoe UI", 10.5f) };
        var closeItem = new ToolStripMenuItem("Zamknij okno");
        closeItem.Click += (_, _) => { if (ConfirmClose(key)) CloseWindow(key, "window_closed"); };
        if (screens.Length > 1)
        {
            var numbers = new ToolStripMenuItem("Pokaż numery ekranów");
            numbers.Click += (_, _) => ShowScreenNumbers();
            menuStrip.Items.Add(numbers);
        }
        if (key.StartsWith("app:") || key == BrowserKey || key.StartsWith("browser:"))
        {
            var another = new ToolStripMenuItem("Otwórz kolejne okno");
            another.Click += (_, _) => _ = OpenAnotherAsync(key);
            menuStrip.Items.Add(another);
            menuStrip.Items.Add(new ToolStripSeparator());
        }
        int placed = hosts.FirstOrDefault(h => h.Value.View == window.View).Key;
        if (placed == 0) placed = 1;
        for (int n = 1; n <= screens.Length; n++)
        {
            int target = n;
            var item = new ToolStripMenuItem(n == 1 ? "Pokaż na ekranie głównym" : "Pokaż na ekranie " + n) { Checked = n == placed };
            item.Click += (_, _) => PlaceWindow(current!, key, target, remember: true);
            menuStrip.Items.Add(item);
        }
        if (screens.Length < 2) menuStrip.Items.Add(new ToolStripMenuItem("Podłączony jest jeden ekran") { Enabled = false });
        menuStrip.Items.Add(new ToolStripSeparator());
        menuStrip.Items.Add(closeItem);
        menuStrip.Closed += (_, _) => BeginInvoke(menuStrip.Dispose);
        menuStrip.Show(button, point);
    }

    /// <summary>Puts a window on screen n (1 = the main Kiosk screen); a window already there goes back to the main screen.</summary>
    private void PlaceWindow(UserSession session, string key, int n, bool remember)
    {
        var window = session.Windows.FirstOrDefault(w => w.Key == key);
        if (window == null) return;
        var screens = OrderedScreens;
        HostOf(window.View)?.Release();
        if (n <= 1 || n > screens.Length) ReturnToMain(window.View);
        else
        {
            if (!hosts.TryGetValue(n, out var host) || host.IsDisposed)
                hosts[n] = host = new ScreenHost(screens[n - 1]) { Owner = this };
            if (host.View is { } previous && previous != window.View) ReturnToMain(host.Release()!);
            if (visibleView == window.View) ShowView(desktop);
            host.Host(window.View);
        }
        if (remember)
        {
            session.Layout ??= ScreenLayouts.Load(session.Identity.Id);
            if (n <= 1) session.Layout.Remove(key); else session.Layout[key] = n;
            ScreenLayouts.Save(session.Identity.Id, session.Layout);
            Audit.Write("window_screen", key + " -> " + n);
        }
        RefreshHosts();
    }

    private void ReturnToMain(Control view)
    {
        if (view.Parent == surface) return;
        view.Parent?.Controls.Remove(view);
        view.Visible = false;
        view.Dock = DockStyle.Fill;
        surface.Controls.Add(view);
    }

    /// <summary>Other screens show the signed-in person's windows; locked or signed out, their idle content again.</summary>
    private void RefreshHosts()
    {
        foreach (var (n, host) in hosts)
        {
            if (host.IsDisposed) continue;
            bool show = host.View != null && current is { Locked: false } person && person.Windows.Any(w => w.View == host.View);
            if (host.Visible != show) host.Visible = show;
            if (idleByScreen.TryGetValue(n, out var idle) && !idle.IsDisposed && idle.Visible == show) idle.Visible = !show;
        }
    }
    private IdleScreen? mainIdle;

    /// <summary>Every screen with configured content shows it, view only; 1 = the main screen, then Windows' order.</summary>
    private void StartIdleScreens()
    {
        StopIdleScreens();
        if (ShellMode || config.IdleScreens.Length == 0) return;
        var ordered = Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            var content = config.IdleScreens.FirstOrDefault(s => s.Screen == i + 1) ?? config.IdleScreens.FirstOrDefault(s => s.Screen == 0);
            if (content == null) continue;
            var window = new IdleScreen(ordered[i], content, i + 1);
            if (ordered[i].Primary) { window.Owner = this; mainIdle = window; } // Above the Kiosk on the main screen.
            else window.TopMost = true;
            idleScreens.Add(window);
            idleByScreen[i + 1] = window;
            if (!ordered[i].Primary || current is not { Locked: false }) window.Show();
        }
        Audit.Write("idle_screens", idleScreens.Count.ToString());
        ScheduleScreenRefresh();
    }

    private readonly System.Windows.Forms.Timer screenRefresh = new();

    /// <summary>Timed refresh of the screens' pages (Konfiguracja → Ekrany).</summary>
    private void ScheduleScreenRefresh()
    {
        screenRefresh.Stop();
        screenRefresh.Tick -= ReloadScreens;
        if (config.ScreenRefreshSeconds <= 0 || idleScreens.Count == 0) return;
        screenRefresh.Interval = config.ScreenRefreshSeconds * 1000;
        screenRefresh.Tick += ReloadScreens;
        screenRefresh.Start();
    }

    private void ReloadScreens(object? sender, EventArgs e) { foreach (var screen in idleScreens) if (!screen.IsDisposed) screen.Reload(); }

    private void StopIdleScreens()
    {
        screenRefresh.Stop();
        foreach (var window in idleScreens) window.Dispose();
        idleScreens.Clear();
        idleByScreen.Clear();
        mainIdle = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose(); screenRefresh.Dispose(); reader.Dispose(); menu.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// After sign-in each screen's content opens again as the signed-in person: the same page or program, but
    /// editable and under their account (so its own logs show who changed what). Locking brings back the
    /// read-only content; the person's windows wait on their screens.
    /// </summary>
    private async Task OpenScreenContentsAsync(UserSession session)
    {
        bool complete = true;
        try
        {
            var screens = OrderedScreens;
            for (int n = 1; n <= screens.Length; n++)
            {
                var content = config.IdleScreens.FirstOrDefault(s => s.Screen == n) ?? config.IdleScreens.FirstOrDefault(s => s.Screen == 0);
                if (content == null) continue;
                if (session.Closed || current != session || session.Locked) { complete = false; break; }
                var title = "Ekran " + n;
                var key = content.Url.Length > 0 && session.Logon == null ? "web:" + n : "app:" + title;
                if (session.Windows.Any(w => w.Key == key)) continue;
                try
                {
                    if (content.Url.Length > 0 && session.Logon == null)
                    {
                        // Without a Windows account for the person: the Kiosk browser, signed in by the person on the page.
                        var view = new BrowserView([], null, session.Identity.Thumbprints);
                        AddWindow(session, key, title, view);
                        if (n > 1) PlaceWindow(session, key, n, remember: false);
                        await view.InitializeAsync(content.Url);
                    }
                    else
                    {
                        var entry = content.Url.Length > 0 ? EdgeForScreen(n, content.Url)
                            : new ApplicationEntry { Name = title, Path = content.Path, Arguments = content.Arguments };
                        session.Layout ??= ScreenLayouts.Load(session.Identity.Id);
                        if (n > 1) session.Layout["app:" + title] = n; // Opens on its own screen.
                        await StartApplicationAsync(session, entry);
                    }
                    Audit.Write("screen_opened_as_user", title);
                }
                catch (Exception ex)
                {
                    complete = false;
                    if (session.Windows.Any(w => w.Key == key)) CloseWindow(key, "screen_open_error", session);
                    Audit.Write("screen_open_error", title + ": " + ex.Message);
                }
            }
            // The main screen's content (if any) is what the person sees first.
            if (current == session && !session.Locked && session.Windows.FirstOrDefault(w => w.Title == "Ekran 1") is { } main && HostOf(main.View) == null) ShowView(main.View);
            session.ScreensOpened = complete && !session.Closed && current == session && !session.Locked;
        }
        finally
        {
            session.ScreensOpening = false;
            bool retry = session.ScreensRetryOnUnlock && !session.ScreensOpened && !session.Closed && current == session && !session.Locked;
            session.ScreensRetryOnUnlock = false;
            if (retry && !IsDisposed) { session.ScreensOpening = true; BeginInvoke(() => _ = OpenScreenContentsAsync(session)); }
        }
    }

    /// <summary>The person's own Edge (their signed-in profile, SSO) for a screen. When their Edge already runs, it opens
    /// the window in that copy and the Kiosk view takes the new window over.</summary>
    private static ApplicationEntry EdgeForScreen(int n, string url)
    {
        return new ApplicationEntry
        {
            Name = "Ekran " + n,
            Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Arguments = "--new-window " + url
        };
    }

    /// <summary>Main screen: input brings up the Kiosk (sign-in / lock screen); left alone while locked or signed out it returns to the content.</summary>
    private void UpdateMainIdle()
    {
        // While Konfiguracja is open the screens accept input, so an administrator can sign their pages in (read-only account).
        foreach (var screen in idleScreens) if (!screen.IsDisposed) screen.Interactive = settingsView != null;
        if (mainIdle == null || mainIdle.IsDisposed) return;
        // Signed in and working, checking a PIN or in Konfiguracja: the Kiosk stays. Locked or nobody there: back to the content.
        bool someone = current is { Locked: false } || verifying || settingsView != null || desktop.AskingPin;
        uint idle = Native.IdleMilliseconds();
        if (mainIdle.Visible)
        {
            if (someone || idle < 700 && DateTime.UtcNow - mainIdle.ShownAt > TimeSpan.FromSeconds(1)) { mainIdle.Hide(); Activate(); }
        }
        else if (!someone && idle >= (uint)config.IdleAfterSeconds * 1000u) mainIdle.Show();
    }

    // ---------------------------------------------------------------- Windows shell mode

    /// <summary>The person signed in to Windows (card / Windows Hello); the Kiosk is their shell.</summary>
    private void StartWindowsSession()
    {
        var identity = WindowsSession.CurrentUser();
        using var token = System.Security.Principal.WindowsIdentity.GetCurrent();
        var session = new UserSession
        {
            Identity = identity,
            // Includes the deny-only Administrators SID of a UAC-filtered admin token.
            IsWindowsAdmin = token.Groups?.Any(g => g.Value == "S-1-5-32-544") == true
        };
        sessions[identity.Id] = session;
        current = session;
        Audit.Write("windows_session_started", identity.Name);
        Unlock(session);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (ShellMode) TryRegisterShellNotifications();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (shellNotificationHandle != IntPtr.Zero)
        {
            WindowsSession.UnregisterNotifications(shellNotificationHandle);
            shellNotificationHandle = IntPtr.Zero;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WindowsSession.SessionChangeMessage && ShellMode)
        {
            var change = message.WParam.ToInt32();
            if (change is WindowsSession.SessionLock or WindowsSession.ConsoleDisconnect or WindowsSession.RemoteDisconnect)
            {
                disconnectedSince ??= DateTime.UtcNow;
                shellLockRequestedAt = null;
                shellUnlockNotified = false;
            }
            else if (change == WindowsSession.SessionUnlock)
            {
                shellLockRequestedAt = null;
                shellCardKey = null;
                shellUnlockNotified = true;
                // Keep the time spent away until ShellTick has enforced the
                // retention limit, even if its timer did not run while locked.
            }
            else if (change == WindowsSession.RemoteConnect)
            {
                // Transport reconnection can still leave Windows at its lock screen.
                shellCardKey = null;
            }
        }
        base.WndProc(ref message);
    }

    private void TryRegisterShellNotifications()
    {
        if (shellNotificationHandle != IntPtr.Zero || !IsHandleCreated) return;
        var handle = Handle;
        if (WindowsSession.RegisterNotifications(handle)) shellNotificationHandle = handle;
        else nextShellNotificationAttempt = DateTime.UtcNow.AddSeconds(5);
    }

    private void ShellTick()
    {
        var now = DateTime.UtcNow;
        if (shellNotificationHandle == IntPtr.Zero && now >= nextShellNotificationAttempt) TryRegisterShellNotifications();
        var state = WindowsSession.State();
        if (state is WindowsSession.Availability.Locked or WindowsSession.Availability.Disconnected)
        {
            disconnectedSince ??= now;
            shellLockRequestedAt = null;
        }
        if (WindowsSession.RetentionExpired(disconnectedSince, now, config.ChangeUserAfterSeconds))
        {
            SignOutWindows("session_expired");
            return;
        }
        else if (disconnectedSince != null && shellLockRequestedAt == null &&
                 WindowsSession.CanResume(state, shellUnlockNotified))
        {
            disconnectedSince = null;
            shellCardKey = null;
            Audit.Write("windows_session_reconnected", current?.Identity.Name ?? "");
        }
        if (disconnectedSince == null) shellUnlockNotified = false;
        if (disconnectedSince != null)
            return;
        if (shellLockRequestedAt is { } requested)
        {
            // LockWorkStation is asynchronous. If Windows has not confirmed the
            // lock, fail closed by signing out rather than leaving work exposed.
            if (now - requested >= TimeSpan.FromSeconds(5)) SignOutWindows("windows_lock_unconfirmed");
            return;
        }
        var observedCard = reader.Snapshot(config.ReaderName);
        if (!config.DisconnectOnCardRemoval) shellCardKey = observedCard;
        else if (shellCardKey == null) shellCardKey = observedCard;
        else if (observedCard != shellCardKey) { DisconnectWindows("card_removed_or_changed"); return; }
        if (Native.IdleMilliseconds() >= (uint)config.LockAfterSeconds * 1000u) DisconnectWindows("idle_lock");
    }

    /// <summary>Like Windows "Switch user": programs keep running, the sign-in screen is shown for the next person.</summary>
    private void DisconnectWindows(string reason)
    {
        menu.HideMenu();
        shellUnlockNotified = false;
        Audit.Write(reason, current?.Identity.Name ?? "");
        try { WindowsSession.LockOrDisconnect(); shellLockRequestedAt = DateTime.UtcNow; }
        catch (Exception ex)
        {
            notice = "Nie udało się zablokować sesji Windows: " + ex.Message;
            Audit.Write("windows_lock_failed", ex.Message);
            SignOutWindows("windows_lock_failed");
        }
    }

    private void SignOutWindows(string reason)
    {
        Audit.Write(reason, current?.Identity.Name ?? "");
        closeConfirmed = true;
        try { WindowsSession.SignOut(); }
        catch (Exception ex) { closeConfirmed = false; notice = "Nie udało się wylogować z Windows: " + ex.Message; Audit.Write("signout_failed", ex.Message); }
    }

    // ---------------------------------------------------------------- shell chrome

    private string UnlockInstruction => config.UnlockMethod switch
    {
        UnlockMethod.CardAndWindowsHello => "Potwierdź w Windows Hello.",
        UnlockMethod.CardAndPin => "Wpisz PIN karty w okienku Windows.",
        _ => ""
    };

    private void RefreshChrome()
    {
        RefreshHosts();
        var state = new ChromeState(current, visibleView, current?.Locked == true, current?.Verified == true,
            verifying, requireRemoval, sessions.Count, current?.Windows.Count ?? 0, notice, current?.Notice,
            current?.Name ?? "", reader.Status);
        if (lastChrome == state) return;
        lastChrome = state;
        int others = sessions.Values.Count(s => s != current);
        taskbar.MenuButton.Enabled = CanUseApps; // Only for a signed-in, unlocked person.
        taskbar.LockButton.Enabled = CanUseApps;
        taskbar.LogoutButton.Enabled = current != null;
        taskbar.SetTasks(CanUseApps ? current!.Windows.Select(w => { var (glyph, icon) = IconFor(current, w); return (w.Key, w.Title, w.View == visibleView, glyph, icon, HostOf(w.View) != null); }).ToArray() : []);
        taskbar.Status.Text = current == null ? (requireRemoval ? "Wyjmij kartę" : "Brak karty") + (others > 0 ? " · zachowane sesje: " + others : "") :
            (current.Locked ? "🔒 " : "● ") + current.Name + (others > 0 ? " · inne sesje: " + others : "");
        taskbar.Status.ForeColor = CanUseApps ? Color.FromArgb(110, 214, 150) : KioskTheme.Muted;

        var retention = config.ChangeUserAfterSeconds > 0
            ? "Wyjęta karta: sesja czeka " + FormatDuration(config.ChangeUserAfterSeconds) + ", potem zostanie zamknięta."
            : "Sesja czeka na powrót karty aż do wylogowania.";
        if (current is { Locked: true } locked)
        {
            var title = locked.Verified ? "Sesja zablokowana" : "Logowanie";
            var detail = locked.Name + "\n" + (locked.Notice ?? (verifying ? "Czekam na potwierdzenie…" : UnlockInstruction));
            desktop.UpdateStatus(title, detail, locked: true, locked.Verified ? "Wznów sesję" : "Zaloguj");
        }
        else if (current != null)
            desktop.UpdateStatus("Witaj, " + current.Name, notice ?? current.Notice ?? "Otwórz Menu na dole, aby uruchomić pulpit zdalny, przeglądarkę lub aplikację.");
        else if (requireRemoval)
            desktop.UpdateStatus("Wyjmij kartę", "Sesja została zamknięta. Wyjmij kartę i włóż ją ponownie, aby zacząć od nowa.");
        else
            desktop.UpdateStatus("Przyłóż kartę", notice ?? (others > 0
                ? "Zachowane sesje: " + others + ". Każdy wraca do swojej sesji, przykładając swoją kartę.\n" + retention
                : "Włóż kartę, aby rozpocząć pracę.\n" + reader.Status.Split('\n')[0]));
    }

    private static string FormatDuration(int seconds) =>
        seconds % 3600 == 0 ? seconds / 3600 + " h" : seconds % 60 == 0 ? seconds / 60 + " min" : seconds + " s";

    private void ToggleMenu()
    {
        if (menu.Visible) { menu.HideMenu(); return; }
        if (!CanUseApps) return;
        if (DateTime.UtcNow - menu.HiddenAt < TimeSpan.FromMilliseconds(300)) return; // The click that just closed it.
        menu.Populate(CanUseApps ? "Wybierz, co chcesz uruchomić." : "Przyłóż kartę, aby uruchamiać pulpity zdalne i aplikacje.", BuildMenu());
        menu.ShowAt(this, taskbar.MenuButton.RectangleToScreen(taskbar.MenuButton.ClientRectangle));
    }

    private static string AuthenticationLabel(RdpAuthentication authentication) => authentication switch
    {
        RdpAuthentication.WindowsCurrentUser => "Windows Hello / bieżący użytkownik",
        RdpAuthentication.EntraId => "Microsoft Entra ID",
        _ => "Karta inteligentna"
    };

    private List<MenuSection> BuildMenu()
    {
        bool can = CanUseApps;
        var s = current;
        var remote = config.RdpConnections.Select(c => new MenuItemSpec(c.Name,
            (s?.Rdp.ContainsKey(c.Name) == true || s?.Applications.ContainsKey(RdpUserHost.WindowName(c)) == true ? "Otwarte · " : "") + c.Server + " · " + AuthenticationLabel(c.Authentication),
            () => OpenRdp(c), can, KioskIcons.Remote)).ToList();
        if (remote.Count == 0) remote.Add(new MenuItemSpec("Brak połączeń", "Dodaj je w Konfiguracji", () => { }, false));
        var apps = new List<MenuItemSpec>();
        if (config.EnableBrowser) apps.Add(new("Przeglądarka", s?.Browser != null ? "Otwarta" : "Internet", () => _ = OpenBrowserAsync(), can,
            KioskIcons.Browser, s?.Logon != null ? KioskIcons.ForProgram(EdgeAsUser.Path) : null));
        // The built-in Notatnik is an ordinary entry of the application list, so it can be removed there.
        apps.AddRange(config.Applications.Select(a => a.IsBuiltInNotes
            ? new MenuItemSpec(a.Name, s?.Notes != null ? "Otwarty" : "Notatki tekstowe", OpenNotes, can, KioskIcons.Notes)
            : new MenuItemSpec(a.Name, s?.Applications.ContainsKey(a.Name) == true ? "Otwarta" : "Aplikacja", () => _ = OpenApplicationAsync(a), can,
                KioskIcons.App, KioskIcons.ForProgram(a.Path))));
        if (apps.Count == 0) apps.Add(new MenuItemSpec("Brak aplikacji", "Dodaj je w Konfiguracji", () => { }, false));
        var system = new List<MenuItemSpec> { new("Pulpit", "Pokaż pulpit", () => ShowView(desktop), true, KioskIcons.Desktop) };
        if (config.IdleScreens.Length > 0)
            system.Add(new MenuItemSpec("Odśwież ekrany", "Uruchom ponownie treść ekranów", RefreshScreens, true, KioskIcons.Refresh));
        if (configPath != null && s != null && (IsAdmin(s) || IsConfiguredAdminCertificate(s, config)))
            system.Add(new MenuItemSpec("Konfiguracja", "Ustawienia Kiosku", () => _ = OpenSettingsAsync(), can, KioskIcons.Settings));
        if (can)
        {
            system.Add(new MenuItemSpec("Zablokuj", "Zablokuj sesję", () => Lock("manual_lock", 0), true, KioskIcons.Lock));
            system.Add(new MenuItemSpec("Wyloguj", "Zamknij swoją sesję", () => { if (current != null && ConfirmEnd([current])) Logout("manual_logout"); }, true, KioskIcons.Logout));
        }
        // Closing the shell itself: in test mode, or for a Kiosk administrator.
        if (config.TestMode || s != null && IsAdmin(s))
            system.Add(new MenuItemSpec("Zamknij", "Zamknij lub uruchom ponownie Kiosk", AskCloseOrRestart, true, "\uE7E8"));
        var commandItems = config.Commands.Select(c => new MenuItemSpec(c.Name, "Polecenie", () => _ = RunCommandAsync(c), can,
            KioskIcons.Command, c.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? KioskIcons.ForProgram(c.Path) : null)).ToList();
        return [new("Pulpity zdalne", remote), new("Aplikacje", apps), new("Polecenia", commandItems), new("System", system)];
    }

    /// <summary>Taskbar icon of a window: the program's own icon, or the icon of the Kiosk function.</summary>
    private static (string? Glyph, Image? Icon) IconFor(UserSession session, Window w)
    {
        if (w.Key.StartsWith("rdp:")) return (KioskIcons.Remote, null);
        if (w.Key == BrowserKey || w.Key.StartsWith("browser:") || w.Key.StartsWith("web:")) return (KioskIcons.Browser, null);
        if (w.Key == NotesKey) return (KioskIcons.Notes, null);
        if (w.Key == SettingsKey) return (KioskIcons.Settings, null);
        if (w.View is ApplicationView app && app.ProgramPath.Length > 0) return (KioskIcons.App, KioskIcons.ForProgram(app.ProgramPath));
        return (KioskIcons.App, null);
    }

    /// <summary>Menu → Polecenia: runs a program or script in the background and shows how it ended.</summary>
    private async Task RunCommandAsync(CommandEntry command)
    {
        if (current is not { } session || !CheckCard()) return;
        if (command.Confirm && MessageBox.Show(this, "Uruchomić polecenie „" + command.Name + "”?", "Polecenie", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (session.Closed || current != session || !CheckCard()) return;
        var logon = command.AsUser && !ShellMode ? session.Logon : null;
        if (command.AsUser && !ShellMode && logon == null)
        {
            notice = "Polecenie wymaga zalogowania do konta Windows w menu Zaloguj.";
            RefreshChrome();
            return;
        }
        var ext = Path.GetExtension(command.Path).ToLowerInvariant();
        var system = Environment.SystemDirectory;
        var (program, arguments) = ext switch
        {
            ".ps1" => (Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -WindowStyle Hidden -File \"" + command.Path + "\" " + command.Arguments),
            ".cmd" or ".bat" => (Path.Combine(system, "cmd.exe"), "/c \"\"" + command.Path + "\" " + command.Arguments + "\""),
            _ => (command.Path, command.Arguments)
        };
        var directory = Path.GetDirectoryName(command.Path);
        Audit.Write("command_started", command.Name);
        notice = "Polecenie „" + command.Name + "” działa…";
        RefreshChrome();
        try
        {
            int code = await SessionCommand.RunAsync(program, arguments, directory, logon,
                session.CommandJobs, session.Lifetime.Token);
            Audit.Write("command_finished", command.Name + ": " + code);
            if (session.Closed || current != session || IsDisposed) return;
            notice = "Polecenie „" + command.Name + "” " + (code == 0 ? "zakończone." : "zakończone z kodem " + code + ".");
        }
        catch (OperationCanceledException) when (session.Closed || IsDisposed)
        {
            Audit.Write("command_stopped", command.Name);
            return;
        }
        catch (Exception ex)
        {
            Audit.Write("command_finished", command.Name + ": error");
            if (session.Closed || current != session || IsDisposed) return;
            notice = "Polecenie „" + command.Name + "” nie udało się uruchomić: " + ex.Message;
        }
        if (!IsDisposed) RefreshChrome();
    }

    /// <summary>Menu → Zamknij: close the Kiosk or start it again (same program, same configuration).</summary>
    private void AskCloseOrRestart()
    {
        var restart = new TaskDialogButton("Uruchom ponownie");
        var close = new TaskDialogButton("Zamknij");
        var page = new TaskDialogPage
        {
            Caption = "Kiosk", Heading = "Zamknąć Kiosk?",
            Text = "„Uruchom ponownie” zamyka Kiosk i od razu otwiera go od nowa (np. po zmianie konfiguracji).",
            Buttons = { restart, close, TaskDialogButton.Cancel }
        };
        var result = TaskDialog.ShowDialog(this, page);
        if (result == close) { Audit.Write("client_close_requested"); Close(); }
        else if (result == restart)
        {
            Audit.Write("client_restart_requested");
            closeConfirmed = !sessions.Values.Any(x => x.HasWork) || ConfirmEnd(sessions.Values);
            if (!closeConfirmed) return;
            // The new copy waits until this one has released its single-instance lock.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => "\"" + a + "\""))
            });
            Close();
        }
    }

    /// <summary>Menu → Odśwież ekrany: the screens' content starts again (pages reload, programs restart).</summary>
    private void RefreshScreens()
    {
        Audit.Write("screens_refreshed");
        StartIdleScreens();
        RefreshHosts();
    }

    /// <summary>A big number on every screen for a few seconds (the numbers used in Konfiguracja and "Pokaż na ekranie").</summary>
    private void ShowScreenNumbers()
    {
        var screens = OrderedScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            var label = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
                BackColor = KioskTheme.Accent, Bounds = new Rectangle(b.Left + b.Width / 2 - 110, b.Top + b.Height / 2 - 110, 220, 220)
            };
            label.Controls.Add(new Label
            {
                Text = (i + 1).ToString(), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 72, FontStyle.Bold), ForeColor = KioskTheme.OnAccent
            });
            var close = new System.Windows.Forms.Timer { Interval = 3000 };
            close.Tick += (_, _) => { close.Dispose(); label.Close(); label.Dispose(); };
            label.Show();
            close.Start();
        }
    }

    private void ShowView(Control view)
    {
        foreach (Control child in surface.Controls) child.Visible = child == view;
        view.BringToFront();
        view.Focus();
        (view as ApplicationView)?.BringToUser();
        visibleView = view;
        if (view != desktop && current != null) current.LastView = view;
        RefreshChrome();
    }

    private void ActivateWindow(string key)
    {
        if (!CanUseApps) return;
        var window = current!.Windows.FirstOrDefault(w => w.Key == key);
        if (window == null) return;
        if (HostOf(window.View) is { } host) { host.Activate(); (window.View as ApplicationView)?.BringToUser(); return; } // On another screen.
        // Like the Windows taskbar: clicking the active window shows the desktop.
        ShowView(visibleView == window.View ? desktop : window.View);
    }

    private void ActivateExisting(string key)
    {
        var window = current?.Windows.FirstOrDefault(w => w.Key == key);
        if (window != null && CanUseApps && HostOf(window.View) is { } host) { host.Activate(); (window.View as ApplicationView)?.BringToUser(); return; }
        if (window != null && CanUseApps) ShowView(window.View);
    }

    private void AddWindow(UserSession session, string key, string title, Control view)
    {
        view.Visible = false;
        if (view.Parent != surface) surface.Controls.Add(view);
        session.Windows.Add(new Window(key, title, view));
        if (session == current && !session.Locked) ShowView(view);
        // A window this person put on another screen before opens there again.
        session.Layout ??= ScreenLayouts.Load(session.Identity.Id);
        if (session.Layout.TryGetValue(key, out var screen) && screen > 1 && screen <= Screen.AllScreens.Length) PlaceWindow(session, key, screen, remember: false);
    }

    private void CloseWindow(string key, string reason, UserSession? session = null)
    {
        session ??= current;
        var window = session?.Windows.FirstOrDefault(w => w.Key == key);
        if (session == null || window == null) return;
        bool wasVisible = visibleView == window.View;
        if (wasVisible) ShowView(desktop);
        CloseWindowCore(session, window);
        if (wasVisible && session == current && !session.Locked && session.Windows.LastOrDefault() is { } next) ShowView(next.View);
        Audit.Write(reason, key);
        RefreshChrome();
    }

    private void CloseWindowCore(UserSession session, Window window)
    {
        session.Windows.Remove(window);
        if (window.Key.StartsWith("rdp:") && session.Rdp.Remove(window.Key[4..], out var rdp))
        {
            try { rdp.Host.Client.Disconnect(); }
            catch (Exception ex) { Audit.Write("disconnect_error", ex.Message); }
        }
        else if (window.Key.StartsWith("app:")) session.Applications.Remove(window.Key[4..]);
        else if (window.Key == BrowserKey) session.Browser = null;
        else if (window.Key == NotesKey) { session.Notes?.Editor.Clear(); session.Notes = null; }
        else if (window.Key == SettingsKey) settingsView = null;
        if (session.LastView == window.View) session.LastView = null;
        HostOf(window.View)?.Release();
        window.View.Parent?.Controls.Remove(window.View);
        window.View.Dispose();
        RefreshHosts();
    }

    // ---------------------------------------------------------------- remote desktops

    /// <summary>The remote desktop as the signed-in person: this program in RDP-only mode (see <see cref="RdpUserHost"/>).</summary>
    private ApplicationEntry RemoteDesktopAsUser(RdpConnection connection) => new()
    {
        Name = RdpUserHost.WindowName(connection),
        Path = Environment.ProcessPath!,
        Arguments = RdpUserHost.ArgumentsFor(connection, config.ConnectTimeoutSeconds, config.RequireCredentialPrompt)
    };

    private void OpenRdp(RdpConnection connection)
    {
        var key = "rdp:" + connection.Name;
        if (current?.Rdp.ContainsKey(connection.Name) == true) { ActivateExisting(key); return; }
        if (!CheckCard()) return;
        var session = current!;
        if (session.Logon != null)
        {
            // Every selected authentication mode runs in the person's Windows
            // context. The helper keeps the configured mode and user name.
            var windowName = RdpUserHost.WindowName(connection);
            if (session.Applications.ContainsKey(windowName)) { ActivateExisting("app:" + windowName); return; }
            _ = OpenApplicationAsync(RemoteDesktopAsUser(connection));
            return;
        }
        RdpHost? host = null;
        try
        {
            host = CreateRdp(connection, config.RequireCredentialPrompt);
            session.Rdp[connection.Name] = new RdpSession { Connection = connection, Host = host, ConnectingSince = DateTime.UtcNow };
            AddWindow(session, key, connection.Name, host);
            // Fresh COM control per connection and per person; never store a PIN/password or reuse credentials.
            host.Client.Connect();
            Audit.Write("connect_requested", connection.Name);
        }
        catch (Exception ex)
        {
            if (session.Windows.Any(w => w.Key == key)) CloseWindow(key, "connect_error", session);
            else if (host != null) { session.Rdp.Remove(connection.Name); surface.Controls.Remove(host); host.Dispose(); }
            notice = "Nie udało się połączyć z „" + connection.Name + "”: " + ex.Message;
            Audit.Write("connect_error", connection.Name + ": " + ex.Message);
            ShowView(desktop);
        }
    }

    private RdpHost CreateRdp(RdpConnection connection, bool promptForCredentials)
    {
        var host = new RdpHost { Dock = DockStyle.Fill, Visible = false };
        try
        {
            ((ISupportInitialize)host).BeginInit();
            surface.Controls.Add(host);
            ((ISupportInitialize)host).EndInit();
            host.CreateControl();
            RdpConfiguration.Apply(host, connection, surface.ClientSize, promptForCredentials);
            return host;
        }
        catch
        {
            surface.Controls.Remove(host); host.Dispose(); throw;
        }
    }

    /// <summary>An application closed with its own × (or exited): remove its Kiosk window and taskbar button.</summary>
    private void MonitorApplications()
    {
        ApplicationView.BeginPollCycle();
        try
        {
            foreach (var session in sessions.Values.ToArray())
            foreach (var (name, view) in session.Applications.ToArray())
                if (view.Started && !view.PollLifetime())
                    CloseWindow("app:" + name, "application_exited", session);
        }
        finally { ApplicationView.EndPollCycle(); }
    }

    private void MonitorRdp()
    {
        foreach (var session in sessions.Values.ToArray())
        foreach (var (name, rdp) in session.Rdp.ToArray())
        {
            int state = (int)rdp.Host.Client.Connected;
            if (state == 1) rdp.WasConnected = true;
            if (rdp.WasConnected && state == 0)
            {
                CloseWindow("rdp:" + name, "remote_disconnect", session);
                session.Notice = "Pulpit zdalny „" + name + "” został rozłączony.";
            }
            else if (!rdp.WasConnected && DateTime.UtcNow - rdp.ConnectingSince > TimeSpan.FromSeconds(config.ConnectTimeoutSeconds))
            {
                CloseWindow("rdp:" + name, "connection_timeout", session);
                session.Notice = "Nie udało się połączyć z „" + name + "” w ciągu " + config.ConnectTimeoutSeconds + " s.";
            }
        }
    }

    // ---------------------------------------------------------------- local windows

    private void OpenNotes()
    {
        if (current?.Notes != null) { ActivateExisting(NotesKey); return; }
        if (!CheckCard()) return;
        ShowNotes();
        Audit.Write("notepad_opened");
    }

    private void ShowNotes()
    {
        var session = current!;
        if (session.Notes == null) { session.Notes = new NotesView(); AddWindow(session, NotesKey, config.Applications.FirstOrDefault(a => a.IsBuiltInNotes)?.Name ?? "Notatnik", session.Notes); }
        else ShowView(session.Notes);
        session.Notes.Editor.Focus();
    }

    private ApplicationEntry EdgeAsUser => new()
    {
        Name = "Przeglądarka",
        Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
        Arguments = "--new-window " + config.BrowserUrl
    };

    private async Task OpenBrowserAsync()
    {
        if (!config.EnableBrowser) return;
        if (current?.Browser != null)
        {
            // Already on another screen: a new browser window for this one.
            if (HostOf(current.Browser) != null && current.Logon == null) await OpenAnotherAsync(BrowserKey); else ActivateExisting(BrowserKey);
            return;
        }
        if (!CheckCard()) return;
        var session = current!;
        if (session.Logon != null)
        {
            // Signed in as the person: their own Edge, with their profile, bookmarks and SSO (like runas).
            await OpenApplicationAsync(EdgeAsUser);
            return;
        }
        var policy = new SitePolicy(config.BrowserOnlyBookmarks, config.Bookmarks, config.BrowserAllowedDomains);
        var requested = new BrowserView(config.Bookmarks, policy, session.Identity.Thumbprints, windowsAccount: ShellMode);
        session.Browser = requested;
        AddWindow(session, BrowserKey, "Przeglądarka", requested);
        try
        {
            // With "only bookmarks" a start page outside the list opens the first bookmark instead.
            await requested.InitializeAsync(policy.Allows(config.BrowserUrl) ? config.BrowserUrl : config.Bookmarks[0].Url);
            if (session.Browser == requested && !requested.IsDisposed) Audit.Write("browser_opened");
        }
        catch (Exception ex)
        {
            if (session.Browser != requested) return; // Session ended while initializing.
            CloseWindow(BrowserKey, "browser_error", session);
            notice = "Nie udało się uruchomić przeglądarki: " + ex.Message;
            Audit.Write("browser_error", ex.Message);
        }
    }

    /// <param name="another">Open one more window even when the program is already open (e.g. one per screen).</param>
    private async Task OpenApplicationAsync(ApplicationEntry entry, bool another = false)
    {
        if (entry.IsBuiltInNotes) { OpenNotes(); return; }
        var key = "app:" + entry.Name;
        if (current?.Applications.TryGetValue(entry.Name, out var existing) == true)
        {
            bool alive = existing.HostedWindow == IntPtr.Zero || AppNative.IsWindow(existing.HostedWindow);
            // Already open on the main screen: show it. Open only on other screens (or asked for): one more window.
            if (alive && !another && HostOf(existing) == null) { ActivateExisting(key); return; }
            if (!alive) CloseWindow(key, "application_exited");
            else entry = entry with { Name = NextInstanceName(current!, entry.Name) };
        }
        if (!CheckCard()) return;
        try { await StartApplicationAsync(current!, entry); }
        catch (Exception) { } // Reported on the desktop by StartApplicationAsync.
    }

    private async Task StartApplicationAsync(UserSession session, ApplicationEntry entry)
    {
        if (sessions.Values.Any(other => other != session && (other.Logon?.Sid ?? "") == (session.Logon?.Sid ?? "") &&
            other.Applications.Values.Any(view => string.Equals(view.ProgramPath, Path.GetFullPath(entry.Path), StringComparison.OrdinalIgnoreCase))))
        {
            notice = "Ten program działa już w zachowanej sesji innej karty na tym samym koncie Windows. Zakończ tę sesję lub użyj osobnych kont w trybie WindowsShell.";
            throw new InvalidOperationException(notice);
        }
        var key = "app:" + entry.Name;
        var requested = new ApplicationView(session);
        session.Applications[entry.Name] = requested;
        AddWindow(session, key, entry.Name, requested);
        try
        {
            await requested.StartAsync(entry, session.Logon);
            if (!requested.IsDisposed) { lastChrome = null; RefreshChrome(); Audit.Write("application_opened", entry.Name); }
        }
        catch (Exception ex)
        {
            if (requested.IsDisposed) return;
            CloseWindow(key, "application_error", session);
            notice = "Nie można osadzić aplikacji „" + entry.Name + "”: " + ex.Message;
            Audit.Write("application_error", entry.Name + ": " + ex.Message);
            throw;
        }
    }

    /// <summary>
    /// An authenticated Windows administrator, or a listed card after its selected certificate key has signed a
    /// fresh challenge. Public certificate fields and card UIDs alone cannot grant administrator privileges.
    /// </summary>
    private bool IsAdmin(UserSession session, Config? settings = null)
    {
        settings ??= config;
        bool listed = settings.AdminCards.Any(a => a.Id.Equals(session.Identity.Id, StringComparison.OrdinalIgnoreCase));
        if (listed && (session.Identity.Id.StartsWith("win:", StringComparison.OrdinalIgnoreCase) && ShellMode ||
            session.Identity.Id.StartsWith("cert:", StringComparison.OrdinalIgnoreCase) && session.AdminCertificateVerified ||
            settings.TestMode)) return true;
        if (settings.AllowWindowsAdministrators && (session.IsWindowsAdmin || session.Logon?.IsAdministrator == true)) return true;
        return settings.TestMode && settings.AdminCards.Length == 0;
    }

    private static bool IsConfiguredAdminCertificate(UserSession session, Config settings) =>
        session.Identity.Id.StartsWith("cert:", StringComparison.OrdinalIgnoreCase) &&
        settings.AdminCards.Any(a => a.Id.Equals(session.Identity.Id, StringComparison.OrdinalIgnoreCase));

    private async Task OpenSettingsAsync()
    {
        if (settingsView != null) { ActivateExisting(SettingsKey); return; }
        // Open windows stay as they are; saved settings apply to what is opened next.
        if (configPath == null || !CanUseApps || current is not { } candidate || !CheckCard()) return;
        if (!IsAdmin(candidate) && IsConfiguredAdminCertificate(candidate, config))
        {
            if (verifying) return;
            verifying = true;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            pinCheck = cancel;
            try
            {
                var result = await CardPin.VerifyAsync(reader.ActiveReader, Handle, cancel.Token, candidate.Identity.Id[5..]);
                if (candidate.Closed || IsDisposed || candidate != current) return;
                if (result.Code != CardPin.Verified)
                {
                    candidate.Notice = result.Message;
                    Audit.Write("admin_card_proof_failed", result.Code.ToString());
                    return;
                }
                if (!cardPresent() || (identifyCard() ?? UncertifiedCard())?.Id != candidate.Identity.Id) return;
                cardKey = reader.Snapshot(config.ReaderName);
                candidate.AdminCertificateVerified = true;
                Audit.Write("admin_card_proof_verified");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Audit.Write("admin_card_proof_error", ex.Message); candidate.Notice = ex.Message; return; }
            finally { pinCheck = null; verifying = false; if (!IsDisposed) RefreshChrome(); }
        }
        if (candidate.Closed || candidate != current || !CanUseApps || !IsAdmin(candidate) || !CheckCard()) return;
        var session = current!;
        var view = new SettingsView(config, session.Identity, updated => IsAdmin(session, updated));
        view.IdentifyScreensRequested += ShowScreenNumbers;
        settingsView = view;
        view.CancelRequested += () => CloseWindow(SettingsKey, "settings_closed", session);
        view.SaveRequested += updated =>
        {
            Configuration.Save(configPath, updated);
            bool modeChanged = updated.SessionMode != config.SessionMode;
            // A live change must not convert an existing card session into the
            // Windows identity session. Apply that transition only at startup.
            config = updated with { SessionMode = config.SessionMode };
            lastChrome = null;
            if (modeChanged) notice = "Zmiana trybu sesji zacznie działać po ponownym uruchomieniu Kiosku.";
            KioskTheme.SetAccent(updated.AccentColor);
            Invalidate(true);
            Audit.Write("settings_saved");
            StartIdleScreens();
            CloseWindow(SettingsKey, "settings_closed", session);
        };
        AddWindow(session, SettingsKey, "Konfiguracja", view);
    }

}

// IMsRdpExtendedSettings (IUnknown): property bag used for EnableRdsAadAuth (Microsoft Entra sign-in).
[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}
