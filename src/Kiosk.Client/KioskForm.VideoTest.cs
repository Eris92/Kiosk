namespace Kiosk.Client;

internal sealed partial class KioskForm
{
    internal async Task VideoSmokeTestAsync()
    {
        using var player = new NewsVideo();
        using var window = new Form { ClientSize = new Size(640, 360), ShowInTaskbar = false, Text = "Kiosk media test" };
        player.View.Dock = player.Cover.Dock = DockStyle.Fill;
        window.Controls.Add(player.View);
        window.Controls.Add(player.Cover);
        window.Show();
        string? failure = null;
        player.Failed += message => failure = message;
        foreach (var file in new[] { "pierwsza-pomoc.mp4", "bhp-ochrona.mp4", "pierwsza-pomoc.mp4" })
        {
            var source = Path.Combine(AppContext.BaseDirectory, "news-example", file);
            if (!File.Exists(source)) throw new InvalidOperationException("Missing media test fixture: " + file);
            using var poster = await VideoPosters.FirstFrameAsync(source)
                ?? throw new InvalidOperationException("Video has no prepared first frame: " + file);
            poster.Save(Path.Combine(AppContext.BaseDirectory, "video-poster-" + file + ".png"));
            using (var pixels = new Bitmap(poster))
            {
                var sample = pixels.GetPixel(0, 0).ToArgb();
                bool varied = false;
                for (int y = 0; y < pixels.Height && !varied; y += 8)
                    for (int x = 0; x < pixels.Width; x += 8)
                        if (pixels.GetPixel(x, y).ToArgb() != sample) { varied = true; break; }
                if (!varied) throw new InvalidOperationException("Video poster contains only an empty background: " + file);
            }
            player.Play(source, false, true, poster);
            if (!player.Cover.Visible || player.Cover.Image == null)
                throw new InvalidOperationException("Video exposed an empty slot before playback: " + file);
            var timeout = Environment.TickCount64 + 20000;
            bool advancing = false;
            while (Environment.TickCount64 < timeout && failure == null)
            {
                await Task.Delay(100);
                if (player.View.CoreWebView2 == null) continue;
                var running = await player.View.CoreWebView2.ExecuteScriptAsync("(()=>{const m=document.querySelector('video');return !!m&&!m.paused&&m.currentTime>0.2&&m.videoWidth>0;})()");
                if (running == "true" && !player.Cover.Visible) { advancing = true; break; }
            }
            if (!advancing) throw new InvalidOperationException("Video did not produce visible moving frames: " + file + "; " + failure);
            player.Stop();
        }
        window.Close();
    }
}
