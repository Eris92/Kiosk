using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

// Every P/Invoke in this assembly targets a Windows system DLL.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Kiosk.Client;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is 3 or 4 && args[0] == "--validate-config")
        {
            try
            {
                Configuration.Load(args[1], args.Length == 4 ? args[3] : null);
                File.WriteAllText(args[2], "OK");
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[2], ex.Message); }
                catch (Exception) { }
                return 1;
            }
        }
        if (args.Length == 1 && args[0] == CardService.Argument) return CardService.Run();
        ApplicationConfiguration.Initialize();
        // As the Windows shell the Kiosk must not vanish over one unexpected error in a click or a timer: it is logged.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Audit.Write("unhandled_error", e.Exception.GetType().Name + ": " + e.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Audit.Write("fatal_error", e.ExceptionObject.ToString() ?? "");
#if KIOSK_SELF_TEST
        if (args.Length > 0 && args[0] == "--app-host-fixture")
        {
            if (args.Length == 3 && args[1] == "--delay-ms" && int.TryParse(args[2], out var delayMilliseconds))
                Thread.Sleep(Math.Clamp(delayMilliseconds, 0, 10_000));
            using var fixture = new Form { Text = "Kiosk host test", Size = new Size(480, 320) };
            fixture.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = "Hosted application test" });
            Application.Run(fixture);
            return 0;
        }
#endif
        if (args.Length == 4 && args[0] == CardPin.Argument && long.TryParse(args[2], out var owner))
            return CardPin.Run(args[1], new IntPtr(owner), args[3]);
        if (args.Length == 2 && args[0] == CardPageHost.Argument) return CardPageHost.Run(args[1]);
        if (args.Length == 2 && args[0] == AdminSettings.Argument) return AdminSettings.Run(args[1]);
        if (args.Length is 1 or 2 && args[0] == ConfigManager.Argument) return ConfigManager.Run(args.Length == 2 ? args[1] : null);
        if (args.Length is 3 or 4 && args[0] == Configuration.SaveArgument) return Configuration.SaveElevated(args[1], args[2], args.Length == 4 ? args[3] : null);
        if (args.Length == 2 && args[0] == RdpUserHost.Argument) return RdpUserHost.Run(args[1]);
        if (args.Length == 1 && args[0] == FilesHost.Argument) return FilesHost.Run();
#if KIOSK_SELF_TEST
        bool selfTest = args.Length > 0 && args[0] == "--self-test";
#endif
        try
        {
#if KIOSK_SELF_TEST
            if (selfTest)
            {
                ConfigManager.SelfTest();
                CtapCard.SelfTest();
                if (CardService.Handle(new CardService.Request { Op = "health" }).Code != -2)
                    throw new InvalidOperationException("Card service health probe touched a reader or returned an unexpected response.");
                if (CardService.Handle(new CardService.Request { Op = "update" }).Code != -2 ||
                    CardService.Handle(new CardService.Request { Op = "dotnet" }).Code != -2)
                    throw new InvalidOperationException("Removed service update commands must be rejected before opening a card reader.");
                var pipeName = "KioskResponseTimeout-" + Guid.NewGuid().ToString("N");
                using (var silentServer = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
                using (var timeoutClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous))
                {
                    var accepting = silentServer.WaitForConnectionAsync();
                    timeoutClient.Connect(1000);
                    if (!accepting.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Synthetic pipe did not connect.");
                    using var serverReader = new StreamReader(silentServer, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
                    using var silentCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    var pendingRequest = BoundedPipeLine.ReadAsync(serverReader, 65536, silentCancellation.Token);
                    if (pendingRequest.IsCompleted)
                        throw new InvalidOperationException("A silent card service request occupied the listener instead of waiting asynchronously.");
                    try
                    {
                        pendingRequest.GetAwaiter().GetResult();
                        throw new InvalidOperationException("A silent card service request did not time out.");
                    }
                    catch (OperationCanceledException) { }
                    using var timeoutReader = new StreamReader(timeoutClient);
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        CardService.ReadLine(timeoutReader, TimeSpan.FromMilliseconds(250));
                        throw new InvalidOperationException("A silent card service response did not time out.");
                    }
                    catch (OperationCanceledException) when (elapsed.Elapsed < TimeSpan.FromSeconds(3)) { }
                }
                pipeName = "KioskImmediateReply-" + Guid.NewGuid().ToString("N");
                using (var replyServer = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 4096, 4096))
                using (var replyClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous))
                {
                    var accepting = replyServer.WaitForConnectionAsync();
                    replyClient.Connect(1000);
                    if (!accepting.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Synthetic reply pipe did not connect.");
                    var expected = new string('x', 3072);
                    replyServer.Write(System.Text.Encoding.UTF8.GetBytes(expected + "\n"));
                    replyServer.Flush();
                    replyServer.Dispose();
                    using var replyReader = new StreamReader(replyClient);
                    if (BoundedPipeLine.Read(replyReader, 4096, TimeSpan.FromSeconds(2)) != expected)
                        throw new InvalidOperationException("A buffered pipe reply was lost when the server closed.");
                }
                pipeName = "KioskDelayedReply-" + Guid.NewGuid().ToString("N");
                using (var delayedServer = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
                using (var delayedClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous))
                {
                    var accepting = delayedServer.WaitForConnectionAsync();
                    delayedClient.Connect(1000);
                    if (!accepting.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Delayed reply pipe did not connect.");
                    using var replyReader = new StreamReader(delayedClient, System.Text.Encoding.UTF8);
                    using var readingStarted = new ManualResetEventSlim();
                    var reading = Task.Run(() => { readingStarted.Set(); return BoundedPipeLine.Read(replyReader, 16, TimeSpan.FromSeconds(2)); });
                    if (!readingStarted.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Delayed reply read did not start.");
                    Thread.Sleep(50);
                    delayedServer.Write(System.Text.Encoding.UTF8.GetBytes("OK\n"));
                    if (!reading.Wait(TimeSpan.FromSeconds(2)) || reading.Result != "OK")
                        throw new InvalidOperationException("A delayed pipe reply was not read.");
                }
                pipeName = "KioskAsyncImpersonation-" + Guid.NewGuid().ToString("N");
                using (var identityServer = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
                using (var identityClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Impersonation))
                {
                    var accepting = identityServer.WaitForConnectionAsync();
                    identityClient.Connect(1000);
                    if (!accepting.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Identity pipe did not connect.");
                    using var identityReader = new StreamReader(identityServer, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
                    using var identityTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var identityRead = BoundedPipeLine.ReadAsync(identityReader, 128, identityTimeout.Token);
                    identityClient.Write(System.Text.Encoding.UTF8.GetBytes("health\n"));
                    if (identityRead.GetAwaiter().GetResult() != "health")
                        throw new InvalidOperationException("An asynchronous request was not read.");
                    var impersonated = false;
                    identityServer.RunAsClient(() =>
                    {
                        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent(ifImpersonating: true);
                        impersonated = identity != null;
                    });
                    if (!impersonated) throw new InvalidOperationException("The asynchronous request lost client impersonation.");
                }
                var largestPageCommand = new string('x', 65535);
                using (var lineReader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(largestPageCommand + "\r\n"))))
                    if (BoundedPipeLine.Read(lineReader, 65535, TimeSpan.FromSeconds(2)) != largestPageCommand)
                        throw new InvalidOperationException("A valid page command was truncated.");
                using (var lineReader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(largestPageCommand + "x\n"))))
                {
                    try
                    {
                        BoundedPipeLine.Read(lineReader, 65535, TimeSpan.FromSeconds(2));
                        throw new InvalidOperationException("An oversized page command was accepted.");
                    }
                    catch (InvalidDataException) { }
                }
                pipeName = "KioskOversizedLine-" + Guid.NewGuid().ToString("N");
                using (var oversizedServer = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
                using (var oversizedClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous))
                {
                    var accepting = oversizedServer.WaitForConnectionAsync();
                    oversizedClient.Connect(1000);
                    if (!accepting.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Oversized-line pipe did not connect.");
                    var bytes = System.Text.Encoding.UTF8.GetBytes(largestPageCommand + "x");
                    var sending = oversizedClient.WriteAsync(bytes, 0, bytes.Length);
                    using var lineReader = new StreamReader(oversizedServer, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
                    try
                    {
                        BoundedPipeLine.Read(lineReader, 65535, TimeSpan.FromSeconds(5));
                        throw new InvalidOperationException("An oversized pipe line without a terminator was accepted.");
                    }
                    catch (InvalidDataException) { }
                    if (!sending.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Oversized-line sender remained blocked.");
                }
                using (var lineReader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1) + "\n"))))
                {
                    try
                    {
                        CardService.ReadLine(lineReader, TimeSpan.FromSeconds(2));
                        throw new InvalidOperationException("An oversized card service response was accepted.");
                    }
                    catch (InvalidDataException) { }
                }
                var configSnapshot = Path.Combine(Path.GetTempPath(), "kiosk-config-test-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var sample = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "client.json")))
                        ?? throw new InvalidOperationException("Self-test configuration is missing.");
                    File.WriteAllText(configSnapshot, JsonSerializer.Serialize(sample with { NewsFolder = "news" }));
                    var logicalPath = Path.Combine(Path.GetTempPath(), "kiosk-config-target-" + Guid.NewGuid().ToString("N"), "client.json");
                    var expectedFolder = Path.GetDirectoryName(Path.GetFullPath(logicalPath))!;
                    var resolved = Configuration.Load(configSnapshot, logicalPath);
                    if (resolved.Folder != expectedFolder || resolved.NewsFolder != Path.Combine(expectedFolder, "news"))
                        throw new InvalidOperationException("Configuration snapshot did not resolve relative paths at its destination.");
                }
                finally { if (File.Exists(configSnapshot)) File.Delete(configSnapshot); }
                CentralConfig.SecretWriteSmokeTest();
                if (!WindowsSession.RetentionExpired(1_000, 31_000, 30) ||
                    WindowsSession.RetentionExpired(1_000, 30_999, 30) ||
                    WindowsSession.CanResume(WindowsSession.Availability.Active, false, true) ||
                    !WindowsSession.CanResume(WindowsSession.Availability.Unknown, true, false))
                    throw new InvalidOperationException("Windows session retention or lock state verification failed.");
                var retry = new SignOutRetry();
                int attempts = 0;
                retry.Request("self_test", 1_000, () => { attempts++; throw new IOException("simulated failure"); }, out _);
                retry.Request("ignored", 2_000, () => attempts++, out _);
                retry.Request("ignored", 11_000, () => attempts++, out _);
                if (attempts != 2 || retry.Reason != "self_test")
                    throw new InvalidOperationException("Windows sign-out retry verification failed.");
                var ownProgram = Environment.ProcessPath ?? throw new InvalidOperationException("Self-test executable path is unavailable.");
                if (Watchdog.Request("unwatch", Environment.ProcessId, ownProgram, "").Code != 0 ||
                    Watchdog.Request("watch", Environment.ProcessId, ownProgram, "").Code != -2)
                    throw new InvalidOperationException("A late watchdog registration after unwatch was not rejected.");
                var csv = Stats.Csv([new Stats.Entry
                {
                    Time = DateTimeOffset.Now, Pc = "@machine", Kind = Stats.Option,
                    Name = " =1+1", Category = "\t+SUM(1;2)", Who = "-1+2"
                }, new Stats.Entry { Time = DateTimeOffset.Now, Name = "＝2+2", Category = "\rtext" }]);
                if (!csv.Contains("\"\t@machine\"", StringComparison.Ordinal) ||
                    !csv.Contains("\"\t =1+1\"", StringComparison.Ordinal) ||
                    !csv.Contains("\"\t\t+SUM(1;2)\"", StringComparison.Ordinal) ||
                    !csv.Contains("\"\t-1+2\"", StringComparison.Ordinal) ||
                    !csv.Contains("\"\t＝2+2\"", StringComparison.Ordinal) ||
                    !csv.Contains("\"\t\rtext\"", StringComparison.Ordinal))
                    throw new InvalidOperationException("Statistics CSV formula protection failed.");
                var readRoot = Path.Combine(Path.GetTempPath(), "kiosk-stats-test-" + Guid.NewGuid().ToString("N"));
                var computerFolder = Path.Combine(readRoot, "PC1");
                var nestedFolder = Path.Combine(computerFolder, "unrelated");
                var month = DateTimeOffset.Now;
                var monthName = month.ToString("yyyy-MM") + ".jsonl";
                var rootFile = Path.Combine(readRoot, monthName);
                var computerFile = Path.Combine(computerFolder, monthName);
                var nestedFile = Path.Combine(nestedFolder, monthName);
                try
                {
                    Directory.CreateDirectory(nestedFolder);
                    var line = JsonSerializer.Serialize(new Stats.Entry { Time = month, Kind = Stats.Option, Name = "test" }) + Environment.NewLine;
                    File.WriteAllText(rootFile, line);
                    File.WriteAllText(computerFile, line);
                    File.WriteAllText(nestedFile, line);
                    if (Stats.Read(readRoot, month.LocalDateTime.Date.AddDays(-1)).Count != 2)
                        throw new InvalidOperationException("Statistics reader traversed beyond its per-computer folders.");
                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    try
                    {
                        Stats.Read(readRoot, month.LocalDateTime.Date.AddDays(-1), cancelled.Token);
                        throw new InvalidOperationException("Cancelled statistics read continued.");
                    }
                    catch (OperationCanceledException) { }
                }
                finally
                {
                    foreach (var file in new[] { nestedFile, computerFile, rootFile }) if (File.Exists(file)) File.Delete(file);
                    foreach (var folder in new[] { nestedFolder, computerFolder, readRoot }) if (Directory.Exists(folder)) Directory.Delete(folder);
                }
                var exportRoot = Path.Combine(Path.GetTempPath(), "kiosk-stats-export-test-" + Guid.NewGuid().ToString("N"));
                var oldMonth = DateTime.Today.AddMonths(-2).ToString("yyyy-MM");
                var currentMonth = DateTime.Today.ToString("yyyy-MM");
                var exportFiles = new[]
                {
                    oldMonth + ".jsonl", "PC1-" + oldMonth + ".csv",
                    currentMonth + ".jsonl", "PC1-" + currentMonth + ".csv",
                    "PC2-" + oldMonth + ".csv", "report-" + oldMonth + ".csv", "notes.jsonl"
                };
                var abandonedTemporary = currentMonth + ".jsonl." + Guid.NewGuid().ToString("N") + ".tmp";
                var unrelatedTemporary = "report." + Guid.NewGuid().ToString("N") + ".tmp";
                var sourceFile = Path.Combine(exportRoot, "source.txt");
                try
                {
                    Directory.CreateDirectory(exportRoot);
                    foreach (var file in exportFiles) File.WriteAllText(Path.Combine(exportRoot, file), "test");
                    File.WriteAllText(Path.Combine(exportRoot, abandonedTemporary), "partial");
                    File.WriteAllText(Path.Combine(exportRoot, unrelatedTemporary), "keep");
                    CentralConfig.PruneStatsExport(exportRoot, "PC1", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentMonth });
                    if (exportFiles.Take(2).Any(file => File.Exists(Path.Combine(exportRoot, file))) ||
                        exportFiles.Skip(2).Any(file => !File.Exists(Path.Combine(exportRoot, file))) ||
                        File.Exists(Path.Combine(exportRoot, abandonedTemporary)) ||
                        !File.Exists(Path.Combine(exportRoot, unrelatedTemporary)))
                        throw new InvalidOperationException("Statistics export removed a file outside its own expired months.");
                    var publishedEntries = new[]
                    {
                        new Stats.Entry { Time = month.AddSeconds(-1), Kind = Stats.SignIn, Name = "published" },
                        new Stats.Entry { Time = month, Kind = Stats.Option, Name = "=SUM(1;2)" }
                    };
                    File.WriteAllLines(sourceFile,
                    [
                        JsonSerializer.Serialize(publishedEntries[0]), "{broken json",
                        JsonSerializer.Serialize(publishedEntries[1])
                    ]);
                    CentralConfig.PublishStatsMonth(sourceFile, exportRoot, "PC1", currentMonth);
                    var publishedJson = File.ReadAllText(Path.Combine(exportRoot, currentMonth + ".jsonl"));
                    var publishedCsv = File.ReadAllText(Path.Combine(exportRoot, "PC1-" + currentMonth + ".csv"));
                    if (!publishedJson.Contains("published", StringComparison.Ordinal) ||
                        publishedCsv != Stats.Csv(Stats.ReadFile(Path.Combine(exportRoot, currentMonth + ".jsonl"))) ||
                        publishedCsv != Stats.Csv(publishedEntries))
                        throw new InvalidOperationException("Streaming statistics export changed CSV contents or lost an event.");
                    try
                    {
                        CentralConfig.PublishStatsMonth(Path.Combine(exportRoot, "missing.jsonl"), exportRoot, "PC1", currentMonth);
                        throw new InvalidOperationException("Statistics export accepted a missing source.");
                    }
                    catch (FileNotFoundException) { }
                    if (File.ReadAllText(Path.Combine(exportRoot, currentMonth + ".jsonl")) != publishedJson ||
                        File.ReadAllText(Path.Combine(exportRoot, "PC1-" + currentMonth + ".csv")) != publishedCsv ||
                        !File.Exists(Path.Combine(exportRoot, unrelatedTemporary)) ||
                        Directory.EnumerateFiles(exportRoot, "*.tmp")
                            .Any(file => !Path.GetFileName(file).Equals(unrelatedTemporary, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Failed statistics export changed published files or left temporary files.");
                }
                finally
                {
                    if (File.Exists(sourceFile)) File.Delete(sourceFile);
                    foreach (var file in exportFiles.Append(abandonedTemporary).Append(unrelatedTemporary))
                    {
                        var exportPath = Path.Combine(exportRoot, file);
                        if (File.Exists(exportPath)) File.Delete(exportPath);
                    }
                    if (Directory.Exists(exportRoot)) Directory.Delete(exportRoot);
                }
                var copyRoot = Path.Combine(Path.GetTempPath(), "kiosk-central-copy-test-" + Guid.NewGuid().ToString("N"));
                var copySource = Path.Combine(copyRoot, "source");
                var configCopy = Path.Combine(copyRoot, "config");
                var newsCopy = Path.Combine(copyRoot, "news-copy");
                var copiedMonthName = DateTime.Today.ToString("yyyy-MM");
                var copiedMonth = copiedMonthName + ".jsonl";
                var copiedCsv = "PC-" + copiedMonthName + ".csv";
                var copiedTemporary = copiedMonth + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var newsEditLock = Path.Combine("news", News.FileName + ".edit.lock");
                var copiedFiles = new[] { "client.json", Path.Combine("news", "item.txt"), newsEditLock,
                    Path.Combine("Stats", "PC", copiedMonth), Path.Combine("Stats", "PC", copiedCsv),
                    Path.Combine("Stats", "PC", copiedTemporary), Path.Combine("Stats", "PC", "asset.txt"),
                    Path.Combine("Stats", "root-logo.txt"), "KioskKomunikaty.tmp" };
                try
                {
                    Directory.CreateDirectory(Path.Combine(copySource, "Stats", "PC"));
                    Directory.CreateDirectory(Path.Combine(copySource, "news"));
                    foreach (var file in copiedFiles) File.WriteAllText(Path.Combine(copySource, file), file);
                    // A save in the news editor holds this file without sharing. Sync must ignore it.
                    using (var held = new FileStream(Path.Combine(copySource, newsEditLock), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        CentralConfig.Copy(copySource, configCopy, skipStatistics: true);
                        CentralConfig.Copy(copySource, newsCopy, skipStatistics: false);
                    }
                    if (!File.Exists(Path.Combine(configCopy, "client.json")) ||
                        !File.Exists(Path.Combine(configCopy, "news", "item.txt")) ||
                        File.Exists(Path.Combine(configCopy, "Stats", "PC", copiedMonth)) ||
                        File.Exists(Path.Combine(configCopy, "Stats", "PC", copiedCsv)) ||
                        File.Exists(Path.Combine(configCopy, "Stats", "PC", copiedTemporary)) ||
                        !File.Exists(Path.Combine(configCopy, "Stats", "PC", "asset.txt")) ||
                        !File.Exists(Path.Combine(configCopy, "Stats", "root-logo.txt")) ||
                        File.Exists(Path.Combine(configCopy, "KioskKomunikaty.tmp")) ||
                        File.Exists(Path.Combine(configCopy, newsEditLock)) ||
                        File.Exists(Path.Combine(newsCopy, newsEditLock)) ||
                        !File.Exists(Path.Combine(newsCopy, "Stats", "PC", copiedMonth)))
                        throw new InvalidOperationException("Central configuration copy included monthly exports or lost required assets.");
                }
                finally
                {
                    foreach (var root in new[] { copySource, configCopy, newsCopy })
                    {
                        foreach (var file in copiedFiles)
                        {
                            var candidate = Path.Combine(root, file);
                            if (File.Exists(candidate)) File.Delete(candidate);
                        }
                        foreach (var folder in new[] { Path.Combine("Stats", "PC"), "Stats", "news" })
                        {
                            var candidate = Path.Combine(root, folder);
                            if (Directory.Exists(candidate)) Directory.Delete(candidate);
                        }
                        if (Directory.Exists(root)) Directory.Delete(root);
                    }
                    if (Directory.Exists(copyRoot)) Directory.Delete(copyRoot);
                }
                var sourceRoot = Path.Combine(Path.GetTempPath(), "kiosk-source-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var firstSource = Path.Combine(sourceRoot, "first");
                    var secondSource = Path.Combine(sourceRoot, "second");
                    var cache = Path.Combine(sourceRoot, "cache");
                    Directory.CreateDirectory(firstSource);
                    Directory.CreateDirectory(secondSource);
                    File.WriteAllText(Path.Combine(firstSource, CentralConfig.FileName), "{}");
                    File.WriteAllText(Path.Combine(secondSource, CentralConfig.FileName), "{}");
                    Directory.CreateDirectory(cache);
                    Task<string> pendingSync;
                    using (var held = new FileStream(Path.Combine(cache, ".sync.lock"), FileMode.OpenOrCreate,
                               FileAccess.ReadWrite, FileShare.None))
                    {
                        using var started = new ManualResetEventSlim();
                        pendingSync = Task.Run(() => { started.Set(); return CentralConfig.Sync(firstSource, cache); });
                        if (!started.Wait(TimeSpan.FromSeconds(5)) || pendingSync.Wait(TimeSpan.FromMilliseconds(150)))
                            throw new InvalidOperationException("Concurrent central sync ignored the cache lock.");
                    }
                    var snapshot = pendingSync.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (CentralConfig.Current(cache, source: firstSource) != snapshot ||
                        CentralConfig.Current(cache, source: secondSource) != null)
                        throw new InvalidOperationException("Central cache was reused for a different source.");
                    File.Delete(Path.Combine(snapshot, ".kiosk-source"));
                    if (CentralConfig.Current(cache, source: firstSource) != null)
                        throw new InvalidOperationException("Central cache without its source marker was accepted.");
                    File.WriteAllText(Path.Combine(snapshot, ".kiosk-source"),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstSource)));
                    File.WriteAllText(Path.Combine(firstSource, News.FileName), "{}");
                    File.WriteAllText(Path.Combine(secondSource, News.FileName), "{}");
                    var newsCache = Path.Combine(sourceRoot, "news-cache");
                    var newsSnapshot = CentralConfig.Sync(firstSource, newsCache, News.FileName);
                    if (CentralConfig.Current(newsCache, News.FileName, firstSource) != newsSnapshot ||
                        CentralConfig.Current(newsCache, News.FileName, secondSource) != null)
                        throw new InvalidOperationException("News cache was reused for a different source.");
                    var share = @"\\server\share";
                    if (CentralConfig.MapNewsSource(firstSource, firstSource, share) != share ||
                        CentralConfig.MapNewsSource(Path.Combine(firstSource, "news"), firstSource, share) != Path.Combine(share, "news") ||
                        CentralConfig.MapNewsSource(secondSource, firstSource, share) != secondSource)
                        throw new InvalidOperationException("News source mapping lost the central root or accepted a different folder.");
                    File.WriteAllText(Path.Combine(secondSource, ".kiosk-source"),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstSource)));
                    File.WriteAllText(Path.Combine(cache, "current.txt"), @"..\second");
                    if (CentralConfig.Current(cache, source: firstSource) != null)
                        throw new InvalidOperationException("Central cache pointer left its cache directory.");
                }
                finally { if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true); }
                using var form = new KioskForm(new Config { TestMode = true, RdpConnections = [new RdpConnection { Name = "Local", Server = "localhost" }] });
                var result = 1;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kiosk");
                        var versioned = Path.Combine(installedRoot, "ClientVersions", "release-0123456789abcdef0123456789abcdef", "Kiosk.Client.exe");
                        if (!SharedAccounts.IsInstalledKiosk(Path.Combine(installedRoot, "Kiosk.Client.exe")) ||
                            !SharedAccounts.IsInstalledKiosk(versioned) ||
                            SharedAccounts.IsInstalledKiosk(Path.Combine(installedRoot, "ClientVersions", "user-copy", "Kiosk.Client.exe")) ||
                            SharedAccounts.IsInstalledKiosk(Path.Combine(installedRoot, "Other", "Kiosk.Client.exe")))
                            throw new InvalidOperationException("Installed client path verification failed.");
                        if (!CardService.AuthorizedClient(versioned, administrator: false) ||
                            CardService.AuthorizedClient(Path.Combine(installedRoot, "Other", "Kiosk.Client.exe"), administrator: false) ||
                            !CardService.AuthorizedClient(null, administrator: true) ||
                            CardService.AuthorizedClient(null, administrator: false))
                            throw new InvalidOperationException("Card service client authorization failed.");
                        var authorizedAccounts = new Config
                        {
                            PageActions = [new PageAction { Url = "https://portal.example/login", Account = "Portal" }],
                            NewsSharePoint = "https://tenant.sharepoint.com/sites/news", NewsSharePointAccount = "News"
                        };
                        if (!SharedAccounts.AuthorizedUse(authorizedAccounts.Active(), new("Portal", "https://portal.example/login/form")) ||
                            SharedAccounts.AuthorizedUse(authorizedAccounts.Active(), new("Portal", "https://portal.example.evil.test/login")) ||
                            SharedAccounts.AuthorizedUse(authorizedAccounts.Active(), new("Portal", "https://portal.example/logins")) ||
                            SharedAccounts.AuthorizedUse(authorizedAccounts.Active(), new("Portal", "http://portal.example/login")) ||
                            SharedAccounts.AuthorizedUse(authorizedAccounts.Active(), new("Other", "https://portal.example/login")) ||
                            !SharedAccounts.AuthorizedUse(authorizedAccounts.Active(), new("News", authorizedAccounts.NewsSharePoint, News: true)) ||
                            SharedAccounts.AuthorizedUse(authorizedAccounts.Active(), new("News", "https://other.sharepoint.com/sites/news", News: true)) ||
                            NewsAccount.AuthorizedSite(authorizedAccounts, authorizedAccounts.NewsSharePoint) ||
                            !NewsAccount.AuthorizedSite(authorizedAccounts with { NewsSharePointAccount = "" }, authorizedAccounts.NewsSharePoint) ||
                            NewsAccount.AuthorizedSite(authorizedAccounts with { NewsSharePointAccount = "" }, "https://other.sharepoint.com/sites/news"))
                            throw new InvalidOperationException("Shared account was allowed for an unconfigured page or SharePoint site.");
                        SharedAccounts.CacheSelfTest();
                        form.SmokeTest();
                        await SharePointNewsSmokeTestAsync(form);
                        await form.EmbeddedSmokeTestAsync();
                        await form.ApplicationSmokeTestAsync();
                        await form.IdleScreenRecoverySmokeTestAsync();
                        await form.CommandSmokeTestAsync();
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), "Config migration and transactional configuration copy, atomic machine secret replacement, settings secret rollback and ACL, bounded CTAP NFC responses and CBOR parsing, bounded card-service pipe response and page-host request, buffered reply after server close, RDP sign-in modes, PCSC, taskbar, menu, lock/resume, per-card sessions (park/switch/return/expire), ordered card page commands, card PIN plumbing, browser, notes, asynchronous file navigation and file commands, bounded news file and reused news pictures, bounded SharePoint news response and request ordering, settings round-trip, responsive application launch, owned application window, idle screen recovery, watchdog late-watch rejection, bounded and cancellable statistics reading, atomic statistics export, safe export pruning, statistics CSV formula protection, bounded central configuration copy, background command and session cleanup passed.");
                        result = 0;
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), ex.ToString()); }
                    finally { form.FinishSelfTest(); form.Close(); }
                };
                Application.Run(form);
                return result;
            }
#endif
            // Without a path: the PC's shared configuration (Install-KioskShell.ps1) wins over the example next to the exe,
            // so every account and every copy of the Kiosk on this PC shows the same Konfiguracja.
            var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kiosk", "client.json");
            var path = args.Length > 0 ? Path.GetFullPath(args[0]) : File.Exists(shared) ? shared : Path.Combine(AppContext.BaseDirectory, "client.json");
            // Konfiguracja z serwera: the server's client.json and files are fetched first (CentralConfig).
            var loaded = CentralConfig.Load(path);
            var config = loaded.Effective;
            KioskTheme.SetAccent(config.AccentColor);
            using var instance = new Mutex(true, "Local\\Kiosk.Client", out var first);
            // "Uruchom ponownie": the previous copy may still be closing — wait for it a moment.
            if (!first)
            {
                try { first = instance.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { first = true; }
            }
            // Already running here (e.g. the watchdog and Windows both started it after a crash): this copy leaves quietly.
            if (!first) { Audit.Write("already_running"); return 0; }
            // A new installation asks once: this PC's own configuration, or a server's (FirstRunSetup).
            if (FirstRunSetup.RunIfNeeded(path, loaded.Local))
            {
                loaded = CentralConfig.Load(path);
                config = loaded.Effective;
                KioskTheme.SetAccent(config.AccentColor);
            }
            // Logi: this user's logs and old unpacked updates, without holding up the start.
            var logLimit = config.LogLimitMb;
            _ = Task.Run(() => { try { Retention.Run(logLimit, machine: false); } catch (Exception ex) { Audit.Write("log_cleanup_failed", ex.Message); } });
            Application.Run(new KioskForm(config, path, loaded));
            return 0;
        }
        catch (Exception ex)
        {
            Audit.Write("startup_error", ex.Message);
#if KIOSK_SELF_TEST
            if (selfTest) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), ex.ToString());
            else MessageBox.Show(ex.Message, "Kiosk startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
#else
            MessageBox.Show(ex.Message, "Kiosk startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
#endif
            return 1;
        }
    }

#if KIOSK_SELF_TEST
    private static async Task SharePointNewsSmokeTestAsync(Control owner)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        long simulatedNow = 10_000;
        using var news = new SharePointNews(owner, async (url, count) =>
        {
            calls.Add(url + "|" + count);
            if (calls.Count == 1)
            {
                started.SetResult();
                await release.Task;
            }
            return [new NewsItem { Title = url }];
        }, () => simulatedNow);
        var first = news.GetAsync("https://first.example", 1, "first-account");
        await started.Task;
        if (!news.Matches("https://first.example", 1, "first-account") || news.Matches("https://second.example", 2, "second-account"))
            throw new InvalidOperationException("SharePoint news accepted cache from a different configuration.");
        var duplicate = news.GetAsync("https://first.example", 1, "first-account");
        var changed = news.GetAsync("https://second.example", 2, "second-account");
        await Task.Yield();
        if (calls.Count != 1) throw new InvalidOperationException("SharePoint news requests overlapped in one WebView2 controller.");
        simulatedNow += 120_000; // A slow first request passed the normal refresh interval while callers queued.
        release.SetResult();
        var firstItems = await first;
        var duplicateItems = await duplicate;
        var changedItems = await changed;
        if (calls.Count != 2 || calls[0] != "https://first.example|1" || calls[1] != "https://second.example|2" ||
            !ReferenceEquals(firstItems, duplicateItems) || changedItems[0].Title != "https://second.example" ||
            !ReferenceEquals(news.Cached, changedItems) || !news.Matches("https://second.example", 2, "second-account"))
            throw new InvalidOperationException("SharePoint news cache did not follow the latest configuration.");

        var startedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closing = new SharePointNews(owner, async (_, _) =>
        {
            startedAgain.SetResult();
            await finish.Task;
            return [new NewsItem { Title = "obsolete" }];
        });
        var active = closing.GetAsync("https://old.example", 1);
        await startedAgain.Task;
        var waiting = closing.GetAsync("https://new.example", 1);
        closing.Dispose();
        if ((await waiting.WaitAsync(TimeSpan.FromSeconds(2))).Count != 0)
            throw new InvalidOperationException("A disposed SharePoint source kept a waiting request.");
        finish.SetResult();
        if ((await active.WaitAsync(TimeSpan.FromSeconds(2))).Count != 0 || closing.Cached.Count != 0)
            throw new InvalidOperationException("A disposed SharePoint source published an obsolete result.");
    }
#endif
}

internal static class Audit
{
    internal static void Write(string action, string detail = "")
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiosk", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"),
                JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, action, detail }) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class RdpHost : AxHost
{
    // MsRdpClient9NotSafeForScripting, provided by Windows mstscax.dll.
    internal RdpHost() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD") { }
    internal dynamic Client => GetOcx() ?? throw new InvalidOperationException("Kontrolka RDP nie jest zainicjalizowana.");
}

internal sealed class CardReader : IDisposable
{
    private IntPtr context;
    private Native.ReaderState[] states = [];
    private uint[] evaluatedStates = [];
    private long listUpdatedAt;
    private long retryAfter;
    private string? evaluatedSelection;
    private string? cachedSnapshot;
    private bool evaluated;
    private bool selectedReaderMissing;
    private string status = "";
    internal string Status
    {
        get => status;
        private set
        {
            if (status == value) return;
            status = value;
            Audit.Write("reader_status", value);
        }
    }
    /// <summary>Reader holding the last detected card; used to check that card's PIN.</summary>
    internal string ActiveReader { get; private set; } = "";
    internal string ActiveAtr { get; private set; } = "";
    /// <summary>Names of the readers connected now (Konfiguracja offers them in a list); empty when none or no service.</summary>
    internal static string[] ListReaders()
    {
        if (Native.SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out var context) != 0) return [];
        try
        {
            uint size = 0;
            if (Native.SCardListReaders(context, null, null, ref size) != 0) return [];
            var names = new char[size];
            if (Native.SCardListReaders(context, null, names, ref size) != 0) return [];
            return new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        finally { Native.SCardReleaseContext(context); }
    }
    internal static bool IsUsableCard(uint state) =>
        (state & 0x20) != 0 && (state & (0x01 | 0x04 | 0x08 | 0x10 | 0x200)) == 0;

    internal enum SignedInCardState { Present, Missing, Changed }

    // The upper-word PC/SC event counter is part of Snapshot. An ATR or reader name alone
    // cannot distinguish two cards exchanged in the same reader.
    internal static SignedInCardState CompareSignedInCard(string? signedInSnapshot, string? observedSnapshot) =>
        signedInSnapshot == null ? SignedInCardState.Changed :
        observedSnapshot == null ? SignedInCardState.Missing :
        string.Equals(signedInSnapshot, observedSnapshot, StringComparison.Ordinal)
            ? SignedInCardState.Present : SignedInCardState.Changed;

    // "Any reader" skips the laptop's SIM slot (LTE modem: "Microsoft UICC ISO Reader …"): it always holds a card,
    // which would sign someone in with no card on the reader.
    internal static Native.ReaderState[] MonitoredReaders(Native.ReaderState[] states, string selected) =>
        selected.Length == 0 ? states.Where(s => s.Reader?.Contains("UICC", StringComparison.OrdinalIgnoreCase) != true).ToArray() :
        states.Where(s => string.Equals(s.Reader, selected, StringComparison.Ordinal)).ToArray();

    private static string Describe(Native.ReaderState state)
    {
        var flags = new (uint Bit, string Name)[]
        {
            (0x01, "IGNORE"), (0x04, "UNKNOWN"), (0x08, "UNAVAILABLE"),
            (0x10, "EMPTY"), (0x20, "PRESENT"), (0x80, "EXCLUSIVE"),
            (0x100, "INUSE"), (0x200, "MUTE"), (0x400, "UNPOWERED")
        };
        return state.Reader + " | 0x" + (state.EventState & 0xFFFF).ToString("X4") +
            " | " + string.Join(", ", flags.Where(f => (state.EventState & f.Bit) != 0).Select(f => f.Name));
    }
    internal string? Snapshot(string selected)
    {
        var now = Environment.TickCount64;
        // A failed PC/SC service/reader probe has no card to monitor. Retry its
        // setup on the topology cadence, rather than on every UI timer tick.
        if (now < retryAfter) return null;
        if (context == IntPtr.Zero)
        {
            var error = Native.SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out context);
            if (error != 0) { context = IntPtr.Zero; retryAfter = now + 1000; Status = "Smart Card service unavailable: " + error.ToString("X8"); return null; }
        }
        // Presence/removal is still checked on every call. Reader topology changes
        // much less often; reuse native buffers and the last evaluation in between.
        int result;
        // A configured reader may be unplugged. Re-enumerating it on every
        // 250 ms poll adds native work without improving the card state check;
        // discover topology changes on the one-second cadence instead.
        if (states.Length == 0 || now - listUpdatedAt >= 1000)
        {
            uint size = 0;
            result = Native.SCardListReaders(context, null, null, ref size);
            if (result != 0) { Reset(); retryAfter = now + 1000; Status = "No reader available: " + result.ToString("X8"); return null; }
            var names = new char[size];
            result = Native.SCardListReaders(context, null, names, ref size);
            if (result != 0) { Reset(); retryAfter = now + 1000; Status = "Reader enumeration failed"; return null; }
            var readers = new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (!readers.SequenceEqual(states.Select(s => s.Reader)))
            {
                states = readers.Select(n => new Native.ReaderState { Reader = n, Atr = new byte[36] }).ToArray();
                evaluatedStates = new uint[states.Length];
                evaluated = false;
            }
            listUpdatedAt = now;
        }
        if (states.Length == 0) { retryAfter = now + 1000; Status = "No reader available"; return null; }
        // If the configured reader is absent, card changes on other readers
        // cannot affect this selection; wait for the next topology refresh.
        if (evaluated && selectedReaderMissing && evaluatedSelection == selected && now - listUpdatedAt < 1000) return null;
        result = Native.SCardGetStatusChange(context, 0, states, (uint)states.Length);
        const int timeout = unchecked((int)0x8010000A);
        if (result != 0 && result != timeout) { Reset(); retryAfter = now + 1000; Status = "Reader status failed: " + result.ToString("X8"); return null; }
        bool changed = !evaluated || evaluatedSelection != selected;
        for (int i = 0; i < states.Length; i++)
        {
            // SCARD_STATE_CHANGED is transient; the upper word event counter is retained.
            var value = states[i].EventState & ~2u;
            if (evaluatedStates[i] != value) changed = true;
            // Keep SCARD_STATE_UNAWARE for an immediate non-waiting snapshot.
            // Asking to wait for a change with a zero timeout adds a PC/SC
            // service round-trip on the common unchanged path on some readers.
            evaluatedStates[i] = value;
        }
        if (!changed) return cachedSnapshot;
        evaluated = true;
        evaluatedSelection = selected;
        cachedSnapshot = null;
        ActiveReader = ActiveAtr = "";
        // ReaderName scopes presence/removal monitoring. Other readers may be virtual
        // (Windows Hello, UICC) and must not block the selected physical reader.
        var monitored = MonitoredReaders(states, selected);
        selectedReaderMissing = monitored.Length == 0;
        var present = monitored.Where(s => IsUsableCard(s.EventState)).ToArray();
        var diagnostics = string.Join("\n", states.Select(Describe));
        if (monitored.Length == 0)
        {
            Status = "Configured reader not found: " + selected + "\n" + diagnostics;
            return null;
        }
        if (present.Length != 1)
        {
            Status = (present.Length == 0 ? "No usable PCSC card detected" : "Multiple PCSC cards detected") + "\n" + diagnostics;
            return null;
        }
        var card = present[0];
        ActiveReader = card.Reader;
        ActiveAtr = Convert.ToHexString(card.Atr.AsSpan(0, checked((int)Math.Min(card.AtrLength, 36u))));
        Status = "Card detected\n" + diagnostics;
        // Upper word is the insertion/removal event counter; detects rapid remove/reinsert.
        // ATR is a change signal only, never an authenticated user identity.
        return cachedSnapshot = card.Reader + ":" + (card.EventState >> 16) + ":" + ActiveAtr;
    }
    /// <summary>
    /// Every reader holding a card now, the configured one or not (a USB security key is its own reader, e.g.
    /// "Yubico YubiKey OTP+FIDO+CCID 0", gone when the key is pulled out). The SIM slot never counts. Null = unknown.
    /// </summary>
    internal string[]? ReadersWithCard()
    {
        if (context == IntPtr.Zero && Native.SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out context) != 0) { context = IntPtr.Zero; return null; }
        uint size = 0;
        int result = Native.SCardListReaders(context, null, null, ref size);
        if (result == unchecked((int)0x8010002E)) return [];   // SCARD_E_NO_READERS_AVAILABLE: the last key was pulled out.
        if (result != 0) { Reset(); return null; }
        var names = new char[size];
        if (Native.SCardListReaders(context, null, names, ref size) != 0) { Reset(); return null; }
        var states = new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => new Native.ReaderState { Reader = n, Atr = new byte[36] }).ToArray();
        if (states.Length == 0) return [];
        if (Native.SCardGetStatusChange(context, 0, states, (uint)states.Length) != 0) { Reset(); return null; }
        return MonitoredReaders(states, "").Where(s => IsUsableCard(s.EventState)).Select(s => s.Reader).ToArray();
    }
    private void Reset()
    {
        if (context != IntPtr.Zero) Native.SCardReleaseContext(context);
        context = IntPtr.Zero; states = []; evaluatedStates = []; evaluated = false;
        retryAfter = 0; evaluatedSelection = null;
        selectedReaderMissing = false;
        cachedSnapshot = null; ActiveReader = ActiveAtr = "";
    }

    public void Dispose() => Reset();
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ReaderState
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Reader;
        public IntPtr UserData;
        public uint CurrentState;
        public uint EventState;
        public uint AtrLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] Atr;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput input);
    internal static uint IdleMilliseconds()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref input)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var idle = unchecked((uint)Environment.TickCount - input.Time);
        // A film in the messages keeps the display awake, which also restarts Windows' input clock:
        // while one plays, the time since the last real key or mouse/touch input is counted instead.
        if (!NewsVideo.AnyPlaying) return idle;
        // If input hooks could not start, retain the last trusted idle baseline. A film must
        // not reset the lock timer merely because Windows reports synthetic activity.
        InputWatch.Compare(idle);
        return Math.Max(idle, InputWatch.IdleMilliseconds);
    }
    [DllImport("winscard.dll")]
    internal static extern int SCardEstablishContext(uint scope, IntPtr reserved1, IntPtr reserved2, out IntPtr context);
    [DllImport("winscard.dll")]
    internal static extern int SCardReleaseContext(IntPtr context);
    [DllImport("winscard.dll", EntryPoint = "SCardListReadersW", CharSet = CharSet.Unicode)]
    internal static extern int SCardListReaders(IntPtr context, string? groups, [Out] char[]? readers, ref uint length);
    [DllImport("winscard.dll", EntryPoint = "SCardGetStatusChangeW", CharSet = CharSet.Unicode)]
    internal static extern int SCardGetStatusChange(IntPtr context, uint timeout, [In, Out] ReaderState[] states, uint count);
}
