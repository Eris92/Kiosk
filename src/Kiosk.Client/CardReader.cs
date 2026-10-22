using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kiosk.Client;

internal sealed class CardReader : IDisposable
{
    private IntPtr context;
    private Native.ReaderState[] states = [];
    private uint[] evaluatedStates = [];
    private long listUpdatedAt;
    private string? evaluatedSelection;
    private string? cachedSnapshot;
    private bool evaluated;
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
        // Presence/removal is still checked on every call. Reader topology changes
        // much less often; reuse native buffers and the last evaluation in between.
        var now = Environment.TickCount64;
        int result;
        if (states.Length == 0 || now - listUpdatedAt >= 1000 || (selected.Length > 0 && !states.Any(s => s.Reader == selected)))
        {
            uint size = 0;
            result = Native.SCardListReaders(context, null, null, ref size);
            if (result != 0) { Reset(); Status = "No reader available: " + result.ToString("X8"); return null; }
            var names = new char[size];
            result = Native.SCardListReaders(context, null, names, ref size);
            if (result != 0) { Reset(); Status = "Reader enumeration failed"; return null; }
            var readers = new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (!readers.SequenceEqual(states.Select(s => s.Reader)))
            {
                states = readers.Select(n => new Native.ReaderState { Reader = n, Atr = new byte[36] }).ToArray();
                evaluatedStates = new uint[states.Length];
                evaluated = false;
            }
            listUpdatedAt = now;
        }
        if (states.Length == 0) { Status = "No reader available"; return null; }
        result = Native.SCardGetStatusChange(context, 0, states, (uint)states.Length);
        const int timeout = unchecked((int)0x8010000A);
        if (result != 0 && result != timeout) { Reset(); Status = "Reader status failed: " + result.ToString("X8"); return null; }
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
    private void Reset()
    {
        if (context != IntPtr.Zero) Native.SCardReleaseContext(context);
        context = IntPtr.Zero; states = []; evaluatedStates = []; evaluated = false;
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
