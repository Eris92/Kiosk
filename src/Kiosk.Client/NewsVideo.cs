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
    internal WebView2 View { get; } = new() { Visible = false, DefaultBackgroundColor = KioskTheme.Surface };
    internal PictureBox Cover { get; } = new() { Visible = false, BackColor = KioskTheme.Surface, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Dictionary<string, Image> firstFrames = new(StringComparer.Ordinal);
    private int generation;
    internal event Action? Ended;
    private static int playingCount;
    /// <summary>Some film is on screen now (the lock and screensaver then count real input only, see <see cref="InputWatch"/>).</summary>
    internal static bool AnyPlaying => Volatile.Read(ref playingCount) > 0;
    private bool counted;
    internal event Action<string>? Failed;

    private Task? starting;
    private (string Source, bool Sound, bool Loop, Image? Poster)? pending;
    private string playing = "";
    private string playingSource = "";
    private bool disposed, broken;
    private bool requested;
    private bool frameShown;
    internal bool ShowingFrame(string source) => requested && frameShown && playingSource == source && View.Visible;
    internal void Pause()
    {
        try { if (View.CoreWebView2 != null) _ = View.CoreWebView2.ExecuteScriptAsync("document.querySelector('video')?.pause();"); }
        catch (Exception) { }
    }
    private readonly bool previewOnly;
    internal event Action<Image>? PreviewReady;
    internal NewsVideo(bool previewOnly = false) { this.previewOnly = previewOnly; }

    /// <summary>Starts the film (again only when it is another one); false while the browser is still starting.</summary>
    internal bool Play(string source, bool sound, bool loop, Image? poster = null)
    {
        if (broken) { View.Visible = false; Failed?.Invoke("brak przeglądarki do filmów (WebView2)"); return false; }
        requested = true;
        var key = source + "|" + sound + "|" + loop;
        if (key == playing) return true;
        Cover.Image = firstFrames.GetValueOrDefault(source) ?? poster;
        Cover.Visible = Cover.Image != null;
        // WebView2 must remain visible to produce frames and start media playback.
        View.Visible = true;
        View.BringToFront();
        Cover.BringToFront();
        if (View.CoreWebView2 == null)
        {
            // The browser starts once; the film follows when it is ready (a failed start was reported, the slide moves on by time).
            if (starting != null) { pending = (source, sound, loop, poster); return false; }
            pending = (source, sound, loop, poster);
            starting = StartAndPlayAsync();
            return false;
        }
        playing = key;
        frameShown = false;
        playingSource = source;
        var version = ++generation;
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
            View.CoreWebView2.NavigateToString(Page(address, sound, loop, version, previewOnly));
            if (!previewOnly) InputWatch.Start(initialIdle);
            Count(!previewOnly);
            return true;
        }
        catch (Exception ex) { Failed?.Invoke(ex.Message); return false; }
    }

    /// <summary>Stops the film and hides the browser (another slide, the carousel hidden).</summary>
    internal void Stop()
    {
        requested = false;
        frameShown = false;
        generation++;
        Cover.Visible = false;
        Cover.Image = null;
        pending = null;
        View.Visible = false;
        Count(false);
        if (playing.Length == 0 || View.CoreWebView2 == null) return;
        playing = "";
        try { _ = View.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('video').forEach(v=>{v.pause();v.style.visibility='hidden';v.removeAttribute('src');v.load();});"); }
        catch (Exception) { }
    }

    private async Task StartAndPlayAsync()
    {
        await StartAsync(); // Continues on the WinForms thread that called Play.
        if (disposed || !requested || View.CoreWebView2 == null || pending is not { } next) return;
        pending = null;
        Play(next.Source, next.Sound, next.Loop, next.Poster);
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
                var prefix = generation + ":";
                if (!requested || !message.StartsWith(prefix, StringComparison.Ordinal)) return;
                message = message[prefix.Length..];
                if (message.StartsWith("poster:", StringComparison.Ordinal))
                {
                    var version = generation;
                    try
                    {
                        var encoded = message[7..];
                        if (encoded.Length > 2_000_000) throw new InvalidDataException("Video preview exceeds the limit.");
                        using var preview = new MemoryStream(Convert.FromBase64String(encoded));
                        if (disposed || !requested || version != generation) return;
                        preview.Position = 0;
                        using var decoded = Image.FromStream(preview);
                        var image = new Bitmap(decoded);
                        var source = playingSource;
                        Cover.Image = null;
                        if (firstFrames.Remove(source, out var old)) old.Dispose();
                        if (firstFrames.Count >= 4) { var oldest = firstFrames.First(); firstFrames.Remove(oldest.Key); oldest.Value.Dispose(); }
                        firstFrames[source] = image; Cover.Image = image;
                        PreviewReady?.Invoke(image);
                    }
                    catch (Exception ex) { Audit.Write("video_preview_failed", ex.GetType().Name); }
                }
                else if (message == "playing") { frameShown = true; Cover.Visible = false; }
                else if (message == "preview-error" && previewOnly) Failed?.Invoke("film: podgląd niedostępny");
                else if (message == "ended") Ended?.Invoke();
                else if (message.StartsWith("error", StringComparison.Ordinal)) Failed?.Invoke("film: " + message);
            };
        }
        catch (Exception ex)
        {
            if (disposed) return;
            broken = true;
            View.Visible = false;
            Failed?.Invoke(ex.Message);   // Films are then skipped: their slides stay as long as pictures.
        }
    }

    private static string Page(string address, bool sound, bool loop, int generation, bool previewOnly)
    {
        var src = System.Net.WebUtility.HtmlEncode(address);
        var crossOrigin = address.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase) ? " crossorigin='anonymous'" : "";
        return "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;height:100%;background:#182336;overflow:hidden}" +
               "video{width:100%;height:100%;object-fit:contain;visibility:hidden;background:#182336}video::-webkit-media-controls,video::-webkit-media-controls-overlay-play-button,video::-webkit-media-controls-start-playback-button{display:none!important}</style></head><body>" +
               "<video id='v'" + crossOrigin + " src='" + src + "' preload='auto' playsinline" + (sound ? "" : " muted") + (loop ? " loop" : "") + "></video><script>" +
               "const v=document.getElementById('v');" +
               "const send=m=>chrome.webview.postMessage('" + generation + ":'+m);" +
               "v.addEventListener('loadeddata',async()=>{v.style.visibility='visible';" +
               (previewOnly ? "try{const c=document.createElement('canvas');c.width=640;c.height=360;const x=c.getContext('2d',{willReadFrequently:true});for(const t of [0,0.1,0.25,0.5,1,2]){if(t>0&&Number.isFinite(v.duration)){const p=Math.min(t,v.duration/2);if(Math.abs(v.currentTime-p)>0.01)await new Promise(r=>{v.addEventListener('seeked',r,{once:true});v.currentTime=p;});}x.fillStyle='#182336';x.fillRect(0,0,640,360);const s=Math.min(640/v.videoWidth,360/v.videoHeight);x.drawImage(v,(640-v.videoWidth*s)/2,(360-v.videoHeight*s)/2,v.videoWidth*s,v.videoHeight*s);const d=x.getImageData(0,0,640,360).data;let varied=false;for(let i=0;i<d.length;i+=128){if(d[i]!==d[0]||d[i+1]!==d[1]||d[i+2]!==d[2]){varied=true;break;}}if(varied||t===2){send('poster:'+c.toDataURL('image/png').split(',')[1]);break;}}}catch(e){send('preview-error');}" : "v.play().catch(()=>{v.muted=true;v.play().catch(()=>send('error playback'));});") + "},{once:true});" +
               "v.addEventListener('playing',()=>{const reveal=()=>{try{const c=document.createElement('canvas');c.width=32;c.height=18;const x=c.getContext('2d',{willReadFrequently:true});x.drawImage(v,0,0,32,18);const d=x.getImageData(0,0,32,18).data;let varied=false;for(let i=4;i<d.length;i+=4){if(d[i]!==d[0]||d[i+1]!==d[1]||d[i+2]!==d[2]){varied=true;break;}}if(!varied&&v.currentTime<2&&v.requestVideoFrameCallback){v.requestVideoFrameCallback(reveal);return;}}catch(e){}send('playing');};if(v.requestVideoFrameCallback)v.requestVideoFrameCallback(reveal);else reveal();});" +
               "v.addEventListener('ended',()=>send('ended'));" +
               "v.addEventListener('error',()=>send('error '+(v.error?v.error.code:'')));" +
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
        Cover.Image = null; Cover.Dispose();
        foreach (var image in firstFrames.Values) image.Dispose();
        firstFrames.Clear();
    }
}
