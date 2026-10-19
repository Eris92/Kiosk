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

    /// <summary>The person signed in to Windows with the card and its PIN; null = nobody (pages open as a guest).</summary>
    private UserLogon? cardLogon;
    /// <summary>The card sign-in screen inside the Kiosk, listening for a tap; null while it starts or after it ended.</summary>
    private FidoSignIn? signInScreen;
    private Task<FidoSignIn?>? signInStarting;
    private DateTime signInFailedAt;
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
        if (cardLogon != null || signInScreen != null || registeringCard) return;
        if (signInStarting != null)
        {
            if (!signInStarting.IsCompleted) return;
            var ready = signInStarting.Status == TaskStatus.RanToCompletion ? signInStarting.Result : null;
            signInStarting = null;
            if (ready == null) { signInFailedAt = DateTime.UtcNow; notice = "Logowanie kartą nie wystartowało. Ponawiam…"; RefreshChrome(); return; }
            signInScreen = ready;
            notice = null;
            _ = WaitForCardSignInAsync(ready);
            RefreshChrome();
            return;
        }
        if (DateTime.UtcNow - signInFailedAt < TimeSpan.FromSeconds(10)) return;
        signInStarting = FidoSignIn.PrepareAsync(desktop.SignInPlace, desktop.DeviceDpi);
    }

    /// <summary>The person taps the card and types the PIN in the sign-in screen; then their Windows session is ready.</summary>
    private async Task WaitForCardSignInAsync(FidoSignIn screen)
    {
        var outcome = await screen.WaitAsync();
        if (signInScreen == screen) signInScreen = null;
        if (outcome.SignIn is not { } signIn)
        {
            if (outcome.Message.StartsWith(FidoSignIn.UnknownKey, StringComparison.Ordinal))
            {
                // A new or reset card: no account knows it. PIN, then registration, instead of the bare error.
                Audit.Write("selfservice_card_unknown", outcome.Message[FidoSignIn.UnknownKey.Length..]);
                _ = RegisterUnknownCardAsync();
                return;
            }
            // A fresh sign-in screen follows on the next tick.
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
        Audit.Write("selfservice_card_watched", signInReaders.Length == 0 ? "(brak czytnika z kartą)" : string.Join(", ", signInReaders));
        notice = null;
        RefreshChrome();
        bool ready;
        try { ready = await Task.Run(logon.EnsureProfile); }
        catch (Exception ex) { ready = false; Audit.Write("profile_failed", ex.Message); }
        if (cardLogon != logon) return;
        // Nothing opens by itself: the person chooses from Szybka pomoc.
        if (!ready) notice = "Windows nie utworzył profilu dla " + logon.DisplayName + ". Strony otworzą się bez logowania Windows.";
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
        var key = reader.Snapshot(config.ReaderName);
        cardKey = key;
        PrepareCardSignIn();
        if (cardLogon == null && notice != null && noticeUntil != default && DateTime.UtcNow >= noticeUntil) { noticeUntil = default; notice = null; RefreshChrome(); }
        if (cardPage is { Started: true } page && !page.Refresh()) CloseCardPage();   // "Zamknij" or its countdown closed it.
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
        cardLogon.Dispose(); // Signs the person out of Windows here: their Edge closes with it.
        cardLogon = null;
        notice = null;
        if (reason != null) Audit.Write(reason);
        RefreshChrome();
    }

    /// <summary>The page open as the signed-in person, over the Kiosk's screen; null = the start screen.</summary>
    private ApplicationView? cardPage;

    /// <summary>A Szybka pomoc page as the signed-in person (their account, SSO): a clean page inside the Kiosk.</summary>
    private bool OpenPageAsCardPerson(string title, string url)
    {
        if (cardLogon == null) return false;
        _ = OpenCardPageAsync(cardLogon, title, url);
        return true;
    }

    private async Task OpenCardPageAsync(UserLogon logon, string title, string url)
    {
        CloseCardPage();
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        helpBrowser?.Close();
        var page = new ApplicationView { AlwaysOverlay = true };
        cardPage = page;
        // In the start screen's middle frame, like the guest page; the page's window is kept exactly over it.
        if (visibleView != desktop) ShowView(desktop);
        desktop.ShowPage(page);
        var area = desktop.PageScreenArea;
        var request = new CardPageHost.Request(title, url, config.HelpCloseSeconds, config.AccentColor,
            HelpBrowserForm.SignInDomains.Concat(HelpPageDomains()).Append(host).ToArray(),
            logon.ProfileDirectory is { } profile ? Path.Combine(profile, @"AppData\Local\Kiosk\Pages") : null,
            [area.X, area.Y, area.Width, area.Height], config.BrowserOnlyBookmarks);
        var entry = new ApplicationEntry { Name = title, Path = Environment.ProcessPath!, Arguments = CardPageHost.Argument + " " + CardPageHost.Encode(request) };
        try
        {
            await page.StartAsync(entry, logon);
            if (cardPage == page) page.BringToUser();
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

    /// <summary>Back to the start screen: the page closes.</summary>
    private void CloseCardPage()
    {
        if (cardPage is not { } page) return;
        cardPage = null;
        if (desktop.Page == page) desktop.ShowPage(null);
        page.Dispose();
    }

    private void SelfServiceStatus()
    {
        if (cardLogon != null)
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
    }
}
