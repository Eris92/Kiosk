using System.Text;

namespace Kiosk.Client;

/// <summary>Owns temporary WebView2 data and removes marked profiles after their exclusive lease has ended.</summary>
internal static class BrowserProfileCleanup
{
    private const string MarkerName = ".kiosk-browser-owner";
    private const string MarkerContent = "Kiosk.Client temporary WebView2 profile v1";

    internal sealed class OwnedProfile(string folder, FileStream lease) : IDisposable
    {
        internal string Folder { get; } = folder;
        public void Dispose() => lease.Dispose();
    }

    internal static OwnedProfile Create(string root)
    {
        Directory.CreateDirectory(root);
        if (IsReparsePoint(root)) throw new IOException("Folder profili przeglądarki jest dowiązaniem.");
        var folder = Path.Combine(root, Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (IsReparsePoint(folder)) throw new IOException("Profil przeglądarki jest dowiązaniem.");
        var lease = new FileStream(Path.Combine(folder, MarkerName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var marker = Encoding.UTF8.GetBytes(MarkerContent);
            lease.Write(marker);
            lease.Flush(flushToDisk: true);
            return new OwnedProfile(folder, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    internal static void RemoveStale(string root)
    {
        try
        {
            if (!Directory.Exists(root) || IsReparsePoint(root)) return;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var name = Path.GetFileName(folder);
                    var separator = name.IndexOf('-');
                    if (separator < 1 || !int.TryParse(name[..separator], out _) ||
                        !Guid.TryParseExact(name[(separator + 1)..], "N", out _) || IsReparsePoint(folder)) continue;
                    // A live Kiosk holds this marker open without sharing. Old folders without the marker
                    // (including the former PID-only layout) cannot be attributed to us and are left alone.
                    var marker = Path.Combine(folder, MarkerName);
                    if (!File.Exists(marker) || IsReparsePoint(marker) ||
                        File.ReadAllText(marker) != MarkerContent || ContainsReparsePoint(folder)) continue;
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ContainsReparsePoint(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        return false;
    }
}
