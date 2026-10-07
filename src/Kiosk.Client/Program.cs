using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kiosk.Client;

internal sealed record Config
{
    public string Server { get; init; } = "";
    public int Port { get; init; } = 3389;
    public string ReaderName { get; init; } = "";
    public int IdleSeconds { get; init; } = 300;
    public int ConnectTimeoutSeconds { get; init; } = 60;
    public bool TestMode { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server) || Server.Any(char.IsWhiteSpace) ||
            Port is < 1 or > 65535 || IdleSeconds is < 10 or > 86400 ||
            ConnectTimeoutSeconds is < 10 or > 300)
            throw new InvalidDataException("Invalid server, port or timeout configuration.");
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool selfTest = args.Length > 0 && args[0] == "--self-test";
        try
        {
            if (selfTest)
            {
                using var form = new KioskForm(new Config { Server = "localhost" });
                form.Show();
                Application.DoEvents();
                form.SmokeTest();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), "COM settings, native layout and idle input passed.");
                return 0;
            }
            var path = args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "client.json") : Path.GetFullPath(args[0]);
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path),
                new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("Empty configuration.");
            config.Validate();
            using var instance = new Mutex(true, "Local\\Kiosk.Client", out var first);
            if (!first) throw new InvalidOperationException("Kiosk is already running in this session.");
            Application.Run(new KioskForm(config));
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

internal sealed class KioskForm : Form
{
    private readonly Config config;
    private readonly Panel surface = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 24) };
    private readonly Button connect = new() { Text = "Connect with smart card", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly CardReader reader = new();
    private RdpHost? rdp;
    private string? activeCard;
    private DateTime connectingSince;
    private bool wasConnected;
    private bool requireRemoval;
    private bool inTick;

    internal KioskForm(Config config)
    {
        this.config = config;
        Text = "Kiosk";
        WindowState = FormWindowState.Maximized;
        FormBorderStyle = config.TestMode ? FormBorderStyle.Sizable : FormBorderStyle.None;
        BackColor = Color.FromArgb(18, 25, 39);
        ForeColor = Color.White;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(8) };
        bar.Controls.Add(connect);
        var disconnect = new Button { Text = "Disconnect / change user", AutoSize = true };
        disconnect.Click += (_, _) => EndSession("manual_disconnect");
        bar.Controls.Add(disconnect);
        if (config.TestMode)
        {
            var exit = new Button { Text = "Exit test", AutoSize = true };
            exit.Click += (_, _) => Close();
            bar.Controls.Add(exit);
        }
        Controls.Add(surface);
        Controls.Add(bar);
        surface.Controls.Add(status);
        status.Text = "Insert smart card\nPIN is entered in the Windows credential dialog";
        connect.Click += (_, _) => BeginSession();
        timer.Tick += (_, _) => TickState();
        Shown += (_, _) => timer.Start();
        FormClosing += (_, _) => { timer.Stop(); EndSession("client_exit"); reader.Dispose(); };
    }

    private void TickState()
    {
        if (inTick) return;
        inTick = true;
        try
        {
            var card = reader.Snapshot(config.ReaderName);
            if (rdp != null)
            {
                if (card == null || card != activeCard) { EndSession("card_removed_or_changed"); return; }
                if (Native.IdleMilliseconds() >= config.IdleSeconds * 1000u) { EndSession("idle_timeout"); return; }
                int state = (int)rdp.Client.Connected;
                if (state == 1) wasConnected = true;
                if (wasConnected && state == 0) { EndSession("remote_disconnect"); return; }
                if (!wasConnected && DateTime.UtcNow - connectingSince > TimeSpan.FromSeconds(config.ConnectTimeoutSeconds))
                    EndSession("connection_timeout");
            }
            else
            {
                if (card == null) requireRemoval = false;
                connect.Enabled = card != null && !requireRemoval;
                status.Text = card == null ? "Insert one smart card\n" + reader.Status :
                    requireRemoval ? "Remove and reinsert the card to continue" : "Smart card detected\nSelect Connect and enter your PIN";
            }
        }
        catch (Exception ex)
        {
            if (rdp != null) EndSession("monitor_error");
            connect.Enabled = false;
            status.Text = "Connection blocked\n" + ex.Message;
        }
        finally { inTick = false; }
    }

    private void BeginSession()
    {
        try
        {
            if (rdp != null || requireRemoval) return;
            activeCard = reader.Snapshot(config.ReaderName) ?? throw new InvalidOperationException("Insert exactly one smart card.");
            dynamic shell = ConfigureRdp();
            // Fresh COM control per connection; never store a PIN/password or reuse credentials.
            connectingSince = DateTime.UtcNow;
            wasConnected = false;
            connect.Enabled = false;
            rdp!.BringToFront();
            shell.Launch();
            Audit.Write("connect_requested");
        }
        catch (Exception ex)
        {
            EndSession("connect_error");
            status.Text = "Connection error\n" + ex.Message;
            Audit.Write("connect_error", ex.Message);
            MessageBox.Show(this, ex.Message, "Connection error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private dynamic ConfigureRdp()
    {
    rdp = new RdpHost { Dock = DockStyle.Fill };
    ((ISupportInitialize)rdp).BeginInit();
    surface.Controls.Add(rdp);
    ((ISupportInitialize)rdp).EndInit();
    rdp.CreateControl();
    dynamic client = rdp.Client;
    client.Server = config.Server;
    client.DesktopWidth = Math.Max(800, surface.Width);
    client.DesktopHeight = Math.Max(600, surface.Height);
    dynamic settings = client.AdvancedSettings7;
    settings.RDPPort = config.Port;
    settings.EnableCredSspSupport = true;
    settings.AuthenticationLevel = 1; // Reject server authentication failures.
    settings.RedirectSmartCards = true;
    settings.RedirectDrives = false;
    settings.RedirectPrinters = false;
    settings.RedirectClipboard = false;
    settings.EnableAutoReconnect = false;
    // Credential prompting is configured through the RDP shell, not AdvancedSettings.
    dynamic shell = client.MsRdpClientShell;
    shell.RdpFileContents = string.Join("\r\n", new[]
    {
        $"full address:s:{config.Server}", $"server port:i:{config.Port}",
        "screen mode id:i:1", $"desktopwidth:i:{Math.Max(800, surface.Width)}",
        $"desktopheight:i:{Math.Max(600, surface.Height)}",
        "prompt for credentials:i:1", "promptcredentialonce:i:0",
        "enablecredsspsupport:i:1", "authentication level:i:1",
        "redirectsmartcards:i:1", "redirectclipboard:i:0", "redirectprinters:i:0",
        "drivestoredirect:s:", "devicestoredirect:s:",
        "autoreconnection enabled:i:0", "disableconnectionsharing:i:1"
    }) + "\r\n";
        return shell;
    }

    internal void SmokeTest()
    {
        if (Marshal.SizeOf<Native.ReaderState>() != 64 ||
            Marshal.OffsetOf<Native.ReaderState>(nameof(Native.ReaderState.EventState)).ToInt32() != 20)
            throw new InvalidOperationException("Unexpected PCSC x64 structure layout.");
        if (!CardReader.IsUsableCard(0x122) || !CardReader.IsUsableCard(0xA2) ||
            !CardReader.IsUsableCard(0x422) || CardReader.IsUsableCard(0x222) ||
            CardReader.IsUsableCard(0x12) || CardReader.IsUsableCard(0x28))
            throw new InvalidOperationException("PCSC card-state regression test failed.");
        var physicalAndVirtual = new[]
        {
            new Native.ReaderState { Reader = "ACS", EventState = 0x422, Atr = new byte[36] },
            new Native.ReaderState { Reader = "UICC", EventState = 0x22, Atr = new byte[36] },
            new Native.ReaderState { Reader = "Windows Hello", EventState = 0x422, Atr = new byte[36] }
        };
        if (CardReader.MonitoredReaders(physicalAndVirtual, "ACS").Count(s => CardReader.IsUsableCard(s.EventState)) != 1 ||
            CardReader.MonitoredReaders(physicalAndVirtual, "").Count(s => CardReader.IsUsableCard(s.EventState)) != 3 ||
            CardReader.MonitoredReaders(physicalAndVirtual, "Missing").Length != 0)
            throw new InvalidOperationException("PCSC reader-selection regression test failed.");
        physicalAndVirtual[0].EventState = 0x12;
        if (CardReader.MonitoredReaders(physicalAndVirtual, "ACS").Any(s => CardReader.IsUsableCard(s.EventState)))
            throw new InvalidOperationException("Virtual reader masked physical card removal.");
        _ = Native.IdleMilliseconds();
        _ = ConfigureRdp(); // Validate every dynamic COM setting without initiating a connection.
        if ((int)rdp!.Client.Connected != 0) throw new InvalidOperationException("Unexpected connection in smoke test.");
        EndSession("smoke_test");
    }

    private void EndSession(string reason)
    {
        var old = rdp;
        rdp = null;
        status.BringToFront(); // Hide the remote desktop before releasing the transport.
        activeCard = null;
        wasConnected = false;
        if (old == null) return;
        requireRemoval = true;
        try { old.Client.Disconnect(); }
        catch (Exception ex) { Audit.Write("disconnect_error", ex.Message); }
        finally { surface.Controls.Remove(old); old.Dispose(); }
        Audit.Write(reason);
    }
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
