using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Kiosk.Client;

/// <summary>
/// News from a SharePoint site (pages published as news), read as the Windows account Kiosk runs as (single sign-on)
/// in a hidden WebView2: no app registration and no password. Banner pictures come along as data, so the slides need no sign-in.
/// </summary>
internal sealed class SharePointNews : IDisposable
{
    // How often is set by the Kiosk (Konfiguracja → Komunikaty → Pobieranie z sieci); this only spares repeated calls close together.
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    internal const int MaxMessageChars = 24_000_000;
    internal const int MaxItems = 20;

    private readonly Control owner;
    private readonly Func<string, int, Task<List<NewsItem>>> fetch;
    private readonly Func<long> nowMilliseconds;
    // WebView2 has one navigation and one message listener. A timer tick or settings save must not
    // start another request until the current navigation and its handlers have finished.
    private readonly SemaphoreSlim fetchGate = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private CoreWebView2Controller? controller;
    private (string Url, int Count, string Account)? request;
    private string account = "";
    private bool signInAttached;
    private List<NewsItem> cached = [];
    /// <summary>The last answer (shown while the next one is fetched).</summary>
    internal List<NewsItem> Cached => cached;
    /// <summary>The owner releases old pictures after all desktop and screen controls stop using them.</summary>
    internal event Action<IReadOnlyList<NewsItem>>? CacheReplaced;
    private long? fetchedAtMilliseconds;
    private bool disposed;

    internal string LastError { get; private set; } = "";

    internal SharePointNews(Control owner, Func<string, int, Task<List<NewsItem>>>? fetch = null, Func<long>? nowMilliseconds = null)
    {
        this.owner = owner;
        this.fetch = fetch ?? FetchAsync;
        this.nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);
    }

    internal bool Matches(string url, int count, string accountName) =>
        !disposed && request is { } current && current == (url, count, accountName.Trim());

    /// <summary>The site's latest news; read again after the minimum refresh interval, otherwise return the last result.</summary>
    /// <param name="accountName">Konfiguracja → Komunikaty "Konto do SharePointa": a shared account (accounts.kiosk) that signs in
    /// on Microsoft's page when the Kiosk's Windows account has no access (a local kiosk account has none).</param>
    internal async Task<List<NewsItem>> GetAsync(string url, int count, string accountName = "")
    {
        try { await fetchGate.WaitAsync(stopped.Token); }
        catch (OperationCanceledException) when (disposed) { return []; }
        try
        {
            if (disposed) return [];
            account = accountName.Trim();
            var wanted = (Url: url, Count: count, Account: account);
            if (request != wanted) { request = wanted; ReplaceCache([]); fetchedAtMilliseconds = null; LastError = ""; }
            var now = nowMilliseconds();
            if (url.Length == 0 || fetchedAtMilliseconds is { } fetched && now - fetched < Refresh.TotalMilliseconds) return cached;
            try
            {
                var latest = await fetch(url, count);
                if (disposed) { News.DisposePictures(latest); return []; }
                ReplaceCache(latest);
                LastError = "";
            }
            catch (Exception ex)
            {
                if (disposed) return [];
                LastError = ex.Message;
                Audit.Write("sharepoint_news_failed", ex.Message);
            }
            // Count from completion: a slow fetch must not make queued callers fetch again immediately.
            fetchedAtMilliseconds = nowMilliseconds(); // One attempt per period, also when it fails.
            return cached;
        }
        finally { fetchGate.Release(); }
    }

    private void ReplaceCache(List<NewsItem> next)
    {
        var previous = cached;
        cached = next;
        if (previous.Count == 0 || ReferenceEquals(previous, next)) return;
        if (CacheReplaced is { } release) release(previous);
        else News.DisposePictures(previous);
    }

    private async Task<List<NewsItem>> FetchAsync(string url, int count)
    {
        stopped.Token.ThrowIfCancellationRequested();
        if (controller == null)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "SharePointNews");
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true });
            stopped.Token.ThrowIfCancellationRequested();
            var created = await environment.CreateCoreWebView2ControllerAsync(owner.Handle);
            if (disposed) { created.Close(); throw new OperationCanceledException(stopped.Token); }
            controller = created;
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
                var signInUrl = core.Source;
                if (!e.IsSuccess || !Uri.TryCreate(signInUrl, UriKind.Absolute, out var at) || at.Scheme != Uri.UriSchemeHttps) return;
                if (!at.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) && !at.Host.Equals("login.microsoft.com", StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    // A shared account named in Konfiguracja, else the login and password saved on this PC.
                    var name = account;
                    var shared = await Task.Run(() => name.Length > 0 ? SharedAccounts.TryGetCached(name, url, news: true) : NewsAccount.TryGet(url));
                    if (disposed || !string.Equals(core.Source, signInUrl, StringComparison.Ordinal)) return;
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
                try { await arrived.Task.WaitAsync(Limit, stopped.Token); }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException(core.Source.Contains("login.", StringComparison.OrdinalIgnoreCase)
                        ? account.Length > 0 || NewsAccount.Saved ? "SharePoint wymaga logowania, a zapisane konto nie zalogowało się (hasło, MFA albo dostęp do witryny)."
                            : "SharePoint wymaga logowania: konto Windows, na którym działa Kiosk, nie ma dostępu do tej strony – podaj konto w Konfiguracji → Komunikaty."
                        : "SharePoint nie odpowiedział w 60 s.");
                }
            }
            finally { core.NavigationCompleted -= Completed; }

            var answer = new TaskCompletionSource<string>();
            void Received(object? s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(source.Host, site.Host, StringComparison.OrdinalIgnoreCase) || source.Port != site.Port) return;
                try
                {
                    var message = e.TryGetWebMessageAsString();
                    if (message.Length > MaxMessageChars) throw new InvalidDataException("Odpowiedź SharePointa jest za duża.");
                    answer.TrySetResult(message);
                }
                catch (Exception ex) { answer.TrySetException(ex); }
            }
            core.WebMessageReceived += Received;
            try
            {
                await core.ExecuteScriptAsync(Script(site, count));
                var json = await answer.Task.WaitAsync(Limit, stopped.Token);
                return Parse(json, site);
            }
            finally { core.WebMessageReceived -= Received; }
        }
        finally { try { core.Navigate("about:blank"); } catch (Exception) { } }
    }

    /// <summary>The site's pages published as news (PromotedState 2), newest first, with their banner pictures as data URLs.</summary>
    private static string Script(Uri site, int count) => """
        (async () => {
          try {
            const bannerLimit = 4000000;
            const bannerTotalLimit = 16000000;
            const metadataLimit = 4000000;
            let bannerBytes = 0;
            async function readLimited(response, limit) {
              const declared = response.headers.get('content-length');
              if (declared !== null && Number(declared) > limit) throw new Error('Odpowiedź SharePointa jest za duża.');
              if (!response.body) throw new Error('SharePoint nie zwrócił danych.');
              const reader = response.body.getReader();
              const chunks = [];
              let size = 0;
              try {
                while (true) {
                  const part = await reader.read();
                  if (part.done) break;
                  size += part.value.byteLength;
                  if (size > limit) { await reader.cancel(); throw new Error('Odpowiedź SharePointa jest za duża.'); }
                  chunks.push(part.value);
                }
              } finally { reader.releaseLock(); }
              const bytes = new Uint8Array(size);
              let offset = 0;
              for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
              return bytes;
            }
            const web = new URL(SITE).pathname.replace(/\/+$/, '');
            const api = location.origin + web + "/_api/web/GetList(@u)/items?@u='" + encodeURIComponent(web + '/SitePages') + "'" +
              "&$select=Title,Description,BannerImageUrl,FileRef,FirstPublishedDate&$filter=PromotedState eq 2&$orderby=FirstPublishedDate desc&$top=" + COUNT;
            const r = await fetch(api, { headers: { Accept: 'application/json;odata=nometadata' } });
            if (!r.ok) throw new Error('SharePoint odpowiedział HTTP ' + r.status);
            const data = JSON.parse(new TextDecoder().decode(await readLimited(r, metadataLimit)));
            if (!Array.isArray(data.value) || data.value.length > COUNT) throw new Error('SharePoint zwrócił zbyt wiele aktualności.');
            const items = [];
            for (const p of data.value) {
              let page;
              try { page = new URL(p.FileRef, location.origin); }
              catch (e) { continue; }
              if (page.origin !== location.origin || page.href.length > 4096) continue;
              let image = '';
              const src = p.BannerImageUrl && (p.BannerImageUrl.Url || p.BannerImageUrl);
              if (typeof src === 'string' && src && bannerBytes < bannerTotalLimit) {
                try {
                  const b = await fetch(new URL(src, location.origin));
                  const declared = b.headers.get('content-length');
                  if (b.ok && b.body && (declared === null || Number(declared) < bannerLimit && Number(declared) <= bannerTotalLimit - bannerBytes)) {
                    const reader = b.body.getReader();
                    const chunks = [];
                    let size = 0;
                    try {
                      while (true) {
                        const part = await reader.read();
                        if (part.done) break;
                        size += part.value.byteLength;
                        bannerBytes += part.value.byteLength;
                        if (size >= bannerLimit || bannerBytes > bannerTotalLimit) { await reader.cancel(); break; }
                        chunks.push(part.value);
                      }
                    } finally { reader.releaseLock(); }
                    if (size > 0 && size < bannerLimit && bannerBytes <= bannerTotalLimit) {
                      const blob = new Blob(chunks, { type: b.headers.get('content-type') || '' });
                      image = await new Promise(done => { const f = new FileReader(); f.onload = () => done(f.result); f.onerror = () => done(''); f.readAsDataURL(blob); });
                    }
                  } else if (b.body) await b.body.cancel();
                } catch (e) { }
              }
              items.push({ title: String(p.Title || '').slice(0, 512), text: String(p.Description || '').slice(0, 4096), url: page.href, image });
            }
            chrome.webview.postMessage(JSON.stringify({ ok: true, items }));
          } catch (e) { chrome.webview.postMessage(JSON.stringify({ ok: false, error: String((e && e.message) || e) })); }
        })();
        """.Replace("SITE", JsonSerializer.Serialize(site.AbsoluteUri)).Replace("COUNT", count.ToString());

    internal static List<NewsItem> Parse(string json, Uri? site = null)
    {
        if (json.Length > MaxMessageChars) throw new InvalidDataException("Odpowiedź SharePointa jest za duża.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(root.GetProperty("error").GetString());
        var items = new List<NewsItem>();
        try
        {
            var seen = 0;
            foreach (var p in root.GetProperty("items").EnumerateArray())
            {
                if (++seen > MaxItems) throw new InvalidDataException("SharePoint zwrócił ponad 20 aktualności.");
                string title = p.GetProperty("title").GetString() ?? "", text = p.GetProperty("text").GetString() ?? "";
                string url = p.GetProperty("url").GetString() ?? "", image = p.GetProperty("image").GetString() ?? "";
                if (title.Length > 512) title = title[..512];
                if (text.Length > 4096) text = text[..4096];
                if (url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps ||
                    site != null && (!string.Equals(page.Host, site.Host, StringComparison.OrdinalIgnoreCase) || page.Port != site.Port)) continue;
                if (title.Length == 0 && text.Length == 0) continue;
                Image? picture = null;
                int comma = image.IndexOf(',');
                if (image.StartsWith("data:image/", StringComparison.Ordinal) && comma > 0 &&
                    image.Length <= News.MaxPictureBytes * 4L / 3 + 256)
                    try { picture = News.DecodePicture(Convert.FromBase64String(image[(comma + 1)..])); }
                    catch (Exception) { picture = null; } // e.g. WebP, which Windows Forms cannot draw.
                items.Add(new NewsItem { Title = title, Text = text, Button = "Czytaj", Action = url, Picture = picture });
            }
        }
        catch { News.DisposePictures(items); throw; }
        return items;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopped.Cancel();
        ReplaceCache([]);
        controller?.Close();
        controller = null;
    }
}
