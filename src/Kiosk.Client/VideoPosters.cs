namespace Kiosk.Client;

/// <summary>Decode paused first frames before publishing slides, rather than showing an empty video slot.</summary>
internal static class VideoPosters
{
    private static readonly Dictionary<string, Image> cache = new(StringComparer.Ordinal);
    private sealed class PreviewWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    internal static async Task PopulateAsync(List<NewsItem> items, Func<bool> cancelled)
    {
        int prepared = 0;
        for (int i = 0; i < items.Count && !cancelled(); i++)
        {
            var item = items[i];
            if (!item.IsVideo || item.Picture != null || prepared++ >= 16) continue;
            var image = await FirstFrameAsync(item.VideoSource);
            if (image != null) items[i] = item with { Picture = image };
        }
    }

    internal static async Task<Image?> FirstFrameAsync(string source)
    {
        var key = source;
        if (File.Exists(source))
        {
            var info = new FileInfo(source);
            key += "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }
        if (cache.TryGetValue(key, out var existing)) return new Bitmap(existing);
        using var player = new NewsVideo(previewOnly: true);
        using var window = new PreviewWindow
        {
            ClientSize = new Size(640, 360), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000)
        };
        player.View.Dock = DockStyle.Fill;
        window.Controls.Add(player.View);
        var ready = new TaskCompletionSource<Image?>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PreviewReady += image => { if (!ready.Task.IsCompleted) ready.TrySetResult(new Bitmap(image)); };
        player.Failed += _ => ready.TrySetResult(null);
        window.Show();
        player.Play(source, false, false);
        try
        {
            var image = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (image == null) return null;
            if (cache.Remove(key, out var old)) old.Dispose();
            if (cache.Count >= 16) { var oldest = cache.First(); cache.Remove(oldest.Key); oldest.Value.Dispose(); }
            cache[key] = image;
            return new Bitmap(image);
        }
        catch (TimeoutException) { ready.TrySetResult(null); return null; }
        finally { window.Close(); }
    }
}
