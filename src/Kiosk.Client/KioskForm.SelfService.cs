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

    private void SelfServiceTick()
    {
        // While a PIN is asked or checked the card is reset on purpose; presence changes are evaluated afterwards.
        if (verifying || desktop.AskingPin) return;
        var key = reader.Snapshot(config.ReaderName);
        if (key == cardKey) return;
        cardKey = key;
        if (key == null) EndCardSession("selfservice_card_removed");
        else _ = StartCardSessionAsync();
    }

    private async Task StartCardSessionAsync()
    {
        EndCardSession(null);
        var folder = Path.Combine(CardFolders, Guid.NewGuid().ToString("N"));
        cardFolder = folder;
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
        }
        else if (config.SelfServiceSignInUrl.Length > 0) ShowHelpPage("Logowanie kartą", config.SelfServiceSignInUrl);
        RefreshChrome();
    }

    /// <summary>The card is gone (or a new one came): its pages close and its sign-in is deleted.</summary>
    private void EndCardSession(string? reason)
    {
        if (cardFolder is not { } folder) return;
        cardFolder = null;
        helpBrowser?.Close();
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
