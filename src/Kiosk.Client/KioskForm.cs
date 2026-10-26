using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// The kiosk shell for a shared PC: a desktop, one Menu button on the bottom taskbar and embedded
/// windows (remote desktops, browser, notes, applications). Every card gets its own session.
/// Removing the card parks an application session (windows keep running, hidden); another card gets
/// its own session at once. In WindowsShell and SelfService/WindowsSession, Windows owns sign-in and lock.
/// </summary>
internal sealed partial class KioskForm : Form
{
    private sealed class RdpSession
    {
        internal required RdpConnection Connection { get; init; }
        internal required RdpHost Host { get; init; }
        internal long ConnectingSinceMilliseconds { get; init; }
        internal bool WasConnected { get; set; }
        internal int StateReadFailures { get; set; }
    }

    private sealed record Window(string Key, string Title, Control View);

    private sealed class UserSession
    {
        internal required CardIdentity Identity { get; init; }
        internal List<Window> Windows { get; } = new();
        internal Dictionary<string, RdpSession> Rdp { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, ApplicationView> Applications { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal SessionCommandScope Commands { get; } = new();
        internal BrowserView? Browser { get; set; }
        internal NotesView? Notes { get; set; }
        internal FilesView? Files { get; set; }
        internal Control? LastView { get; set; }
        internal bool Locked { get; set; } = true;
        internal bool Verified { get; set; }
        internal bool AutoConnected { get; set; }
        internal long InactiveSinceMilliseconds { get; set; } = Environment.TickCount64;
        internal string? Notice { get; set; }
        internal bool HasWork => Notes?.Dirty == true || Applications.Count > 0 || Rdp.Count > 0 || Commands.HasRunning;
        /// <summary>The card's account (UPN from its certificate) is in this PC's local Administrators group.</summary>
        internal bool IsWindowsAdmin { get; set; }
        /// <summary>The person's own Windows / Entra account after "Zaloguj" (SignInAsUser); their programs run as it.</summary>
        internal UserLogon? Logon { get; set; }
        internal string Name => Logon is { FriendlyName.Length: > 0 } logon ? logon.FriendlyName : People.FriendlyName("", Identity.Name, Identity.Upn);
        /// <summary>Which screen each window goes on (window key → 1 = main, 2… = others); remembered per card.</summary>
        internal Dictionary<string, int>? Layout { get; set; }
        /// <summary>The screens' content was opened as this person (editable) after their first unlock.</summary>
        internal bool ScreensOpened { get; set; }
    }

    private const string BrowserKey = "browser", NotesKey = "notes", FilesKey = "files", SettingsKey = "settings";
    // As saved in Konfiguracja (unticked entries included) and what Kiosk works with (only the ticked ones).
    private Config fullConfig = null!, activeConfig = null!;
    private Config config { get => activeConfig; set { fullConfig = value; activeConfig = value.Active(); PageScripts.Actions = activeConfig.PageActions; } }
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
    private long nextWindowMonitorMilliseconds;
    // The messages' folder is read again every minute, so a change on the share shows up without restarting.
    private readonly System.Windows.Forms.Timer newsTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer statsTimer = new() { Interval = 60 * 60_000 };
    /// <summary>Each load's number: a load that is overtaken (Konfiguracja saved meanwhile) does not show its older result.</summary>
    private int newsLoad;
    private bool newsShown;
    private string screenBarsReported = "";
    // Komunikaty → "Ekrany wygaszacza": the bars also shown on the screens' content (empty = off).
    private List<NewsItem> screenBars = [];
    private HashSet<Image> displayedNewsPictures = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Image> retiredNewsPictures = new(ReferenceEqualityComparer.Instance);
    private SharePointNews? sharePointNews;
    private readonly CardReader reader = new();
    private Func<CardIdentity?> identifyCard;
    private Func<bool> cardPresent;
    private string? cardKey;
    private bool requireRemoval, inTick, closeConfirmed, verifying;
    private CancellationTokenSource? pinCheck;
    private string? shellCardKey;
    private long? disconnectedSinceMilliseconds;
    private long? shellLockRequestedAtMilliseconds;
    private bool shellUnlockNotified;
    private bool shellLockNotified;
    private readonly SignOutRetry shellSignOut = new();
    private IntPtr shellNotificationHandle;
    private long nextShellNotificationAttemptMilliseconds;
    private bool ShellMode => config.SessionMode == SessionMode.WindowsShell;
    private bool WindowsSessionMode => ShellMode || SelfServiceWindowsSession;
    private string? notice;
    private bool modeRestartPending;

    private bool CanUseApps => current is { Locked: false } && shellSignOut.Reason == null;

    /// <summary>The PC's own client.json when the configuration comes from a server (it keeps ConfigSource).</summary>
    private Config? localConfig;

    internal KioskForm(Config config, string? configPath = null, CentralConfig.Loaded? loaded = null)
    {
        this.config = config;
        // SelfService with Windows sign-in has no card or application windows to poll at 250 ms.
        if (SelfServiceWindowsSession) timer.Interval = 1000;
        this.configPath = configPath;
        localConfig = loaded?.Local;
        notice = loaded?.Problem;
        identifyCard = IdentifyPresentCard;
        cardPresent = () => PresentReader() != null;
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
        Shown += (_, _) => { timer.Start(); Watchdog.Watch(this); RotateScreens(); StartIdleScreens(); statsTimer.Start(); _ = LoadNewsAsync(); newsTimer.Start(); };
        newsTimer.Tick += (_, _) => _ = LoadNewsAsync();
        statsTimer.Tick += (_, _) => ExportStats();
        desktop.News.ActionRequested += item => _ = RunNewsActionAsync(item);
        desktop.Help.ActionRequested += item => _ = RunHelpAsync(item);
        desktop.Tiles.ActionRequested += item => _ = RunHelpAsync(item);
        desktop.AccountRequested += PortalAccount;
        desktop.PageCloseRequested += () => { helpBrowser?.Close(); CloseCardPage(); };
        FormClosing += (_, e) =>
        {
            if (!closeConfirmed && !ConfirmEnd(sessions.Values)) { e.Cancel = true; return; }
            timer.Stop(); CloseAllSessions("client_exit"); reader.Dispose();
        };
        FormClosed += (_, _) =>
        {
            timer.Dispose(); newsTimer.Dispose(); statsTimer.Dispose(); screenRefresh.Dispose(); menu.Dispose();
            StopIdleScreens();
            NewsSlides.Publish([], config.NewsSeconds);
            desktop.SetNews([], config.NewsSeconds);
            screenBars = [];
            sharePointNews?.Dispose();
            ReplaceDisplayedNewsPictures([]);
            foreach (var host in hosts.Values) host.Dispose();
        };
        if (WindowsSessionMode) StartWindowsSession();
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
                if (shellSignOut.Reason != null) return;
                MonitorWindows();
                return;
            }
            if (SelfService)
            {
                SelfServiceTick();
                UpdateMainIdle();
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
                    else CardInserted(identifyCard() ?? UncertifiedCard());
                }
            }
            if (current is { Locked: false })
            {
                var idle = Native.IdleMilliseconds();
                if (idle >= (uint)config.LockAfterSeconds * 1000u) Lock("idle_lock", idle);
            }
            ExpireSessions();
            MonitorWindows();
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
    private CardIdentity UncertifiedCard()
    {
        var uid = CardPin.ReadUid(reader.ActiveReader);
        return new CardIdentity(uid != null ? "uid:" + uid : "card:" + reader.ActiveReader + "|" + reader.ActiveAtr, "Karta");
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
        if (current?.Identity.Id == identity.Id) return;
        if (current != null) Park(current, "card_changed");
        if (!sessions.TryGetValue(identity.Id, out var session))
        {
            session = new UserSession { Identity = identity };
            _ = CheckWindowsAdministratorAsync(session);
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
        DismissQuestions();
        if (settingsView != null && session.Windows.Any(w => w.Key == SettingsKey)) CloseWindow(SettingsKey, "settings_closed", session);
        if (!session.Locked) session.InactiveSinceMilliseconds = Environment.TickCount64;
        session.Locked = true;
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
        DismissQuestions();
        session.Locked = true;
        session.InactiveSinceMilliseconds = Environment.TickCount64 - idleMilliseconds;
        ShowView(desktop);
        Audit.Write(reason, session.Identity.Name);
        RefreshChrome();
    }

    private void Unlock(UserSession session)
    {
        if (!session.ScreensOpened && !ShellMode && !SelfService && config.IdleScreens.Length > 0) { session.ScreensOpened = true; BeginInvoke(() => _ = OpenScreenContentsAsync(session)); }
        session.Locked = false;
        session.Verified = true;
        session.Notice = null;
        Audit.Write("unlocked", session.Identity.Name);
        Stats.Record(Stats.SignIn, ShellMode || SelfServiceWindowsSession ? "Windows" : "Karta", person: session.Identity.Name);
        ShowView(session.LastView != null && session.Windows.Any(w => w.View == session.LastView) ? session.LastView : desktop);
        if (!SelfService && !session.AutoConnected)
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
        if (!ok || current != session) { RefreshChrome(); return; }
        // The card may have been swapped while the dialog was open.
        if (!cardPresent() || (identifyCard()?.Id ?? session.Identity.Id) != session.Identity.Id)
        {
            cardKey = null; // Re-evaluate the card on the next tick.
            RefreshChrome();
            return;
        }
        cardKey = reader.Snapshot(config.ReaderName); // A PIN check resets the card; that is not a new insertion.
        if (config.SignInAsUser && session.Logon == null)
        {
            // The card alone: Windows signs the person in with the card and its PIN, no login and no password.
            var signedIn = await SignInWithCardAsync();
            if (signedIn == null) { session.Notice ??= "Logowanie anulowane."; RefreshChrome(); return; }
            if (signedIn.SignIn == null)
            {
                session.Notice = "Nie udało się zalogować kartą: " + signedIn.Message;
                Audit.Write("card_signin_failed", signedIn.Message);
                RefreshChrome();
                return;
            }
            session.Logon = UserLogon.FromCard(signedIn.SignIn);
            Audit.Write("signed_in_with_card", session.Logon.DisplayName);
            await PrepareProfileAsync(session);
        }
        Unlock(session);
    }

    /// <summary>
    /// A new card without a FIDO2 PIN: the person chooses one here (typed twice) and the card stores it.
    /// Returns the new PIN to continue with, or null when the card already had one (or setting it was cancelled).
    /// </summary>
    private async Task<string?> EnsureCardPinAsync(string readerName, int idleSeconds = 0)
    {
        var state = await Task.Run(() => CardService.Call(new CardService.Request { Op = "haspin", Reader = readerName }));
        if (state.Code != 0x35) return null;
        string message = "Karta nie ma jeszcze PIN-u. Ustaw nowy PIN (od 4 znaków) – będziesz go podawać przy logowaniu.";
        while (await desktop.AskPinAsync(message, cardPresent, idleSeconds) is { } pin)
        {
            if (pin.Length < 4) { message = "PIN musi mieć co najmniej 4 znaki. Ustaw nowy PIN."; continue; }
            var again = await desktop.AskPinAsync("Wpisz nowy PIN jeszcze raz.", cardPresent, idleSeconds);
            if (again == null) break;
            if (again != pin) { message = "PIN-y się różnią. Ustaw nowy PIN."; continue; }
            var set = await Task.Run(() => CardService.Call(new CardService.Request { Op = "setpin", Reader = readerName, Pin = pin }));
            Audit.Write(set.Code == 0 ? "card_pin_set" : "card_pin_set_failed", set.Code + " " + set.Message);
            if (set.Code == 0) return pin;
            message = set.Message + " Spróbuj ponownie.";
        }
        return null;
    }

    /// <summary>Someone who never signed in to this PC: create their Windows profile before Edge and the apps start.</summary>
    private async Task PrepareProfileAsync(UserSession session)
    {
        var logon = session.Logon!;
        if (logon.ProfileDirectory != null) return;
        session.Notice = "Pierwsze logowanie na tym komputerze: tworzę profil Windows…";
        RefreshChrome();
        bool ready;
        try { ready = await Task.Run(logon.EnsureProfile); }
        catch (Exception ex) { ready = false; Audit.Write("profile_failed", ex.Message); }
        session.Notice = ready ? null
            : "Windows nie utworzył profilu dla " + logon.DisplayName + ". Zaloguj się raz do Windows na tym komputerze kartą, potem wróć do Kiosku.";
        RefreshChrome();
    }

    /// <summary>
    /// Card + PIN sign the person in to Windows (see <see cref="FidoSignIn"/>): the Edge-FIDO2 sign-in screen inside the
    /// Kiosk's card; null when cancelled. The Kiosk itself never talks to the card meanwhile: that would break
    /// Windows' exchange with it.
    /// </summary>
    private async Task<FidoSignIn.Outcome?> SignInWithCardAsync()
    {
        verifying = true;
        // The PIN screen is drawn in the card, where a Szybka pomoc page would hide it.
        helpBrowser?.Close();
        desktop.ShowSignIn(true);
        try { return await FidoSignIn.SignInAsync(desktop.SignInPlace, desktop.DeviceDpi); }
        finally { verifying = false; desktop.ShowSignIn(false); cardKey = reader.Snapshot(config.ReaderName); }
    }

    private async Task<CardPin.Result> VerifyFidoPinAsync()
    {
        var readerName = reader.ActiveReader;
        string message = "Wpisz PIN karty.";
        var first = await EnsureCardPinAsync(readerName);
        while ((first ?? await desktop.AskPinAsync(message, cardPresent)) is { } pin)
        {
            first = null;
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
            // Signing in with the card checks its PIN itself (VerifyAndUnlockAsync).
            return true;
        }
        switch (config.UnlockMethod)
        {
            case UnlockMethod.CardAndWindowsHello:
            {
                // Windows Hello confirms the person at the device; the Windows session itself is never locked.
                var result = await WindowsHello.VerifyAsync(Handle, "Potwierdź, aby otworzyć sesję Kiosku");
                if (result == WindowsHello.Result.Verified) return true;
                if (result is WindowsHello.Result.DeviceNotPresent or WindowsHello.Result.NotConfiguredForUser or WindowsHello.Result.DisabledByPolicy)
                {
                    session.Notice = "Windows Hello jest niedostępne (" + result + "). Zmień sposób logowania w Konfiguracji.";
                    Audit.Write("unlock_hello_unavailable", result.ToString());
                    return false;
                }
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
                    var result = session.Identity.Id.StartsWith("cert:")
                        ? await CardPin.VerifyAsync(reader.ActiveReader, Handle, cancel.Token)
                        : await VerifyFidoPinAsync();
                    if (result.Code == CardPin.Verified) return true;
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
        var now = Environment.TickCount64;
        var limitMilliseconds = config.ChangeUserAfterSeconds * 1000L;
        foreach (var session in sessions.Values.ToArray())
        {
            if (!session.Locked || verifying && session == current) continue;
            if (now - session.InactiveSinceMilliseconds < limitMilliseconds) continue;
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
        if (session == current) { menu.HideMenu(); DismissQuestions(); ShowView(desktop); current = null; }
        session.Commands.Dispose();
        foreach (var window in session.Windows.ToArray()) CloseWindowCore(session, window);
        session.Logon?.Dispose(); // Forget the password with the session.
        session.Logon = null;
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
        if (reader.Snapshot(config.ReaderName) == null) { cardKey = null; return false; }
        notice = null;
        return true;
    }

    private bool ConfirmEnd(IEnumerable<UserSession> affected) =>
        !affected.Any(s => s.HasWork) ||
        Ask("Zakończyć sesję? Niezapisane notatki zostaną usunięte, a pulpity zdalne, aplikacje i polecenia zamknięte.", "Wyloguj");

    private bool ConfirmClose(string key)
    {
        string? question = key == NotesKey && current?.Notes?.Dirty == true ? "Zamknąć notatnik? Niezapisany tekst zostanie usunięty." :
            key.StartsWith("app:") ? "Zamknąć aplikację? Niezapisane dane mogą zostać utracone." :
            key.StartsWith("rdp:") ? "Rozłączyć ten pulpit zdalny?" : null;
        return question == null || Ask(question, "Zamknij okno");
    }

    /// <summary>
    /// A Tak/Nie question of the person working now. Answered "Tak" only counts while the same person still works:
    /// a card taken away or the lock closes the question (see <see cref="DismissQuestions"/>) and it reads as "Nie".
    /// </summary>
    private bool Ask(string question, string title)
    {
        var asker = current;
        return MessageBox.Show(this, question, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes &&
            current == asker && (asker == null || !asker.Locked);
    }

    private ContextMenuStrip? taskMenu;

    /// <summary>
    /// The lock, a card taken away or a session closed: questions and menus of the person who left close with "Nie",
    /// so nobody else finds them over the lock screen (they block the window, and their "Tak" would act for someone else).
    /// </summary>
    private void DismissQuestions()
    {
        taskMenu?.Close();
        AppNative.EnumThreadWindows(AppNative.GetCurrentThreadId(), (window, _) =>
        {
            var name = new System.Text.StringBuilder(16);
            if (AppNative.GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32770")
            {
                AppNative.PostMessage(window, 0x0111, (IntPtr)7, IntPtr.Zero);   // WM_COMMAND IDNO (a Tak/Nie box has no Anuluj)
                AppNative.PostMessage(window, 0x0111, (IntPtr)2, IntPtr.Zero);   // IDCANCEL: other questions, file dialogs
            }
            return true;
        }, IntPtr.Zero);
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
            // Another embedded browser window for this session and Windows account.
            var title = NextInstanceName(session, "Przeglądarka");
            var view = new BrowserView(config.Bookmarks,
                new SitePolicy(config.BrowserOnlyBookmarks, config.Bookmarks, config.BrowserAllowedDomains),
                () => CurrentBrowserCertificates(session), windowsAccount: ShellMode,
                expectedCertificateUpn: ShellMode ? session.Identity.Upn : null);
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
        menuStrip.Closed += (_, _) => { if (taskMenu == menuStrip) taskMenu = null; BeginInvoke(menuStrip.Dispose); };
        taskMenu = menuStrip;
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
        if (ShellMode || SelfServiceWindowsSession || config.IdleScreens.Length == 0) return;
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
            window.SetBars(screenBars);
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
        foreach (var window in idleScreens) window.Dispose();
        idleScreens.Clear();
        idleByScreen.Clear();
        mainIdle = null;
    }

    /// <summary>
    /// After sign-in each screen's content opens again as the signed-in person: the same page or program, but
    /// editable and under their account (so its own logs show who changed what). Locking brings back the
    /// read-only content; the person's windows wait on their screens.
    /// </summary>
    private async Task OpenScreenContentsAsync(UserSession session)
    {
        var screens = OrderedScreens;
        for (int n = 1; n <= screens.Length; n++)
        {
            var content = config.IdleScreens.FirstOrDefault(s => s.Screen == n) ?? config.IdleScreens.FirstOrDefault(s => s.Screen == 0);
            if (content == null || current != session || session.Locked) continue;
            var title = "Ekran " + n;
            try
            {
                if (NewsSlides.IsAddress(content.Url)) continue;   // Komunikaty stay the Kiosk's own slides.
                if (content.Url.Length > 0 && session.Logon == null)
                {
                    // Without a Windows account for the person: the Kiosk browser, signed in by the person on the page.
                    var key = "web:" + n;
                    var view = new BrowserView([], null, () => CurrentBrowserCertificates(session),
                        windowsAccount: ShellMode, expectedCertificateUpn: ShellMode ? session.Identity.Upn : null);
                    AddWindow(session, key, title, view);
                    if (n > 1) PlaceWindow(session, key, n, remember: false);
                    await view.InitializeAsync(content.Url);
                }
                else
                {
                    var entry = content.Url.Length > 0 ? EdgeForScreen(session.Logon!, n, content.Url)
                        : new ApplicationEntry { Name = title, Path = content.Path, Arguments = content.Arguments };
                    session.Layout ??= ScreenLayouts.Load(session.Identity.Id);
                    if (n > 1) session.Layout["app:" + title] = n; // Opens on its own screen.
                    await StartApplicationAsync(session, entry);
                }
                Audit.Write("screen_opened_as_user", title);
            }
            catch (Exception ex) { Audit.Write("screen_open_error", title + ": " + ex.Message); }
        }
        // The main screen's content (if any) is what the person sees first.
        if (current == session && !session.Locked && session.Windows.FirstOrDefault(w => w.Title == "Ekran 1") is { } main && HostOf(main.View) == null) ShowView(main.View);
    }

    /// <summary>The person's own Edge (their signed-in profile, SSO) for a screen. When their Edge already runs, it opens
    /// the window in that copy and the Kiosk view takes the new window over.</summary>
    private static ApplicationEntry EdgeForScreen(UserLogon logon, int n, string url)
    {
        return new ApplicationEntry
        {
            Name = "Ekran " + n,
            Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Arguments = "--new-window " + url
        };
    }

    private long? mainIdleHeldAtMilliseconds;
    /// <summary>Self-service: when the card (or a USB key) was put on the reader.</summary>
    private long? cardPlacedAtMilliseconds;

    /// <summary>
    /// Self-service: a card just put on the reader means someone is there, but only for the screensaver's time.
    /// A card left lying there (or a USB key that stays in the port) no longer holds the screensaver back.
    /// </summary>
    private bool CardJustPlaced => SelfService && cardKey != null && cardPlacedAtMilliseconds is { } placed &&
        Environment.TickCount64 - placed < config.IdleAfterSeconds * 1000L;

    /// <summary>For the log: what keeps the Kiosk in front of the main screen's screensaver.</summary>
    private string MainIdleReason(uint idle)
    {
        var why = new List<string>();
        if (current is { Locked: false }) why.Add("zalogowana sesja");
        if (verifying) why.Add("sprawdzanie PIN-u");
        if (settingsView != null) why.Add("Konfiguracja otwarta");
        if (desktop.AskingPin) why.Add("pole PIN");
        if (helpBrowser != null) why.Add("strona otwarta");
        if (SelfService && cardLogon != null) why.Add("zalogowany kartą");
        if (CardJustPlaced) why.Add("karta właśnie położona");
        return (why.Count == 0 ? "nic" : string.Join(", ", why)) + "; bezczynność " + idle / 1000 + " s, film " + NewsVideo.AnyPlaying +
               ", wejście " + (InputWatch.Running ? InputWatch.IdleMilliseconds / 1000 + " s" : "-");
    }

    /// <summary>Main screen: input brings up the Kiosk (sign-in / lock screen); left alone while locked or signed out it returns to the content.</summary>
    private void UpdateMainIdle()
    {
        // While Konfiguracja is open the screens accept input, so an administrator can sign their pages in (read-only account).
        // Only while that administrator still works: Konfiguracja left open under the lock makes no screen usable for others.
        bool settings = settingsView != null && CanUseApps;
        foreach (var screen in idleScreens) if (!screen.IsDisposed) screen.Interactive = settings;
        if (mainIdle == null || mainIdle.IsDisposed) return;
        // Signed in and working, checking a PIN or in Konfiguracja: the Kiosk stays. Locked or nobody there: back to the content.
        bool someone = current is { Locked: false } || verifying || settings || desktop.AskingPin || helpBrowser != null || SelfService && cardLogon != null || CardJustPlaced;
        uint idle = Native.IdleMilliseconds();
        if (mainIdle.Visible)
        {
            if (someone || idle < 700 && mainIdle.ShownAtMilliseconds is { } shown && Environment.TickCount64 - shown > 1000)
            {
                Audit.Write("main_idle_hidden", MainIdleReason(idle));
                mainIdle.Hide(); Activate(); desktop.FocusSignIn();
            }
        }
        else if (someone && idle >= (uint)config.IdleAfterSeconds * 1000u)
        {
            // Why the screensaver waits although nobody touched anything (at most every 5 minutes).
            if (mainIdleHeldAtMilliseconds is not { } held || Environment.TickCount64 - held > 300_000)
            { mainIdleHeldAtMilliseconds = Environment.TickCount64; Audit.Write("main_idle_held", MainIdleReason(idle)); }
        }
        else if (!someone && idle >= (uint)config.IdleAfterSeconds * 1000u)
        {
            mainIdle.Show();
            // Whoever comes next starts on "Strona główna", not in the category the last person left open.
            desktop.GoHome();
        }
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

    private void ObserveWindowsSessionChange(Message message)
    {
        if (message.Msg == WindowsSession.SessionChangeMessage && WindowsSessionMode)
        {
            var change = message.WParam.ToInt32();
            if (change is WindowsSession.SessionLock or WindowsSession.ConsoleDisconnect or WindowsSession.RemoteDisconnect)
            {
                disconnectedSinceMilliseconds ??= Environment.TickCount64;
                shellLockRequestedAtMilliseconds = null;
                shellUnlockNotified = false;
                if (change == WindowsSession.SessionLock) shellLockNotified = true;
            }
            else if (change == WindowsSession.SessionUnlock)
            {
                shellLockRequestedAtMilliseconds = null;
                shellCardKey = null;
                shellUnlockNotified = true;
                shellLockNotified = false;
                // Keep the time spent away until ShellTick checks the retention limit.
            }
            else if (change == WindowsSession.RemoteConnect) shellCardKey = null;
        }
    }

    private void TryRegisterShellNotifications()
    {
        if (shellNotificationHandle != IntPtr.Zero || !IsHandleCreated) return;
        var handle = Handle;
        if (WindowsSession.RegisterNotifications(handle)) shellNotificationHandle = handle;
        else nextShellNotificationAttemptMilliseconds = Environment.TickCount64 + 5000;
    }

    private void ShellTick()
    {
        if (shellSignOut.Reason is { } pendingReason) { SignOutWindows(pendingReason); return; }
        var now = Environment.TickCount64;
        if (shellNotificationHandle == IntPtr.Zero && now >= nextShellNotificationAttemptMilliseconds) TryRegisterShellNotifications();
        var state = WindowsSession.State();
        if (state is WindowsSession.Availability.Locked or WindowsSession.Availability.Disconnected)
        {
            disconnectedSinceMilliseconds ??= now;
            shellLockRequestedAtMilliseconds = null;
        }
        if (WindowsSession.RetentionExpired(disconnectedSinceMilliseconds, now, config.ChangeUserAfterSeconds))
        {
            SignOutWindows("session_expired");
            return;
        }
        if (disconnectedSinceMilliseconds != null && shellLockRequestedAtMilliseconds == null &&
            WindowsSession.CanResume(state, shellUnlockNotified, shellLockNotified))
        {
            disconnectedSinceMilliseconds = null;
            shellCardKey = null;
            Audit.Write("windows_session_reconnected", current?.Identity.Name ?? "");
        }
        if (disconnectedSinceMilliseconds == null) shellUnlockNotified = false;
        if (disconnectedSinceMilliseconds != null) return;
        if (shellLockRequestedAtMilliseconds is { } requested)
        {
            // A lock request is asynchronous. Fail closed if Windows never confirms it.
            if (now - requested >= 5000) SignOutWindows("windows_lock_unconfirmed");
            return;
        }
        if (config.DisconnectOnCardRemoval)
        {
            var observedCard = reader.Snapshot(config.ReaderName);
            if (shellCardKey == null) shellCardKey = observedCard;
            else if (observedCard != shellCardKey && !verifying) { DisconnectWindows("card_removed_or_changed"); return; }
        }
        else shellCardKey = null;
        if (Native.IdleMilliseconds() >= (uint)config.LockAfterSeconds * 1000u) DisconnectWindows("idle_lock");
    }

    /// <summary>Lock the console or disconnect RDP while retaining the person's Windows session.</summary>
    private void DisconnectWindows(string reason)
    {
        menu.HideMenu();
        DismissQuestions();
        shellUnlockNotified = false;
        Audit.Write(reason, current?.Identity.Name ?? "");
        try { WindowsSession.LockOrDisconnect(); shellLockRequestedAtMilliseconds = Environment.TickCount64; }
        catch (Exception ex)
        {
            notice = "Nie udało się zablokować sesji Windows: " + ex.Message;
            Audit.Write("windows_lock_failed", ex.Message);
            SignOutWindows("windows_lock_failed");
        }
    }

    private void SignOutWindows(string reason)
    {
        bool firstRequest = shellSignOut.Reason == null;
        if (firstRequest)
        {
            Audit.Write(reason, current?.Identity.Name ?? "");
            closeConfirmed = true;
            timer.Interval = 1000;
            notice = null;
            if (SelfServiceWindowsSession) { helpBrowser?.Close(); CloseCardPage(); }
        }
        if (!shellSignOut.Request(reason, Environment.TickCount64, WindowsSession.SignOut, out var failure)) return;
        if (failure != null)
        {
            notice = "Nie udało się wylogować z Windows: " + failure.Message;
            Audit.Write("signout_failed", failure.Message);
        }
        if (firstRequest && !IsDisposed)
        {
            menu.HideMenu();
            DismissQuestions();
            if (visibleView != desktop) ShowView(desktop);
            else RefreshChrome();
        }
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
        taskbar.Centered = config.TaskbarAlignment == TaskbarAlignment.Center;
        desktop.SetBranding(config.BrandTitle, config.BrandSubtitle, config.BrandLogo);
        TileIcons.SetCategories(config.MenuCategories);
        desktop.SetHelp(config.HelpActions);
        desktop.Help.SignedIn = shellSignOut.Reason == null && (!SelfService || SelfServiceWindowsSession || cardLogon != null);
        desktop.Portal = SelfService;
        RefreshHosts();
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
        taskbar.Visible = !SelfService;   // Punkt samoobsługowy: no Menu, only Szybka pomoc on the start screen.
        if (SelfService) SelfServiceStatus();
        else if (shellSignOut.Reason != null)
            desktop.UpdateStatus("Wylogowywanie", notice ?? "Trwa kończenie sesji Windows.");
        else if (current is { Locked: true } locked)
        {
            var title = locked.Verified ? "Sesja zablokowana" : "Logowanie";
            var detail = locked.Name + "\n" + (locked.Notice ?? (verifying ? "Czekam na potwierdzenie…" : UnlockInstruction));
            desktop.UpdateStatus(title, detail, locked: true, locked.Verified ? "Wznów sesję" : "Zaloguj");
        }
        else if (current != null)
            desktop.UpdateStatus(current.Name, notice ?? current.Notice ?? "Otwórz Menu na dole, aby uruchomić pulpit zdalny, przeglądarkę lub aplikację.", person: current.Name);
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
        if (menu.HiddenAtMilliseconds is { } hidden && Environment.TickCount64 - hidden < 300) return; // The click that just closed it.
        PopulateMenu(CanUseApps ? "" : "Przyłóż kartę, aby uruchamiać pulpity zdalne i aplikacje.");
        menu.ShowAt(this, taskbar.MenuButton.RectangleToScreen(taskbar.MenuButton.ClientRectangle), taskbar.Centered);
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
            (s?.Rdp.ContainsKey(c.Name) == true ? "Otwarte · " : "") + c.Server + " · " + AuthenticationLabel(c.Authentication),
            () => OpenRdp(c), can, KioskIcons.Remote)).ToList();
        if (remote.Count == 0) remote.Add(new MenuItemSpec("Brak połączeń", "Dodaj je w Konfiguracji", () => { }, false));
        var apps = new List<MenuItemSpec>();
        // The built-in browser and Notatnik are ordinary entries of the application list, so they can be removed there.
        apps.AddRange(config.Applications.Select(a => a.IsBuiltInBrowser
            ? new MenuItemSpec(a.Name, s?.Browser != null ? "Otwarta" : "Internet", () => _ = OpenBrowserAsync(), can,
                KioskIcons.Browser, s?.Logon != null ? KioskIcons.ForProgram(EdgeAsUser.Path) : null)
            : a.IsBuiltInNotes
            ? new MenuItemSpec(a.Name, s?.Notes != null ? "Otwarty" : "Notatki tekstowe", OpenNotes, can, KioskIcons.Notes)
            : a.IsBuiltInFiles
            ? new MenuItemSpec(a.Name, s?.Files != null ? "Otwarte" : "Foldery i pliki", OpenFiles, can, KioskIcons.Files)
            : new MenuItemSpec(a.Name, s?.Applications.ContainsKey(a.Name) == true ? "Otwarta" : "Aplikacja", () => _ = OpenApplicationAsync(a), can,
                KioskIcons.App, KioskIcons.ForProgram(a.Path))));
        if (apps.Count == 0) apps.Add(new MenuItemSpec("Brak aplikacji", "Dodaj je w Konfiguracji", () => { }, false));
        var commandItems = config.Commands.Select(c => new MenuItemSpec(c.Name, "Polecenie", () => RunCommand(c), can,
            KioskIcons.Command, c.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? KioskIcons.ForProgram(c.Path) : null)).ToList();
        return [new("Pulpity zdalne", remote), new("Aplikacje", apps), new("Polecenia", commandItems)];
    }

    /// <summary>The menu's footer, as in Windows 11: Pulpit and Konfiguracja on the left; the power button
    /// (Zablokuj, Wyloguj, Uruchom ponownie, Wyłącz, Zamknij aplikację) on the right.</summary>
    private (List<MenuItemSpec> Left, List<MenuItemSpec> Power) BuildMenuFooter()
    {
        bool can = CanUseApps;
        var s = current;
        var left = new List<MenuItemSpec> { new("Pulpit", "Pokaż pulpit", () => ShowView(desktop), true, KioskIcons.Desktop) };
        if (configPath != null && s != null && IsAdmin(s))
            left.Add(new MenuItemSpec("Konfiguracja", "Ustawienia Kiosku", OpenSettings, can, KioskIcons.Settings));
        if (config.IdleScreens.Length > 0)
            left.Add(new MenuItemSpec("Odśwież ekrany", "Uruchom ponownie treść ekranów", RefreshScreens, true, KioskIcons.Refresh));
        var power = new List<MenuItemSpec>();
        if (can)
        {
            power.Add(new MenuItemSpec("Zablokuj", "Zablokuj sesję", () => Lock("manual_lock", 0), true, KioskIcons.Lock));
            power.Add(new MenuItemSpec("Wyloguj", "Zamknij swoją sesję", () => { if (current != null && ConfirmEnd([current])) Logout("manual_logout"); }, true, KioskIcons.Logout));
            power.Add(new MenuItemSpec("Uruchom ponownie", "Uruchom ponownie komputer", () => PowerOffComputer(restart: true), true, KioskIcons.Restart));
            power.Add(new MenuItemSpec("Wyłącz", "Wyłącz komputer", () => PowerOffComputer(restart: false), true, KioskIcons.Power));
        }
        // Closing the shell itself: in test mode, or for a Kiosk administrator.
        if (config.TestMode || s != null && IsAdmin(s))
            power.Add(new MenuItemSpec("Zamknij aplikację", "Zamknij lub uruchom ponownie Kiosk", AskCloseOrRestart, true, KioskIcons.CloseApp));
        return (left, power);
    }

    private void PopulateMenu(string subtitle)
    {
        var (left, power) = BuildMenuFooter();
        menu.Populate(subtitle, BuildMenu(), left, power);
    }

    /// <summary>Taskbar icon of a window: the program's own icon, or the icon of the Kiosk function.</summary>
    private static (string? Glyph, Image? Icon) IconFor(UserSession session, Window w)
    {
        if (w.Key.StartsWith("rdp:")) return (KioskIcons.Remote, null);
        if (w.Key == BrowserKey || w.Key.StartsWith("browser:") || w.Key.StartsWith("web:")) return (KioskIcons.Browser, null);
        if (w.Key == NotesKey) return (KioskIcons.Notes, null);
        if (w.Key == FilesKey) return (KioskIcons.Files, null);
        if (w.Key == SettingsKey) return (KioskIcons.Settings, null);
        if (w.View is ApplicationView app && app.ProgramPath.Length > 0) return (KioskIcons.App, KioskIcons.ForProgram(app.ProgramPath));
        return (KioskIcons.App, null);
    }

    /// <summary>Menu → Polecenia: runs a program or script in the background and shows how it ended.</summary>
    private void RunCommand(CommandEntry command)
    {
        if (!CanUseApps || current is not { } session) return;
        if (command.Confirm && !Ask("Uruchomić polecenie „" + command.Name + "”?", "Polecenie")) return;
        if (current != session || !CanUseApps) return;
        _ = RunCommandAsync(session, command);
    }

    private async Task RunCommandAsync(UserSession session, CommandEntry command)
    {
        if (!session.Commands.TryBegin(out var cancellation)) return;
        try
        {
            var (program, arguments) = command.Resolve();
            var directory = Path.GetDirectoryName(command.Path);
            Audit.Write("command_started", command.Name);
            notice = null;
            session.Notice = "Polecenie „" + command.Name + "” działa…";
            RefreshChrome();
            int code = await Task.Run(() => SessionCommand.RunAsync(program, arguments, directory,
                command.AsUser ? session.Logon : null, session.Commands, cancellation));
            Audit.Write("command_finished", command.Name + ": " + code);
            session.Notice = "Polecenie „" + command.Name + "” " + (code == 0 ? "zakończone." : "zakończone z kodem " + code + ".");
            if (current == session && !session.Locked && !IsDisposed) RefreshChrome();
        }
        catch (OperationCanceledException) { Audit.Write("command_cancelled", command.Name); }
        catch (Exception ex)
        {
            Audit.Write("command_failed", command.Name + ": " + ex.Message);
            session.Notice = "Polecenie „" + command.Name + "”: nie udało się uruchomić: " + ex.Message;
            if (current == session && !session.Locked && !IsDisposed) RefreshChrome();
        }
        finally { session.Commands.End(); }
    }

    /// <summary>Menu → Uruchom ponownie / Wyłącz: restarts or shuts down the computer (all sessions end).</summary>
    private void PowerOffComputer(bool restart)
    {
        string action = restart ? "Uruchomić ponownie komputer?" : "Wyłączyć komputer?";
        string text = sessions.Values.Any(x => x.HasWork)
            ? "Wszystkie sesje zostaną zakończone: niezapisane notatki zostaną usunięte, a pulpity zdalne, aplikacje i polecenia zamknięte."
            : "Wszystkie sesje zostaną zakończone.";
        if (!Ask(text, action)) return;
        Audit.Write(restart ? "computer_restart_requested" : "computer_shutdown_requested", current?.Identity.Name ?? "");
        if (config.TestMode) { notice = "Tryb testowy: komputer nie został " + (restart ? "uruchomiony ponownie." : "wyłączony."); RefreshChrome(); return; }
        closeConfirmed = true;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                (restart ? "/r" : "/s") + " /t 0") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            closeConfirmed = false;
            notice = "Nie udało się " + (restart ? "uruchomić ponownie" : "wyłączyć") + " komputera: " + ex.Message;
            Audit.Write("computer_power_failed", ex.Message);
            RefreshChrome();
        }
    }

    /// <summary>Menu → Zamknij aplikację: close the Kiosk or start it again (same program, same configuration).</summary>
    private void AskCloseOrRestart()
    {
        var restart = new TaskDialogButton("Uruchom ponownie");
        var close = new TaskDialogButton("Zamknij aplikację");
        var page = new TaskDialogPage
        {
            Caption = "Kiosk", Heading = "Zamknąć Kiosk?",
            Text = "„Uruchom ponownie” zamyka Kiosk i od razu otwiera go od nowa (np. po zmianie konfiguracji).",
            Buttons = { restart, close, TaskDialogButton.Cancel }
        };
        var asker = current;
        var result = TaskDialog.ShowDialog(this, page);
        if (current != asker || asker?.Locked == true) return;   // Closed by the lock: nothing happens.
        if (result == close) { Audit.Write("client_close_requested"); Close(); }
        else if (result == restart)
        {
            Audit.Write("client_restart_requested");
            closeConfirmed = !sessions.Values.Any(x => x.HasWork) || ConfirmEnd(sessions.Values);
            if (!closeConfirmed) return;
            RestartClient();
        }
    }

    private void RestartClient()
    {
        // The new copy waits until this one has released its single-instance lock.
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1)) start.ArgumentList.Add(argument);
        if (System.Diagnostics.Process.Start(start) == null) throw new InvalidOperationException("Nie udało się uruchomić nowej kopii Kiosku.");
        Close();
    }

    private HelpBrowserForm? helpBrowser;

    /// <summary>
    /// Szybka pomoc, locked or not: a help page (the person's browser when signed in, else a guest window),
    /// or the card itself: change its PIN, set it up again, reset it.
    /// </summary>
    private long statsExportedAtMilliseconds = -1;
    private int statsExportRunning;

    /// <summary>Konfiguracja → Statystyki → Automatycznie: every N hours the service copies the statistics to the export folder (checked hourly; a failure is tried again the next hour).</summary>
    private void ExportStats()
    {
        int hours = config.StatsExportHours;
        if (hours <= 0) return;
        var last = Interlocked.Read(ref statsExportedAtMilliseconds);
        if (last >= 0 && Environment.TickCount64 - last < hours * 3_600_000L - 300_000L) return;
        if (Interlocked.CompareExchange(ref statsExportRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var answer = CardService.Call(new CardService.Request { Op = "syncstats" });
                if (answer.Code is 0 or -2) // No folder set: nothing to do until the next time.
                    Interlocked.Exchange(ref statsExportedAtMilliseconds, Environment.TickCount64);
                else Audit.Write("stats_export_failed", answer.Message);
            }
            catch (Exception ex) { Audit.Write("stats_export_failed", ex.Message); }
            finally { Volatile.Write(ref statsExportRunning, 0); }
        });
    }

    /// <summary>Who is signed in, for the statistics (turned into a code there, never kept as a name).</summary>
    private string StatsPerson => cardLogon?.DisplayName ?? (current is { Locked: false } person ? person.Identity.Name : "");

    private async Task RunHelpAsync(HelpAction item)
    {
        if (verifying || desktop.AskingPin || shellSignOut.Reason != null) return;
        if (!desktop.Help.SignedIn && item.NeedsSignIn) return;
        Audit.Write("help_action", item.Name + " " + item.Action);
        Stats.Record(Stats.Option, item.Name, item.CategoryPath, desktop.Help.SignedIn ? StatsPerson : "");
        switch (item.Action.ToLowerInvariant())
        {
            case HelpAction.ChangePin: await ChangeCardPinAsync(); break;
            case HelpAction.Reenroll: await ForgetCardAsync(); break;
            case HelpAction.ResetCard: await ResetCardAsync(); break;
            case HelpAction.FirstSignIn: await FirstSignInAsync(); break;
            default:
                // Always the help window (also when signed in), so it closes after the time set for Szybka pomoc.
                ShowHelpPage(item.Name, item.Action, item.Side);
                break;
        }
        RefreshChrome();
    }

    /// <summary>A Szybka pomoc page; in Punkt samoobsługowy with the card's sign-in while the card lies on the reader.</summary>
    private void ShowHelpPage(string title, string url, string side = "")
    {
        if (SelfService && OpenPageAsCardPerson(title, url, side)) return;
        helpBrowser?.Close();
        ShowSideFor(side);
        // In the start screen's middle frame: no separate window over the screen.
        var window = new HelpBrowserForm(title, url, HelpPageDomains(), config.HelpCloseSeconds,
            windowsAccount: SelfServiceWindowsSession, onlyListed: config.BrowserOnlyBookmarks) { TopLevel = false, TopMost = false };
        window.FormClosed += (_, _) =>
        {
            if (desktop.Page == window) { desktop.ShowPage(null); desktop.SetPageTitle(null); }
            if (helpBrowser == window) { helpBrowser = null; RefreshChrome(); }
        };
        helpBrowser = window;
        if (visibleView != desktop) ShowView(desktop);
        desktop.ShowPage(window);
        desktop.SetPageTitle(title);
        window.Show();
        window.Focus();
        window.Start();
    }

    /// <summary>Menu → "Obok": the second page or file at the page's right (before the page is shown, so the page gets the left part).</summary>
    private void ShowSideFor(string side)
    {
        if (string.IsNullOrWhiteSpace(side)) { desktop.ShowSide(null); return; }
        side = side.Trim().Trim('"');
        string url;
        if (Uri.TryCreate(side, UriKind.Absolute, out var uri) && !uri.IsFile) url = uri.AbsoluteUri;
        else
        {
            // A file: a full path, or one relative to the configuration's folder (instrukcje\mfa.pdf).
            var path = uri?.IsFile == true ? uri.LocalPath : side;
            // Konfiguracja z serwera: the copy of the server's folder the configuration was read from.
            if (!Path.IsPathRooted(path)) path = Path.Combine(config.Folder.Length > 0 ? config.Folder : Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory, path);
            url = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        var title = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile ? Path.GetFileNameWithoutExtension(u.LocalPath) : "Instrukcja";
        if (u?.IsFile == true && !File.Exists(u.LocalPath)) Audit.Write("side_page_missing", u.LocalPath);
        var panel = new SidePage(title, url, HelpPageDomains(), config.BrowserOnlyBookmarks);
        // Reading the instructions keeps the page open.
        panel.Activity += () => helpBrowser?.Touch();
        panel.Toggled += () => desktop.RefreshSide();
        desktop.ShowSide(panel);
        panel.Start();
        Audit.Write("side_page", url);
    }

    /// <summary>Sites a Szybka pomoc page may open with "tylko strony z listy": the allowed domains and the bookmarks' sites.</summary>
    private IEnumerable<string> HelpPageDomains() =>
        config.BrowserAllowedDomains.Concat(config.Bookmarks.Where(b => b.Enabled)
            .Select(b => Uri.TryCreate(b.Url, UriKind.Absolute, out var u) ? u.Host : "").Where(h => h.Length > 0));

    /// <summary>
    /// The reader holding the card: the one from Konfiguracja, or in Punkt samoobsługowy the only reader holding one
    /// (a USB key is its own reader, and the NFC reader from Konfiguracja may not be plugged in). Null = no card.
    /// </summary>
    private string? PresentReader()
    {
        if (reader.Snapshot(config.ReaderName) != null && reader.ActiveReader.Length > 0) return reader.ActiveReader;
        if (!SelfService) return null;
        return reader.ReadersWithCard() is [var only] ? only : null;
    }

    private bool CardForHelp(out string readerName)
    {
        readerName = PresentReader() ?? "";
        if (readerName.Length > 0) return true;
        ShowHelpResult("Przyłóż kartę do czytnika (albo włóż klucz) i wybierz to jeszcze raz.");
        return false;
    }

    private void ShowHelpResult(string text)
    {
        notice = text;
        noticeUntilMilliseconds = Environment.TickCount64 + 10_000;   // Punkt samoobsługowy: shown in place of the sign-in for a while.
        if (current != null) current.Notice = text;
        RefreshChrome();
    }

    /// <summary>Szybka pomoc → Zmień PIN karty: current PIN, then the new one twice, all on the lock card.</summary>
    private async Task ChangeCardPinAsync()
    {
        if (!CardForHelp(out var readerName)) return;
        verifying = true;
        try
        {
            string message = "Zmiana PIN-u karty: wpisz obecny PIN.";
            while (await desktop.AskPinAsync(message, cardPresent, config.HelpCloseSeconds) is { } old)
            {
                var fresh = await desktop.AskPinAsync("Wpisz nowy PIN (od 4 znaków).", cardPresent, config.HelpCloseSeconds);
                if (fresh == null) break;
                if (fresh.Length < 4) { message = "Nowy PIN musi mieć co najmniej 4 znaki. Wpisz obecny PIN."; continue; }
                var again = await desktop.AskPinAsync("Wpisz nowy PIN jeszcze raz.", cardPresent, config.HelpCloseSeconds);
                if (again == null) break;
                if (again != fresh) { message = "Nowe PIN-y się różnią. Wpisz obecny PIN."; continue; }
                var answer = await Task.Run(() => CardService.Call(new CardService.Request { Op = "changepin", Reader = readerName, Pin = old, NewPin = fresh }));
                Audit.Write(answer.Code == 0 ? "card_pin_changed" : "card_pin_change_failed", answer.Code + " " + answer.Message);
                if (answer.Code == 0x31) { message = "Obecny PIN jest nieprawidłowy. Spróbuj ponownie."; continue; }
                ShowHelpResult(answer.Code == 0 ? "PIN karty został zmieniony. Od teraz używaj nowego PIN-u." : "Nie udało się zmienić PIN-u: " + answer.Message);
                break;
            }
        }
        finally { verifying = false; cardKey = reader.Snapshot(config.ReaderName); }
    }

    /// <summary>
    /// Szybka pomoc → Pierwsze logowanie: the Windows sign-in screen for one sign-in (profile, SSO); the Kiosk Card
    /// service brings this session back and signs the person out as soon as their desktop has started.
    /// </summary>
    private async Task FirstSignInAsync()
    {
        verifying = true;
        bool confirmed;
        try
        {
            confirmed = await desktop.AskAsync("Pierwsze logowanie", "Za chwilę pojawi się ekran logowania Windows. Zaloguj się swoim kontem (kartą, Windows Hello albo hasłem). " +
                "Windows przygotuje Twój profil i logowanie jednokrotne, a potem sam wróci do Kiosku – to może potrwać kilka minut. Kontynuować?", config.HelpCloseSeconds);
        }
        finally { verifying = false; }
        if (!confirmed) { ShowHelpResult("Anulowano."); return; }
        var answer = await Task.Run(() => CardService.Call(new CardService.Request { Op = "firstsignin" }));
        Audit.Write(answer.Code == 0 ? "first_signin_started" : "first_signin_unavailable", answer.Message);
        if (answer.Code != 0) { ShowHelpResult("Pierwsze logowanie nie jest dostępne: " + answer.Message); return; }
        ShowHelpResult("Po zalogowaniu do Windows wrócisz tu automatycznie.");
        if (current != null) Park(current, "first_signin");
        try { WindowsSession.LockOrDisconnect(); }
        catch (Exception ex) { ShowHelpResult("Nie udało się pokazać ekranu logowania Windows: " + ex.Message); }
    }

    /// <summary>Szybka pomoc → Skonfiguruj kartę od nowa: Kiosk forgets the account saved for this card.</summary>
    private async Task ForgetCardAsync()
    {
        if (!CardForHelp(out _)) return;
        var id = (identifyCard() ?? UncertifiedCard()).Id;
        if (id == null) { ShowHelpResult("Nie udało się odczytać karty."); return; }
        // While the question is open the card and idle screens wait (taking the card away must not switch the desktop under it).
        verifying = true;
        bool confirmed;
        try
        {
            confirmed = await desktop.AskAsync("Skonfiguruj kartę od nowa", "Kiosk zapomni konto zapisane dla tej karty. Przy następnym logowaniu podasz login, hasło i PIN karty, " +
                "a karta zostanie zapamiętana od nowa (np. po zmianie hasła). Kontynuować?", config.HelpCloseSeconds);
        }
        finally { verifying = false; } // A card taken away meanwhile is handled on the next tick.
        if (!confirmed) { ShowHelpResult("Anulowano: karta zostaje bez zmian."); return; }
        CardVault.Forget(id);
        Audit.Write("card_forgotten");
        // The account stays signed in for an open session of this card: close it, so the next sign-in really asks again.
        if (sessions.TryGetValue(id, out var session) && (!session.HasWork || ConfirmEnd([session])))
        {
            bool wasCurrent = session == current;
            CloseSession(session, "card_forgotten");
            if (wasCurrent) requireRemoval = cardKey != null;
            RefreshChrome();
        }
        ShowHelpResult("Kiosk zapomniał konto tej karty. Wyjmij kartę i przyłóż ją ponownie: podasz login, hasło i PIN.");
    }

    /// <summary>
    /// Szybka pomoc → Resetuj kartę: the card's own FIDO2 reset (no PIN, no keys), then a new PIN right away.
    /// Cards allow it only just after they got power: Kiosk powers the card again, or asks to put it on the reader again.
    /// </summary>
    private async Task ResetCardAsync()
    {
        if (!CardForHelp(out var readerName)) return;
        verifying = true;
        bool confirmed;
        try
        {
            confirmed = await desktop.AskAsync("Resetuj kartę", "Reset usuwa z karty PIN i WSZYSTKIE klucze FIDO2 – także te do logowania do Windows, Microsoft 365 i innych stron. " +
                "Tego nie można cofnąć. Potem ustawisz nowy PIN, a klucz logowania dodasz ponownie (np. przez „Skonfiguruj MFA”).\n\nZresetować kartę?",
                config.HelpCloseSeconds, warning: true) &&
                await desktop.AskAsync("Resetuj kartę", "Na pewno? Wszystkie klucze na tej karcie zostaną usunięte.", config.HelpCloseSeconds, warning: true);
        }
        finally { verifying = false; } // A card taken away meanwhile is handled on the next tick.
        if (!confirmed) { ShowHelpResult("Anulowano: karta zostaje bez zmian."); return; }
        var id = (identifyCard() ?? UncertifiedCard()).Id;
        verifying = true;
        try
        {
            var answer = await Task.Run(() => CardService.Call(new CardService.Request { Op = "reset", Reader = readerName }));
            if (answer.Code is 0x30 or 0x27)
            {
                // Too late after power-up: the person takes the card off and puts it back; the reset follows at once.
                ShowHelpResult("Zdejmij kartę z czytnika i przyłóż ją ponownie – Kiosk od razu ją zresetuje.");
                RefreshChrome();
                var until = Environment.TickCount64 + 30_000;
                while (cardPresent() && Environment.TickCount64 < until) await Task.Delay(100);
                while (!cardPresent() && Environment.TickCount64 < until) await Task.Delay(100);
                if (!cardPresent()) { ShowHelpResult("Reset karty anulowany: karta nie wróciła do czytnika."); return; }
                readerName = PresentReader() ?? readerName;
                answer = await Task.Run(() => CardService.Call(new CardService.Request { Op = "reset", Reader = readerName }));
            }
            Audit.Write(answer.Code == 0 ? "card_reset" : "card_reset_failed", answer.Code + " " + answer.Message);
            if (answer.Code != 0) { ShowHelpResult("Nie udało się zresetować karty: " + answer.Message); return; }
            if (id != null) CardVault.Forget(id);
            var pin = await EnsureCardPinAsync(readerName, config.HelpCloseSeconds);
            ShowHelpResult(pin != null
                ? "Karta zresetowana i ma nowy PIN. Przy logowaniu podasz login, hasło i nowy PIN; klucz do logowania w Windows dodaj ponownie."
                : "Karta zresetowana. Przy następnym logowaniu ustawisz nowy PIN.");
        }
        finally { verifying = false; cardKey = reader.Snapshot(config.ReaderName); }
    }

    private void RetireNewsPictures(IReadOnlyList<NewsItem> items)
    {
        retiredNewsPictures.UnionWith(News.Pictures(items));
        ReleaseRetiredNewsPictures();
    }

    private void ReplaceDisplayedNewsPictures(IEnumerable<NewsItem> items)
    {
        var previous = displayedNewsPictures;
        displayedNewsPictures = News.Pictures(items);
        retiredNewsPictures.UnionWith(previous);
        ReleaseRetiredNewsPictures();
    }

    private void ReleaseRetiredNewsPictures()
    {
        var cached = sharePointNews is { } source ? News.Pictures(source.Cached) : [];
        var bars = News.Pictures(screenBars);
        foreach (var picture in retiredNewsPictures.ToArray())
            if (!displayedNewsPictures.Contains(picture) && !cached.Contains(picture) && !bars.Contains(picture))
            {
                retiredNewsPictures.Remove(picture);
                picture.Dispose();
            }
    }

    /// <summary>Company messages from Konfiguracja → Komunikaty (kiosk-news.json in a folder or on a share).</summary>
    private async Task LoadNewsAsync()
    {
        // A save in Konfiguracja always loads again at once, also while an earlier load still waits (e.g. for SharePoint).
        int load = ++newsLoad;
        // The share and SharePoint are asked again only every NewsRefreshMinutes (Konfiguracja → Komunikaty), not every minute.
        int every = Math.Clamp(config.NewsRefreshMinutes, 1, 1440) * 60_000;
        if (newsTimer.Interval != every) newsTimer.Interval = every;
        var folder = config.NewsFolder;
        List<NewsItem> mine = [];
        try
        {
            var show = config.NewsShow;
            // First load: the last copy of the share at once, the share itself may take a while (or be away).
            if (show != NewsDisplay.Off && !newsShown)
                try
                {
                    var early = await Task.Run(() => CentralConfig.NewsCopy(folder) is { } copy ? News.Load(copy) : []);
                    if (early.Count > 0 && !IsDisposed && load == newsLoad)
                    {
                        if (show == NewsDisplay.BarsOnly)
                        {
                            News.DisposePictures(early.Where(n => !n.IsBar));
                            early = early.Where(n => n.IsBar).ToList();
                        }
                        NewsSlides.Publish(early, config.NewsSeconds);
                        desktop.SetNews(early, config.NewsSeconds, config.NewsSize);
                        ReplaceDisplayedNewsPictures(early);
                    }
                    else News.DisposePictures(early);
                }
                catch (Exception ex) { Audit.Write("news_failed", "kopia: " + ex.Message); }
            newsShown = true;
            if (show != NewsDisplay.Off)
                try { mine = await Task.Run(() => News.Load(CentralConfig.NewsFolderFor(folder))); }   // A share: through the Kiosk Card service.
                catch (Exception ex) { Audit.Write("news_failed", ex.Message); }
            if (show == NewsDisplay.BarsOnly)
            {
                News.DisposePictures(mine.Where(n => !n.IsBar));
                mine = mine.Where(n => n.IsBar).ToList();
            }
            if (IsDisposed || load != newsLoad) { News.DisposePictures(mine); return; }
            void Show(List<NewsItem> other)
            {
                var all = News.Merge(mine, other, config.NewsOrder);
                NewsSlides.Publish(all, config.NewsSeconds);   // Ekrany with kiosk:komunikaty
                desktop.SetNews(all, config.NewsSeconds, config.NewsSize);
                screenBars = config.NewsBarsOnScreens ? mine.Where(n => n.IsBar).Take(3).ToList() : [];
                foreach (var screen in idleScreens) if (!screen.IsDisposed) screen.SetBars(screenBars);
                ReplaceDisplayedNewsPictures(all);
                // For the log: bars on the screensaver screens (Komunikaty → "Ekrany wygaszacza"), once per change.
                var report = "opcja " + config.NewsBarsOnScreens + ", pasków " + mine.Count(n => n.IsBar) + ", na ekranach " + screenBars.Count + ", ekranów wygaszacza " + idleScreens.Count;
                if (report != screenBarsReported) { screenBarsReported = report; Audit.Write("screen_bars", report); }
            }
            bool sharePoint = show == NewsDisplay.All && config.NewsSharePoint.Length > 0 && IsHandleCreated;
            if (sharePointNews != null && (!sharePoint ||
                !sharePointNews.Matches(config.NewsSharePoint, config.NewsSharePointCount, config.NewsSharePointAccount)))
            {
                // A changed site or account gets its own controller immediately. Old news must not appear
                // under the new configuration while the old navigation is still finishing.
                sharePointNews.Dispose();
                sharePointNews = null;
            }
            // The folder's messages at once; SharePoint (its sign-in can take a while) joins them when it answers.
            Show(sharePoint && sharePointNews != null ? sharePointNews.Cached : []);
            if (!sharePoint) return;
            if (sharePointNews == null)
            {
                sharePointNews = new SharePointNews(this);
                sharePointNews.CacheReplaced += RetireNewsPictures;
            }
            var other = await sharePointNews.GetAsync(config.NewsSharePoint, config.NewsSharePointCount, config.NewsSharePointAccount);
            if (IsDisposed || load != newsLoad) return;
            Show(other);
        }
        catch (Exception ex)
        {
            Audit.Write("news_failed", ex.Message);
            if (load != newsLoad || IsDisposed) { RetireNewsPictures(mine); return; }
            NewsSlides.Publish([], config.NewsSeconds);
            desktop.SetNews([], config.NewsSeconds);
            screenBars = [];
            foreach (var screen in idleScreens) if (!screen.IsDisposed) screen.SetBars([]);
            RetireNewsPictures(mine);
            ReplaceDisplayedNewsPictures([]);
        }
    }

    /// <summary>The button of a message: a page in the browser, or an application, remote desktop or command from the lists.</summary>
    private async Task RunNewsActionAsync(NewsItem item)
    {
        Audit.Write("news_action", item.Title + " " + item.Action);
        Stats.Record(Stats.News, item.Title.Length > 0 ? item.Title : item.Button, person: StatsPerson);
        if (!CanUseApps) { notice = "Przyłóż kartę i zaloguj się, aby otworzyć „" + (item.Title.Length > 0 ? item.Title : item.Button) + "”."; return; }
        var action = item.Action;
        string Name(string prefix) => action[prefix.Length..].Trim();
        if (action.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            var app = config.Applications.FirstOrDefault(a => string.Equals(a.Name, Name("app:"), StringComparison.OrdinalIgnoreCase));
            if (app != null) await OpenApplicationAsync(app); else notice = "Nie ma aplikacji „" + Name("app:") + "” w Konfiguracji.";
        }
        else if (action.StartsWith("rdp:", StringComparison.OrdinalIgnoreCase))
        {
            var rdp = config.RdpConnections.FirstOrDefault(r => string.Equals(r.Name, Name("rdp:"), StringComparison.OrdinalIgnoreCase));
            if (rdp != null) OpenRdp(rdp); else notice = "Nie ma pulpitu zdalnego „" + Name("rdp:") + "” w Konfiguracji.";
        }
        else if (action.StartsWith("cmd:", StringComparison.OrdinalIgnoreCase))
        {
            var command = config.Commands.FirstOrDefault(c => string.Equals(c.Name, Name("cmd:"), StringComparison.OrdinalIgnoreCase));
            if (command != null) RunCommand(command); else notice = "Nie ma polecenia „" + Name("cmd:") + "” w Konfiguracji.";
        }
        else if (Uri.TryCreate(action, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file")
            await OpenBrowserAsync(uri.AbsoluteUri);
        else notice = "Nieznana akcja komunikatu: " + action;
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
        surface.SuspendLayout();
        foreach (Control child in surface.Controls) if (child.Visible != (child == view)) child.Visible = child == view;
        surface.ResumeLayout(false);
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
        else if (window.Key == FilesKey) session.Files = null;
        else if (window.Key == SettingsKey) settingsView = null;
        if (session.LastView == window.View) session.LastView = null;
        HostOf(window.View)?.Release();
        window.View.Parent?.Controls.Remove(window.View);
        window.View.Dispose();
        RefreshHosts();
    }

    // ---------------------------------------------------------------- remote desktops

    /// <summary>The remote desktop as the signed-in person: this program in RDP-only mode (see <see cref="RdpUserHost"/>).</summary>
    private static ApplicationEntry RemoteDesktopAsUser(RdpConnection connection) => new()
    {
        Name = connection.Name,
        Path = Environment.ProcessPath!,
        Arguments = RdpUserHost.Argument + " " + connection.Server + " " + connection.Port
    };

    private void OpenRdp(RdpConnection connection)
    {
        var key = "rdp:" + connection.Name;
        if (current?.Rdp.ContainsKey(connection.Name) == true) { ActivateExisting(key); return; }
        if (!CheckCard()) return;
        var session = current!;
        if (session.Logon != null && connection.Authentication != RdpAuthentication.EntraId)
        {
            // Signed in with "Zaloguj": the remote desktop runs as the person, with Remote Credential Guard (Kerberos).
            // No password is sent — Credential Guard on this PC refuses to hand saved passwords to RDP anyway.
            _ = OpenApplicationAsync(RemoteDesktopAsUser(connection));
            return;
        }
        RdpHost? host = null;
        try
        {
            host = CreateRdp(connection, config.RequireCredentialPrompt, session.Logon);
            session.Rdp[connection.Name] = new RdpSession { Connection = connection, Host = host, ConnectingSinceMilliseconds = Environment.TickCount64 };
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

    private RdpHost CreateRdp(RdpConnection connection, bool promptForCredentials, UserLogon? logon = null)
    {
        // "Bieżący użytkownik" after "Zaloguj": the person's own account signs in to the remote desktop.
        // Signed in with "Zaloguj": the person's own account opens the remote desktop without asking again. Also for
        // "Karta inteligentna": a FIDO2 card has no certificate for the remote sign-in. Entra ID keeps its own web sign-in.
        bool asLoggedOnUser = logon != null && connection.Authentication != RdpAuthentication.EntraId;
        if (asLoggedOnUser) promptForCredentials = false;
        var host = new RdpHost { Dock = DockStyle.Fill, Visible = false };
        ((ISupportInitialize)host).BeginInit();
        surface.Controls.Add(host);
        ((ISupportInitialize)host).EndInit();
        host.CreateControl();
        dynamic client = host.Client;
        client.Server = connection.Server;
        if (connection.UserName.Length > 0) client.UserName = connection.UserName;
        client.DesktopWidth = Math.Max(800, surface.Width);
        client.DesktopHeight = Math.Max(600, surface.Height);
        dynamic settings = client.AdvancedSettings7;
        settings.RDPPort = connection.Port;
        settings.EnableCredSspSupport = true;
        settings.AuthenticationLevel = 1; // Reject server authentication failures.
        settings.RedirectSmartCards = true; // The card (or Windows Hello virtual card) signs in inside the session.
        settings.RedirectDrives = false;
        settings.RedirectPrinters = false;
        settings.RedirectClipboard = false;
        settings.EnableAutoReconnect = false;
        client.FullScreen = false;
        settings.SmartSizing = true;
        settings.DisplayConnectionBar = false;
        settings.HotKeyFullScreen = 0;
        var credentials = (IRdpCredentials)(object)client;
        // RequireCredentialPrompt=false lets Windows use the signed-in (card / Hello / Entra) identity
        // without forcing the dialog; prompting is still allowed when no such credential exists.
        credentials.SetPromptForCredentials(promptForCredentials);
        credentials.SetPromptForCredsOnClient(true);
        credentials.SetAllowPromptingForCredentials(true);
        credentials.SetAllowCredentialSaving(false);
        if (credentials.GetPromptForCredentials() != promptForCredentials || !credentials.GetPromptForCredsOnClient() ||
            !credentials.GetAllowPromptingForCredentials() || credentials.GetAllowCredentialSaving())
            throw new InvalidOperationException("Nie udało się zastosować zasad pytania o poświadczenia RDP.");
        if (asLoggedOnUser)
        {
            // jan@firma.pl rather than AzureAD\jan@firma.pl: the remote server knows the account by its UPN.
            client.UserName = logon!.Domain == "AzureAD" ? logon.User : logon.Login;
            var password = logon.Reveal();
            try { settings.ClearTextPassword = new string(password, 0, Array.IndexOf(password, '\0')); }
            finally { Array.Clear(password); }
        }
        if (connection.Authentication == RdpAuthentication.EntraId)
        {
            var extended = (IRdpExtendedSettings)(object)client;
            object enabled = true;
            // Write-only for this property: the control rejects reading it back (E_UNEXPECTED),
            // so a successful put is the confirmation.
            extended.SetProperty("EnableRdsAadAuth", ref enabled);
        }
        return host;
    }

    /// <summary>Poll hosted programs and RDP at a slower cadence than card presence and idle state.</summary>
    private void MonitorWindows()
    {
        // Card and idle state still need the 250 ms tick. Enumerating desktop windows and querying every
        // RDP ActiveX control that often adds unnecessary work to the UI thread.
        var now = Environment.TickCount64;
        if (now < nextWindowMonitorMilliseconds) return;
        nextWindowMonitorMilliseconds = now + 1000;
        MonitorRdp();
        MonitorApplications();
    }

    /// <summary>An application closed with its own × (or exited): remove its Kiosk window and taskbar button.</summary>
    private void MonitorApplications()
    {
        foreach (var session in sessions.Values.ToArray())
        foreach (var (name, view) in session.Applications.ToArray())
            if (view.Started && !view.RefreshHostedWindow())
                CloseWindow("app:" + name, "application_exited", session);
    }

    private void MonitorRdp()
    {
        foreach (var session in sessions.Values.ToArray())
        foreach (var (name, rdp) in session.Rdp.ToArray())
        {
            int state;
            try
            {
                state = (int)rdp.Host.Client.Connected;
                rdp.StateReadFailures = 0;
            }
            catch (Exception ex)
            {
                // A dying ActiveX control can reject Connected while it disconnects. One failed read
                // must not be mistaken for a smart-card failure and park another person's session.
                if (++rdp.StateReadFailures < 3) continue;
                Audit.Write("rdp_monitor_error", name + ": " + ex.Message);
                CloseWindow("rdp:" + name, "remote_control_error", session);
                session.Notice = "Pulpit zdalny „" + name + "” przestał odpowiadać.";
                continue;
            }
            if (state == 1) rdp.WasConnected = true;
            if (rdp.WasConnected && state == 0)
            {
                CloseWindow("rdp:" + name, "remote_disconnect", session);
                session.Notice = "Pulpit zdalny „" + name + "” został rozłączony.";
            }
            else if (!rdp.WasConnected && Environment.TickCount64 - rdp.ConnectingSinceMilliseconds >= config.ConnectTimeoutSeconds * 1000L)
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

    /// <summary>Menu → Pliki: the Kiosk's file manager; after "Zaloguj" it works as that person, in their folders.</summary>
    private void OpenFiles()
    {
        if (current?.Files != null) { ActivateExisting(FilesKey); return; }
        if (!CheckCard()) return;
        var session = current!;
        var logon = session.Logon;
        if (logon is { WithCard: true })
        {
            // Signed in with the card: Pliki run as the person in their own window, with their rights.
            _ = OpenApplicationAsync(new ApplicationEntry
            {
                Name = config.Applications.FirstOrDefault(a => a.IsBuiltInFiles)?.Name ?? "Pliki",
                Path = Environment.ProcessPath!, Arguments = FilesHost.Argument
            });
            return;
        }
        Func<Func<object?>, object?> runAs = logon != null ? logon.RunAs : work => work();
        try { session.Files = new FilesView(runAs, logon?.ProfileDirectory); }
        catch (Exception ex) { notice = "Nie można otworzyć Plików: " + ex.Message; Audit.Write("files_error", ex.Message); RefreshChrome(); return; }
        session.Files.OpenRequested += path => _ = OpenFileAsync(session, path);
        AddWindow(session, FilesKey, config.Applications.FirstOrDefault(a => a.IsBuiltInFiles)?.Name ?? "Pliki", session.Files);
        Audit.Write("files_opened");
    }

    /// <summary>Pliki: a file opens in the program Windows associates with it, hosted like any application.</summary>
    private async Task OpenFileAsync(UserSession session, string path)
    {
        if (current != session) return;
        var command = FileAssociation.Command(path);
        if (command == null)
        {
            notice = FileAssociation.IsProgram(path) ? "Z Plików nie można uruchamiać programów ani skryptów." :
                "Brak programu do otwarcia „" + Path.GetFileName(path) + "” na tym komputerze.";
            Audit.Write("file_open_failed", Path.GetExtension(path));
            RefreshChrome();
            return;
        }
        Audit.Write("file_opened", Path.GetExtension(path));
        await OpenApplicationAsync(new ApplicationEntry { Name = Path.GetFileName(path), Path = command.Value.Program, Arguments = command.Value.Arguments });
    }

    private ApplicationEntry EdgeAsUser => new()
    {
        Name = "Przeglądarka",
        Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
        Arguments = "--new-window " + config.BrowserUrl
    };

    /// <param name="url">A page to open (company message); null = the start page.</param>
    private IReadOnlyCollection<string> CurrentBrowserCertificates(UserSession session)
    {
        if (session.Locked || current != session || shellSignOut.Reason != null ||
            !sessions.TryGetValue(session.Identity.Id, out var active) || active != session ||
            WindowsSessionMode && (disconnectedSinceMilliseconds != null || shellLockRequestedAtMilliseconds != null)) return [];
        try
        {
            if (ShellMode && string.IsNullOrWhiteSpace(session.Identity.Upn)) return [];
            var observed = reader.Snapshot(config.ReaderName);
            if (observed == null) return [];
            if (!ShellMode) return observed == cardKey ? session.Identity.Thumbprints : [];
            return CardPin.ReadIdentity(reader.ActiveReader)?.Thumbprints ?? [];
        }
        catch (Exception ex) { Audit.Write("browser_certificate_error", ex.Message); return []; }
    }

    private async Task OpenBrowserAsync(string? url = null)
    {
        if (!config.Applications.Any(a => a.IsBuiltInBrowser)) { if (url != null) notice = "Przeglądarka jest wyłączona w Konfiguracji."; return; }
        if (current?.Browser != null && url != null && current.Logon == null)
        {
            ActivateExisting(BrowserKey);
            await current.Browser.OpenTrustedTabAsync(url);
            return;
        }
        if (current?.Browser != null && url == null)
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
            if (url == null) { await OpenApplicationAsync(EdgeAsUser); return; }
            if (session.Applications.ContainsKey(EdgeAsUser.Name))
            {
                // Edge is already open as the person: the page opens as a new tab of its window (a second Edge process only hands it over).
                try
                {
                    var edge = EdgeAsUser.Path;
                    var (process, thread) = session.Logon.Start(edge, "\"" + edge + "\" \"" + url + "\"", Path.GetDirectoryName(edge), show: 1);
                    AppNative.ResumeThread(thread);
                    AppNative.CloseHandle(thread);
                    AppNative.CloseHandle(process);
                    ActivateExisting("app:" + EdgeAsUser.Name);
                }
                catch (Exception ex) { notice = "Nie udało się otworzyć strony: " + ex.Message; Audit.Write("news_open_failed", ex.Message); }
            }
            else await OpenApplicationAsync(EdgeAsUser with { Arguments = "--new-window \"" + url + "\"" });
            return;
        }
        var policy = new SitePolicy(config.BrowserOnlyBookmarks, config.Bookmarks, config.BrowserAllowedDomains);
        if (url != null) policy.Trust(url);
        var requested = new BrowserView(config.Bookmarks, policy, () => CurrentBrowserCertificates(session),
            windowsAccount: ShellMode, expectedCertificateUpn: ShellMode ? session.Identity.Upn : null);
        session.Browser = requested;
        AddWindow(session, BrowserKey, config.Applications.FirstOrDefault(a => a.IsBuiltInBrowser)?.Name ?? "Przeglądarka", requested);
        try
        {
            // With "only bookmarks" a start page outside the list opens the first bookmark instead.
            await requested.InitializeAsync(url ?? (policy.Allows(config.BrowserUrl) ? config.BrowserUrl : config.Bookmarks[0].Url));
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
        if (entry.IsBuiltInFiles) { OpenFiles(); return; }
        if (entry.IsBuiltInBrowser) { await OpenBrowserAsync(); return; }
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
        var key = "app:" + entry.Name;
        var requested = new ApplicationView();
        session.Applications[entry.Name] = requested;
        AddWindow(session, key, entry.Name, requested);
        try
        {
            // Konto wspólne: this PC's account runs it, the shared one signs in to the file server (runas /netonly).
            IProgramStarter? starter = string.IsNullOrWhiteSpace(entry.RunAs) ? session.Logon
                : new NetworkAccountStarter(entry.Name);
            if (requested.IsDisposed) return;
            await requested.StartAsync(entry, starter);
            if (!requested.IsDisposed) { Audit.Write("application_opened", entry.Name); Stats.Record(Stats.App, entry.Name, person: session.Identity.Name); }
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
    /// Who may open Konfiguracja: a card on the AdminCards list, or (when allowed) a member of this PC's local
    /// Administrators group: the card's account, the account signed in with Zaloguj, or the Windows account running Kiosk (an administrator starting it
    /// to set it up). Test mode no longer opens it for everyone: configuration is decided by administrators.
    /// </summary>
    private bool IsAdmin(UserSession session, Config? settings = null)
    {
        settings ??= config;
        if (settings.AdminCards.Any(a => a.Id == session.Identity.Id)) return true;
        // The account signed in with "Zaloguj" counts too: FIDO2 cards carry no certificate naming it.
        return settings.AllowWindowsAdministrators && (session.IsWindowsAdmin || session.Logon?.IsAdministrator == true || RunningAsWindowsAdmin);
    }

    // Includes the deny-only Administrators SID of a UAC-filtered admin token.
    private static bool RunningAsWindowsAdmin = IsWindowsAdminToken();
    private static bool IsWindowsAdminToken()
    {
        using var token = System.Security.Principal.WindowsIdentity.GetCurrent();
        return token.Groups?.Any(g => g.Value == "S-1-5-32-544") == true;
    }

    private async Task CheckWindowsAdministratorAsync(UserSession session)
    {
        var upn = session.Identity.Upn;
        if (upn.Length == 0) return;
        session.IsWindowsAdmin = await Task.Run(() => LocalAdministrators.Contains(upn));
        if (session.IsWindowsAdmin) Audit.Write("windows_administrator", session.Identity.Name);
        RefreshChrome();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (WindowsSessionMode) TryRegisterShellNotifications();
        AdminSettings.RegisterHotkey(Handle);
        windowsKey ??= new WindowsKey(() => { if (IsHandleCreated) BeginInvoke(new Action(ToggleMenu)); });
    }

    private WindowsKey? windowsKey;

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (shellNotificationHandle != IntPtr.Zero)
        {
            WindowsSession.UnregisterNotifications(shellNotificationHandle);
            shellNotificationHandle = IntPtr.Zero;
        }
        AdminSettings.UnregisterHotkey(Handle);
        windowsKey?.Dispose();
        windowsKey = null;
        base.OnHandleDestroyed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Alt+F4 never closes the shell itself, even when no keyboard hook is running.
        if ((keyData & Keys.KeyCode) == Keys.F4 && (keyData & Keys.Alt) != 0) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private const int WmSysCommand = 0x0112, ScClose = 0xF060;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WindowsSession.SessionChangeMessage) ObserveWindowsSessionChange(m);
        // Alt+F4 also arrives as SC_CLOSE (also while a child has the focus); the Kiosk's own exit calls Close() instead.
        if (m.Msg == WmSysCommand && (m.WParam.ToInt64() & 0xFFF0) == ScClose)
        {
            Audit.Write("shell_close_shortcut_blocked");
            m.Result = IntPtr.Zero;
            return;
        }
        if (AdminSettings.IsHotkey(m))
        {
            // Konfiguracja → "Skrót Ctrl+Alt+K": turned off, the shortcut does nothing.
            if (config.AdminHotkey) OpenSettingsAsAdministrator(); else Audit.Write("admin_hotkey_disabled");
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Ctrl+Alt+K: an admin card opens Konfiguracja as usual; anyone else is asked for a Windows administrator.</summary>
    private void OpenSettingsAsAdministrator()
    {
        if (configPath == null) return;
        if (current != null && CanUseApps && IsAdmin(current)) { OpenSettings(); return; }
        AdminSettings.Open(this, configPath, () =>
        {
            NewsAccount.Forget();
            if (!ReloadConfig()) return;
            ApplySavedSettings();
        });
    }

    /// <summary>Saved Konfiguracja takes effect at once (both ways of opening it): screens, messages, colours.</summary>
    private void ApplySavedSettings()
    {
        Invalidate(true);
        RotateScreens();
        StartIdleScreens();
        _ = LoadNewsAsync();
        RefreshChrome();
        if (modeRestartPending) BeginInvoke(new Action(RestartForModeChange));
    }

    private void RestartForModeChange()
    {
        if (!modeRestartPending || IsDisposed) return;
        if (sessions.Values.Any(s => s.HasWork) && !ConfirmEnd(sessions.Values))
        {
            notice = "Zmiana trybu została zapisana. Zamknij pracę i uruchom Kiosk ponownie, aby ją zastosować.";
            RefreshChrome();
            return;
        }
        try
        {
            closeConfirmed = true;
            RestartClient();
            modeRestartPending = false;
        }
        catch (Exception ex)
        {
            closeConfirmed = false;
            notice = "Zmiana trybu została zapisana, ale ponowne uruchomienie nie powiodło się: " + ex.Message;
            Audit.Write("mode_restart_failed", ex.Message);
            RefreshChrome();
        }
    }

    /// <summary>Konfiguracja → Ekrany → Obrót: Windows' orientation of the monitors (before the screens are laid out).</summary>
    private void RotateScreens()
    {
        // The monitors belong to this PC: its own file wins over a server's configuration.
        try { DisplayRotation.Apply(localConfig is { ScreenRotations.Length: > 0 } local ? local.ScreenRotations : config.ScreenRotations); }
        catch (Exception ex) { Audit.Write("screen_rotation_failed", ex.Message); }
    }

    /// <summary>Reads the saved configuration again (and the server's, when it has a ConfigSource).</summary>
    private bool ReloadConfig()
    {
        try
        {
            var loaded = CentralConfig.Load(configPath!);
            var next = loaded.Effective;
            modeRestartPending = next.SessionMode != config.SessionMode ||
                next.SelfServiceAuthentication != config.SelfServiceAuthentication;
            if (!modeRestartPending) config = next;
            localConfig = loaded.Local;
            notice = modeRestartPending ? "Zmiana trybu została zapisana. Kiosk uruchomi się ponownie." : loaded.Problem;
        }
        catch (Exception ex) { Audit.Write("settings_reload_failed", ex.Message); return false; }
        KioskTheme.SetAccent(config.AccentColor);
        return true;
    }

    /// <summary>From a server's configuration only its address (ConfigSource) is saved into the PC's own file.</summary>
    private Config ToSave(Config edited) => localConfig is { ConfigSource.Length: > 0 } local ? local with { ConfigSource = edited.ConfigSource, ScreenRotations = edited.ScreenRotations } : edited;

    private void OpenSettings()
    {
        if (settingsView != null) { ActivateExisting(SettingsKey); return; }
        // Open windows stay as they are; saved settings apply to what is opened next.
        if (configPath == null || !CanUseApps || !IsAdmin(current!)) return;
        var session = current!;
        var view = new SettingsView(fullConfig, session.Identity, updated => IsAdmin(session, updated.Active()));
        view.IdentifyScreensRequested += ShowScreenNumbers;
        view.SwitchUserRequested += () => DisconnectWindows("manual_switch_user");
        settingsView = view;
        view.CancelRequested += () => CloseWindow(SettingsKey, "settings_closed", session);
        view.SaveRequested += updated =>
        {
            Configuration.Save(configPath, ToSave(updated), new CentralConfig.Secrets(view.AccountChange, view.AccountsKey));
            NewsAccount.Forget();
            if (!ReloadConfig()) { config = updated; KioskTheme.SetAccent(updated.AccentColor); }
            Audit.Write("settings_saved");
            ApplySavedSettings();
            CloseWindow(SettingsKey, "settings_closed", session);
        };
        AddWindow(session, SettingsKey, "Konfiguracja", view);
    }

#if KIOSK_SELF_TEST
    // ---------------------------------------------------------------- self-test

    /// <summary>Pictures of every Konfiguracja tab at 1280×720 and 1024×576 (= 1280×720 at 125 %), for checking the layout from CI.</summary>
    private static void SaveSettingsScreenshots(Config settings)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "screenshots"));   // out\screenshots, outside the package
        Directory.CreateDirectory(folder);
        foreach (var (size, scale) in new[] { (new Size(1280, 720), 1f), (new Size(1920, 1080), 1.5f) })
        {
            SettingsView.ScaleOverride = scale;
            using var form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = size, ShowInTaskbar = false };
            var view = new SettingsView(settings);
            form.Controls.Add(view);
            form.Show();
            var tabs = view.Controls.Cast<Control>().SelectMany(c => c.Controls.OfType<TabControl>()).Single();
            for (var i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                using var picture = new Bitmap(size.Width, size.Height);
                form.DrawToBitmap(picture, new Rectangle(Point.Empty, size));
                picture.Save(Path.Combine(folder, size.Width + "-" + i + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Close();
        }
        SettingsView.ScaleOverride = null;
    }

    /// <summary>kiosk-news.json is read with its pictures, dates and actions; a picture of the desktop with the messages.</summary>
    private void NewsSmokeTest()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kiosk-news-test-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        try
        {
            using (var picture = new Bitmap(320, 180))
            {
                using (var g = Graphics.FromImage(picture))
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 320, 180), Color.FromArgb(0, 120, 212), Color.FromArgb(61, 220, 132), 30f))
                    g.FillRectangle(brush, 0, 0, 320, 180);
                picture.Save(Path.Combine(folder, "a.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            File.WriteAllText(Path.Combine(folder, "pomoc.html"), "<h1>Zgłoś problem</h1>");
            File.WriteAllText(Path.Combine(folder, News.FileName), """
                [
                  // comments and trailing commas are allowed
                  { "Title": "Aktualizacja A", "Text": "W piątek po 18:00 aktualizujemy system kadrowy.", "Image": "a.png", "Button": "Szczegóły", "Action": "https://example.com/aktualizacja" },
                  { "Title": "Zgłoś problem", "Text": "Coś nie działa? Wyślij zgłoszenie do IT.", "Button": "Zgłoś", "Action": "pomoc.html" },
                  { "Title": "Stary", "Until": "2020-01-01" },
                  { "Kind": "Pasek", "Title": "Wyłączony", "Color": "orange", "Enabled": false },
                  { "Kind": "Pasek", "Title": "Nie działa system XXX", "Text": "Trwa naprawa.", "Color": "red", "TextColor": "white" },
                ]
                """);
            var all = News.Load(folder);
            var logoFile = Path.Combine(folder, "a.png");
            desktop.SetBranding(config.BrandTitle, config.BrandSubtitle, logoFile);
            var loadedLogo = desktop.Controls.OfType<PictureBox>().FirstOrDefault(p => p.Image != null)?.Image;
            if (loadedLogo == null) throw new InvalidOperationException("The configured logo was not decoded.");
            File.Delete(logoFile);
            using (var renderedLogo = new Bitmap(loadedLogo.Width, loadedLogo.Height))
            using (var graphics = Graphics.FromImage(renderedLogo))
                graphics.DrawImage(loadedLogo, 0, 0);
            desktop.SetBranding(config.BrandTitle, config.BrandSubtitle, config.BrandLogo);
            var items = all.Where(i => !i.IsBar).ToList();
            if (all.Count(i => i.IsBar) != 1 || News.ParseColor("red", Color.Empty).ToArgb() != Color.Red.ToArgb() ||
                News.ParseColor("#00FF00", Color.Empty).ToArgb() != Color.Lime.ToArgb() || News.ParseColor("nic", Color.Blue) != Color.Blue)
                throw new InvalidOperationException("The bar message was not read.");
            var at = new DateTime(2026, 10, 20, 16, 30, 0);
            if (!News.Shown(new NewsItem { From = "2026-10-20 16:00", Until = "2026-10-20 17:00" }, at) ||
                News.Shown(new NewsItem { From = "2026-10-20 16:45" }, at) || News.Shown(new NewsItem { Until = "2026-10-20 16:15" }, at) ||
                !News.Shown(new NewsItem { From = "2026-10-20", Until = "2026-10-20" }, at) || !News.Shown(new NewsItem { Until = "2026-10-20T16:30" }, at))
                throw new InvalidOperationException("From / Until with hours were not applied.");
            if (items.Count != 2 || items[0].Picture == null || !items[1].Action.StartsWith("file:///", StringComparison.Ordinal) || News.Load("").Count != 0)
                throw new InvalidOperationException("Company messages were not read correctly.");
            if (News.ResolveAction("https:// wp.pl", folder) != "https://wp.pl" || News.ResolveAction("wp.pl", folder) != "https://wp.pl" ||
                News.ResolveAction("app:Excel", folder) != "app:Excel")
                throw new InvalidOperationException("Company message addresses were not normalised.");
            if (News.VideoFor("brak.mp4", folder) != "" || News.VideoFor("https://firma.pl/film.mp4", folder) != "https://firma.pl/film.mp4" ||
                items.Any(i => i.IsVideo))
                throw new InvalidOperationException("A message film was resolved wrongly.");
            var trusted = new SitePolicy(true, [new Bookmark { Name = "G", Url = "https://www.google.com" }], []);
            trusted.Trust(items[1].Action);
            trusted.Trust("https://wp.pl");
            if (!trusted.Allows(items[1].Action) || !trusted.Allows("https://wp.pl/news") || trusted.Allows("https://example.org") ||
                trusted.Allows(new Uri(Path.Combine(Path.GetTempPath(), "other.html")).AbsoluteUri))
                throw new InvalidOperationException("Company message pages are not allowed in the browser.");
            var spItems = SharePointNews.Parse("""{"ok":true,"items":[{"title":"Nowa strona","text":"Opis","url":"https://firma.sharepoint.com/sites/i/SitePages/a.aspx","image":""}]}""");
            var merged = News.Merge(items, spItems, NewsOrdering.Alternate);
            if (spItems.Count != 1 || spItems[0].Action != "https://firma.sharepoint.com/sites/i/SitePages/a.aspx" || merged.Count != 3 ||
                merged[0] != spItems[0] || merged[1] != items[0] || News.Merge(items, spItems, NewsOrdering.MineFirst)[0] != items[0])
                throw new InvalidOperationException("SharePoint news were not merged with the folder's messages.");
            if (desktop.Help.Count != HelpAction.Defaults.Length) throw new InvalidOperationException("Menu is not on the desktop.");
            desktop.SetNews(all, 8);
            ReplaceDisplayedNewsPictures(all);
            if (desktop.News.Count != 2 || desktop.BarCount != 1) throw new InvalidOperationException("Company messages are not on the desktop.");
            var screenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "screenshots"));
            Directory.CreateDirectory(screenshots);
            if (desktop.Width > 0 && desktop.Height > 0)
            {
                using var shot = new Bitmap(desktop.Width, desktop.Height);
                desktop.DrawToBitmap(shot, new Rectangle(Point.Empty, desktop.Size));
                shot.Save(Path.Combine(screenshots, "desktop.png"), System.Drawing.Imaging.ImageFormat.Png);
                int standard = desktop.News.Height;
                desktop.SetNews(merged, 8, NewsBanner.Large);
                ReplaceDisplayedNewsPictures(merged);
                if (items[0].Picture is not { Width: > 0 }) throw new InvalidOperationException("A picture shared by successive news lists was released too early.");
                using var large = new Bitmap(desktop.Width, desktop.Height);
                desktop.DrawToBitmap(large, new Rectangle(Point.Empty, desktop.Size));
                large.Save(Path.Combine(screenshots, "desktop-large.png"), System.Drawing.Imaging.ImageFormat.Png);
                if (desktop.News.Visible && desktop.News.Height < standard) throw new InvalidOperationException("The large banner is smaller.");
            }
            desktop.SetNews([], 8);
            ReplaceDisplayedNewsPictures([]);
            bool released = false;
            try { _ = items[0].Picture!.Width; }
            catch (ArgumentException) { released = true; }
            catch (ObjectDisposedException) { released = true; }
            if (!released) throw new InvalidOperationException("A replaced news picture was not released.");
            File.WriteAllText(Path.Combine(folder, News.FileName), """[{"Title":null,"Text":"Działa","Image":null,"Action":null,"Kind":null,"Video":null}]""");
            var nullable = News.Load(folder);
            if (nullable.Count != 1 || nullable[0].Title != "" || nullable[0].Text != "Działa" || nullable[0].IsBar || nullable[0].Action != "")
                throw new InvalidOperationException("Null fields in a news entry were not normalized.");

            using (var source = new Bitmap(3000, 1000))
            using (var encoded = new MemoryStream())
            {
                using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.Crimson);
                source.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
                using var scaled = News.DecodePicture(encoded.ToArray());
                if (scaled.Width != 1920 || scaled.Height != 640 || ((Bitmap)scaled).GetPixel(960, 320).ToArgb() != Color.Crimson.ToArgb())
                    throw new InvalidOperationException("News picture was not bounded and rendered correctly.");
            }
            using (var oversized = File.Create(Path.Combine(folder, "oversized.png"))) oversized.SetLength(News.MaxPictureBytes + 1L);
            File.WriteAllText(Path.Combine(folder, News.FileName), "[{\"Title\":\"Large image\",\"Image\":\"oversized.png\"}]");
            var limited = News.Load(folder);
            if (limited.Count != 1 || limited[0].Picture != null)
                throw new InvalidOperationException("Oversized news picture hid the message or was decoded.");
            using (var shared = new Bitmap(12, 12)) shared.Save(Path.Combine(folder, "shared.png"), System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(Path.Combine(folder, News.FileName),
                "[{\"Title\":\"First\",\"Image\":\"shared.png\"},{\"Title\":\"Second\",\"Image\":\".\\\\shared.png\"}]");
            var reused = News.Load(folder);
            try
            {
                if (reused.Count != 2 || reused[0].Picture == null || !ReferenceEquals(reused[0].Picture, reused[1].Picture))
                    throw new InvalidOperationException("Repeated news picture was decoded more than once.");
            }
            finally { News.DisposePictures(reused); }
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private void SaveShellScreenshot()
    {
        try
        {
            var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "screenshots"));
            Directory.CreateDirectory(folder);
            PopulateMenu("");
            menu.ShowAt(this, taskbar.MenuButton.RectangleToScreen(taskbar.MenuButton.ClientRectangle));
            menu.OpenPowerList();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
            var bounds = Screen.FromControl(this).Bounds;
            using var picture = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(picture)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            picture.Save(Path.Combine(folder, "shell.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Audit.Write("self_test_screenshot_failed", ex.Message); }
        finally { menu.HideMenu(); }
    }

    internal void SmokeTest()
    {
        timer.Stop();
        BrowserProfileCleanupSmokeTest();
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
            CardReader.MonitoredReaders(physicalAndVirtual, "").Count(s => CardReader.IsUsableCard(s.EventState)) != 2 ||
            CardReader.MonitoredReaders(physicalAndVirtual, "Missing").Length != 0)
            throw new InvalidOperationException("PCSC reader-selection regression test failed.");
        physicalAndVirtual[0].EventState = 0x12;
        if (CardReader.MonitoredReaders(physicalAndVirtual, "ACS").Any(s => CardReader.IsUsableCard(s.EventState)))
            throw new InvalidOperationException("Virtual reader masked physical card removal.");

        // Older single-server files migrate to the connection list and the lock / retention timers.
        var legacy = JsonSerializer.Deserialize<Config>("{\"Server\":\"rds.example.local\",\"Port\":3390,\"IdleSeconds\":120,\"AutoUpdate\":true,\"UpdateChannel\":\"dev\",\"SelfServiceSignInUrl\":\"https://old.example/sign-in\",\"SelfServiceLinkUrl\":\"https://old.example/link\"}")!.Migrate();
        legacy.Validate();
        var migratedJson = JsonSerializer.Serialize(legacy);
        if (legacy.RdpConnections.Single().Server != "rds.example.local" || legacy.RdpConnections[0].Port != 3390 ||
            legacy.LockAfterSeconds != 120 || legacy.ChangeUserAfterSeconds != 600 || legacy.Server != null ||
            legacy.Applications.Length != 3 || !legacy.Applications[0].IsBuiltInBrowser || !legacy.Applications[1].IsBuiltInNotes || !legacy.Applications[2].IsBuiltInFiles || !legacy.EnableBrowser ||
            legacy.ConfigVersion != Config.CurrentVersion || legacy.Migrate().Applications.Length != 3 ||
            legacy.AutoUpdate != null || legacy.UpdateChannel != null || legacy.SelfServiceSignInUrl != null || legacy.SelfServiceLinkUrl != null ||
            migratedJson.Contains("AutoUpdate", StringComparison.Ordinal) ||
            migratedJson.Contains("UpdateChannel", StringComparison.Ordinal) ||
            migratedJson.Contains("SelfServiceSignInUrl", StringComparison.Ordinal) ||
            migratedJson.Contains("SelfServiceLinkUrl", StringComparison.Ordinal))
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

    private static void BrowserProfileCleanupSmokeTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiosk-browser-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var live = BrowserProfileCleanup.Create(root);
            string staleFolder;
            using (var stale = BrowserProfileCleanup.Create(root))
            {
                staleFolder = stale.Folder;
                File.WriteAllText(Path.Combine(staleFolder, "WebView2.data"), "old");
            }
            var unowned = Path.Combine(root, "123-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unowned);
            File.WriteAllText(Path.Combine(unowned, "WebView2.data"), "foreign");
            var legacy = Path.Combine(root, "456");
            Directory.CreateDirectory(legacy);
            BrowserProfileCleanup.RemoveStale(root);
            if (!Directory.Exists(live.Folder) || Directory.Exists(staleFolder) ||
                !Directory.Exists(unowned) || !Directory.Exists(legacy))
                throw new InvalidOperationException("Temporary browser profile ownership cleanup failed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal async Task EmbeddedSmokeTestAsync()
    {
        if (BrowserView.SecureCredentialOrigin("https://help.example:8443/private") != "https://help.example:8443" ||
            BrowserView.SecureCredentialOrigin("http://help.example/") != null ||
            BrowserView.SecureCredentialOrigin("https://user:password@help.example/") != null)
            throw new InvalidOperationException("Unsafe browser credential origin was accepted.");
        using (var promptParent = new Panel { Size = new Size(640, 480) })
        {
            var prompt = HelpSignIn.AskAsync(promptParent, "example.invalid", "test\\user", 60);
            promptParent.Dispose();
            if (await prompt.WaitAsync(TimeSpan.FromSeconds(2)) != null)
                throw new InvalidOperationException("Closing a page did not cancel its password prompt.");
        }
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
        // A real HTTP Basic challenge must be cancelled before WebView2 can show a password prompt.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var challenge = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var request = new byte[4096];
                await stream.ReadAtLeastAsync(request, 1);
                var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"kiosk-test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);
            });
            var blockedAuth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            browserView.Web.CoreWebView2.BasicAuthenticationRequested += (_, e) => blockedAuth.TrySetResult(e.Cancel);
            browserView.Web.CoreWebView2.Navigate("http://127.0.0.1:" + port + "/");
            if (!await blockedAuth.Task.WaitAsync(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("An HTTP page could ask for a password.");
            await challenge.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { listener.Stop(); }
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
        const string bookmarkUrl = "https://bookmark.invalid/";
        var bookmarkNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = browserView.Web.CoreWebView2;
        void OnBookmarkStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
        {
            // A prior HTTP challenge can still emit a navigation event after its request has finished.
            if (!string.Equals(e.Uri, bookmarkUrl, StringComparison.Ordinal)) return;
            e.Cancel = true;
            bookmarkNavigation.TrySetResult();
        }
        core.NavigationStarting += OnBookmarkStarting;
        var bookmarkButton = browserView.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single();
        try
        {
            bookmarkButton.PerformClick();
            await bookmarkNavigation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { core.NavigationStarting -= OnBookmarkStarting; }

        // Konfiguracja is for admins: a listed card or a Windows administrator; test mode alone does not open it.
        var adminTest = config with { TestMode = false, AdminCards = [new AdminCard { Name = "Alicja", Id = alice.Id }] };
        var runningAsAdmin = RunningAsWindowsAdmin;   // CI runs as an administrator
        RunningAsWindowsAdmin = false;
        var bobSession = new UserSession { Identity = bob };
        if (!IsAdmin(aliceSession, adminTest) || IsAdmin(bobSession, adminTest) ||
            IsAdmin(bobSession, config with { TestMode = true, AdminCards = [], AllowWindowsAdministrators = false }))
            throw new InvalidOperationException("Admin card check failed.");
        bobSession.IsWindowsAdmin = true;
        if (!IsAdmin(bobSession, adminTest) || IsAdmin(bobSession, adminTest with { AllowWindowsAdministrators = false }))
            throw new InvalidOperationException("Windows administrator check failed.");
        bobSession.IsWindowsAdmin = false;
        RunningAsWindowsAdmin = true;   // an administrator started Kiosk to set it up
        if (!IsAdmin(bobSession, adminTest) || IsAdmin(bobSession, adminTest with { AllowWindowsAdministrators = false }))
            throw new InvalidOperationException("Administrator running Kiosk check failed.");
        RunningAsWindowsAdmin = runningAsAdmin;
        if (!LocalAdministrators.Matches(@"FIRMA\j.kowalski", "j.kowalski", "firma.pl") || !LocalAdministrators.Matches(@"AzureAD\JanKowalski", "JanKowalski", "AzureAD") ||
            LocalAdministrators.Matches(@"OTHER\j.kowalski", "j.kowalski", "firma.pl") || LocalAdministrators.Matches("Jan", "Jan", "x.pl"))
            throw new InvalidOperationException("Administrators group matching failed.");
        _ = LocalAdministrators.Contains("nobody@example.invalid"); // Reads the real group without failing.

        PopulateMenu("test");
        if (menu.ItemCount < config.RdpConnections.Length + 3) throw new InvalidOperationException("Menu is incomplete.");

        // In-app lock hides every window; resume with the same card restores the last one.
        Lock("self_test_lock", 0);
        if (!aliceSession.Locked || !desktop.Visible || browserView.Visible || taskbar.TaskCount != 0 || taskbar.MenuButton.Enabled)
            throw new InvalidOperationException("Lock did not hide the session.");
        await VerifyAndUnlockAsync();
        if (aliceSession.Locked || !browserView.Visible || taskbar.TaskCount != 2) throw new InvalidOperationException("Resume did not restore the session.");
        NewsSmokeTest();
        if (People.FriendlyName("", @"FIRMA\AnnaNowak") != "Anna Nowak" || People.FriendlyName("", "jan.kowalski@firma.pl") != "Jan Kowalski" ||
            People.FriendlyName("Anna Nowak", @"AzureAD\x") != "Anna Nowak" || People.FriendlyName("", "Alicja Test") != "Alicja Test" || People.Initials("Jan Kowalski") != "JK")
            throw new InvalidOperationException("People's names are not shown nicely.");
        // "Włączony": unticked entries stay in the configuration but are not used.
        var switched = config with
        {
            HelpActions = [.. HelpAction.Defaults.Select((h, i) => h with { Enabled = i != 0 })],
            Commands = [new CommandEntry { Name = "Off", Path = @"C:\Windows\System32\cmd.exe", Enabled = false }]
        };
        var active = switched.Active();
        if (active.HelpActions.Length != HelpAction.Defaults.Length - 1 || active.Commands.Length != 0 || switched.HelpActions.Length != HelpAction.Defaults.Length
            || (switched with { ShowHelp = false }).Active().HelpActions.Length != 0)
            throw new InvalidOperationException("Disabled entries were not filtered.");
        SaveShellScreenshot();

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
        var parkedBob = sessions[bob.Id];
        parkedBob.InactiveSinceMilliseconds = Environment.TickCount64 - config.ChangeUserAfterSeconds * 1000L + 1000;
        ExpireSessions();
        if (!sessions.ContainsKey(bob.Id)) throw new InvalidOperationException("Parked session expired before its retention limit.");
        parkedBob.InactiveSinceMilliseconds = Environment.TickCount64 - config.ChangeUserAfterSeconds * 1000L - 1000;
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
        await FilesNavigationSmokeTestAsync();
        // Card PIN unlock runs in a child process; a missing reader must fail cleanly, without a dialog.
        var pin = await CardPin.VerifyAsync("Kiosk self-test missing reader", Handle, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        if (pin.Code != CardPin.NoKey || pin.Message.Length == 0) throw new InvalidOperationException("Card PIN check: " + pin.Code + " " + pin.Message);
        if (CardPin.ReadIdentity("Kiosk self-test missing reader") != null) throw new InvalidOperationException("Identity read from a missing reader.");

        var entry = new ApplicationEntry { Name = "Test", Path = Environment.ProcessPath!, Arguments = "--app-host-fixture" };
        // A slow executable path must leave the UI responsive; closing the page while it waits must abort launch.
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var stalled = new ApplicationView { ProgramExistsForTest = _ => { entered.Set(); release.Wait(); return true; } })
        {
            surface.Controls.Add(stalled);
            var pending = stalled.StartAsync(entry);
            try
            {
                if (!await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))))
                    throw new InvalidOperationException("Slow application path probe did not start.");
                var uiPulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginInvoke(() => uiPulse.TrySetResult());
                await uiPulse.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (pending.IsCompleted) throw new InvalidOperationException("Slow application path probe did not wait.");
            }
            finally
            {
                stalled.Dispose();
                release.Set();
                surface.Controls.Remove(stalled);
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (stalled.ProgramPath.Length != 0) throw new InvalidOperationException("Closed application page continued launching.");
        }
        if (new Config().SelfServiceAuthentication != SelfServiceAuthentication.WindowsSession)
            throw new InvalidOperationException("New self-service configuration did not select Windows SSO.");
        var legacyAuthFile = Path.Combine(AppContext.BaseDirectory, "self-test-legacy-auth.json");
        try
        {
            File.WriteAllText(legacyAuthFile, """{"ConfigVersion":5,"SessionMode":"SelfService","SelfServiceSignInUrl":"https://old.example/sign-in","SelfServiceLinkUrl":"https://old.example/link"}""");
            var oldSelfService = Configuration.Load(legacyAuthFile);
            if (oldSelfService.SelfServiceAuthentication != SelfServiceAuthentication.CardBroker ||
                oldSelfService.SelfServiceSignInUrl != null || oldSelfService.SelfServiceLinkUrl != null)
                throw new InvalidOperationException("Existing self-service configuration changed its sign-in method.");
            File.WriteAllText(legacyAuthFile, """{"ConfigVersion":5,"SessionMode":"Kiosk"}""");
            if (Configuration.Load(legacyAuthFile).SelfServiceAuthentication != SelfServiceAuthentication.WindowsSession)
                throw new InvalidOperationException("An application-station configuration did not offer Windows SSO when switched to self-service.");
        }
        finally { File.Delete(legacyAuthFile); }
        var testConfig = config with
        {
            Applications = [entry],
            Bookmarks = [new Bookmark { Name = "Google", Url = "https://www.google.com" }],
            RdpConnections = [new RdpConnection { Name = "Biuro", Server = "rds.example.local" },
                new RdpConnection { Name = "Entra", Server = "pc.example.local", Authentication = RdpAuthentication.EntraId, UserName = "jan@example.com" }],
            AutoConnectRdpName = "Biuro", RequireCredentialPrompt = false, UnlockMethod = UnlockMethod.CardAndPin,
            ConfigVersion = Config.CurrentVersion, EnableBrowser = false,
            LockAfterSeconds = 60, ChangeUserAfterSeconds = 30, AccentColor = "#3DDC84",
            SessionMode = SessionMode.SelfService, SelfServiceAuthentication = SelfServiceAuthentication.WindowsSession,
            BrowserOnlyBookmarks = true, BrowserAllowedDomains = ["login.microsoftonline.com"],
            AdminCards = [new AdminCard { Name = "Admin", Id = "cert:ADMIN" }], AllowWindowsAdministrators = false
        };
        var configFile = Path.Combine(AppContext.BaseDirectory, "self-test-config.json");
        Configuration.Save(configFile, testConfig);
        var restored = Configuration.Load(configFile);
        if (restored.Applications.Single() != entry || restored.Bookmarks.Single() != testConfig.Bookmarks.Single() ||
            !restored.RdpConnections.SequenceEqual(testConfig.RdpConnections) || restored.AutoConnectRdpName != "Biuro" ||
            restored.RequireCredentialPrompt || restored.UnlockMethod != UnlockMethod.CardAndPin ||
            restored.LockAfterSeconds != 60 || restored.ChangeUserAfterSeconds != 30 || restored.AccentColor != "#3DDC84" ||
            restored.SessionMode != SessionMode.SelfService || restored.SelfServiceAuthentication != SelfServiceAuthentication.WindowsSession ||
            !restored.BrowserOnlyBookmarks || restored.BrowserAllowedDomains.Single() != "login.microsoftonline.com" || restored.EnableBrowser)
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
                edited.ChangeUserAfterSeconds != 30 || edited.AccentColor != "#3DDC84" ||
                edited.SessionMode != SessionMode.SelfService || edited.SelfServiceAuthentication != SelfServiceAuthentication.WindowsSession ||
                edited.AdminCards.Single() != restored.AdminCards.Single() || edited.AllowWindowsAdministrators ||
                !edited.BrowserOnlyBookmarks || edited.BrowserAllowedDomains.Single() != "login.microsoftonline.com" ||
                edited.EnableBrowser || edited.ReaderName != restored.ReaderName || !edited.Applications.SequenceEqual(restored.Applications))
                throw new InvalidOperationException("Settings view does not round-trip the configuration.");
        }
        SaveSettingsScreenshots(restored);

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
        _ = WindowsSession.State();
        CloseAllSessions("shell_smoke_test");
        config = kioskConfig;
    }

    internal async Task IdleScreenRecoverySmokeTestAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "self-test-idle-host.exe");
        IdleScreen? idle = null;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            idle = new IdleScreen(Screen.FromControl(this), new ScreenContent { Path = path, Arguments = "--app-host-fixture" }, 1);
            idle.Show();
            for (int i = 0; i < 50 && !idle.ShowingError; i++) await Task.Delay(100);
            if (!idle.ShowingError) throw new InvalidOperationException("Idle screen did not report a failed application launch.");
            File.Copy(Environment.ProcessPath!, path);
            for (int i = 0; i < 200 && (!idle.ApplicationStarted || idle.ShowingError); i++) await Task.Delay(100);
            if (!idle.ApplicationStarted || idle.ShowingError)
                throw new InvalidOperationException("Idle screen did not retry its application and clear the error.");
        }
        finally
        {
            idle?.Dispose();
            for (int i = 0; i < 100 && File.Exists(path); i++)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(100); }
            }
            if (File.Exists(path)) throw new IOException("Idle screen test left its application executable in use.");
        }
    }

    private async Task FilesNavigationSmokeTestAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiosk-files-test-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "visible.txt"), "file contents");
        File.WriteAllText(Path.Combine(second, "stale.txt"), "old result");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int hold = 0;
        Func<Func<object?>, object?> runAs = work =>
        {
            if (Volatile.Read(ref hold) == 1) { entered.Set(); release.Wait(); }
            return work();
        };
        var files = new FilesView(runAs, null);
        surface.Controls.Add(files);
        try
        {
            files.CreateControl();
            await files.NavigateForSmokeTestAsync(first).WaitAsync(TimeSpan.FromSeconds(10));
            await files.PlacesForSmokeTestAsync.WaitAsync(TimeSpan.FromSeconds(10));
            if (files.FolderForSmokeTest != first || !files.ItemsForSmokeTest.SequenceEqual(["visible.txt"]))
                throw new InvalidOperationException("Files did not show the requested folder.");
            Volatile.Write(ref hold, 1);
            var stale = files.NavigateForSmokeTestAsync(second);
            if (!await Task.Run(() => entered.Wait(5000))) throw new InvalidOperationException("Slow folder test did not start.");
            Volatile.Write(ref hold, 0);
            await files.NavigateForSmokeTestAsync(first).WaitAsync(TimeSpan.FromSeconds(10));
            release.Set();
            await stale.WaitAsync(TimeSpan.FromSeconds(10));
            if (files.FolderForSmokeTest != first || !files.ItemsForSmokeTest.SequenceEqual(["visible.txt"]))
                throw new InvalidOperationException("A stale folder result replaced the current folder.");

            // Exercise the real file commands on an isolated tree, including their impersonated worker path.
            var source = Path.Combine(root, "source");
            var nested = Path.Combine(source, "nested");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(nested, "data.txt"), "Kiosk file work");
            var hiddenSource = Path.Combine(nested, "hidden.txt");
            File.WriteAllText(hiddenSource, "hidden");
            File.SetAttributes(hiddenSource, File.GetAttributes(hiddenSource) | FileAttributes.Hidden);
            await files.NavigateForSmokeTestAsync(root);
            if (!await files.PasteForSmokeTestAsync([source], destination, move: false) ||
                File.ReadAllText(Path.Combine(destination, "source", "nested", "data.txt")) != "Kiosk file work" ||
                (File.GetAttributes(Path.Combine(destination, "source", "nested", "hidden.txt")) & FileAttributes.Hidden) == 0)
                throw new InvalidOperationException("Background folder copy failed.");
            if (!await files.ZipForSmokeTestAsync([source])) throw new InvalidOperationException("Background ZIP failed.");
            var archive = Path.Combine(root, "source.zip");
            if (!File.Exists(archive) || !await files.UnzipForSmokeTestAsync(archive) ||
                File.ReadAllText(Path.Combine(root, "source (2)", "source", "nested", "data.txt")) != "Kiosk file work" ||
                !Directory.Exists(Path.Combine(root, "source (2)", "source", "empty")))
                throw new InvalidOperationException("Background ZIP extraction failed.");
            var unsafeArchive = Path.Combine(root, "unsafe.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(unsafeArchive, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("safe.txt").Open())) writer.Write("temporary");
                zip.CreateEntry("../outside.txt");
            }
            if (await files.UnzipForSmokeTestAsync(unsafeArchive) || File.Exists(Path.Combine(root, "outside.txt")) ||
                Directory.Exists(Path.Combine(root, "unsafe")) || Directory.GetDirectories(root, "unsafe.partial-*").Length != 0)
                throw new InvalidOperationException("Unsafe ZIP path or partial extraction was accepted.");
            if (!await files.PasteForSmokeTestAsync([source], destination, move: true) || Directory.Exists(source) ||
                !await files.DeleteForSmokeTestAsync([Path.Combine(destination, "source (2)")]) ||
                Directory.Exists(Path.Combine(destination, "source (2)")))
                throw new InvalidOperationException("Background move or delete failed.");

            // A pending copy must leave the UI free to navigate, then finish in the captured destination.
            entered.Reset(); release.Reset();
            Volatile.Write(ref hold, 1);
            var copying = files.PasteForSmokeTestAsync([archive], destination, move: false);
            if (!await Task.Run(() => entered.Wait(5000))) throw new InvalidOperationException("Slow file operation test did not start.");
            Volatile.Write(ref hold, 0);
            await files.NavigateForSmokeTestAsync(first).WaitAsync(TimeSpan.FromSeconds(10));
            release.Set();
            if (!await copying.WaitAsync(TimeSpan.FromSeconds(10)) || files.FolderForSmokeTest != first ||
                !File.Exists(Path.Combine(destination, "source.zip")))
                throw new InvalidOperationException("A file operation blocked navigation or lost its destination.");

            entered.Reset(); release.Reset();
            Volatile.Write(ref hold, 1);
            var cancelled = files.PasteForSmokeTestAsync([archive], destination, move: false);
            if (!await Task.Run(() => entered.Wait(5000))) throw new InvalidOperationException("Cancellation test did not start.");
            files.Dispose(); // A closed card session must not publish a queued file operation.
            release.Set();
            if (await cancelled.WaitAsync(TimeSpan.FromSeconds(10)) ||
                File.Exists(Path.Combine(destination, "source (2).zip")) ||
                Directory.EnumerateFileSystemEntries(destination, "*.partial-*").Any())
                throw new InvalidOperationException("File work continued after its view was closed.");
        }
        finally
        {
            release.Set();
            surface.Controls.Remove(files);
            files.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    internal async Task CommandSmokeTestAsync()
    {
        var original = config;
        var marker = Path.Combine(AppContext.BaseDirectory, "self-test-command-" + Guid.NewGuid().ToString("N"));
        var script = marker + ".ps1";
        var completedScript = marker + ".done.ps1";
        System.Diagnostics.Process? parent = null, child = null;
        UserSession? session = null;
        try
        {
            config = config with { SessionMode = SessionMode.Kiosk, UnlockMethod = UnlockMethod.Card };
            CardInserted(new CardIdentity("cert:COMMAND", "Command Test"));
            session = current ?? throw new InvalidOperationException("Command test did not open a session.");
            File.WriteAllText(completedScript, "exit 7");
            RunCommand(new CommandEntry { Name = "Completed fixture", Path = completedScript });
            var until = DateTime.UtcNow.AddSeconds(10);
            while (session.Commands.HasRunning && DateTime.UtcNow < until) await Task.Delay(50);
            if (session.Commands.HasRunning || session.HasWork || session.Notice != "Polecenie „Completed fixture” zakończone z kodem 7.")
                throw new InvalidOperationException("A completed command did not release its session job or report its exit code.");
            File.WriteAllText(script, """
                param([string]$Marker, [switch]$Child)
                $suffix = if ($Child) { '.child' } else { '.parent' }
                Set-Content -LiteralPath ($Marker + $suffix) -Value $PID -NoNewline
                if ($Child) { Start-Sleep -Seconds 3600; return }
                $arguments = '-NoProfile -NonInteractive -File "' + $PSCommandPath + '" -Marker "' + $Marker + '" -Child'
                $process = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
                """);
            RunCommand(new CommandEntry { Name = "Command fixture", Path = script,
                Arguments = "-Marker \"" + marker + "\"" });
            // Existence alone is not enough: Set-Content can still hold the newly created file open.
            var markerDeadline = Environment.TickCount64 + 15_000;
            static async Task<int> ReadPidAsync(string path, long deadline)
            {
                while (Environment.TickCount64 < deadline)
                {
                    try
                    {
                        if (int.TryParse(File.ReadAllText(path), out var pid) && pid > 0) return pid;
                    }
                    catch (IOException) { } // The file is not ready or the fixture still has it open.
                    await Task.Delay(50);
                }
                throw new InvalidOperationException("Background command did not write a readable process marker: " + Path.GetFileName(path));
            }
            var parentPid = await ReadPidAsync(marker + ".parent", markerDeadline);
            var childPid = await ReadPidAsync(marker + ".child", markerDeadline);
            try { parent = System.Diagnostics.Process.GetProcessById(parentPid); }
            catch (ArgumentException) { } // The short wrapper already exited.
            child = System.Diagnostics.Process.GetProcessById(childPid);
            if (parent != null) await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (child.HasExited || !session.HasWork)
                throw new InvalidOperationException("A child process did not remain in its session after its parent exited.");
            CloseSession(session, "command_smoke_test");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            until = DateTime.UtcNow.AddSeconds(5);
            while (session.Commands.HasRunning && DateTime.UtcNow < until) await Task.Delay(50);
            if (session.Commands.HasRunning || sessions.ContainsKey(session.Identity.Id))
                throw new InvalidOperationException("Background command remained after its session closed.");
        }
        finally
        {
            if (session != null && sessions.ContainsKey(session.Identity.Id)) CloseSession(session, "command_smoke_cleanup");
            foreach (var process in new[] { parent, child })
            {
                if (process == null) continue;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            foreach (var suffix in new[] { ".parent", ".child" })
                if (File.Exists(marker + suffix)) File.Delete(marker + suffix);
            if (File.Exists(script)) File.Delete(script);
            if (File.Exists(completedScript)) File.Delete(completedScript);
            config = original;
        }
    }

    internal void FinishSelfTest()
    {
        timer.Stop();
        closeConfirmed = true;
        CloseAllSessions("self_test_finished");
    }
#endif
}

// IMsRdpExtendedSettings (IUnknown): property bag used for EnableRdsAadAuth (Microsoft Entra sign-in).
[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}
