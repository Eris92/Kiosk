using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// The login and password that sign in to the SharePoint news site when the Kiosk's own Windows account cannot (a local
/// kiosk account): Konto Kiosku (Konfiguracja → Konto Kiosku), or an older separate account saved before it. Kept encrypted
/// for this computer in a file only SYSTEM and administrators may open; the Kiosk gets it from the Kiosk Card service.
/// </summary>
internal static class NewsAccount
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kiosk.NewsAccount");
    internal static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kiosk", "NewsAccount.cred");

    internal static bool LegacySaved => File.Exists(File_);
    internal static bool Saved => CentralConfig.AccountSaved || LegacySaved;

    /// <summary>Konto Kiosku replaces the older separate account (saved or removed with it).</summary>
    internal static void RemoveLegacy()
    {
        if (!LegacySaved) return;
        File.Delete(File_);
        Audit.Write("news_account_removed", "zastąpione Kontem Kiosku");
    }

    /// <summary>Saves (or with an empty login removes) the account; needs an administrator.</summary>
    internal static void Write(CentralConfig.AccountChange change)
    {
        if (change.User.Trim().Length == 0) { if (File.Exists(File_)) File.Delete(File_); Audit.Write("news_account_removed"); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SharedAccounts.Account(change.User.Trim(), change.Password));
        try { CentralConfig.WriteSecret(File_, plain, Entropy); }
        finally { Array.Clear(plain); }
        Audit.Write("news_account_saved", change.User.Trim());
    }

    /// <summary>Kiosk Card service, "newsaccount": only for the configured SharePoint news site.</summary>
    internal static CardService.Response ForService(string? clientProgram, string site)
    {
        try
        {
            if (!SharedAccounts.IsInstalledKiosk(clientProgram)) return new CardService.Response { Code = -2, Message = "Konto do SharePointa podaje usługa tylko programowi Kiosk z Program Files." };
            if (!Saved) return new CardService.Response { Code = -2, Message = "Nie zapisano loginu i hasła do SharePointa." };
            // Only when this PC's configuration signs in to SharePoint news with it (no shared account named there):
            // otherwise the Kiosk has no use for Konto Kiosku, which stays in the service (shares, statistics).
            var config = Configuration.Load(Path.Combine(CentralConfig.MachineConfigFolder(), CentralConfig.FileName));
            if (!AuthorizedSite(config, site))
                return new CardService.Response { Code = -2, Message = "Konfiguracja tego komputera nie loguje się Kontem Kiosku do SharePointa." };
            if (CentralConfig.ReadAccount() is { } kiosk) return new CardService.Response { Message = kiosk.User, Secret = kiosk.Password };
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

    internal static bool AuthorizedSite(Config config, string site) =>
        config.NewsSharePoint.Length > 0 && config.NewsSharePointAccount.Trim().Length == 0 &&
        string.Equals(config.NewsSharePoint, site, StringComparison.OrdinalIgnoreCase);

    private static SharedAccounts.Account? cached;
    private static bool asked;
    private static long askedAt;
    private static string cachedSite = "";
    private static readonly object cacheGate = new();
    private const long CacheLifetimeMilliseconds = 5 * 60 * 1000;

    /// <summary>The saved account, refreshed after five minutes or when Konfiguracja changes; null = none.</summary>
    internal static SharedAccounts.Account? TryGet(string site)
    {
        lock (cacheGate)
        {
            var now = Environment.TickCount64;
            if (asked && cachedSite == site && now - askedAt < CacheLifetimeMilliseconds) return cached;
            asked = true;
            askedAt = now;
            cachedSite = site;
            if (!Saved) return cached = null;
            var answer = CardService.Call(new CardService.Request { Op = "newsaccount", Source = site });
            if (answer.Code != 0 || answer.Secret == null) { Audit.Write("news_account_failed", answer.Message); return cached = null; }
            return cached = new SharedAccounts.Account(answer.Message, answer.Secret);
        }
    }

    internal static void Forget() { lock (cacheGate) { asked = false; cached = null; cachedSite = ""; askedAt = 0; } }
}
