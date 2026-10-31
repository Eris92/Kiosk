namespace Kiosk.Client;

/// <summary>
/// Punkt samoobsługowy (SessionMode.SelfService): Szybka pomoc and Komunikaty without the application Menu
/// or taskbar. A card and PIN sign the person in inside Kiosk, and help pages run in that person's account.
/// Removing the card ends only the person's Kiosk session. Ctrl+Alt+K opens Konfiguracja for administrators.
/// </summary>
internal sealed partial class KioskForm
{
    private bool SelfService => config.SessionMode == SessionMode.SelfService;
    // Retained for the old configuration schema. OS session ownership now belongs only to WindowsShell.
    private bool SelfServiceWindowsSession => false;
    private bool localCardAttempted;
    private string? localAttemptedCardId;
    private Task? cardSessionClosing;

    /// <summary>The person signed in to Windows with the card and its PIN; null = nobody (pages open as a guest).</summary>
    private UserLogon? cardLogon;
    /// <summary>The card sign-in screen inside the Kiosk, listening for a tap; null while it starts or after it ended.</summary>
    private FidoSignIn? signInScreen;
    private Task<FidoSignIn?>? signInStarting;
    private bool cardSignInFinalizing;
    private long? signInFailedAtMilliseconds;
    /// <summary>Card sign-in screens that did not start in a row: each waits longer (10 s, 20 s… up to 5 minutes).</summary>
    private int signInStartFailures;
    /// <summary>An unsupported Windows build cannot start this adapter; retrying it does not change the result.</summary>
    private bool signInUnsupported;
    /// <summary>A Szybka pomoc result (card reset, PIN change…) is shown in the card instead of the sign-in until then.</summary>
    private long? noticeUntilMilliseconds;

    /// <summary>When the signed-in card disappeared; its pages close at once and sign-out follows the configured delay.</summary>
    private long? cardAwaySinceMilliseconds;
    /// <summary>The exact PC/SC card insertion observed after sign-in, including its reader event counter.</summary>
    private string? signedInCardSnapshot;
    /// <summary>Konfiguracja → Szybka pomoc: "Wyloguj po zdjęciu karty".</summary>
    private int CardReturnSeconds => config.CardRemovedSeconds;

    /// <summary>Nobody signed in: the card sign-in screen is (re)started in the start screen and waits for a tap.</summary>
    private void PrepareCardSignIn()
    {
        if (cardSessionClosing != null)
        {
            if (!cardSessionClosing.IsCompleted) return;
            cardSessionClosing = null; notice = null;
        }
        // A page is open (e.g. Skonfiguruj MFA): no card sign-in screen meanwhile. It is a window of another program
        // inside the Kiosk's, and such a window shares the Kiosk's keyboard: typing in the page went nowhere while it
        // was starting, failing or waiting. It starts again on Strona główna.
        if (desktop.Page != null && cardLogon == null && signInScreen is { } waiting)
        {
            signInScreen = null;   // Its end is then no error (WaitForCardSignInAsync: an earlier screen).
            _ = Task.Run(waiting.Dispose);
            Audit.Write("fido_paused_for_page");
            RefreshChrome();
        }
        if (cardLogon != null || signInScreen != null || registeringCard || cardSignInFinalizing) return;
        if (signInStarting != null)
        {
            if (!signInStarting.IsCompleted) return;
            var ready = signInStarting.Status == TaskStatus.RanToCompletion ? signInStarting.Result : null;
            // An exception outside PrepareAsync's own handling was lost before: written down and shown.
            var reason = signInStarting.Exception?.GetBaseException() is { } fault
                ? fault.GetType().Name + ": " + fault.Message : FidoSignIn.LastError;
            if (signInStarting.Exception != null) Audit.Write("fido_prepare_failed", signInStarting.Exception.GetBaseException().ToString());
            signInStarting = null;
            if (ready != null && desktop.Page != null) { _ = Task.Run(ready.Dispose); return; }   // A page opened meanwhile.
            if (ready == null)
            {
                if (FidoSignIn.IsUnsupported(reason))
                {
                    signInUnsupported = true;
                    notice = "Logowanie kartą jest niedostępne na tym komputerze. Strony pomocy możesz otworzyć bez logowania. Skontaktuj się z administratorem.";
                    Audit.Write("fido_unsupported_windows", reason);
                }
                else
                {
                    signInFailedAtMilliseconds = Environment.TickCount64;
                    signInStartFailures++;
                    notice = "Logowanie kartą nie wystartowało" + (reason.Length > 0 ? ": " + reason.TrimEnd('.') : "") + ". Ponawiam…";
                }
                RefreshChrome();
                return;
            }
            signInScreen = ready;
            signInStartFailures = 0;
            notice = null;
            _ = WaitForCardSignInAsync(ready);
            RefreshChrome();
            return;
        }
        // A PC where it cannot start (e.g. Windows refuses the card sign-in there) is not asked again every minute.
        if (desktop.Page != null || signInUnsupported) return;
        var pause = TimeSpan.FromSeconds(Math.Min(300, 10 << Math.Min(5, Math.Max(0, signInStartFailures - 1))));
        if (signInFailedAtMilliseconds is { } failedAt && Environment.TickCount64 - failedAt < pause.TotalMilliseconds) return;
        Audit.Write("fido_prepare_start");
        try { signInStarting = FidoSignIn.PrepareAsync(desktop.SignInPlace, desktop.DeviceDpi); }
        catch (Exception ex) { signInStarting = Task.FromException<FidoSignIn?>(ex); }
    }

    /// <summary>The person taps the card and types the PIN in the sign-in screen; then their Windows session is ready.</summary>
    private async Task WaitForCardSignInAsync(FidoSignIn screen)
    {
        var outcome = await screen.WaitAsync();
        bool latest = signInScreen == screen;
        if (latest) signInScreen = null;
        if (outcome.SignIn is not { } signIn)
        {
            if (!latest) return;   // An earlier screen, already replaced by a newer one: nothing to show.
            if (outcome.Message.StartsWith(FidoSignIn.UnknownKey, StringComparison.Ordinal))
            {
                // A new or reset card: no account knows it. PIN, then registration, instead of the bare error.
                Audit.Write("selfservice_card_unknown", outcome.Message[FidoSignIn.UnknownKey.Length..]);
                _ = RegisterUnknownCardAsync();
                return;
            }
            if (FidoSignIn.IsUnsupported(outcome.Message))
            {
                signInUnsupported = true;
                notice = "Logowanie kartą jest niedostępne na tym komputerze. Strony pomocy możesz otworzyć bez logowania. Skontaktuj się z administratorem.";
                Audit.Write("fido_unsupported_windows", outcome.Message);
                RefreshChrome();
                return;
            }
            // A fresh sign-in screen follows after the usual pause (not at once: a screen per tick piled up).
            signInFailedAtMilliseconds = Environment.TickCount64;
            Audit.Write("selfservice_card_signin_failed", outcome.Message);
            notice = outcome.Message.IndexOf("Anulowano", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Przyłóż kartę do czytnika, aby zalogować się ponownie."
                : "Nie udało się zalogować kartą: " + outcome.Message;
            RefreshChrome();
            return;
        }
        if (!latest)
        {
            _ = Task.Run(signIn.Dispose);
            return;
        }
        var logon = UserLogon.FromCard(signIn);
        await CompleteCardSignInAsync(logon);
    }

    private async Task CompleteCardSignInAsync(UserLogon logon, string? expectedCardId = null)
    {
        string? cardSnapshot = null;
        cardSignInFinalizing = true;
        notice = null;
        RefreshChrome();
        try
        {
            // Windows can reset the card while finishing sign-in. Capture two matching observations.
            string? candidate = null;
            for (int attempt = 0; attempt < 20 && cardSnapshot == null; attempt++)
            {
                var observed = reader.Snapshot(config.ReaderName);
                if (observed != null && observed == candidate) cardSnapshot = observed;
                else { candidate = observed; await Task.Delay(100); }
            }
        }
        catch (Exception ex) { Audit.Write("selfservice_card_monitor_failed", ex.Message); }
        finally { cardSignInFinalizing = false; }
        if (IsDisposed || !SelfService || SelfServiceWindowsSession) { logon.Dispose(); return; }
        if (cardSnapshot == null)
        {
            logon.Dispose();
            signInFailedAtMilliseconds = Environment.TickCount64;
            notice = "Nie można monitorować karty w czytniku. Sesja została zakończona; sprawdź czytnik w Konfiguracji i zaloguj się ponownie.";
            Audit.Write("selfservice_card_unmonitored");
            RefreshChrome();
            return;
        }
        if (expectedCardId != null && identifyCard()?.Id != expectedCardId)
        {
            logon.Dispose();
            notice = "Karta została zmieniona podczas logowania. Spróbuj ponownie swoją kartą.";
            Audit.Write("selfservice_card_changed_during_signin");
            RefreshChrome();
            return;
        }
        helpBrowser?.Close();
        cardLogon = logon;
        cardAwaySinceMilliseconds = null;
        signedInCardSnapshot = cardKey = cardSnapshot;
        Audit.Write("selfservice_signed_in_with_card", logon.DisplayName);
        Stats.Record(Stats.SignIn, "Karta i PIN", person: logon.DisplayName);
        Audit.Write("selfservice_card_watched", reader.ActiveReader);
        notice = null;
        RefreshChrome();
        bool ready;
        try { ready = await Task.Run(logon.EnsureProfile); }
        catch (Exception ex) { ready = false; Audit.Write("profile_failed", ex.Message); }
        if (cardLogon != logon) return;
        bool sameCard;
        try
        {
            sameCard = reader.Snapshot(config.ReaderName) == signedInCardSnapshot &&
                (expectedCardId == null || identifyCard()?.Id == expectedCardId);
        }
        catch (Exception ex) { Audit.Write("selfservice_card_monitor_failed", ex.Message); sameCard = false; }
        if (!sameCard)
        {
            EndCardSession("selfservice_card_changed_during_profile");
            RefreshChrome();
            return;
        }
        // Nothing opens by itself: the person chooses from Szybka pomoc.
        if (!ready) notice = "Windows nie utworzył profilu dla " + logon.DisplayName + ". Strony otworzą się bez logowania Windows.";
        // Most people open a page: its window and browser start now, hidden, so the page they choose opens at once.
        // (Only a card reset or a PIN change needs no page; the window then simply goes with the card session.)
        else reusableStarting = StartReusablePageAsync(logon);
        RefreshChrome();
    }

    /// <summary>The card sign-in waits (no listening for a card) while the Kiosk itself talks to an unknown card.</summary>
    private bool registeringCard;

    /// <summary>
    /// Windows does not know the tapped card (new, or reset): the card gets a PIN if it has none, then the person
    /// chooses whether to register it; yes opens "Skonfiguruj MFA" in the middle frame to add it as a security key.
    /// </summary>
    private async Task RegisterUnknownCardAsync()
    {
        registeringCard = true;
        verifying = true;
        try
        {
            var readerName = PresentReader();
            if (readerName == null)
            {
                // Windows read it with a tap: the card may already be off the reader.
                notice = "Ta karta nie jest jeszcze zarejestrowana. Przyłóż ją ponownie, aby ją skonfigurować.";
                RefreshChrome();
                var until = Environment.TickCount64 + 20_000;
                while ((readerName = PresentReader()) == null && Environment.TickCount64 < until) await Task.Delay(200);
                notice = null;
            }
            if (readerName == null) { ShowHelpResult("Karta nie wróciła do czytnika. Przyłóż ją, aby spróbować ponownie."); return; }
            var pin = await EnsureCardPinAsync(readerName, config.HelpCloseSeconds);
            var register = await desktop.AskAsync("Zarejestrować kartę?",
                (pin != null ? "PIN karty jest ustawiony. " : "") + "Ta karta nie jest jeszcze przypisana do żadnego konta. Zarejestrować ją teraz?\n\n" +
                "Otworzy się „Skonfiguruj MFA”: zaloguj się swoim kontem i dodaj klucz zabezpieczeń – tę kartę.", config.HelpCloseSeconds);
            Audit.Write(register ? "card_register_started" : "card_register_declined");
            if (!register) { ShowHelpResult("Karta nie została zarejestrowana. Logowanie nią zadziała po dodaniu jej w „Skonfiguruj MFA”."); return; }
            var mfa = config.HelpActions.FirstOrDefault(h => h.Action.Contains("mfa", StringComparison.OrdinalIgnoreCase) ||
                h.Action.Contains("security-info", StringComparison.OrdinalIgnoreCase) || h.Action.Contains("mysecurityinfo", StringComparison.OrdinalIgnoreCase));
            ShowHelpPage(mfa?.Name ?? "Skonfiguruj MFA", mfa?.Action ?? "https://aka.ms/mysecurityinfo");
        }
        catch (Exception ex) { Audit.Write("card_register_failed", ex.Message); ShowHelpResult("Nie udało się przygotować karty: " + ex.Message); }
        finally { registeringCard = false; verifying = false; cardKey = reader.Snapshot(config.ReaderName); RefreshChrome(); }
    }

    private void SelfServiceTick()
    {
        if (verifying) return;
        if (cardSessionClosing != null)
        {
            if (!cardSessionClosing.IsCompleted) return;
            cardSessionClosing = null; notice = null; RefreshChrome();
        }
        if (SelfServiceWindowsSession)
        {
            if (!config.TestMode) ShellTick();
            return;
        }
        var key = ReadSelfServiceCardSnapshot();
        bool cardChanged = key != cardKey;
        if (cardChanged) nextPeriodicChromeRefreshMilliseconds = 0;
        if (key != null && cardChanged) cardPlacedAtMilliseconds = Environment.TickCount64;
        cardKey = key;
        if (config.CardAuthentication == CardAuthentication.Local)
        {
            if (key == null) { localCardAttempted = false; localAttemptedCardId = null; }
            else
            {
                // CTAP can reset the same card and change the reader event counter. Retry only
                // when a different card is actually present, never merely after a failed PIN.
                if (cardChanged && localCardAttempted && cardLogon == null &&
                    identifyCard() is { } observed && observed.Id != localAttemptedCardId)
                    localCardAttempted = false;
                if (cardLogon == null && !localCardAttempted)
                {
                    localCardAttempted = true;
                    _ = StartLocalSelfServiceAsync();
                }
            }
        }
        else PrepareCardSignIn();
        ObserveSignedInCard(key);
        if (cardLogon == null && notice != null && noticeUntilMilliseconds is { } noticeDeadline && Environment.TickCount64 >= noticeDeadline)
        { noticeUntilMilliseconds = null; notice = null; RefreshChrome(); }
        if (cardPage is { Started: true } page && !page.RefreshHostedWindow()) CloseCardPage();   // "Zamknij" or its countdown closed it.
        // The card session's own window: "Zamknij" or its countdown parked it (it stays for the next page).
        else if (cardPage != null && cardPage == reusablePage && HelpBrowserForm.GetProp(cardPage.HostedWindow, HelpBrowserForm.ParkedProperty) != IntPtr.Zero)
            CloseCardPage();
        if (reusablePage is { Started: true } waiting && cardPage != waiting && !waiting.RefreshHostedWindow())
        {
            Audit.Write("card_page_reusable_lost");   // Closed or crashed: pages start on their own again.
            DropReusablePage();
        }
    }

    private string? ReadSelfServiceCardSnapshot()
    {
        try { return reader.Snapshot(config.ReaderName); }
        catch (Exception ex) { Audit.Write("selfservice_card_monitor_failed", ex.Message); return null; }
    }

    private void ObserveSignedInCard(string? observed)
    {
        if (cardLogon == null) return;
        if (cardAwaySinceMilliseconds is { } awaySince &&
            Environment.TickCount64 - awaySince >= CardReturnSeconds * 1000L)
        {
            EndCardSession("selfservice_card_removed");
            return;
        }
        switch (CardReader.CompareSignedInCard(signedInCardSnapshot, observed))
        {
            case CardReader.SignedInCardState.Present:
                // A temporary reader failure could hide an exchange. Never resume this sign-in after a missing observation.
                return;
            case CardReader.SignedInCardState.Changed:
                EndCardSession("selfservice_card_changed");
                notice = "Karta została zmieniona. Zaloguj się ponownie.";
                RefreshChrome();
                return;
            case CardReader.SignedInCardState.Missing:
                if (cardAwaySinceMilliseconds == null)
                {
                    cardAwaySinceMilliseconds = Environment.TickCount64;
                    // A different person must not use the previous person's page during the grace period.
                    helpBrowser?.Close();
                    CloseCardPage();
                    DropReusablePage();
                    notice = "Karta zdjęta. Strony zostały zamknięte; wylogowanie nastąpi za " + CardReturnSeconds + " s.";
                    Audit.Write("selfservice_card_away");
                    RefreshChrome();
                }
                return;
        }
    }

    /// <summary>The card is gone: the person is signed out here, their pages and programs close.</summary>
    private void EndCardSession(string? reason)
    {
        if (cardLogon == null) return;
        cardAwaySinceMilliseconds = null;
        signedInCardSnapshot = null;
        helpBrowser?.Close();
        CloseCardPage();
        DropReusablePage();
        cardSessionClosing = cardLogon.CloseAsync();
        cardLogon = null;
        notice = "Zamykam sesję…";
        if (reason != null) Audit.Write(reason);
        RefreshChrome();
    }

    /// <summary>The page open as the signed-in person, over the Kiosk's screen; null = the start screen.</summary>
    private ApplicationView? cardPage;
    private readonly CardPageCommandQueue cardPageCommands = new();
    private int cardPageRequestVersion;

    /// <summary>A Szybka pomoc page as the signed-in person (their account, SSO): a clean page inside the Kiosk.</summary>
    private bool OpenPageAsCardPerson(string title, string url, string side = "")
    {
        if (cardLogon == null) return false;
        ObserveSignedInCard(ReadSelfServiceCardSnapshot());
        if (cardLogon == null || cardAwaySinceMilliseconds != null) return true;
        CloseCardPage();
        _ = OpenCardPageAsync(cardLogon, title, url, side, cardPageRequestVersion);
        return true;
    }

    private async Task OpenCardPageAsync(UserLogon logon, string title, string url, string side, int requestVersion)
    {
        bool Latest() => !IsDisposed && cardPageRequestVersion == requestVersion && cardLogon == logon && cardAwaySinceMilliseconds == null;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        helpBrowser?.Close();
        // The card session's window, still starting (signed in a moment ago): it is waited for rather than started twice.
        if (reusableStarting is { IsCompleted: false } starting) { await starting; if (!Latest()) return; }
        if (!Latest()) return;
        if (reusablePage is { Started: true } reusable && reusableHost > 0 && reusable.RefreshHostedWindow())
        {
            ShowSideFor(side);
            var domains = HelpBrowserForm.SignInDomains.Concat(HelpPageDomains()).Append(host).ToArray();
            int seconds = config.HelpCloseSeconds, hostPid = reusableHost;
            bool onlyListed = config.BrowserOnlyBookmarks;
            bool HostCurrent() => Latest() && reusablePage == reusable && reusableHost == hostPid;
            var opened = await cardPageCommands.OpenAsync(
                () => CardPageHost.Send(hostPid, "open", title, url, seconds, domains, onlyListed),
                () => CardPageHost.Send(hostPid, "park"), HostCurrent, () =>
                {
                    cardPage = reusable;
                    if (visibleView != desktop) ShowView(desktop);
                    desktop.ShowPage(reusable);
                    desktop.SetPageTitle(title);
                    reusable.BringToUser();
                    desktop.RefreshSide();
                    Audit.Write("selfservice_page_as_person", "reused");
                });
            if (!Latest()) return;
            if (opened) return;
            // It did not answer: this page (and the next ones) in a program of its own, as before.
            DropReusablePage();
        }
        if (!Latest()) return;
        // The second element first: the person's page is laid over the left part only.
        ShowSideFor(side);
        var page = new ApplicationView { AlwaysOverlay = true };
        cardPage = page;
        // In the start screen's middle frame, like the guest page; the page's window is kept exactly over it.
        if (visibleView != desktop) ShowView(desktop);
        desktop.ShowPage(page);
        desktop.SetPageTitle(title);
        var area = desktop.PageScreenArea;
        var request = new CardPageHost.Request(title, url, config.HelpCloseSeconds, config.AccentColor,
            HelpBrowserForm.SignInDomains.Concat(HelpPageDomains()).Append(host).ToArray(),
            logon.ProfileDirectory is { } profile ? Path.Combine(profile, @"AppData\Local\Kiosk\Pages") : null,
            [area.X, area.Y, area.Width, area.Height], config.BrowserOnlyBookmarks, desktop.SideWidth(area.Width));
        var entry = new ApplicationEntry { Name = title, Path = Environment.ProcessPath!, Arguments = CardPageHost.Argument + " " + CardPageHost.Encode(request) };
        try
        {
            await page.StartAsync(entry, logon);
            if (!Latest()) return;
            if (cardPage == page) { page.BringToUser(); desktop.RefreshSide(); }   // Now its window can leave room for the element beside it.
            Audit.Write("selfservice_page_as_person");
        }
        catch (Exception ex)
        {
            if (!Latest()) return;
            Audit.Write("selfservice_page_as_person_error", ex.Message);
            if (cardPage == page) CloseCardPage();
            notice = "Nie udało się otworzyć strony na Twoim koncie: " + ex.Message;
            RefreshChrome();
        }
    }

    /// <summary>Back to the start screen: the page closes (the card session's own window only leaves the screen).</summary>
    private void CloseCardPage()
    {
        cardPageRequestVersion++; // A pending open must not restore a page after Close or sign-out.
        if (cardPage is not { } page) return;
        cardPage = null;
        if (desktop.Page == page) { desktop.ShowPage(null); desktop.SetPageTitle(null); }
        if (page != reusablePage) { page.Dispose(); return; }
        // Closed from the Kiosk (the start screen, signing out, another page): the page itself ends too (a film stops).
        if (HelpBrowserForm.GetProp(page.HostedWindow, HelpBrowserForm.ParkedProperty) == IntPtr.Zero)
        {
            var hostPid = reusableHost;
            _ = cardPageCommands.ParkAsync(() => CardPageHost.Send(hostPid, "park"));
        }
    }

    /// <summary>The card session's page window (<see cref="CardPageHost"/>): started once after the sign-in, reused by every page.</summary>
    private ApplicationView? reusablePage;
    private Task? reusableStarting;
    /// <summary>Its process, which alone answers on its pipe.</summary>
    private int reusableHost;

    private async Task StartReusablePageAsync(UserLogon logon)
    {
        var page = new ApplicationView { AlwaysOverlay = true };
        reusablePage = page;
        var area = desktop.PageScreenArea;
        string kioskAccount;
        using (var self = System.Security.Principal.WindowsIdentity.GetCurrent()) kioskAccount = self.User!.Value;
        // Every configured page's site at once: the window's browser keeps its sign-in settings for all its pages.
        var domains = HelpBrowserForm.SignInDomains.Concat(HelpPageDomains())
            .Concat(config.HelpActions.Select(h => Uri.TryCreate(h.Action, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.Host : ""))
            .Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var request = new CardPageHost.Request("Kiosk", "", config.HelpCloseSeconds, config.AccentColor, domains,
            logon.ProfileDirectory is { } profile ? Path.Combine(profile, @"AppData\Local\Kiosk\Pages") : null,
            [area.X, area.Y, area.Width, area.Height], config.BrowserOnlyBookmarks, Kiosk: Environment.ProcessId, KioskSid: kioskAccount);
        var entry = new ApplicationEntry { Name = "Kiosk", Path = Environment.ProcessPath!, Arguments = CardPageHost.Argument + " " + CardPageHost.Encode(request) };
        try
        {
            await page.StartAsync(entry, logon);
            if (reusablePage != page || page.HostedWindow == IntPtr.Zero) return;
            AppNative.GetWindowThreadProcessId(page.HostedWindow, out var pid);
            reusableHost = (int)pid;
            Audit.Write("card_page_reusable_ready");
        }
        catch (Exception ex)
        {
            // Pages then start on their own, as before.
            Audit.Write("card_page_reusable_failed", ex.Message);
            if (reusablePage == page) DropReusablePage();
        }
    }

    private void DropReusablePage()
    {
        if (reusablePage is not { } page) return;
        reusablePage = null;
        reusableHost = 0;
        if (cardPage == page) CloseCardPage();
        if (desktop.Page == page) { desktop.ShowPage(null); desktop.SetPageTitle(null); }
        page.Dispose();
    }

    private void SelfServiceStatus()
    {
        if (SelfServiceWindowsSession)
        {
            var signingOut = shellSignOut.Reason != null;
            var name = signingOut ? "Wylogowywanie" : current?.Name ?? Environment.UserName;
            desktop.ClearCardLogin();
            desktop.UpdateStatus(name, notice ?? (signingOut ? "Trwa kończenie sesji Windows." :
                config.SelfServiceSignedInMessage),
                person: signingOut ? null : name);
        }
        else if (cardLogon != null)
        {
            var name = cardLogon.FriendlyName.Length > 0 ? cardLogon.FriendlyName : cardLogon.DisplayName;
            desktop.ClearCardLogin();
            desktop.UpdateStatus(name, notice ?? config.SelfServiceSignedInMessage, person: name);
        }
        else if (signInScreen != null && !(notice != null && noticeUntilMilliseconds is { } deadline && Environment.TickCount64 < deadline))
            desktop.SetCardLogin(DesktopView.CardLoginStage.Provider, "Przyłóż klucz lub włóż go do USB, potem dotknij klucza.");
        else
        {
            var stage = cardSessionClosing is { IsCompleted: false } ? DesktopView.CardLoginStage.Closing :
                cardSignInFinalizing ? DesktopView.CardLoginStage.Verifying :
                notice != null ? DesktopView.CardLoginStage.Error :
                signInStarting != null ? DesktopView.CardLoginStage.Preparing : DesktopView.CardLoginStage.Waiting;
            desktop.SetCardLogin(stage, notice ??
                (cardSignInFinalizing ? "Trwa przygotowanie Twojej sesji." : "Przyłóż klucz lub włóż go do USB, potem dotknij klucza."));
        }
        // The portal's bar on top: the person and "Zakończ sesję", or a notice in place of the page's line.
        // The name in large letters (without the domain when there is no friendly name), the account under it.
        var person = SelfServiceWindowsSession ? shellSignOut.Reason == null ? current?.Name : null :
            cardLogon == null ? null : cardLogon.FriendlyName.Length > 0 ? cardLogon.FriendlyName : cardLogon.DisplayName.Split('\\')[^1];
        desktop.SetPortalState(person, notice,
            warning: cardAwaySinceMilliseconds != null || notice?.StartsWith("Nie ", StringComparison.Ordinal) == true,
            login: SelfServiceWindowsSession ? current?.Identity.Upn : cardLogon?.DisplayName);
    }

    /// <summary>Ends only the person's card session. The workstation's Windows session stays signed in.</summary>
    private void PortalAccount()
    {
        if (AnnouncementsOnly) return;
        if (cardLogon != null) EndCardSession("selfservice_signed_out");
        RefreshChrome();
    }
}

/// <summary>Orders commands to one reusable card page; a stale open is parked before the next request runs.</summary>
internal sealed class CardPageCommandQueue
{
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<bool> OpenAsync(Func<bool> open, Func<bool> park, Func<bool> current, Action onOpened)
    {
        await gate.WaitAsync();
        try
        {
            if (!current()) return false;
            var opened = await Task.Run(open);
            if (current())
            {
                if (opened) onOpened(); // Commit on the UI thread before another close can interleave.
                return opened;
            }
            if (opened) await Task.Run(park);
            return false;
        }
        catch (Exception ex) { Audit.Write("card_page_command_failed", ex.Message); return false; }
        finally { gate.Release(); }
    }

    internal async Task ParkAsync(Func<bool> park)
    {
        await gate.WaitAsync();
        try { await Task.Run(park); }
        catch (Exception ex) { Audit.Write("card_page_park_failed", ex.Message); }
        finally { gate.Release(); }
    }
}
