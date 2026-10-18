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

    /// <summary>When the card left the reader; the sign-in waits <see cref="CardReturnSeconds"/> for it to come back.</summary>
    private DateTime? cardAwaySince;
    // Windows may ask to lift the card and tap it again, and resets it: that must not end the sign-in.
    private const int CardReturnSeconds = 20;

    /// <summary>Nobody signed in: the card sign-in screen is (re)started in the start screen and waits for a tap.</summary>
    private void PrepareCardSignIn()
    {
        if (cardLogon != null || signInScreen != null) return;
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
        Audit.Write("selfservice_signed_in_with_card", logon.DisplayName);
        notice = null;
        RefreshChrome();
        bool ready;
        try { ready = await Task.Run(logon.EnsureProfile); }
        catch (Exception ex) { ready = false; Audit.Write("profile_failed", ex.Message); }
        if (cardLogon != logon) return;
        if (!ready) notice = "Windows nie utworzył profilu dla " + logon.DisplayName + ". Strony otworzą się bez logowania Windows.";
        else if (config.SelfServiceSignInUrl.Length > 0) ShowHelpPage("Logowanie kartą", config.SelfServiceSignInUrl);
        RefreshChrome();
    }

    private void SelfServiceTick()
    {
        var key = reader.Snapshot(config.ReaderName);
        cardKey = key;
        PrepareCardSignIn();
        if (cardPage is { View.Started: true } page && !page.View.Refresh()) CloseCardPage();   // Its Edge window was closed.
        if (cardLogon == null) return;
        if (key != null)
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
        helpBrowser?.Close();
        CloseCardPage();
        cardLogon.Dispose(); // Signs the person out of Windows here: their Edge closes with it.
        cardLogon = null;
        notice = null;
        if (reason != null) Audit.Write(reason);
        RefreshChrome();
    }

    /// <summary>The page open in the person's Edge, inside the Kiosk window; null = the start screen.</summary>
    private CardPage? cardPage;

    /// <summary>A Szybka pomoc page in the signed-in person's own Edge (their account, SSO), inside the Kiosk window.</summary>
    private bool OpenPageAsCardPerson(string url)
    {
        if (cardLogon == null) return false;
        _ = OpenCardPageAsync(cardLogon, url);
        return true;
    }

    private async Task OpenCardPageAsync(UserLogon logon, string url)
    {
        CloseCardPage();
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        // The Kiosk's own Edge folder in the person's profile: their everyday Edge may still run in another
        // (disconnected) Windows session of the same account, and would take the page into that invisible session.
        var folder = logon.ProfileDirectory is { } profile ? "--user-data-dir=\"" + Path.Combine(profile, @"AppData\Local\Kiosk\Edge") + "\" " : "";
        var entry = new ApplicationEntry { Name = "Strona", Path = edge, Arguments = folder + "--no-first-run --new-window \"" + url + "\"" };
        var page = new CardPage(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url);
        page.CloseRequested += () => { if (cardPage == page) CloseCardPage(); };
        cardPage = page;
        surface.Controls.Add(page);
        ShowView(page);
        try
        {
            await page.View.StartAsync(entry, logon);
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

    /// <summary>Back to the start screen: the page's Edge window closes.</summary>
    private void CloseCardPage()
    {
        if (cardPage is not { } page) return;
        cardPage = null;
        ShowView(desktop);
        surface.Controls.Remove(page);
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
        else if (signInScreen != null) desktop.ShowSignIn(true);
        else
        {
            desktop.ShowSignIn(false);
            desktop.UpdateStatus("Logowanie kartą", notice ?? "Przygotowuję logowanie kartą…");
        }
    }
}

/// <summary>A page of the signed-in person's Edge inside the Kiosk: a bar with "Wróć" over the Edge window.</summary>
internal sealed class CardPage : Panel
{
    internal ApplicationView View { get; } = new();
    internal event Action? CloseRequested;

    internal CardPage(string title)
    {
        Dock = DockStyle.Fill;
        BackColor = KioskTheme.Background;
        var header = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(52), BackColor = KioskTheme.ShellBar };
        var heading = new Label
        {
            Text = title, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0), Font = new Font("Segoe UI", 11), ForeColor = KioskTheme.Text
        };
        var back = new Button { Text = "Wróć do Kiosku", Dock = DockStyle.Right, Width = LogicalToDeviceUnits(180) };
        KioskTheme.StyleButton(back, primary: true);
        back.Click += (_, _) => CloseRequested?.Invoke();
        header.Controls.Add(heading);
        header.Controls.Add(back);
        Controls.Add(View);
        Controls.Add(header);
    }
}
