using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Kiosk.Client;

/// <summary>
/// Akcje stron: after a page of the Kiosk's own browser or a screen loads, clicks the buttons named in the
/// configuration (cookies…) and signs in with a shared account: login, password, the sign-in button. Pages a person
/// opens in Edge after "Zaloguj" are Edge's own and are not touched.
/// </summary>
internal static class PageScripts
{
    /// <summary>Set by the Kiosk whenever the configuration is (re)loaded.</summary>
    internal static PageAction[] Actions { get; set; } = [];

    /// <param name="kiosk">The Kiosk's own page while nobody works (screens, lock screen, Szybka pomoc), not a person's browser.</param>
    internal static void Attach(CoreWebView2 core, bool kiosk)
    {
        // Consent windows in a frame of another site (a CMP): only the clicks there, never a password.
        core.FrameCreated += (_, created) =>
        {
            var frame = created.Frame;
            frame.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                try
                {
                    foreach (var action in Applies(core.Source, kiosk).Where(a => a.Click.Trim().Length > 0))
                        await frame.ExecuteScriptAsync(Script(action with { Account = "" }, null));
                }
                catch (Exception ex) { Audit.Write("page_action_error", "frame: " + ex.Message); }
            };
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (!e.IsSuccess) return;
            var url = core.Source;
            foreach (var action in Applies(url, kiosk))
            {
                try
                {
                    SharedAccounts.Account? account = null;
                    // A password goes only to an encrypted page.
                    if (action.Account.Trim().Length > 0 && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        account = await Task.Run(() => SharedAccounts.TryGetCached(action.Account.Trim()));
                    if (core.Source != url) return;   // Navigated on in the meantime: the next page gets its own turn.
                    await core.ExecuteScriptAsync(Script(action, account));
                    Audit.Write("page_action", (kiosk ? "kiosk " : "user ") + action.Url + (account != null ? " +konto" : ""));
                }
                catch (Exception ex) { Audit.Write("page_action_error", action.Url + ": " + ex.Message); }
            }
        };
    }

    private static IEnumerable<PageAction> Applies(string url, bool kiosk) =>
        Actions.Where(a => a.Matches(url) && (a.Scope == PageScope.All || a.Scope == PageScope.Kiosk == kiosk));

    private static string Script(PageAction action, SharedAccounts.Account? account)
    {
        var clicks = action.Click.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string J(object? value) => JsonSerializer.Serialize(value);
        return "(() => {" +
            "const clicks = " + J(clicks) + ", user = " + J(account?.User) + ", pass = " + J(account?.Password) + ", submit = " + J(action.Submit.Trim()) + ";" +
            """
            const page = 'kiosk:' + location.host + location.pathname;
            const visible = el => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
            // "przejdz do serwisu" finds "PRZEJDŹ DO SERWISU": no case, no Polish letters, single spaces.
            const norm = v => (v || '').toLowerCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/ł/g, 'l').replace(/\s+/g, ' ').trim();
            const label = el => norm(el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('title'));
            // Consent windows often live in a shadow root (web components): search those too.
            const deep = (root, selector, out) => {
              root.querySelectorAll(selector).forEach(e => out.push(e));
              root.querySelectorAll('*').forEach(e => { if (e.shadowRoot) deep(e.shadowRoot, selector, out); });
              return out;
            };
            const find = t => {
              t = norm(t);
              const all = deep(document, 'button, a, [role=button], input[type=submit], input[type=button]', []).filter(visible);
              return all.find(b => label(b) === t) || all.find(b => label(b).includes(t));
            };
            const set = (el, v) => {
              const d = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value');
              if (d && d.set) d.set.call(el, v); else el.value = v;
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            const send = form => setTimeout(() => {
              const s = submit ? find(submit) : null;
              if (s) s.click();
              else if (form) { if (form.requestSubmit) form.requestSubmit(); else form.submit(); }
            }, 300);
            const done = new Set();
            let tries = 0;
            const step = () => {
              for (const t of clicks) if (!done.has(t)) { const b = find(t); if (b) { b.click(); done.add(t); } }
              if (pass !== null) {
                const p = [...document.querySelectorAll('input[type=password]')].find(visible);
                if (p && !sessionStorage.getItem(page + ':pass')) {
                  sessionStorage.setItem(page + ':pass', '1');
                  const scope = p.form || document;
                  const u = [...scope.querySelectorAll('input[type=email], input[type=text], input:not([type])')].filter(visible)
                    .filter(i => i.compareDocumentPosition(p) & Node.DOCUMENT_POSITION_FOLLOWING).pop();
                  if (u && user && u.value !== user) set(u, user);
                  set(p, pass);
                  send(p.form);
                } else if (!p && user && !sessionStorage.getItem(page + ':user')) {
                  // Two steps (e.g. Microsoft): the login first, the password on the next view.
                  const u = [...document.querySelectorAll('input[type=email], input[autocomplete=username], input[name*=login i], input[name*=user i]')].find(visible);
                  if (u && !u.value) { sessionStorage.setItem(page + ':user', '1'); set(u, user); send(u.form); }
                }
              }
              if (++tries < 40) setTimeout(step, 750);   // about 30 s: banners that come late
            };
            step();
            })();
            """;
    }
}
