using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Kiosk.Client;

/// <summary>
/// News from a SharePoint site (pages published as news), read as the Windows account Kiosk runs as (single sign-on)
/// in a hidden WebView2: no app registration and no password. Banner pictures come along as data, so the slides need no sign-in.
/// </summary>
internal sealed class SharePointNews : IDisposable
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly Control owner;
    private CoreWebView2Controller? controller;
    private string key = "";
    private string account = "";
    private bool signInAttached;
    private List<NewsItem> cached = [];
    private DateTime fetchedAt = DateTime.MinValue;

    internal string LastError { get; private set; } = "";

    internal SharePointNews(Control owner) => this.owner = owner;

    /// <summary>The site's latest news; read again every 10 minutes, otherwise (also after an error) the last result.</summary>
    /// <param name="accountName">Konfiguracja → Komunikaty "Konto do SharePointa": a shared account (accounts.kiosk) that signs in
    /// on Microsoft's page when the Kiosk's Windows account has no access (a local kiosk account has none).</param>
    internal async Task<List<NewsItem>> GetAsync(string url, int count, string accountName = "")
    {
        account = accountName.Trim();
        var wanted = url + "|" + count + "|" + account;
        if (wanted != key) { key = wanted; cached = []; fetchedAt = DateTime.MinValue; }
        if (url.Length == 0 || DateTime.UtcNow - fetchedAt < Refresh) return cached;
        fetchedAt = DateTime.UtcNow; // One attempt per period, also when it fails.
        try { cached = await FetchAsync(url, count); LastError = ""; }
        catch (Exception ex) { LastError = ex.Message; Audit.Write("sharepoint_news_failed", ex.Message); }
        return cached;
    }

    private async Task<List<NewsItem>> FetchAsync(string url, int count)
    {
        if (controller == null)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "SharePointNews");
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true });
            controller = await environment.CreateCoreWebView2ControllerAsync(owner.Handle);
            controller.IsVisible = false;
            controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
            controller.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
        }
        var core = controller.CoreWebView2;
        var site = new Uri(url);
        if (!signInAttached)
        {
            // Microsoft's sign-in on the way: the shared account types its login and password (the session then stays in this profile).
            signInAttached = true;
            core.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess || !Uri.TryCreate(core.Source, UriKind.Absolute, out var at) || at.Scheme != Uri.UriSchemeHttps) return;
                if (!at.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) && !at.Host.Equals("login.microsoft.com", StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    // A shared account named in Konfiguracja, else the login and password saved on this PC.
                    var name = account;
                    var shared = await Task.Run(() => name.Length > 0 ? SharedAccounts.TryGetCached(name) : NewsAccount.TryGet());
                    if (shared == null) { Audit.Write("sharepoint_news_account_missing", name.Length > 0 ? name : "login i hasło"); return; }
                    await core.ExecuteScriptAsync(PageScripts.SignIn(shared));
                    Audit.Write("sharepoint_news_sign_in", shared.User);
                }
                catch (Exception ex) { Audit.Write("sharepoint_news_sign_in_failed", ex.Message); }
            };
        }
        try
        {
            // The site's own page first: Windows signs in on the way (login.microsoftonline.com and back), then its REST API answers.
            var arrived = new TaskCompletionSource<bool>();
            void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (Uri.TryCreate(core.Source, UriKind.Absolute, out var at) && string.Equals(at.Host, site.Host, StringComparison.OrdinalIgnoreCase))
                    arrived.TrySetResult(e.IsSuccess || e.HttpStatusCode is >= 200 and < 500);
            }
            core.NavigationCompleted += Completed;
            try
            {
                core.Navigate(site.AbsoluteUri);
                if (await Task.WhenAny(arrived.Task, Task.Delay(Limit)) != arrived.Task)
                    throw new InvalidOperationException(core.Source.Contains("login.", StringComparison.OrdinalIgnoreCase)
                        ? account.Length > 0 || NewsAccount.Saved ? "SharePoint wymaga logowania, a zapisane konto nie zalogowało się (hasło, MFA albo dostęp do witryny)."
                            : "SharePoint wymaga logowania: konto Windows, na którym działa Kiosk, nie ma dostępu do tej strony – podaj konto w Konfiguracji → Komunikaty."
                        : "SharePoint nie odpowiedział w 60 s.");
            }
            finally { core.NavigationCompleted -= Completed; }

            var answer = new TaskCompletionSource<string>();
            void Received(object? s, CoreWebView2WebMessageReceivedEventArgs e) => answer.TrySetResult(e.TryGetWebMessageAsString());
            core.WebMessageReceived += Received;
            try
            {
                await core.ExecuteScriptAsync(Script(site, count));
                if (await Task.WhenAny(answer.Task, Task.Delay(Limit)) != answer.Task) throw new TimeoutException("SharePoint nie odpowiedział w 60 s.");
            }
            finally { core.WebMessageReceived -= Received; }
            return Parse(answer.Task.Result);
        }
        finally { try { core.Navigate("about:blank"); } catch (Exception) { } }
    }

    /// <summary>The site's pages published as news (PromotedState 2), newest first, with their banner pictures as data URLs.</summary>
    private static string Script(Uri site, int count) => """
        (async () => {
          try {
            const web = new URL(SITE).pathname.replace(/\/+$/, '');
            const api = location.origin + web + "/_api/web/GetList(@u)/items?@u='" + encodeURIComponent(web + '/SitePages') + "'" +
              "&$select=Title,Description,BannerImageUrl,FileRef,FirstPublishedDate&$filter=PromotedState eq 2&$orderby=FirstPublishedDate desc&$top=" + COUNT;
            const r = await fetch(api, { headers: { Accept: 'application/json;odata=nometadata' } });
            if (!r.ok) throw new Error('SharePoint odpowiedział HTTP ' + r.status);
            const data = await r.json();
            const items = [];
            for (const p of data.value || []) {
              let image = '';
              const src = p.BannerImageUrl && (p.BannerImageUrl.Url || p.BannerImageUrl);
              if (typeof src === 'string' && src) {
                try {
                  const b = await fetch(new URL(src, location.origin));
                  const blob = b.ok ? await b.blob() : null;
                  if (blob && blob.size < 4000000)
                    image = await new Promise(done => { const f = new FileReader(); f.onload = () => done(f.result); f.onerror = () => done(''); f.readAsDataURL(blob); });
                } catch (e) { }
              }
              items.push({ title: p.Title || '', text: p.Description || '', url: new URL(p.FileRef, location.origin).href, image });
            }
            chrome.webview.postMessage(JSON.stringify({ ok: true, items }));
          } catch (e) { chrome.webview.postMessage(JSON.stringify({ ok: false, error: String((e && e.message) || e) })); }
        })();
        """.Replace("SITE", JsonSerializer.Serialize(site.AbsoluteUri)).Replace("COUNT", count.ToString());

    internal static List<NewsItem> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(root.GetProperty("error").GetString());
        var items = new List<NewsItem>();
        foreach (var p in root.GetProperty("items").EnumerateArray())
        {
            string title = p.GetProperty("title").GetString() ?? "", text = p.GetProperty("text").GetString() ?? "";
            string url = p.GetProperty("url").GetString() ?? "", image = p.GetProperty("image").GetString() ?? "";
            if (title.Length == 0 && text.Length == 0) continue;
            Image? picture = null;
            int comma = image.IndexOf(',');
            if (image.StartsWith("data:image/", StringComparison.Ordinal) && comma > 0)
                try { picture = Image.FromStream(new MemoryStream(Convert.FromBase64String(image[(comma + 1)..]))); }
                catch (Exception) { picture = null; } // e.g. WebP, which Windows Forms cannot draw.
            items.Add(new NewsItem { Title = title, Text = text, Button = "Czytaj", Action = url, Picture = picture });
        }
        return items;
    }

    public void Dispose() { controller?.Close(); controller = null; }
}
