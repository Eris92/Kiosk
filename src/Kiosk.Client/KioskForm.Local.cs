namespace Kiosk.Client;

internal sealed partial class KioskForm
{
    private async Task<UserLogon?> SignInLocalAsync(CardIdentity identity)
    {
        if (identity.Id.StartsWith("card:", StringComparison.Ordinal))
        {
            notice = "Czytnik nie udostępnia identyfikatora karty. Nie można bezpiecznie przypisać konta.";
            return null;
        }
        var readerName = reader.ActiveReader;
        string message = "Wpisz PIN karty.";
        while (await desktop.AskPinAsync(message, cardPresent, config.HelpCloseSeconds) is { } pin)
        {
            try
            {
                CardVault.Result result;
                if (CardVault.IsEnrolled(identity.Id))
                {
                    result = await Task.Run(() => CardVault.Open(identity.Id, readerName, pin));
                    if (result.Logon != null) return result.Logon;
                    if (result.Code == CardPin.WrongPin) { message = "Błędny PIN. Spróbuj ponownie."; continue; }
                    if (result.Code is not (CardPin.Verified or CardPin.NoKey)) { notice = result.Message; return null; }
                }
                else
                {
                    var verified = await Task.Run(() => CardService.Call(new CardService.Request { Op = "verify", Reader = readerName, Pin = pin }));
                    if (verified.Code == 0x31) { message = "Błędny PIN. Spróbuj ponownie."; continue; }
                    if (verified.Code != 0) { notice = verified.Message; return null; }
                }
                if (!cardPresent()) return null;
                var logon = await desktop.AskAccountAsync(cardPresent);
                if (logon == null) return null;
                if (!cardPresent() || (identifyCard() ?? UncertifiedCard()).Id != identity.Id) { logon.Dispose(); return null; }
                try
                {
                    result = await Task.Run(() => CardVault.Enroll(identity.Id, logon, readerName, pin));
                    if (result.Code == CardPin.Verified) return logon;
                }
                catch { logon.Dispose(); throw; }
                logon.Dispose();
                notice = result.Message;
                return null;
            }
            catch (Exception ex) { notice = "Nie udało się odczytać konta karty: " + ex.GetType().Name; return null; }
        }
        return null;
    }

    private async Task StartLocalSelfServiceAsync()
    {
        verifying = true;
        try
        {
            var identity = identifyCard() ?? UncertifiedCard();
            var logon = await SignInLocalAsync(identity);
            if (logon != null) await CompleteCardSignInAsync(logon);
        }
        finally { verifying = false; RefreshChrome(); }
    }
}
