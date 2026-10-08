using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kiosk.Client;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "--app-host-fixture")
        {
            using var fixture = new Form { Text = "Kiosk host test", Size = new Size(480, 320) };
            fixture.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = "Hosted application test" });
            Application.Run(fixture);
            return 0;
        }
        if (args.Length == 3 && args[0] == CardPin.Argument && long.TryParse(args[2], out var owner))
            return CardPin.Run(args[1], new IntPtr(owner));
        if (args.Length == 1 && args[0] == CardService.Argument) return CardService.Run();
        if (args.Length == 2 && args[0] == "--ctap-info")
        {
            // Diagnostics: talks FIDO2 to the card in the given reader without a PIN (authenticatorGetInfo).
            try { using var card = new CtapCard(args[1]); Console.WriteLine(card.Info()); return 0; }
            catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }
        }
        if (args.Length >= 2 && args[0] == "--try-app")
        {
            // Diagnostic: host one application exactly as the Kiosk would, to check whether it can be embedded.
            using var host = new Form { Text = "Kiosk – test aplikacji", Size = new Size(1280, 800), StartPosition = FormStartPosition.CenterScreen };
            var view = new ApplicationView();
            host.Controls.Add(view);
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (_, _) => { if (view.Started && !view.Refresh()) host.Close(); };
            host.Shown += async (_, _) =>
            {
                try { await view.StartAsync(new ApplicationEntry { Name = "Test", Path = args[1], Arguments = string.Join(" ", args.Skip(2)) }); timer.Start(); }
                catch (Exception ex) { MessageBox.Show(host, ex.Message, "Test aplikacji"); host.Close(); }
            };
            Application.Run(host);
            return 0;
        }
        bool selfTest = args.Length > 0 && args[0] == "--self-test";
        try
        {
            if (selfTest)
            {
                using var form = new KioskForm(new Config { RdpConnections = [new RdpConnection { Name = "Local", Server = "localhost" }] });
                var result = 1;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        form.SmokeTest();
                        await form.EmbeddedSmokeTestAsync();
                        await form.ApplicationSmokeTestAsync();
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), "Config migration, RDP sign-in modes, PCSC, taskbar, menu, lock/resume, per-card sessions (park/switch/return/expire), card PIN plumbing, browser, notes, settings round-trip, owned application window and session cleanup passed.");
                        result = 0;
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), ex.ToString()); }
                    finally { form.FinishSelfTest(); form.Close(); }
                };
                Application.Run(form);
                return result;
            }
            var path = args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "client.json") : Path.GetFullPath(args[0]);
            var config = Configuration.Load(path);
            KioskTheme.SetAccent(config.AccentColor);
            using var instance = new Mutex(true, "Local\\Kiosk.Client", out var first);
            if (!first) throw new InvalidOperationException("Kiosk is already running in this session.");
            Application.Run(new KioskForm(config, path));
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
    internal dynamic Client => GetOcx();
}

internal sealed class CardReader : IDisposable
{
    private IntPtr context;
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
    internal static bool IsUsableCard(uint state) =>
        (state & 0x20) != 0 && (state & (0x01 | 0x04 | 0x08 | 0x10 | 0x200)) == 0;

    internal static Native.ReaderState[] MonitoredReaders(Native.ReaderState[] states, string selected) =>
        selected.Length == 0 ? states :
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
        if (context == IntPtr.Zero)
        {
            var error = Native.SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out context);
            if (error != 0) { context = IntPtr.Zero; Status = "Smart Card service unavailable: " + error.ToString("X8"); return null; }
        }
        uint size = 0;
        int result = Native.SCardListReaders(context, null, null, ref size);
        if (result != 0) { Reset(); Status = "No reader available: " + result.ToString("X8"); return null; }
        var names = new char[size];
        result = Native.SCardListReaders(context, null, names, ref size);
        if (result != 0) { Reset(); Status = "Reader enumeration failed"; return null; }
        var readers = new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var states = readers.Select(n => new Native.ReaderState { Reader = n, Atr = new byte[36] }).ToArray();
        if (states.Length == 0) { Status = "No reader available"; return null; }
        result = Native.SCardGetStatusChange(context, 0, states, (uint)states.Length);
        if (result != 0) { Reset(); Status = "Reader status failed: " + result.ToString("X8"); return null; }
        // ReaderName scopes presence/removal monitoring. Other readers may be virtual
        // (Windows Hello, UICC) and must not block the selected physical reader.
        var monitored = MonitoredReaders(states, selected);
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
        return card.Reader + ":" + (card.EventState >> 16) + ":" + Convert.ToHexString(card.Atr.AsSpan(0, checked((int)Math.Min(card.AtrLength, 36u))));
    }
    private void Reset() { if (context != IntPtr.Zero) Native.SCardReleaseContext(context); context = IntPtr.Zero; }
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
        return unchecked((uint)Environment.TickCount - input.Time);
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
