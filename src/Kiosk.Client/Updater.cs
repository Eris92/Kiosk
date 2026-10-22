using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
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

    internal sealed record Release(string Tag, string DownloadUrl, string? SignatureUrl = null);

    /// <summary>Konfiguracja → Aktualizacja → "Kanał": main = stabilne wydania, dev = także wersje testowe.</summary>
    internal static readonly string[] Channels = ["main", "dev"];
    internal static bool ValidChannel(string? channel) => channel != null && Channels.Contains(channel);
    private const string ChannelVariable = "KIOSK_UPDATE_CHANNEL";
    private static string channel = ValidChannel(Environment.GetEnvironmentVariable(ChannelVariable)) ? Environment.GetEnvironmentVariable(ChannelVariable)! : "main";

    /// <summary>The channel this Kiosk updates from (set from its configuration; the installer it starts inherits it).</summary>
    internal static string Channel
    {
        get => channel;
        set => channel = ValidChannel(value) ? value : "main";
    }

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

    /// <summary>
    /// The newest release of <see cref="Channel"/>: main = GitHub's "latest" (built from main), dev = the newest build-N
    /// of all, the test versions (pre-releases of the dev branches) included.
    /// </summary>
    internal static async Task<Release> LatestAsync()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (Channel != "dev")
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/" + Repository + "/releases/latest"));
            return FromJson(json.RootElement) ?? throw new InvalidDataException("Wydanie " + json.RootElement.GetProperty("tag_name").GetString() + " nie zawiera pliku " + Asset + ".");
        }
        using var list = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/" + Repository + "/releases?per_page=30"));
        return list.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean())
            .Select(FromJson).OfType<Release>()
            .Where(r => Number(r.Tag) != null)
            .OrderByDescending(r => Number(r.Tag))
            .FirstOrDefault() ?? throw new InvalidDataException("Brak wydań Kiosku w kanale dev.");
    }

    private static Release? FromJson(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        string? zip = null, signature = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name == Asset) zip = asset.GetProperty("browser_download_url").GetString();
            else if (name == Asset + ".sig") signature = asset.GetProperty("browser_download_url").GetString();
        }
        return zip == null ? null : new Release(tag, zip, signature);
    }

    /// <summary>
    /// The public half of the key the build in GitHub signs each package with (secret KIOSK_SIGNING_KEY). The signature
    /// covers the tag and the package's SHA-256, so an old package cannot pass as a new release either.
    /// The owner's key (scripts\New-KioskSigningKey.ps1); its private half is the GitHub secret KIOSK_SIGNING_KEY.
    /// </summary>
    private const string SigningKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAElQiilb9sgWxDGnrOAXqS7K25kgnC
        KX9+i5+46zCT5bptnDLwowE4x2r/iqCYY7Gampw6iTFdpmqqR2iJXT03NQ==
        -----END PUBLIC KEY-----
        """;

    private static async Task VerifySignatureAsync(Release release, string zip)
    {
        if (SigningKey.Length == 0) { Audit.Write("update_unsigned_allowed", release.Tag); return; }
        if (release.SignatureUrl == null) throw new InvalidDataException("Wydanie " + release.Tag + " nie jest podpisane – nie instaluję go.");
        using var client = Client();
        var text = (await client.GetStringAsync(release.SignatureUrl)).Trim();
        byte[] signature;
        try { signature = Convert.FromBase64String(text); }
        catch (FormatException) { throw new InvalidDataException("Podpis wydania " + release.Tag + " jest uszkodzony."); }
        byte[] hash;
        await using (var file = File.OpenRead(zip)) hash = await SHA256.HashDataAsync(file);
        var signed = System.Text.Encoding.UTF8.GetBytes(release.Tag + "\n" + Convert.ToHexString(hash).ToLowerInvariant());
        using var key = ECDsa.Create();
        key.ImportFromPem(SigningKey);
        if (!key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            Audit.Write("update_signature_bad", release.Tag);
            throw new InvalidDataException("Podpis wydania " + release.Tag + " się nie zgadza – nie instaluję go. Gdy ta wersja Kiosku ma inny klucz, zainstaluj raz paczkę z GitHub ręcznie (Install-KioskShell.ps1).");
        }
        Audit.Write("update_signature_ok", release.Tag);
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
        // Only a package signed in GitHub with the Kiosk's key is installed: not a changed or swapped file.
        await VerifySignatureAsync(release, zip);
        var extracted = Path.Combine(root, "package");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, extracted)); // Off the window's thread: unpacking takes a while.
        var exe = Path.Combine(extracted, "out", "client", "Kiosk.Client.exe");
        if (!File.Exists(exe)) throw new InvalidDataException("W paczce nie ma out\\client\\Kiosk.Client.exe.");
        // Without the .NET it needs the new copy would not even start, and this Kiosk would already be closed:
        // installed first (as administrator, or by the Kiosk Card service), else the update stops here.
        if (MissingRuntimes(Path.GetDirectoryName(exe)!) is [_, ..] missing)
        {
            Audit.Write("runtime_install_started", string.Join(", ", missing));
            var installed = await Task.Run(() => IsElevated() ? InstallRuntime() : CardService.Call(new CardService.Request { Op = "dotnet" }));
            if (installed.Code != 0 || MissingRuntimes(Path.GetDirectoryName(exe)!) is [_, ..])
                throw new InvalidDataException(release.Tag + " wymaga " + string.Join(", ", missing) + ", a instalacja się nie udała (" + installed.Message +
                                               "). Zainstaluj .NET 10 Desktop Runtime x64 (https://dotnet.microsoft.com/download/dotnet/10.0).");
        }
        return Path.GetDirectoryName(exe)!;
    }

    // Microsoft's list of .NET 10 releases: the newest Desktop Runtime installer and its SHA-512.
    private const string RuntimeReleases = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json";

    /// <summary>
    /// Installs the newest .NET 10 Desktop Runtime x64 (administrator or the Kiosk Card service): Microsoft's installer
    /// named in its release list, run only when its SHA-512 matches that list. No winget: it does not work in the kiosk
    /// shell or for the system account. Nothing of the caller's is used.
    /// </summary>
    internal static CardService.Response InstallRuntime()
    {
        if (!IsElevated()) return Failed("instalacja .NET wymaga administratora");
        var folder = Path.Combine(Path.GetTempPath(), "Kiosk-dotnet-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var client = Client();
            using var list = JsonDocument.Parse(client.GetStringAsync(RuntimeReleases).GetAwaiter().GetResult());
            var latest = list.RootElement.GetProperty("latest-release").GetString();
            var release = list.RootElement.GetProperty("releases").EnumerateArray().First(r => r.GetProperty("release-version").GetString() == latest);
            var desktop = release.GetProperty("windowsdesktop");
            var installer = desktop.GetProperty("files").EnumerateArray().First(f => f.GetProperty("name").GetString() == "windowsdesktop-runtime-win-x64.exe");
            var url = new Uri(installer.GetProperty("url").GetString()!);
            if (url.Scheme != "https" || !url.Host.Equals("builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase))
                return Failed("nieoczekiwany adres instalatora .NET: " + url.Host);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "windowsdesktop-runtime-win-x64.exe");
            using (var download = client.GetStreamAsync(url).GetAwaiter().GetResult())
            using (var target = File.Create(file))
                download.CopyTo(target);
            using (var stream = File.OpenRead(file))
                if (!Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(stream)).Equals(installer.GetProperty("hash").GetString(), StringComparison.OrdinalIgnoreCase))
                    return Failed("suma SHA-512 instalatora .NET się nie zgadza");
            var version = desktop.GetProperty("version").GetString();
            var code = Run(file, "/install /quiet /norestart");
            // 3010: installed, Windows wants a restart; 1638: a newer version is there already.
            if (!RuntimeInstalled()) return Failed("instalator .NET " + version + " zakończył się kodem " + code);
            Audit.Write("runtime_installed", version + ", kod " + code);
            return new CardService.Response { Message = ".NET Desktop Runtime " + version + " zainstalowany." };
        }
        catch (Exception ex) { return Failed(ex.Message); }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

        static CardService.Response Failed(string why)
        {
            Audit.Write("runtime_install_failed", why);
            return new CardService.Response { Code = -1, Message = why };
        }
    }

    private static bool RuntimeInstalled() =>
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App")) &&
        Directory.EnumerateDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"), "10.*").Any();

    private static int Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
            return process.WaitForExit(15 * 60000) ? process.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; }
    }

    /// <summary>The .NET frameworks the Kiosk.Client.exe in <paramref name="folder"/> needs and this PC lacks
    /// ("Microsoft.WindowsDesktop.App 10"); none for a self-contained build, which carries its own.</summary>
    internal static List<string> MissingRuntimes(string folder)
    {
        var missing = new List<string>();
        var config = Path.Combine(folder, "Kiosk.Client.runtimeconfig.json");
        if (!File.Exists(config) || File.Exists(Path.Combine(folder, "hostfxr.dll"))) return missing;
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        var options = json.RootElement.GetProperty("runtimeOptions");
        List<JsonElement> frameworks = options.TryGetProperty("frameworks", out var list) ? list.EnumerateArray().ToList()
            : options.TryGetProperty("framework", out var one) ? [one] : [];
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared");
        foreach (var framework in frameworks)
        {
            var name = framework.GetProperty("name").GetString() ?? "";
            var major = (framework.GetProperty("version").GetString() ?? "").Split('.')[0];
            var installed = Path.Combine(shared, name);
            if (!Directory.Exists(installed) || !Directory.EnumerateDirectories(installed, major + ".*").Any()) missing.Add(name + " " + major);
        }
        return missing;
    }

    private static bool launched;

    /// <summary>Starts the downloaded copy, which installs itself once this process has exited. Close Kiosk right after.</summary>
    internal static void Launch(string newClient)
    {
        // Ctrl+Alt+U and "Aktualizuj automatycznie" at the same time: one installer only (two Kiosks started after it).
        if (launched) return;
        launched = true;
        var target = AppContext.BaseDirectory.TrimEnd('\\');
        var arguments = new[] { Argument, Environment.ProcessId.ToString(), target }.Concat(Environment.GetCommandLineArgs().Skip(1));
        var start = new ProcessStartInfo(Path.Combine(newClient, "Kiosk.Client.exe"))
        {
            UseShellExecute = false, WorkingDirectory = newClient, Arguments = Join(arguments)
        };
        start.Environment[ChannelVariable] = Channel;   // The installer asks the service for the same channel.
        Process.Start(start);
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
            else if (SameFolder(target, InstalledFolder) && CardService.Call(new CardService.Request { Op = "update", Source = Channel }) is { Code: 0 } installed)
            {
                // The Kiosk Card service (LocalSystem) installed the latest release itself: no administrator needed.
                Audit.Write("update_by_service", installed.Message);
                // An older service leaves its own .NET in place; --apply-update-service removes it, Kiosk starts after that.
                for (var wait = 0; wait < 60 && File.Exists(Path.Combine(target, "hostfxr.dll")) && !File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll")); wait++)
                    Thread.Sleep(1000);
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
    internal static CardService.Response InstallLatest(string? requestedChannel = null)
    {
        Channel = requestedChannel ?? "main";   // main or dev only; anything else is main. Every package is still checked.
        var release = LatestAsync().GetAwaiter().GetResult();
        // Only official builds (build-N), never an older one than this.
        if (Number(release.Tag) is not { } latest)
            return new CardService.Response { Code = -2, Message = "Najnowsze wydanie " + release.Tag + " nie jest wydaniem Kiosku (build-N)." };
        if (Number(CurrentVersion) is { } running && latest < running)
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
        // The service that copied the files may be an older one, which leaves its own .NET behind.
        try { PruneRuntime(AppContext.BaseDirectory, InstalledFolder); }
        catch (Exception ex) { Audit.Write("update_failed", ex.Message); }
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

    private const string SetAside = ".kiosk-old-";

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
        // Files set aside by an earlier update, once nothing uses them any more.
        if (Directory.Exists(target))
            foreach (var old in Directory.EnumerateFiles(target, "*" + SetAside + "*", SearchOption.AllDirectories))
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
                catch (IOException) when (attempt < 5) { Thread.Sleep(1000); }
                catch (IOException) when (attempt == 5 && File.Exists(destination))
                {
                    // Still in use (the Kiosk of another signed-in person): Windows lets a running file be renamed, so it is
                    // set aside and the new one goes in its place. No half-old, half-new installation.
                    File.Move(destination, destination + SetAside + Guid.NewGuid().ToString("N")[..8]);
                }
            }
        }
        PruneRuntime(source, target);
    }

    /// <summary>
    /// After a self-contained Kiosk, one that uses the .NET installed in Windows: the old copy's own .NET files go.
    /// With hostfxr.dll left next to Kiosk.Client.exe Windows would still start it as self-contained, and it would fail.
    /// </summary>
    private static void PruneRuntime(string source, string target)
    {
        if (File.Exists(Path.Combine(source, "hostfxr.dll")) || !File.Exists(Path.Combine(target, "hostfxr.dll"))) return;
        var stale = Directory.EnumerateFiles(target)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("createdump.exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => !File.Exists(Path.Combine(source, Path.GetFileName(f))))
            .OrderBy(f => Path.GetFileName(f).Equals("hostfxr.dll", StringComparison.OrdinalIgnoreCase)) // Last: it marks the folder as not done yet.
            .ToList();
        foreach (var file in stale)
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Audit.Write("update_runtime_removed", target + " (" + stale.Count + ")");
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
        using var sc = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), verb + " " + CardServiceName) { UseShellExecute = false, CreateNoWindow = true })!;
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
