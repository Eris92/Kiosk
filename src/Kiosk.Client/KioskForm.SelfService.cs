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
        cardLogon.Dispose(); // Signs the person out of Windows here: their Edge closes with it.
        cardLogon = null;
        notice = null;
        if (reason != null) Audit.Write(reason);
        RefreshChrome();
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
