namespace Kiosk.Client;

/// <summary>
/// Punkt samoobsługowy (SessionMode.SelfService): only the start screen with Szybka pomoc and Komunikaty, no Menu,
/// no taskbar and no sessions. A card put on the reader starts one browser sign-in (FIDO2 on the web) that every
/// Szybka pomoc page shares until the card is taken away; then the pages close and nothing is left behind.
/// A card without a PIN gets one first, and the page that links the card to the person's account opens.
/// Ctrl+Alt+K (Konfiguracja) and Ctrl+Alt+U (update) work as in the other modes.
/// </summary>
internal sealed partial class KioskForm
{
    private bool SelfService => config.SessionMode == SessionMode.SelfService;
    /// <summary>The card's browser folder while it lies on the reader; null = no card (help pages are guest windows).</summary>
    private string? cardFolder;
    private static string CardFolders => Path.Combine(Path.GetTempPath(), "KioskCard");

    /// <summary>The person signed in to Windows with the card and its PIN: Szybka pomoc pages open in their own Edge
    /// (their account, SSO, a device Microsoft recognises). Null = not signed in, pages use the card's web browser.</summary>
    private UserLogon? cardLogon;

    /// <summary>The card of the current sign-in (its UID): the same card coming back continues it.</summary>
    private string? cardId;
    /// <summary>When the card left the reader; the sign-in waits <see cref="CardReturnSeconds"/> for it to come back.</summary>
    private DateTime? cardAwaySince;
    // Signing in with a security key on the web, Windows may ask to lift the card and tap it again, and resets it:
    // that must not end the sign-in it belongs to.
    private const int CardReturnSeconds = 20;

    /// <summary>The card on the reader: its certificate, or the UID of a FIDO2 card; null when it cannot be read.</summary>
    private string? PresentCardId()
    {
        try { return (identifyCard() ?? UncertifiedCard()).Id; }
        catch (Exception ex) { Audit.Write("card_identity_error", ex.Message); return null; }
    }

    /// <summary>Keeps the card's browser running between Szybka pomoc pages: a web sign-in is mostly a session cookie,
    /// which lives only as long as the browser does, so without it every page asked to sign in again.</summary>
    private CardBrowserKeeper? cardKeeper;
    /// <summary>The Windows security window (passkey / security key) the card was already woken for.</summary>
    private IntPtr credentialPrompt;

    private static bool SameCardInPlace(string a, string b) =>
        a[..a.IndexOf(':')] == b[..b.IndexOf(':')] && a[(a.LastIndexOf(':') + 1)..] == b[(b.LastIndexOf(':') + 1)..];

    /// <summary>
    /// Windows' security key window waits for a card to be put on the reader ("Dotknij czytnika kluczem"); a card
    /// that already lies there is not noticed. When that window opens, the card is switched off and on once, which
    /// Windows sees as a fresh tap, so the person only types the PIN.
    /// </summary>
    private void WakeCardForWindows()
    {
        var prompt = IntPtr.Zero;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("CredentialUIBroker"))
            using (process)
                try { if (process.MainWindowHandle != IntPtr.Zero) prompt = process.MainWindowHandle; }
                catch (InvalidOperationException) { }
        if (prompt == IntPtr.Zero || prompt == credentialPrompt) return;
        credentialPrompt = prompt;
        var readerName = reader.ActiveReader;
        Audit.Write("selfservice_card_wake");
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);   // Let the window start listening first.
            try { CtapCard.PowerCycle(readerName); }
            catch (Exception ex) { Audit.Write("selfservice_card_wake_error", ex.Message); }
        });
    }

    private void SelfServiceTick()
    {
        // While a PIN is asked or checked the card is reset on purpose; presence changes are evaluated afterwards.
        if (verifying || desktop.AskingPin) return;
        var key = reader.Snapshot(config.ReaderName);
        if (key == null && cardAwaySince is { } away && DateTime.UtcNow - away >= TimeSpan.FromSeconds(CardReturnSeconds))
        {
            cardAwaySince = null;
            EndCardSession("selfservice_card_removed");
            return;
        }
        if (cardFolder != null && key != null) WakeCardForWindows();
        if (key == cardKey) return;
        var previous = cardKey;
        cardKey = key;
        // Reset in place (Windows signing in with it, or the Kiosk waking it): the same reader and card, only the
        // insert counter moved. Nobody swaps cards within a quarter second, and reading it now would disturb Windows.
        if (cardFolder != null && cardAwaySince == null && previous != null && key != null && SameCardInPlace(previous, key)) return;
        if (key == null)
        {
            if (cardFolder == null) return;
            cardAwaySince = DateTime.UtcNow;
            notice = "Karta zdjęta. Jeśli Windows o to prosi, przyłóż ją ponownie – logowanie czeka " + CardReturnSeconds + " s.";
            Audit.Write("selfservice_card_away");
            RefreshChrome();
            return;
        }
        // Back on the reader (or reset by Windows during the sign-in): the same card simply continues.
        if (cardFolder != null && cardId != null && PresentCardId() == cardId)
        {
            if (cardAwaySince != null) { cardAwaySince = null; notice = null; Audit.Write("selfservice_card_back"); RefreshChrome(); }
            return;
        }
        cardAwaySince = null;
        _ = StartCardSessionAsync();
    }

    private async Task StartCardSessionAsync()
    {
        EndCardSession(null);
        var folder = Path.Combine(CardFolders, Guid.NewGuid().ToString("N"));
        cardFolder = folder;
        cardId = PresentCardId();
        cardKeeper = new CardBrowserKeeper(folder);
        notice = null;
        Audit.Write("selfservice_card");
        RefreshChrome();
        string? newPin = null;
        verifying = true;
        try { newPin = await EnsureCardPinAsync(reader.ActiveReader, config.HelpCloseSeconds); }
        catch (Exception ex) { notice = "Nie udało się sprawdzić karty: " + ex.Message; Audit.Write("selfservice_pin_error", ex.Message); }
        finally { verifying = false; cardKey = reader.Snapshot(config.ReaderName); }
        if (cardFolder != folder || cardKey == null) { RefreshChrome(); return; } // Taken away meanwhile.
        if (newPin != null && config.SelfServiceLinkUrl.Length > 0)
        {
            notice = "PIN karty ustawiony. Zaloguj się na stronie i dodaj kartę: Dodaj metodę logowania → Klucz zabezpieczeń.";
            ShowHelpPage("Połącz kartę z kontem", config.SelfServiceLinkUrl);
            RefreshChrome();
            return;
        }
        // The card and its PIN sign the person in to Windows: no login or password on any page.
        var signedIn = await SignInWithCardAsync();
        if (cardFolder != folder || cardKey == null) { signedIn?.SignIn?.Dispose(); RefreshChrome(); return; } // Taken away meanwhile.
        if (signedIn?.SignIn is { } signIn)
        {
            var logon = UserLogon.FromCard(signIn);
            cardLogon = logon;
            Audit.Write("selfservice_signed_in_with_card", logon.DisplayName);
            notice = "Zalogowano: " + logon.FriendlyName + ".";
            RefreshChrome();
            bool ready;
            try { ready = await Task.Run(logon.EnsureProfile); }
            catch (Exception ex) { ready = false; Audit.Write("profile_failed", ex.Message); }
            if (cardLogon != logon) return;
            if (!ready) notice = "Windows nie utworzył profilu dla " + logon.DisplayName + ". Strony otworzą się bez logowania Windows.";
        }
        else if (signedIn != null)
        {
            notice = "Nie udało się zalogować kartą do Windows (" + signedIn.Message + "). Zaloguj się kartą na stronie.";
            Audit.Write("selfservice_card_signin_failed", signedIn.Message);
        }
        if (config.SelfServiceSignInUrl.Length > 0) ShowHelpPage("Logowanie kartą", config.SelfServiceSignInUrl);
        RefreshChrome();
    }

    /// <summary>The card is gone (or a new one came): its pages close and its sign-in is deleted.</summary>
    private void EndCardSession(string? reason)
    {
        if (cardFolder is not { } folder) return;
        cardFolder = null;
        cardId = null;
        cardAwaySince = null;
        helpBrowser?.Close();
        cardKeeper?.Close();
        cardKeeper = null;
        cardLogon?.Dispose(); // Signs the person out of Windows here: their Edge closes with it.
        cardLogon = null;
        credentialPrompt = IntPtr.Zero;
        notice = null;
        if (reason != null) Audit.Write(reason);
        DeleteCardFolder(folder);
        RefreshChrome();
    }

    /// <summary>The browser process may hold the folder for a moment after its window closed.</summary>
    private static void DeleteCardFolder(string folder) => _ = Task.Run(async () =>
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { await Task.Delay(1000); }
            catch (UnauthorizedAccessException) { await Task.Delay(1000); }
        }
    });

    /// <summary>Sign-ins left over from a Kiosk that did not close normally.</summary>
    private static void ClearCardFolders()
    {
        try { if (Directory.Exists(CardFolders)) foreach (var folder in Directory.GetDirectories(CardFolders)) DeleteCardFolder(folder); }
        catch (Exception ex) { Audit.Write("selfservice_cleanup_error", ex.Message); }
    }

    /// <summary>A Szybka pomoc page in the signed-in person's own Edge (a new window of it when it already runs).</summary>
    private bool OpenPageAsCardPerson(string url)
    {
        if (cardLogon == null) return false;
        try
        {
            var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            var (process, thread) = cardLogon.Start(edge, "\"" + edge + "\" --new-window --start-maximized --no-first-run \"" + url + "\"", Path.GetDirectoryName(edge), show: 3);
            AppNative.ResumeThread(thread);
            AppNative.CloseHandle(thread);
            AppNative.CloseHandle(process);
            Audit.Write("selfservice_page_as_person");
            return true;
        }
        catch (Exception ex)
        {
            Audit.Write("selfservice_page_as_person_error", ex.Message);
            notice = "Nie udało się otworzyć strony na Twoim koncie: " + ex.Message;
            return false;
        }
    }

    private void SelfServiceStatus()
    {
        if (cardFolder == null)
            desktop.UpdateStatus("Przyłóż kartę", notice ?? "Przyłóż kartę, aby zalogować się nią i skorzystać z Szybkiej pomocy. " +
                "Bez karty strony Szybkiej pomocy otwierają się w oknie gościa.");
        else
            desktop.UpdateStatus("Karta przyłożona", notice ?? (verifying || desktop.AskingPin ? "Sprawdzam kartę…" :
                "Wybierz zadanie z Szybkiej pomocy. Logujesz się raz – działa, dopóki karta leży na czytniku. Zabierz kartę, aby zakończyć."));
    }
}

/// <summary>An invisible window holding the card session's browser open (see KioskForm.cardKeeper).</summary>
internal sealed class CardBrowserKeeper : Form
{
    private readonly BrowserView browser;

    internal CardBrowserKeeper(string folder)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        Opacity = 0;
        browser = new BrowserView([], null, null, windowsAccount: false, singlePage: true, profileFolder: folder) { Dock = DockStyle.Fill };
        Controls.Add(browser);
        Show();
        _ = Start();
    }

    protected override bool ShowWithoutActivation => true;

    private async Task Start()
    {
        try { await browser.InitializeAsync("about:blank"); }
        catch (Exception ex) { Audit.Write("selfservice_keeper_error", ex.Message); }
    }
}
