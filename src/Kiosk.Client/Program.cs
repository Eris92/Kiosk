using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kiosk.Client;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--validate-config")
        {
            try
            {
                Configuration.Load(args[1]);
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
        if (args.Length > 0 && args[0] == "--app-host-fixture")
        {
            using var fixture = new Form { Text = "Kiosk host test", Size = new Size(480, 320) };
            fixture.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = "Hosted application test" });
            Application.Run(fixture);
            return 0;
        }
        if (args.Length == 4 && args[0] == CardPin.Argument && long.TryParse(args[2], out var owner))
            return CardPin.Run(args[1], new IntPtr(owner), args[3]);
        if (args.Length == 2 && args[0] == CardPageHost.Argument) return CardPageHost.Run(args[1]);
        if (args.Length == 2 && args[0] == AdminSettings.Argument) return AdminSettings.Run(args[1]);
        if (args.Length is 1 or 2 && args[0] == ConfigManager.Argument) return ConfigManager.Run(args.Length == 2 ? args[1] : null);
        if (args.Length is 3 or 4 && args[0] == Configuration.SaveArgument) return Configuration.SaveElevated(args[1], args[2], args.Length == 4 ? args[3] : null);
        if (args.Length == 2 && args[0] == RdpUserHost.Argument) return RdpUserHost.Run(args[1]);
        if (args.Length == 1 && args[0] == FilesHost.Argument) return FilesHost.Run();
        bool selfTest = args.Length > 0 && args[0] == "--self-test";
        try
        {
            if (selfTest)
            {
                if (CardService.Handle(new CardService.Request { Op = "health" }).Code != -2)
                    throw new InvalidOperationException("Card service health probe touched a reader or returned an unexpected response.");
                if (CardService.Handle(new CardService.Request { Op = "update" }).Code != -2 ||
                    CardService.Handle(new CardService.Request { Op = "dotnet" }).Code != -2)
                    throw new InvalidOperationException("Removed service update commands must be rejected before opening a card reader.");
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
                        form.SmokeTest();
                        await SharePointNewsSmokeTestAsync(form);
                        await form.EmbeddedSmokeTestAsync();
                        await form.ApplicationSmokeTestAsync();
                        await form.CommandSmokeTestAsync();
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), "Config migration, RDP sign-in modes, PCSC, taskbar, menu, lock/resume, per-card sessions (park/switch/return/expire), card PIN plumbing, browser, notes, SharePoint news request ordering, settings round-trip, owned application window, background command and session cleanup passed.");
                        result = 0;
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), ex.ToString()); }
                    finally { form.FinishSelfTest(); form.Close(); }
                };
                Application.Run(form);
                return result;
            }
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
            if (selfTest) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), ex.ToString());
            else MessageBox.Show(ex.Message, "Kiosk startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task SharePointNewsSmokeTestAsync(Control owner)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        using var news = new SharePointNews(owner, async (url, count) =>
        {
            calls.Add(url + "|" + count);
            if (calls.Count == 1)
            {
                started.SetResult();
                await release.Task;
            }
            return [new NewsItem { Title = url }];
        });
        var first = news.GetAsync("https://first.example", 1, "first-account");
        await started.Task;
        if (!news.Matches("https://first.example", 1, "first-account") || news.Matches("https://second.example", 2, "second-account"))
            throw new InvalidOperationException("SharePoint news accepted cache from a different configuration.");
        var duplicate = news.GetAsync("https://first.example", 1, "first-account");
        var changed = news.GetAsync("https://second.example", 2, "second-account");
        await Task.Yield();
        if (calls.Count != 1) throw new InvalidOperationException("SharePoint news requests overlapped in one WebView2 controller.");
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
        if (!NewsVideo.AnyPlaying || !InputWatch.Running) return idle;
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
