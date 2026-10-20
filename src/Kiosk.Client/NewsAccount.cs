using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Konfiguracja → Komunikaty: the login and password that sign in to the SharePoint news site when the Kiosk's own
/// Windows account cannot (a local kiosk account). Kept encrypted for this computer in a file only SYSTEM and
/// administrators may open; the Kiosk gets it from the Kiosk Card service, like the shared accounts.
/// </summary>
internal static class NewsAccount
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kiosk.NewsAccount");
    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kiosk", "NewsAccount.cred");

    internal static bool Saved => File.Exists(File_);

    /// <summary>Saves (or with an empty login removes) the account; needs an administrator.</summary>
    internal static void Write(CentralConfig.AccountChange change)
    {
        if (change.User.Trim().Length == 0) { if (File.Exists(File_)) File.Delete(File_); Audit.Write("news_account_removed"); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SharedAccounts.Account(change.User.Trim(), change.Password));
        try { CentralConfig.WriteSecret(File_, plain, Entropy); }
        finally { Array.Clear(plain); }
        Audit.Write("news_account_saved", change.User.Trim());
    }

    /// <summary>Kiosk Card service, "newsaccount": only for the installed Kiosk program.</summary>
    internal static CardService.Response ForService(string? clientProgram)
    {
        try
        {
            if (!SharedAccounts.IsInstalledKiosk(clientProgram)) return new CardService.Response { Code = -2, Message = "Konto do SharePointa podaje usługa tylko programowi Kiosk z Program Files." };
            if (!Saved) return new CardService.Response { Code = -2, Message = "Nie zapisano loginu i hasła do SharePointa." };
            var plain = CentralConfig.ReadSecret(File_, Entropy);
            try
            {
                var account = JsonSerializer.Deserialize<SharedAccounts.Account>(plain) ?? throw new InvalidDataException("Puste konto.");
                return new CardService.Response { Message = account.User, Secret = account.Password };
            }
            finally { Array.Clear(plain); }
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    private static SharedAccounts.Account? cached;
    private static bool asked;

    /// <summary>The saved account, asked of the service once per Kiosk start (or after Konfiguracja is saved); null = none.</summary>
    internal static SharedAccounts.Account? TryGet()
    {
        if (asked) return cached;
        asked = true;
        if (!Saved) return cached = null;
        var answer = CardService.Call(new CardService.Request { Op = "newsaccount" });
        if (answer.Code != 0 || answer.Secret == null) { Audit.Write("news_account_failed", answer.Message); return cached = null; }
        return cached = new SharedAccounts.Account(answer.Message, answer.Secret);
    }

    internal static void Forget() { asked = false; cached = null; }
}
