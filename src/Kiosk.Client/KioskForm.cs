using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Security.Credentials.UI;

namespace Kiosk.Client;

/// <summary>
/// The kiosk shell for a shared PC: a desktop, one Menu button on the bottom taskbar and embedded
/// windows (remote desktops, browser, notes, applications). Every card gets its own session.
/// Removing the card parks the session (windows keep running, hidden); another card gets its own
/// session at once; reinserting a card brings its session back. Sessions expire after
/// ChangeUserAfterSeconds of inactivity. Nothing here touches the Windows lock screen.
/// </summary>
internal sealed class KioskForm : Form
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
        internal bool HasWork => Notes?.Dirty == true || Applications.Count > 0 || Rdp.Count > 0;
        /// <summary>The card's account (UPN from its certificate) is in this PC's local Administrators group.</summary>
        internal bool IsWindowsAdmin { get; set; }
        /// <summary>The person's own Windows / Entra account after "Zaloguj" (SignInAsUser); their programs run as it.</summary>
        internal UserLogon? Logon { get; set; }
        internal string Name => Logon?.DisplayName ?? Identity.Name;
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
    private bool shellCardSeen;
    private DateTime? disconnectedSince;
    private bool ShellMode => config.SessionMode == SessionMode.WindowsShell;
    private string? notice;

    private bool CanUseApps => current is { Locked: false };
    private bool HasSessionWindows => current?.Windows.Any(w => w.Key != SettingsKey) == true;

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
        taskbar.LockButton.Click += (_, _) => Lock("manual_lock", 0);
        taskbar.LogoutButton.Click += (_, _) => { if (current != null && ConfirmEnd([current])) Logout("manual_logout"); };
        taskbar.ExitButton.Visible = config.TestMode;
        taskbar.ExitButton.Click += (_, _) => Close();
        desktop.ResumeRequested += () => _ = VerifyAndUnlockAsync();

        timer.Tick += (_, _) => TickState();
        Shown += (_, _) => timer.Start();
        FormClosing += (_, e) =>
        {
            if (!closeConfirmed && !ConfirmEnd(sessions.Values)) { e.Cancel = true; return; }
            timer.Stop(); CloseAllSessions("client_exit"); reader.Dispose();
        };
        FormClosed += (_, _) => menu.Dispose();
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
                    else CardInserted(identifyCard() ?? UncertifiedCard());
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
        if (settingsView != null && session.Windows.Any(w => w.Key == SettingsKey)) CloseWindow(SettingsKey, "settings_closed", session);
        if (!session.Locked) session.InactiveSince = DateTime.UtcNow;
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
        session.Locked = true;
        session.InactiveSince = DateTime.UtcNow - TimeSpan.FromMilliseconds(idleMilliseconds);
        ShowView(desktop);
        Audit.Write(reason, session.Identity.Name);
        RefreshChrome();
    }

    private void Unlock(UserSession session)
    {
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
            // Like runas: the person's own account, checked by Windows; asked once per Kiosk session.
            try { session.Logon = UserLogon.Prompt(Handle, "Podaj login (np. jan@firma.pl) i hasło swojego konta. Aplikacje, przeglądarka i pulpity zdalne uruchomią się jako Ty."); }
            catch (Exception ex) { session.Notice = "Nie udało się zalogować: " + ex.Message; Audit.Write("signin_error", ex.Message); }
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
                while (PinDialog.Ask(this, message) is { } pin)
                {
                    var saved = await Task.Run(() => CardVault.Enroll(id, logon, readerName, pin));
                    Audit.Write(saved.Code == CardPin.Verified ? "card_enrolled" : "card_enroll_failed", saved.Code.ToString());
                    if (saved.Code == CardPin.WrongPin) { message = saved.Message + " Spróbuj ponownie."; continue; }
                    session.Notice = saved.Code == CardPin.Verified ? null : "Karta nie została zapamiętana: " + saved.Message;
                    break;
                }
            }
            catch (Exception ex) { session.Notice = "Karta nie została zapamiętana: " + ex.Message; Audit.Write("card_enroll_failed", ex.Message); }
            finally { verifying = false; cardKey = reader.Snapshot(config.ReaderName); }
        }
        Unlock(session);
    }

    private async Task<CardPin.Result> VerifyFidoPinAsync()
    {
        var readerName = reader.ActiveReader;
        string message = "Wpisz PIN karty.";
        while (PinDialog.Ask(this, message) is { } pin)
        {
            try
            {
                await Task.Run(() => { using var card = new CtapCard(readerName); card.UsePin(pin); });
                return new(CardPin.Verified, "PIN karty potwierdzony.");
            }
            catch (CtapException ex) when (ex.Code == 0x31) { message = ex.Message + " Spróbuj ponownie."; }
            catch (CtapException ex) { return new(CardPin.Failed, ex.Message); }
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
                var pin = PinDialog.Ask(this, message);
                if (pin == null) { session.Notice = "Logowanie anulowane."; return false; }
                var opened = await Task.Run(() => CardVault.Open(id, readerName, pin));
                if (opened.Logon != null) { session.Logon = opened.Logon; Audit.Write("card_vault_opened", opened.Logon.DisplayName); return true; }
                Audit.Write("card_vault_failed", opened.Code.ToString());
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
                    var result = await CardPin.VerifyAsync(reader.ActiveReader, Handle, cancel.Token);
                    // A FIDO2 card has no certificate key: Kiosk asks for its PIN and the card checks it (CTAP2).
                    if (result.Code == CardPin.NoKey) result = await VerifyFidoPinAsync();
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
        if (session == current) { menu.HideMenu(); ShowView(desktop); current = null; }
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
        MessageBox.Show(this, "Zakończyć sesję? Niezapisane notatki zostaną usunięte, a pulpity zdalne i aplikacje zamknięte.",
            "Wyloguj", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private bool ConfirmClose(string key)
    {
        string? question = key == NotesKey && current?.Notes?.Dirty == true ? "Zamknąć notatnik? Niezapisany tekst zostanie usunięty." :
            key.StartsWith("app:") ? "Zamknąć aplikację? Niezapisane dane mogą zostać utracone." :
            key.StartsWith("rdp:") ? "Rozłączyć ten pulpit zdalny?" : null;
        return question == null || MessageBox.Show(this, question, "Zamknij okno", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
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

    private void ShellTick()
    {
        if (WindowsSession.IsDisconnected())
        {
            disconnectedSince ??= DateTime.UtcNow;
            if (config.ChangeUserAfterSeconds > 0 && DateTime.UtcNow - disconnectedSince >= TimeSpan.FromSeconds(config.ChangeUserAfterSeconds))
                SignOutWindows("session_expired");
            return;
        }
        if (disconnectedSince != null)
        {
            // The person signed in again (Windows checked their card / Hello): continue where they left off.
            disconnectedSince = null;
            shellCardSeen = false;
            Audit.Write("windows_session_reconnected", current?.Identity.Name ?? "");
        }
        if (reader.Snapshot(config.ReaderName) != null) shellCardSeen = true;
        else if (shellCardSeen && config.DisconnectOnCardRemoval) { DisconnectWindows("card_removed"); return; }
        if (Native.IdleMilliseconds() >= (uint)config.LockAfterSeconds * 1000u) DisconnectWindows("idle_lock");
    }

    /// <summary>Like Windows "Switch user": programs keep running, the sign-in screen is shown for the next person.</summary>
    private void DisconnectWindows(string reason)
    {
        menu.HideMenu();
        shellCardSeen = false;
        Audit.Write(reason, current?.Identity.Name ?? "");
        try { WindowsSession.Disconnect(); }
        catch (Exception ex) { notice = "Nie udało się rozłączyć sesji Windows: " + ex.Message; Audit.Write("disconnect_failed", ex.Message); }
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
        int others = sessions.Values.Count(s => s != current);
        taskbar.MenuButton.Enabled = CanUseApps; // Only for a signed-in, unlocked person.
        taskbar.LockButton.Enabled = CanUseApps;
        taskbar.LogoutButton.Enabled = current != null;
        taskbar.SetTasks(CanUseApps ? current!.Windows.Select(w => (w.Key, w.Title, w.View == visibleView)).ToArray() : []);
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
            (s?.Rdp.ContainsKey(c.Name) == true ? "Otwarte · " : "") + c.Server + " · " + AuthenticationLabel(c.Authentication),
            () => OpenRdp(c), can)).ToList();
        if (remote.Count == 0) remote.Add(new MenuItemSpec("Brak połączeń", "Dodaj je w Konfiguracji", () => { }, false));
        var apps = new List<MenuItemSpec>();
        if (config.EnableBrowser) apps.Add(new("Przeglądarka", s?.Browser != null ? "Otwarta" : "Internet", () => _ = OpenBrowserAsync(), can));
        // The built-in Notatnik is an ordinary entry of the application list, so it can be removed there.
        apps.AddRange(config.Applications.Select(a => a.IsBuiltInNotes
            ? new MenuItemSpec(a.Name, s?.Notes != null ? "Otwarty" : "Notatki tekstowe", OpenNotes, can)
            : new MenuItemSpec(a.Name, s?.Applications.ContainsKey(a.Name) == true ? "Otwarta" : "Aplikacja", () => _ = OpenApplicationAsync(a), can)));
        if (apps.Count == 0) apps.Add(new MenuItemSpec("Brak aplikacji", "Dodaj je w Konfiguracji", () => { }, false));
        var system = new List<MenuItemSpec> { new("Pulpit", "Pokaż pulpit", () => ShowView(desktop)) };
        if (configPath != null && s != null && IsAdmin(s))
            system.Add(new MenuItemSpec("Konfiguracja", HasSessionWindows ? "Zamknij najpierw otwarte okna" : "Ustawienia Kiosku", OpenSettings, can && !HasSessionWindows));
        if (can)
        {
            system.Add(new MenuItemSpec("Zablokuj", "Zablokuj sesję", () => Lock("manual_lock", 0)));
            system.Add(new MenuItemSpec("Wyloguj", "Zamknij swoją sesję", () => { if (current != null && ConfirmEnd([current])) Logout("manual_logout"); }));
        }
        return [new("Pulpity zdalne", remote), new("Aplikacje", apps), new("System", system)];
    }

    private void ShowView(Control view)
    {
        foreach (Control child in surface.Controls) child.Visible = child == view;
        view.BringToFront();
        view.Focus();
        visibleView = view;
        if (view != desktop && current != null) current.LastView = view;
        RefreshChrome();
    }

    private void ActivateWindow(string key)
    {
        if (!CanUseApps) return;
        var window = current!.Windows.FirstOrDefault(w => w.Key == key);
        if (window == null) return;
        // Like the Windows taskbar: clicking the active window shows the desktop.
        ShowView(visibleView == window.View ? desktop : window.View);
    }

    private void ActivateExisting(string key)
    {
        var window = current?.Windows.FirstOrDefault(w => w.Key == key);
        if (window != null && CanUseApps) ShowView(window.View);
    }

    private void AddWindow(UserSession session, string key, string title, Control view)
    {
        view.Visible = false;
        if (view.Parent != surface) surface.Controls.Add(view);
        session.Windows.Add(new Window(key, title, view));
        if (session == current && !session.Locked) ShowView(view);
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
        surface.Controls.Remove(window.View);
        window.View.Dispose();
    }

    // ---------------------------------------------------------------- remote desktops

    private void OpenRdp(RdpConnection connection)
    {
        var key = "rdp:" + connection.Name;
        if (current?.Rdp.ContainsKey(connection.Name) == true) { ActivateExisting(key); return; }
        if (!CheckCard()) return;
        var session = current!;
        RdpHost? host = null;
        try
        {
            host = CreateRdp(connection, config.RequireCredentialPrompt, session.Logon);
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

    private RdpHost CreateRdp(RdpConnection connection, bool promptForCredentials, UserLogon? logon = null)
    {
        // "Bieżący użytkownik" after "Zaloguj": the person's own account signs in to the remote desktop.
        bool asLoggedOnUser = logon != null && connection.Authentication == RdpAuthentication.WindowsCurrentUser;
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
            client.UserName = logon!.Login;
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

    /// <summary>An application closed with its own × (or exited): remove its Kiosk window and taskbar button.</summary>
    private void MonitorApplications()
    {
        foreach (var session in sessions.Values.ToArray())
        foreach (var (name, view) in session.Applications.ToArray())
            if (view.Started && !view.Refresh())
                CloseWindow("app:" + name, "application_exited", session);
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
        if (current?.Browser != null) { ActivateExisting(BrowserKey); return; }
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

    private async Task OpenApplicationAsync(ApplicationEntry entry)
    {
        if (entry.IsBuiltInNotes) { OpenNotes(); return; }
        var key = "app:" + entry.Name;
        if (current?.Applications.TryGetValue(entry.Name, out var existing) == true)
        {
            if (existing.HostedWindow == IntPtr.Zero || AppNative.IsWindow(existing.HostedWindow)) { ActivateExisting(key); return; }
            CloseWindow(key, "application_exited");
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
            await requested.StartAsync(entry, session.Logon);
            if (!requested.IsDisposed) Audit.Write("application_opened", entry.Name);
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
    /// Administrators group. While the list is empty in test mode everyone may, so the first admin can add a card.
    /// </summary>
    private bool IsAdmin(UserSession session, Config? settings = null)
    {
        settings ??= config;
        if (settings.AdminCards.Any(a => a.Id == session.Identity.Id)) return true;
        if (settings.AllowWindowsAdministrators && session.IsWindowsAdmin) return true;
        return settings.TestMode && settings.AdminCards.Length == 0;
    }

    private async Task CheckWindowsAdministratorAsync(UserSession session)
    {
        var upn = session.Identity.Upn;
        if (upn.Length == 0) return;
        session.IsWindowsAdmin = await Task.Run(() => LocalAdministrators.Contains(upn));
        if (session.IsWindowsAdmin) Audit.Write("windows_administrator", session.Identity.Name);
        RefreshChrome();
    }

    private void OpenSettings()
    {
        if (settingsView != null) { ActivateExisting(SettingsKey); return; }
        if (HasSessionWindows || configPath == null || !CanUseApps || !IsAdmin(current!)) return;
        var session = current!;
        var view = new SettingsView(config, session.Identity, updated => IsAdmin(session, updated));
        settingsView = view;
        view.CancelRequested += () => CloseWindow(SettingsKey, "settings_closed", session);
        view.SaveRequested += updated =>
        {
            Configuration.Save(configPath, updated);
            config = updated;
            KioskTheme.SetAccent(updated.AccentColor);
            Invalidate(true);
            Audit.Write("settings_saved");
            CloseWindow(SettingsKey, "settings_closed", session);
        };
        AddWindow(session, SettingsKey, "Konfiguracja", view);
    }

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
        if (!LocalAdministrators.Matches(@"INVESTA\k.lechmyc", "k.lechmyc", "investa.pl") || !LocalAdministrators.Matches(@"AzureAD\JanKowalski", "JanKowalski", "AzureAD") ||
            LocalAdministrators.Matches(@"OTHER\k.lechmyc", "k.lechmyc", "investa.pl") || LocalAdministrators.Matches("Kris", "Kris", "x.pl"))
            throw new InvalidOperationException("Administrators group matching failed.");
        _ = LocalAdministrators.Contains("nobody@example.invalid"); // Reads the real group without failing.

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

// IMsRdpExtendedSettings (IUnknown): property bag used for EnableRdsAadAuth (Microsoft Entra sign-in).
[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}
