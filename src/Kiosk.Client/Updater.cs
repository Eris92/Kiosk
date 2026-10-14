using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Kiosk.Client;

/// <summary>
/// Konfiguracja → Aktualizacja: downloads the latest GitHub Release (Kiosk-win-x64.zip) and lets the NEW copy replace
/// this one. The new Kiosk.Client.exe, started from the download folder with <see cref="Argument"/>, waits until this
/// process has closed, copies itself over the installation (asking for an administrator only when the folder needs
/// it), and starts the installed Kiosk again as the same user with the same arguments. Configuration files stay.
/// </summary>
internal static class Updater
{
    internal const string Argument = "--apply-update";
    private const string CopyArgument = "--apply-update-copy";
    internal const string ServiceRefreshArgument = "--apply-update-service";
    private const string Repository = "Eris92/Kiosk";
    private const string Asset = "Kiosk-win-x64.zip";
    private const string CardServiceName = "KioskCard";

    internal sealed record Release(string Tag, string DownloadUrl);

    /// <summary>The release tag this build was made from ("build-15"), or "dev" for a local build.</summary>
    internal static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            return version.StartsWith("build-", StringComparison.Ordinal) || version.StartsWith('v') ? version : "dev";
        }
    }

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kiosk", "1.0"));
        return client;
    }

    internal static async Task<Release> LatestAsync()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var json = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/" + Repository + "/releases/latest"));
        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == Asset)
                return new Release(tag, asset.GetProperty("browser_download_url").GetString()!);
        throw new InvalidDataException("Wydanie " + tag + " nie zawiera pliku " + Asset + ".");
    }

    /// <summary>Downloads and unpacks the release; returns the folder with the new Kiosk.Client.exe.</summary>
    internal static async Task<string> DownloadAsync(Release release, IProgress<int>? percent = null)
    {
        // Its own folder per download: the update at start and "Sprawdź i zaktualizuj" may run at the same time
        // (one folder for both ended in "Kiosk-win-x64.zip is being used by another process").
        var parent = Path.Combine(Path.GetTempPath(), "Kiosk-update");
        var root = Path.Combine(parent, release.Tag + "-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            foreach (var old in Directory.EnumerateDirectories(parent, release.Tag + "*"))
                if (old != root && Directory.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1))
                    try { Directory.Delete(old, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        var zip = Path.Combine(root, Asset);
        using (var client = Client())
        using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var download = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read, shown = -1;
            // A stalled connection fails after a minute instead of leaving "Pobieram…" on screen for good.
            using var stalled = new CancellationTokenSource();
            while (true)
            {
                stalled.CancelAfter(TimeSpan.FromSeconds(60));
                try { read = await download.ReadAsync(buffer, stalled.Token); }
                catch (OperationCanceledException) { throw new TimeoutException("Pobieranie stoi od minuty, sprawdź połączenie z internetem."); }
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0 && (int)(done * 100 / total) is var p && p != shown) percent?.Report(shown = p);
            }
        }
        var extracted = Path.Combine(root, "package");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, extracted)); // Off the window's thread: unpacking takes a while.
        var exe = Path.Combine(extracted, "out", "client", "Kiosk.Client.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("W paczce nie ma out\\client\\Kiosk.Client.exe.");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>Starts the downloaded copy, which installs itself once this process has exited. Close Kiosk right after.</summary>
    internal static void Launch(string newClient)
    {
        var target = AppContext.BaseDirectory.TrimEnd('\\');
        var arguments = new[] { Argument, Environment.ProcessId.ToString(), target }.Concat(Environment.GetCommandLineArgs().Skip(1));
        Process.Start(new ProcessStartInfo(Path.Combine(newClient, "Kiosk.Client.exe"))
        {
            UseShellExecute = false, WorkingDirectory = newClient, Arguments = Join(arguments)
        });
    }

    /// <summary>Kiosk.Client.exe --apply-update &lt;pid&gt; &lt;install folder&gt; [Kiosk arguments…]</summary>
    internal static int Apply(string[] args)
    {
        var target = args[2];
        var restart = args.Skip(3).ToArray();
        string? failure = null;
        try
        {
            try { using var old = Process.GetProcessById(int.Parse(args[1])); old.WaitForExit(60000); }
            catch (ArgumentException) { }
            if (IsElevated() || CanWrite(target) && (ServiceFolder() is not { } service || CanWrite(service))) Copy(target);
            else if (SameFolder(target, InstalledFolder) && CardService.Call(new CardService.Request { Op = "update" }) is { Code: 0 } installed)
            {
                // The Kiosk Card service (LocalSystem) installed the latest release itself: no administrator needed.
                Audit.Write("update_by_service", installed.Message);
            }
            else
            {
                // Program Files: only this step runs as administrator; Kiosk itself starts again as the signed-in user.
                using var copy = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory, Arguments = Join([CopyArgument, target])
                })!;
                copy.WaitForExit();
                if (copy.ExitCode != 0) failure = "Kopiowanie nowej wersji nie powiodło się (kod " + copy.ExitCode + ").";
            }
        }
        catch (System.ComponentModel.Win32Exception) { failure = "Aktualizacja wymaga zgody administratora Windows."; }
        catch (Exception ex) { failure = ex.Message; }
        Audit.Write(failure == null ? "update_applied" : "update_failed", failure ?? target);
        if (failure != null) MessageBox.Show(failure + "\nUruchamiam dotychczasową wersję.", "Aktualizacja Kiosku", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Process.Start(new ProcessStartInfo(Path.Combine(target, "Kiosk.Client.exe")) { UseShellExecute = false, WorkingDirectory = target, Arguments = Join(restart) });
        return failure == null ? 0 : 1;
    }

    /// <summary>Kiosk.Client.exe --apply-update-copy &lt;install folder&gt; (elevated).</summary>
    internal static bool IsCopy(string[] args) => args.Length == 2 && args[0] == CopyArgument;
    internal static int CopyElevated(string target)
    {
        try { Copy(target); return 0; }
        catch (Exception ex) { Audit.Write("update_failed", ex.Message); return 2; }
    }

    private static string InstalledFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kiosk");

    /// <summary>
    /// Kiosk Card service, "update": downloads the latest release of <see cref="Repository"/> itself (HTTPS, fixed
    /// address, into the service's own temp folder, which users cannot write) and copies it over Program Files\Kiosk.
    /// The caller passes nothing but the request, so a user can only ask for the official latest release, never an
    /// older one. The service's own folder is refreshed afterwards by a separate copy, because the service stops.
    /// </summary>
    internal static CardService.Response InstallLatest()
    {
        var release = LatestAsync().GetAwaiter().GetResult();
        if (Number(release.Tag) is { } latest && Number(CurrentVersion) is { } running && latest < running)
            return new CardService.Response { Code = -2, Message = "Najnowsze wydanie " + release.Tag + " jest starsze niż " + CurrentVersion + "." };
        var folder = DownloadAsync(release).GetAwaiter().GetResult();
        CopyFolder(folder, InstalledFolder);
        if (ServiceFolder() is { } service && !SameFolder(service, InstalledFolder))
            Process.Start(new ProcessStartInfo(Path.Combine(folder, "Kiosk.Client.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder, Arguments = Join([ServiceRefreshArgument])
            });
        Audit.Write("update_installed_by_service", release.Tag);
        return new CardService.Response { Message = release.Tag };
    }

    /// <summary>Kiosk.Client.exe --apply-update-service (started by the service): stop it, refresh its folder, start it.</summary>
    internal static int RefreshService()
    {
        if (!IsElevated() || ServiceFolder() is not { } service) return 2;
        try { RunSc("stop"); CopyFolder(AppContext.BaseDirectory, service); }
        catch (Exception ex) { Audit.Write("update_failed", ex.Message); }
        RunSc("start");
        return 0;
    }

    /// <summary>True when <paramref name="tag"/> is a later build than the running one (local "dev" builds never update
    /// themselves; a build named by its commit, e.g. build-4c800f4, counts as older than any numbered release).</summary>
    internal static bool IsNewer(string tag) => Number(tag) is { } latest &&
        (Number(CurrentVersion) is { } installed ? latest > installed : CurrentVersion.StartsWith("build-", StringComparison.Ordinal));

    /// <summary>Remembers that this release was already tried, so a failing install does not restart Kiosk at every start.</summary>
    internal static bool FirstAttempt(string tag)
    {
        var marker = Path.Combine(Path.GetTempPath(), "Kiosk-update", "attempted-" + tag);
        if (File.Exists(marker)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, DateTime.Now.ToString("O"));
        return true;
    }

    private static int? Number(string tag) =>
        tag.StartsWith("build-", StringComparison.Ordinal) && int.TryParse(tag[6..], out var n) ? n : null;

    private static void Copy(string target)
    {
        CopyFolder(AppContext.BaseDirectory, target);
        // The Kiosk Card service runs its own copy of Kiosk.Client.exe: stop it, refresh it, start it again.
        if (ServiceFolder() is { } service && IsElevated() && !SameFolder(service, target))
        {
            RunSc("stop");
            CopyFolder(AppContext.BaseDirectory, service);
            RunSc("start");
        }
    }

    private static void CopyFolder(string source, string target)
    {
        source = Path.GetFullPath(source);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            // The installed configuration stays; the package's own client.json is only an example.
            if (name.Equals("client.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".local.json", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("self-test", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            for (var attempt = 1; ; attempt++)
            {
                try { File.Copy(file, destination, true); break; }
                catch (IOException) when (attempt < 30) { Thread.Sleep(1000); }
            }
        }
    }

    private static string? ServiceFolder()
    {
        var image = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + CardServiceName, "ImagePath", null) as string;
        if (string.IsNullOrWhiteSpace(image)) return null;
        var exe = image.StartsWith('"') ? image[1..image.IndexOf('"', 1)] : image.Split(' ')[0];
        var folder = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(exe));
        return folder != null && Directory.Exists(folder) ? folder : null;
    }

    private static void RunSc(string verb)
    {
        using var sc = Process.Start(new ProcessStartInfo("sc.exe", verb + " " + CardServiceName) { UseShellExecute = false, CreateNoWindow = true })!;
        sc.WaitForExit(30000);
        if (verb == "stop") Thread.Sleep(3000);
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".update-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    private static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(a => "\"" + a.TrimEnd('\\') + "\""));
}
