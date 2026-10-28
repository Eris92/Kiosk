using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FidoRun
{
    // What a signed-in person's sign-in screen (KioskBroker) still starts for the Kiosk once the person may have walked
    // away (Kiosk mode keeps several people's sessions "parked"):
    //  - only while the card the person signed in with lies on a reader, or within GraceSeconds after it was last seen
    //    there (a click just before the card was taken away);
    //  - only programs from the PC's configuration: the file the Kiosk Card service wrote (--allowed), lines
    //    "P <program>" (any arguments: the Kiosk itself, Edge, the configured applications) and "C <command line>"
    //    (Menu → Polecenia, exactly that line: PowerShell or cmd with any other script is not one of them).
    // The card is told apart by its reader, ATR and serial number (GET DATA); a USB key is its own reader.
    internal static class BrokerPolicy
    {
        internal const int GraceSeconds = 10;
        // Windows resets the card while signing in: the card is looked for until then after the sign-in.
        private const int CaptureSeconds = 60;

        private static readonly object gate = new object();
        private static HashSet<string> allowedPrograms;
        private static HashSet<string> allowedCommands;
        private static HashSet<string> signedCards;
        private static DateTime signedInAt;
        private static DateTime lastSeen = DateTime.MinValue;
        private static Action<string> log;
        private static Thread watch;

        internal static void Configure(string file, Action<string> logger)
        {
            log = logger;
            if (file == null) return; // An administrator's own diagnostic run.
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            HashSet<string> programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in lines)
            {
                if (entry.StartsWith("P ", StringComparison.Ordinal)) programs.Add(Normalize(entry.Substring(2)));
                else if (entry.StartsWith("C ", StringComparison.Ordinal))
                {
                    int separator = entry.IndexOf('\t', 2);
                    if (separator > 2) commands.Add(Normalize(entry.Substring(2, separator - 2)) + "\t" + entry.Substring(separator + 1).Trim());
                }
            }
            allowedPrograms = programs;
            allowedCommands = commands;
            try { File.Delete(file); }
            catch (IOException error) { log("BrokerPolicy=AllowlistCleanupFailed; " + error.Message); }
            catch (UnauthorizedAccessException error) { log("BrokerPolicy=AllowlistCleanupFailed; " + error.Message); }
        }

        // The person signed in: the cards on the readers now are theirs (or the first ones seen soon after).
        internal static void SignedIn()
        {
            lock (gate)
            {
                signedInAt = DateTime.UtcNow;
                HashSet<string> now = Present();
                signedCards = now.Count > 0 ? now : null;
                if (signedCards != null) lastSeen = DateTime.UtcNow;
            }
            log("BrokerPolicy=SignedIn; Cards=" + (signedCards == null ? "0 (looking)" : signedCards.Count.ToString()));
            watch = new Thread(Watch) { IsBackground = true, Name = "BrokerPolicy card" };
            watch.Start();
        }

        // The person's card is checked every second, so a start right after it was taken away still finds it "seen".
        private static void Watch()
        {
            while (true)
            {
                HashSet<string> now = Present();
                lock (gate)
                {
                    if (signedCards == null && now.Count > 0 && (DateTime.UtcNow - signedInAt).TotalSeconds <= CaptureSeconds)
                    {
                        signedCards = now;
                        log("BrokerPolicy=CardFound; Cards=" + now.Count);
                    }
                    if (signedCards != null && now.Overlaps(signedCards)) lastSeen = DateTime.UtcNow;
                }
                Thread.Sleep(1000);
            }
        }

        // Null = allowed; otherwise why not.
        internal static string Check(string application, string commandLine)
        {
            lock (gate)
            {
                if (signedCards == null)
                {
                    // A key without a reader (FIDO over HID only): no card to look for, as in the Kiosk itself.
                    if ((DateTime.UtcNow - signedInAt).TotalSeconds > CaptureSeconds) log("BrokerPolicy=NoCardReader");
                }
                else if ((DateTime.UtcNow - lastSeen).TotalSeconds > GraceSeconds)
                    return "Karta osoby nie leży na czytniku: jej programy uruchamiają się tylko przy niej.";
            }
            if (allowedPrograms == null) return null;   // Started without the list (an administrator's own test).
            string program = Normalize(application);
            if (allowedPrograms.Contains(program) || allowedCommands.Contains(program + "\t" + commandLine.Trim())) return null;
            return "Tego programu nie ma w konfiguracji tego komputera: " + Path.GetFileName(application);
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path.Trim().Trim('"')); }
            catch (Exception) { return path; }
        }

        // ------------------------------------------------------------------ cards on the readers

        private static readonly Dictionary<string, string> serials = new Dictionary<string, string>(StringComparer.Ordinal);

        private static HashSet<string> Present()
        {
            HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
            IntPtr context;
            if (SCardEstablishContext(2, IntPtr.Zero, IntPtr.Zero, out context) != 0) return found;
            try
            {
                foreach (string reader in Readers(context))
                {
                    if (reader.IndexOf("UICC", StringComparison.OrdinalIgnoreCase) >= 0 || reader.IndexOf("SIM", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    ReaderState[] state = { new ReaderState { Reader = reader, Atr = new byte[36] } };
                    if (SCardGetStatusChange(context, 0, state, 1) != 0 || (state[0].EventState & 0x20) == 0) continue;   // SCARD_STATE_PRESENT
                    string atr = BitConverter.ToString(state[0].Atr, 0, (int)Math.Min(state[0].AtrLength, 36u));
                    // The serial is read once per card on the reader (the upper word counts insertions and removals),
                    // not every second while the person works with it.
                    string key = reader + "|" + atr + "|" + (state[0].EventState >> 16);
                    string serial;
                    lock (serials)
                        if (!serials.TryGetValue(key, out serial)) serials[key] = serial = Serial(context, reader);
                    found.Add(reader + "|" + atr + "|" + serial);
                }
            }
            finally { SCardReleaseContext(context); }
            return found;
        }

        private static string Serial(IntPtr context, string reader)
        {
            IntPtr card;
            uint protocol;
            if (SCardConnect(context, reader, 2, 3, out card, out protocol) != 0) return "";   // SHARED, T0 | T1
            try
            {
                IoRequest send = new IoRequest { Protocol = protocol, Length = 8 };
                byte[] command = { 0xFF, 0xCA, 0x00, 0x00, 0x00 }, response = new byte[258];
                int length = response.Length;
                if (SCardTransmit(card, ref send, command, command.Length, IntPtr.Zero, response, ref length) != 0 ||
                    length < 3 || response[length - 2] != 0x90 || response[length - 1] != 0x00) return "";
                return BitConverter.ToString(response, 0, length - 2);
            }
            finally { SCardDisconnect(card, 0); }   // SCARD_LEAVE_CARD
        }

        private static List<string> Readers(IntPtr context)
        {
            List<string> result = new List<string>();
            int length = 0;
            if (SCardListReaders(context, null, null, ref length) != 0 || length <= 0) return result;
            char[] buffer = new char[length];
            if (SCardListReaders(context, null, buffer, ref length) != 0) return result;
            foreach (string name in new string(buffer, 0, length).Split('\0')) if (name.Length > 0) result.Add(name);
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ReaderState
        {
            public string Reader;
            public IntPtr UserData;
            public uint CurrentState;
            public uint EventState;
            public uint AtrLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)] public byte[] Atr;
        }

        [StructLayout(LayoutKind.Sequential)] private struct IoRequest { public uint Protocol; public int Length; }

        [DllImport("winscard.dll")] private static extern int SCardEstablishContext(uint scope, IntPtr r1, IntPtr r2, out IntPtr context);
        [DllImport("winscard.dll")] private static extern int SCardReleaseContext(IntPtr context);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardListReadersW")]
        private static extern int SCardListReaders(IntPtr context, string groups, char[] readers, ref int length);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardGetStatusChangeW")]
        private static extern int SCardGetStatusChange(IntPtr context, int timeout, [In, Out] ReaderState[] states, int count);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode, EntryPoint = "SCardConnectW")]
        private static extern int SCardConnect(IntPtr context, string reader, uint share, uint protocols, out IntPtr card, out uint protocol);
        [DllImport("winscard.dll")] private static extern int SCardDisconnect(IntPtr card, uint disposition);
        [DllImport("winscard.dll")]
        private static extern int SCardTransmit(IntPtr card, ref IoRequest send, byte[] command, int commandLength, IntPtr receive, byte[] response, ref int responseLength);
    }
}
