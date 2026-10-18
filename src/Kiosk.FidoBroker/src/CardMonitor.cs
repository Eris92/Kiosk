using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace FidoRun
{
    // Watches the smart-card subsystem (readers/NFC) and raises a one-shot callback when a
    // card that was present is removed. Used for the kiosk: pull the card, end the session.
    // Only observes reader state; it never reads card data or talks to the key.
    internal sealed class CardMonitor : IDisposable
    {
        private const uint SCARD_SCOPE_SYSTEM = 2;
        private const uint SCARD_STATE_UNAWARE = 0x0000;
        private const uint SCARD_STATE_CHANGED = 0x0002;
        private const uint SCARD_STATE_UNKNOWN = 0x0004;
        private const uint SCARD_STATE_UNAVAILABLE = 0x0008;
        private const uint SCARD_STATE_EMPTY = 0x0010;
        private const uint SCARD_STATE_PRESENT = 0x0020;
        private const int SCARD_E_TIMEOUT = unchecked((int)0x8010000A);
        private const int SCARD_E_CANCELLED = unchecked((int)0x80100002);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ScardReaderState
        {
            public string Reader;
            public IntPtr UserData;
            public uint CurrentState;
            public uint EventState;
            public uint AtrLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] Atr;
        }

        [DllImport("winscard.dll", SetLastError = true)]
        private static extern int SCardEstablishContext(uint scope, IntPtr r1, IntPtr r2, out IntPtr context);
        [DllImport("winscard.dll", SetLastError = true)]
        private static extern int SCardReleaseContext(IntPtr context);
        [DllImport("winscard.dll", SetLastError = true)]
        private static extern int SCardCancel(IntPtr context);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardListReadersW", SetLastError = true)]
        private static extern int SCardListReaders(IntPtr context, string groups, char[] readers, ref int readersLength);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardGetStatusChangeW", SetLastError = true)]
        private static extern int SCardGetStatusChange(IntPtr context, int timeout, [In, Out] ScardReaderState[] states, int count);

        private readonly Action onRemoved;
        private readonly Action<string> log;
        private IntPtr context;
        private Thread worker;
        private volatile bool stopping;
        private int fired;

        public CardMonitor(Action onRemoved, Action<string> log) { this.onRemoved = onRemoved; this.log = log; }

        public void Start()
        {
            if (SCardEstablishContext(SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out context) != 0)
            {
                log("CardMonitor=Unavailable; smart-card service not reachable.");
                return;
            }
            worker = new Thread(Run) { IsBackground = true, Name = "FidoRunCardMonitor" };
            worker.Start();
        }

        private List<string> ListReaders()
        {
            List<string> result = new List<string>();
            int length = 0;
            if (SCardListReaders(context, null, null, ref length) != 0 || length <= 0) return result;
            char[] buffer = new char[length];
            if (SCardListReaders(context, null, buffer, ref length) != 0) return result;
            int start = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\0')
                {
                    if (i > start) result.Add(new string(buffer, start, i - start));
                    start = i + 1;
                    if (i + 1 >= buffer.Length || buffer[i + 1] == '\0') break;
                }
            }
            return result;
        }

        // A UICC/SIM reader always reports a card present; it is not a removable security
        // card, so it must not keep the "card present" state latched.
        private static bool Ignored(string reader)
        {
            if (string.IsNullOrEmpty(reader)) return true;
            return reader.IndexOf("UICC", StringComparison.OrdinalIgnoreCase) >= 0
                || reader.IndexOf("SIM", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Run()
        {
            try
            {
                List<string> all = ListReaders();
                List<string> readers = new List<string>();
                foreach (string r in all) if (!Ignored(r)) readers.Add(r);
                log("CardMonitor=Watching; Readers=" + (readers.Count == 0 ? "none" : string.Join(" | ", readers.ToArray())));
                if (readers.Count == 0) { log("CardMonitor=NoReaders; card removal cannot be detected."); return; }

                ScardReaderState[] states = new ScardReaderState[readers.Count];
                bool[] hadCard = new bool[readers.Count];
                for (int i = 0; i < readers.Count; i++)
                    states[i] = new ScardReaderState { Reader = readers[i], CurrentState = SCARD_STATE_UNAWARE, Atr = new byte[36] };

                bool loggedInitial = false;
                while (!stopping)
                {
                    int status = SCardGetStatusChange(context, 1000, states, states.Length);
                    if (stopping) return;
                    if (status == SCARD_E_CANCELLED) return;
                    if (status == SCARD_E_TIMEOUT) { Ack(states); continue; }
                    if (status != 0)
                    {
                        if (SleepOrStop(800)) return;
                        all = ListReaders(); readers = new List<string>();
                        foreach (string r in all) if (!Ignored(r)) readers.Add(r);
                        if (readers.Count == 0) continue;
                        states = new ScardReaderState[readers.Count];
                        hadCard = new bool[readers.Count];
                        for (int i = 0; i < readers.Count; i++)
                            states[i] = new ScardReaderState { Reader = readers[i], CurrentState = SCARD_STATE_UNAWARE, Atr = new byte[36] };
                        continue;
                    }

                    // Per-reader: when a reader that HAD a card no longer has one, the card was
                    // pulled from that reader -> end the session.
                    for (int i = 0; i < states.Length; i++)
                    {
                        bool present = (states[i].EventState & SCARD_STATE_PRESENT) != 0;
                        if (hadCard[i] && !present)
                        {
                            if (Interlocked.Exchange(ref fired, 1) == 0)
                            {
                                log("CardMonitor=CardRemoved; Reader=" + states[i].Reader + "; ending session for the next user.");
                                try { onRemoved(); } catch (Exception error) { log("CardMonitor callback failed: " + error.Message); }
                            }
                            return;
                        }
                        hadCard[i] = present;
                    }

                    if (!loggedInitial)
                    {
                        bool anyPresent = false;
                        for (int i = 0; i < hadCard.Length; i++) if (hadCard[i]) anyPresent = true;
                        log("CardMonitor=InitialState; CardPresentOnWatchedReader=" + anyPresent);
                        loggedInitial = true;
                    }
                    Ack(states);
                }
            }
            catch (Exception error) { log("CardMonitor=Stopped; " + error.GetType().Name + "; " + error.Message); }
        }

        private static void Ack(ScardReaderState[] states)
        {
            for (int i = 0; i < states.Length; i++) states[i].CurrentState = states[i].EventState & ~SCARD_STATE_CHANGED;
        }

        private bool SleepOrStop(int ms)
        {
            for (int waited = 0; waited < ms; waited += 100) { if (stopping) return true; Thread.Sleep(100); }
            return stopping;
        }

        public void Dispose()
        {
            stopping = true;
            if (context != IntPtr.Zero) { try { SCardCancel(context); } catch { } }
            try { if (worker != null && worker.IsAlive) worker.Join(2000); } catch { }
            if (context != IntPtr.Zero) { try { SCardReleaseContext(context); } catch { } context = IntPtr.Zero; }
        }
    }
}
