namespace Kiosk.Client;

/// <summary>
/// Punkt samoobsługowy (SessionMode.SelfService): only the start screen with Szybka pomoc and Komunikaty, no Menu,
/// no taskbar and no sessions. Its only sign-in is the card: Windows' card + PIN sign-in (the Edge-FIDO2 PIN screen,
/// <see cref="FidoSignIn"/>) sits in the start screen's card and waits for a tap; the PIN goes into Windows' own field
/// there. Signed in, Szybka pomoc pages open in the person's own Edge (their account, SSO) until the card is taken away.
/// Ctrl+Alt+K (Konfiguracja) and Ctrl+Alt+U (update) work as in the other modes.
/// </summary>
internal sealed partial class KioskForm
{
    private bool SelfService => config.SessionMode == SessionMode.SelfService;
    private bool SelfServiceWindowsSession => SelfService && config.SelfServiceAuthentication == SelfServiceAuthentication.WindowsSession;

    /// <summary>The person signed in to Windows with the card and its PIN; null = nobody (pages open as a guest).</summary>
    private UserLogon? cardLogon;
    /// <summary>The card sign-in screen inside the Kiosk, listening for a tap; null while it starts or after it ended.</summary>
    private FidoSignIn? signInScreen;
    private Task<FidoSignIn?>? signInStarting;
    private DateTime signInFailedAt;
    /// <summary>Card sign-in screens that did not start in a row: each waits longer (10 s, 20 s… up to 5 minutes).</summary>
    private int signInStartFailures;
    /// <summary>An unsupported Windows build cannot start this adapter; retrying it does not change the result.</summary>
    private bool signInUnsupported;
    /// <summary>A Szybka pomoc result (card reset, PIN change…) is shown in the card instead of the sign-in until then.</summary>
    private DateTime noticeUntil;

    /// <summary>When the card left the reader; the sign-in waits <see cref="CardReturnSeconds"/> for it to come back.</summary>
    private DateTime? cardAwaySince;
    /// <summary>
    /// The readers that held a card when the person signed in: the card on the NFC reader, or the USB key (its own
    /// reader). The configured reader alone is not enough: with a USB key it holds nothing, and the person was signed
    /// out right after signing in. Empty = the key has no reader (FIDO over HID only): nothing to watch.
    /// </summary>
    private string[] signInReaders = [];
    /// <summary>Konfiguracja → Szybka pomoc: "Wyloguj po zdjęciu karty".</summary>
    private int CardReturnSeconds => config.CardRemovedSeconds;

    /// <summary>Nobody signed in: the card sign-in screen is (re)started in the start screen and waits for a tap.</summary>
    private void PrepareCardSignIn()
    {
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
        if (cardLogon != null || signInScreen != null || registeringCard) return;
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
                if (reason.Contains("account adapter has not been verified", StringComparison.OrdinalIgnoreCase) ||
                    reason.Contains("Native CTAP assertion entry is unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    signInUnsupported = true;
                    notice = "Logowanie kartą jest niedostępne na tej wersji Windows. Strony pomocy możesz otworzyć bez logowania. Skontaktuj się z administratorem.";
                    Audit.Write("fido_unsupported_windows", reason);
                }
                else
                {
                    signInFailedAt = DateTime.UtcNow;
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
        if (DateTime.UtcNow - signInFailedAt < pause) return;
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
            // A fresh sign-in screen follows after the usual pause (not at once: a screen per tick piled up).
            signInFailedAt = DateTime.UtcNow;
            Audit.Write("selfservice_card_signin_failed", outcome.Message);
            notice = "Nie udało się zalogować kartą: " + outcome.Message;
            RefreshChrome();
            return;
        }
        var logon = UserLogon.FromCard(signIn);
        cardLogon = logon;
        cardAwaySince = null;
        cardKey = reader.Snapshot(config.ReaderName);
        signInReaders = reader.ReadersWithCard() ?? [];
        Audit.Write("selfservice_signed_in_with_card", logon.DisplayName);
        Stats.Record(Stats.SignIn, "Karta i PIN", person: logon.DisplayName);
        Audit.Write("selfservice_card_watched", signInReaders.Length == 0 ? "(brak czytnika z kartą)" : string.Join(", ", signInReaders));
        notice = null;
        RefreshChrome();
        bool ready;
        try { ready = await Task.Run(logon.EnsureProfile); }
        catch (Exception ex) { ready = false; Audit.Write("profile_failed", ex.Message); }
        if (cardLogon != logon) return;
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
                var until = DateTime.UtcNow.AddSeconds(20);
                while ((readerName = PresentReader()) == null && DateTime.UtcNow < until) await Task.Delay(200);
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
        if (SelfServiceWindowsSession)
        {
            if (!config.TestMode) ShellTick();
            return;
        }
        var key = reader.Snapshot(config.ReaderName);
        if (key != null && key != cardKey) cardPlacedAt = DateTime.UtcNow;
        cardKey = key;
        PrepareCardSignIn();
        if (cardLogon == null && notice != null && noticeUntil != default && DateTime.UtcNow >= noticeUntil) { noticeUntil = default; notice = null; RefreshChrome(); }
        if (cardPage is { Started: true } page && !page.Refresh()) CloseCardPage();   // "Zamknij" or its countdown closed it.
        // The card session's own window: "Zamknij" or its countdown parked it (it stays for the next page).
        else if (cardPage != null && cardPage == reusablePage && HelpBrowserForm.GetProp(cardPage.HostedWindow, HelpBrowserForm.ParkedProperty) != IntPtr.Zero)
            CloseCardPage();
        if (reusablePage is { Started: true } waiting && cardPage != waiting && !waiting.Refresh())
        {
            Audit.Write("card_page_reusable_lost");   // Closed or crashed: pages start on their own again.
            DropReusablePage();
        }
        if (cardLogon == null) return;
        if (signInReaders.Length == 0)
        {
            // Windows resets the card while signing in; the first reader seen holding it afterwards is the one watched.
            signInReaders = reader.ReadersWithCard() ?? [];
            if (signInReaders.Length > 0) Audit.Write("selfservice_card_watched", string.Join(", ", signInReaders));
            return;
        }
        var holding = reader.ReadersWithCard();
        if (holding == null) return;   // The smart card service did not answer: no decision this tick.
        if (holding.Intersect(signInReaders, StringComparer.Ordinal).Any())
        {
            if (cardAwaySince != null) { cardAwaySince = null; notice = null; Audit.Write("selfservice_card_back"); RefreshChrome(); }
            return;
        }
        if (cardAwaySince == null)
        {
            cardAwaySince = DateTime.UtcNow;
            notice = "Karta zdjęta. Jeśli nie przyłożysz jej ponownie w ciągu " + CardReturnSeconds + " s, nastąpi wylogowanie.";
            Audit.Write("selfservice_card_away");
            RefreshChrome();
        }
        else if (DateTime.UtcNow - cardAwaySince.Value >= TimeSpan.FromSeconds(CardReturnSeconds)) EndCardSession("selfservice_card_removed");
    }

    /// <summary>The card is gone: the person is signed out here, their pages and programs close.</summary>
    private void EndCardSession(string? reason)
    {
        if (cardLogon == null) return;
        cardAwaySince = null;
        signInReaders = [];
        helpBrowser?.Close();
        CloseCardPage();
        DropReusablePage();
        cardLogon.Dispose(); // Signs the person out of Windows here: their Edge closes with it.
        cardLogon = null;
        notice = null;
        if (reason != null) Audit.Write(reason);
        RefreshChrome();
    }

    /// <summary>The page open as the signed-in person, over the Kiosk's screen; null = the start screen.</summary>
    private ApplicationView? cardPage;

    /// <summary>A Szybka pomoc page as the signed-in person (their account, SSO): a clean page inside the Kiosk.</summary>
    private bool OpenPageAsCardPerson(string title, string url, string side = "")
    {
        if (cardLogon == null) return false;
        _ = OpenCardPageAsync(cardLogon, title, url, side);
        return true;
    }

    private async Task OpenCardPageAsync(UserLogon logon, string title, string url, string side = "")
    {
        CloseCardPage();
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        helpBrowser?.Close();
        // The card session's window, still starting (signed in a moment ago): it is waited for rather than started twice.
        if (reusableStarting is { IsCompleted: false } starting) { await starting; if (cardLogon != logon) return; }
        if (reusablePage is { Started: true } reusable && reusableHost > 0 && reusable.Refresh())
        {
            ShowSideFor(side);
            var domains = HelpBrowserForm.SignInDomains.Concat(HelpPageDomains()).Append(host).ToArray();
            int seconds = config.HelpCloseSeconds, hostPid = reusableHost;
            bool onlyListed = config.BrowserOnlyBookmarks;
            if (await Task.Run(() => CardPageHost.Send(hostPid, "open", title, url, seconds, domains, onlyListed)) && reusablePage == reusable && cardLogon == logon)
            {
                cardPage = reusable;
                if (visibleView != desktop) ShowView(desktop);
                desktop.ShowPage(reusable);
                desktop.SetPageTitle(title);
                reusable.BringToUser();
                desktop.RefreshSide();
                Audit.Write("selfservice_page_as_person", "reused");
                return;
            }
            // It did not answer: this page (and the next ones) in a program of its own, as before.
            DropReusablePage();
        }
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
            if (cardPage == page) { page.BringToUser(); desktop.RefreshSide(); }   // Now its window can leave room for the element beside it.
            Audit.Write("selfservice_page_as_person");
        }
        catch (Exception ex)
        {
            Audit.Write("selfservice_page_as_person_error", ex.Message);
            if (cardPage == page) CloseCardPage();
            notice = "Nie udało się otworzyć strony na Twoim koncie: " + ex.Message;
            RefreshChrome();
        }
    }

    /// <summary>Back to the start screen: the page closes (the card session's own window only leaves the screen).</summary>
    private void CloseCardPage()
    {
        if (cardPage is not { } page) return;
        cardPage = null;
        if (desktop.Page == page) { desktop.ShowPage(null); desktop.SetPageTitle(null); }
        if (page != reusablePage) { page.Dispose(); return; }
        // Closed from the Kiosk (the start screen, signing out, another page): the page itself ends too (a film stops).
        if (HelpBrowserForm.GetProp(page.HostedWindow, HelpBrowserForm.ParkedProperty) == IntPtr.Zero)
        {
            var hostPid = reusableHost;
            _ = Task.Run(() => CardPageHost.Send(hostPid, "park"));
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
            desktop.ShowSignIn(false);
            desktop.UpdateStatus(name, notice ?? (signingOut ? "Trwa kończenie sesji Windows." :
                "Wybierz zadanie z Szybkiej pomocy. Strony użyją Twojego konta Windows. Po pracy zakończ sesję."),
                person: signingOut ? null : name);
        }
        else if (cardLogon != null)
        {
            var name = cardLogon.FriendlyName.Length > 0 ? cardLogon.FriendlyName : cardLogon.DisplayName;
            desktop.ShowSignIn(false);
            desktop.UpdateStatus(name, notice ?? "Wybierz zadanie z Szybkiej pomocy – strony otworzą się na Twoim koncie. " +
                "Zabierz kartę z czytnika, aby się wylogować.", person: name);
        }
        else if (signInScreen != null && !(notice != null && DateTime.UtcNow < noticeUntil)) desktop.ShowSignIn(true);
        else
        {
            desktop.ShowSignIn(false);
            desktop.UpdateStatus("Logowanie kartą", notice ?? "Przygotowuję logowanie kartą…");
        }
        // The portal's bar on top: the person and "Zakończ sesję", or a notice in place of the page's line.
        // The name in large letters (without the domain when there is no friendly name), the account under it.
        var person = SelfServiceWindowsSession ? shellSignOut.Reason == null ? current?.Name : null :
            cardLogon == null ? null : cardLogon.FriendlyName.Length > 0 ? cardLogon.FriendlyName : cardLogon.DisplayName.Split('\\')[^1];
        desktop.SetPortalState(person, notice,
            warning: cardAwaySince != null || notice?.StartsWith("Nie ", StringComparison.Ordinal) == true,
            login: SelfServiceWindowsSession ? current?.Identity.Upn : cardLogon?.DisplayName);
    }

    /// <summary>"Zaloguj kartą" shows the card sign-in (again: back to the tiles); "Zakończ sesję" signs the person out.</summary>
    private void PortalAccount()
    {
        if (SelfServiceWindowsSession)
        {
            if (config.TestMode) { notice = "Tryb testowy: wylogowanie Windows jest wyłączone."; RefreshChrome(); }
            else SignOutWindows("selfservice_signed_out");
            return;
        }
        // The card sign-in waits on Strona główna all the time (as in build-176); the button only signs out.
        if (cardLogon != null) EndCardSession("selfservice_signed_out");
        RefreshChrome();
    }
}
