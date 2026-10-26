using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kiosk.Client;

/// <summary>
/// The film of a message ("Video" in kiosk-news.json): a small browser over the slide plays the MP4 and tells the
/// carousel when it has ended, so the slide stays as long as the film (pictures keep the time from Konfiguracja).
/// </summary>
internal sealed class NewsVideo : IDisposable
{
    private const string Host = "kiosk-news.video";
    internal WebView2 View { get; } = new() { Visible = false, DefaultBackgroundColor = Color.Black };
    internal event Action? Ended;
    private static int playingCount;
    /// <summary>Some film is on screen now (the lock and screensaver then count real input only, see <see cref="InputWatch"/>).</summary>
    internal static bool AnyPlaying => Volatile.Read(ref playingCount) > 0;
    private bool counted;
    internal event Action<string>? Failed;

    private Task? starting;
    private (string Source, bool Sound, bool Loop)? pending;
    private string playing = "";
    private bool disposed, broken;

    /// <summary>Starts the film (again only when it is another one); false while the browser is still starting.</summary>
    internal bool Play(string source, bool sound, bool loop)
    {
        if (broken) { View.Visible = false; Failed?.Invoke("brak przeglądarki do filmów (WebView2)"); return false; }
        View.Visible = true;
        View.BringToFront();
        if (View.CoreWebView2 == null)
        {
            // The browser starts once; the film follows when it is ready (a failed start was reported, the slide moves on by time).
            if (starting != null) { pending = (source, sound, loop); return false; }
            pending = (source, sound, loop);
            starting = StartAsync();
            starting.ContinueWith(t =>
            {
                if (disposed || !View.IsHandleCreated) return;
                View.BeginInvoke(() => { if (View.CoreWebView2 != null && View.Visible && pending is { } next) Play(next.Source, next.Sound, next.Loop); });
            }, TaskScheduler.Default);
            return false;
        }
        var key = source + "|" + sound + "|" + loop;
        if (key == playing) return true;
        playing = key;
        try
        {
            string address = source;
            if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // A file: its folder is served under a private name, so the page may play it.
                View.CoreWebView2.ClearVirtualHostNameToFolderMapping(Host);
                View.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, Path.GetDirectoryName(source)!, CoreWebView2HostResourceAccessKind.Allow);
                address = "https://" + Host + "/" + Uri.EscapeDataString(Path.GetFileName(source));
            }
            // Capture Windows' idle time before the video can keep its input clock awake.
            var initialIdle = Native.IdleMilliseconds();
            View.CoreWebView2.NavigateToString(Page(address, sound, loop));
            InputWatch.Start(initialIdle);
            Count(true);
            return true;
        }
        catch (Exception ex) { Failed?.Invoke(ex.Message); return false; }
    }

    /// <summary>Stops the film and hides the browser (another slide, the carousel hidden).</summary>
    internal void Stop()
    {
        View.Visible = false;
        Count(false);
        if (playing.Length == 0 || View.CoreWebView2 == null) return;
        playing = "";
        try { View.CoreWebView2.NavigateToString("<html><body style='background:#000'></body></html>"); }
        catch (Exception) { }
    }

    private async Task StartAsync()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "NewsVideo");
            // Films start on their own, also with sound (nobody clicks a screensaver first).
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" };
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, options);
            await View.EnsureCoreWebView2Async(environment);
            var core = View.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) =>
            {
                var message = e.TryGetWebMessageAsString() ?? "";
                if (message == "ended") Ended?.Invoke();
                else if (message.StartsWith("error", StringComparison.Ordinal)) Failed?.Invoke("film: " + message);
            };
        }
        catch (Exception ex)
        {
            broken = true;
            View.Visible = false;
            Failed?.Invoke(ex.Message);   // Films are then skipped: their slides stay as long as pictures.
        }
    }

    private static string Page(string address, bool sound, bool loop)
    {
        var src = System.Net.WebUtility.HtmlEncode(address);
        return "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;height:100%;background:#000;overflow:hidden}" +
               "video{width:100%;height:100%;object-fit:contain;background:#000}video::-webkit-media-controls,video::-webkit-media-controls-overlay-play-button,video::-webkit-media-controls-start-playback-button{display:none!important}</style></head><body>" +
               "<video id='v' src='" + src + "' autoplay playsinline" + (sound ? "" : " muted") + (loop ? " loop" : "") + "></video><script>" +
               "const v=document.getElementById('v');" +
               "v.addEventListener('ended',()=>chrome.webview.postMessage('ended'));" +
               "v.addEventListener('error',()=>chrome.webview.postMessage('error ' + (v.error ? v.error.code : '')));" +
               // Sound refused by the browser: play silently rather than not at all.
               "v.play().catch(()=>{v.muted=true;v.play().catch(e=>chrome.webview.postMessage('error ' + e.name));});" +
               "</script></body></html>";
    }

    private void Count(bool on)
    {
        if (on == counted) return;
        counted = on;
        Interlocked.Add(ref playingCount, on ? 1 : -1);
    }

    public void Dispose()
    {
        Count(false);
        disposed = true;
        View.Dispose();
    }
}
