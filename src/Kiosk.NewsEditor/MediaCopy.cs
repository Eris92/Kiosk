namespace Kiosk.NewsEditor;

/// <summary>Copy media without blocking the editor or exposing a partially copied file to kiosks.</summary>
internal static class MediaCopy
{
    internal static Task CopyAsync(string source, string destination, bool overwrite, Action<string>? validate, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                    FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
                    await output.FlushAsync(cancellation).ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                validate?.Invoke(temporary);
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellation);

#if KIOSK_SELF_TEST
    internal static async Task SelfTestAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "KioskMediaCopyTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "source.mp4");
            var destination = Path.Combine(folder, "target.mp4");
            File.WriteAllText(source, "new media");
            File.WriteAllText(destination, "old media");
            try { await CopyAsync(source, destination, true, _ => throw new InvalidDataException("invalid"), CancellationToken.None); }
            catch (InvalidDataException) { }
            if (File.ReadAllText(destination) != "old media" || Directory.EnumerateFiles(folder, "*.tmp").Any())
                throw new InvalidOperationException("Failed media validation changed the published file.");
            using var stop = new CancellationTokenSource();
            try { await CopyAsync(source, destination, true, _ => stop.Cancel(), stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            if (File.ReadAllText(destination) != "old media" || Directory.EnumerateFiles(folder, "*.tmp").Any())
                throw new InvalidOperationException("Cancelled media copy changed the published file.");
            await CopyAsync(source, destination, true, null, CancellationToken.None);
            if (File.ReadAllText(destination) != "new media")
                throw new InvalidOperationException("Completed media copy did not publish the new file.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
#endif
}
