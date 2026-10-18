using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FidoRun
{
    internal sealed class Options
    {
        public string Mode = "authenticate";
        public string Scenario = "logon";
        public string UserArray = "empty";
        public string DisplayMode = "native";
        public string ExpectedSid;
        public bool UseAuthenticatedAccount;
        public bool WatchCard;
        public bool AccountPicker;
        public bool NativeAccount;
        public bool DiagnosticUi;
        public string StartUrl = "https://entra.microsoft.com/";
        // The full-screen PIN screen replaces the diagnostic window whenever the card is watched.
        public bool PinScreen { get { return (WatchCard || Broker) && !DiagnosticUi && Mode != "inspect"; } }
        // Started by the Kiosk Card service for the Kiosk process ClientPid (see KioskBroker).
        public bool Broker { get { return Mode == "kiosk-broker"; } }
        public int ClientPid;
        // The Kiosk asks for the PIN on its own screen and hands it over the pipe; this window stays invisible.
        public bool PinFromClient;
        public string ResolveTargetSid(string authenticatedSid)
        {
            string sid = new SecurityIdentifier(authenticatedSid).Value;
            if (ExpectedSid != null)
                return String.Equals(ExpectedSid, sid, StringComparison.OrdinalIgnoreCase) ? ExpectedSid : null;
            return UseAuthenticatedAccount ? sid : null;
        }
        public int TimeoutSeconds = 180;
        public bool Help;

        public static Options Parse(string[] args)
        {
            Options result = new Options();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index++)
            {
                string key = args[index].ToLowerInvariant();
                if (key == "--help" || key == "-h") { result.Help = true; continue; }
                if (key == "--account-picker") { result.AccountPicker = true; continue; }
                if (key == "--no-account-picker") { result.NativeAccount = true; continue; }
                if (key == "--kiosk") { result.WatchCard = true; continue; }
                if (key == "--diagnostic-ui") { result.DiagnosticUi = true; continue; }
                if (key == "--pin-from-client") { result.PinFromClient = true; continue; }
                if (!seen.Add(key)) throw new ArgumentException("Duplicate argument: " + key);
                if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + key);
                string value = args[++index];
                switch (key)
                {
                    case "--mode": result.Mode = value.ToLowerInvariant(); break;
                    case "--scenario": result.Scenario = value.ToLowerInvariant(); break;
                    case "--user-array": result.UserArray = value.ToLowerInvariant(); break;
                    case "--display-state": result.DisplayMode = value.ToLowerInvariant(); break;
                    case "--expected-sid": result.ExpectedSid = value; break;
                    case "--url": result.StartUrl = ParseStartUrl(value); break;
                    case "--client-pid":
                        if (!Int32.TryParse(value, out result.ClientPid) || result.ClientPid <= 0) throw new ArgumentException("Bad --client-pid.");
                        break;
                    case "--target":
                        if (!String.Equals(value, "authenticated", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("Target must be authenticated.");
                        result.UseAuthenticatedAccount = true; break;
                    case "--timeout":
                        int seconds;
                        if (!Int32.TryParse(value, out seconds) || seconds < 30 || seconds > 300)
                            throw new ArgumentException("Timeout must be between 30 and 300 seconds.");
                        result.TimeoutSeconds = seconds; break;
                    default: throw new ArgumentException("Unknown argument: " + key);
                }
            }
            // A local installation can choose the target account for double-click startup.
            // Explicit modes keep their command-line behavior, including authenticate-only.
            string targetFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FidoRun.target-sid");
            if (!result.Help && File.Exists(targetFile))
            {
                string configured = File.ReadAllText(targetFile).Trim();
                if (String.Equals(configured, "authenticated", StringComparison.OrdinalIgnoreCase))
                {
                    result.UseAuthenticatedAccount = true;
                    // The kiosk config (target-sid = authenticated) enables card-removal
                    // watching by default, so removing the key ends the session.
                    result.WatchCard = true;
                }
                else if (!seen.Contains("--mode") && result.ExpectedSid == null && !result.UseAuthenticatedAccount)
                    result.ExpectedSid = new SecurityIdentifier(configured).Value;
                if (!seen.Contains("--mode")) result.Mode = "launch-edge";
            }
            string urlFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FidoRun.start-url");
            if (!result.Help && !seen.Contains("--url") && File.Exists(urlFile))
            {
                string configuredUrl = File.ReadAllText(urlFile).Trim();
                if (configuredUrl.Length > 0) result.StartUrl = ParseStartUrl(configuredUrl);
            }
            if (result.Mode != "inspect" && result.Mode != "authenticate" && result.Mode != "launch-edge" && result.Mode != "diagnose" && result.Mode != "session" && result.Mode != "kiosk-broker")
                throw new ArgumentException("Mode must be inspect, authenticate, launch-edge, diagnose, session, or kiosk-broker.");
            if (result.Broker)
            {
                // The Kiosk watches the card and ends the session itself; the broker only signs in and starts programs.
                if (result.ClientPid <= 0) throw new ArgumentException("kiosk-broker requires --client-pid.");
                result.WatchCard = false;
                result.UseAuthenticatedAccount = true;
            }
            if (result.Scenario != "logon" && result.Scenario != "credui-current")
                throw new ArgumentException("Scenario must be logon or credui-current.");
            if (result.UserArray != "empty" && result.UserArray != "none")
                throw new ArgumentException("User array must be empty or none.");
            DisplayState.ForHost(result.DisplayMode, true);
            if (result.NativeAccount && result.AccountPicker)
                throw new ArgumentException("Choose either --account-picker or --no-account-picker.");
            if (!result.NativeAccount && (result.Mode == "launch-edge" || result.Mode == "session" || result.Broker))
                result.AccountPicker = true;
            if ((result.Mode == "launch-edge" || result.Mode == "session") && String.IsNullOrWhiteSpace(result.ExpectedSid) && !result.UseAuthenticatedAccount)
                throw new ArgumentException("launch-edge and session require --target authenticated or --expected-sid.");
            return result;
        }

        // Edge receives the address as one command-line argument, so only plain https URLs pass.
        public static string ParseStartUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps
                || uri.AbsoluteUri.IndexOfAny(new[] { ' ', '"', '\t' }) >= 0)
                throw new ArgumentException("Start URL must be an absolute https:// address.");
            return uri.AbsoluteUri;
        }
    }

    // Scoped to a single explicit user request and credential generation.
    // Provider callbacks alone cannot grant this continuation authority.
    internal sealed class SubmissionConsent
    {
        private readonly object gate = new object();
        private int generation;
        private bool waiting;
        public void Begin(int current)
        {
            if (current <= 0) throw new ArgumentOutOfRangeException("current");
            lock (gate) { generation = current; waiting = false; }
        }
        public void Wait(int current) { lock (gate) { if (generation == current && current > 0) waiting = true; } }
        public bool CanContinue(int current) { lock (gate) { return current > 0 && generation == current && waiting; } }
        public void Clear() { lock (gate) { generation = 0; waiting = false; } }
    }

    internal sealed class TraceLog : IDisposable
    {
        private readonly object gate = new object();
        private readonly StreamWriter writer;
        public readonly string PathName;
        public event Action<string> Line;
        private int lineCount;
        private bool disposed;
        public static string DisplayText(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            int length = Math.Min(value.Length, 512);
            if (length < value.Length && length > 0 && Char.IsHighSurrogate(value[length - 1])) length--;
            StringBuilder result = new StringBuilder(length + 3);
            for (int index = 0; index < length; index++)
            {
                char current = value[index];
                result.Append(Char.IsControl(current) ? ' ' : current);
            }
            if (length < value.Length) result.Append("...");
            return result.ToString();
        }
        public TraceLog()
        {
            string directory = Path.Combine(Path.GetTempPath(), "FidoRun");
            Directory.CreateDirectory(directory);
            PathName = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id + ".log");
            writer = new StreamWriter(new FileStream(PathName, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            writer.AutoFlush = true;
        }
        public void Write(string message)
        {
            string line = DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + message.Replace('\r', ' ').Replace('\n', ' ');
            lock (gate)
            {
                if (disposed) return;
                if (++lineCount > 10000) return;
                writer.WriteLine(line);
                Console.WriteLine(line);
            }
            Action<string> handler = Line;
            if (handler != null) { try { handler(line); } catch { } }
        }
        public void Dispose() { lock (gate) { disposed = true; writer.Dispose(); } }
    }

    internal static class Layout
    {
        public static void Check()
        {
            if (IntPtr.Size != 8 || Marshal.SizeOf(typeof(CredentialSerialization)) != 32 ||
                Marshal.OffsetOf(typeof(CredentialSerialization), "Buffer").ToInt32() != 24 ||
                Marshal.SizeOf(typeof(FieldDescriptor)) != 32 ||
                Marshal.SizeOf(typeof(LsaString)) != 16 || Marshal.SizeOf(typeof(Luid)) != 8 ||
                Marshal.SizeOf(typeof(TokenSource)) != 16 || Marshal.SizeOf(typeof(QuotaLimits)) != 48 ||
                Marshal.SizeOf(typeof(StartupInfo)) != 104)
                throw new InvalidOperationException("Unexpected x64 native structure layout.");
        }
    }

    internal static class Program
    {
        public static int Result = 2;
        private static long operationStarted;
        [ThreadStatic] private static int operationDepth;
        public static void BeginOperation()
        {
            if (operationDepth++ == 0) Interlocked.Exchange(ref operationStarted, DateTime.UtcNow.Ticks);
        }
        public static void EndOperation()
        {
            if (--operationDepth == 0) Interlocked.Exchange(ref operationStarted, 0);
        }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Options options = Options.Parse(args);
                if (options.Help)
                {
                    Console.WriteLine("FidoRun 0.1.29 experimental native Windows FIDO provider host (x64, .NET Framework 4.8).");
                    Console.WriteLine("FidoRun.exe [--mode inspect|authenticate|launch-edge|diagnose|session] [--scenario logon|credui-current]");
                    Console.WriteLine("            [--user-array empty|none] [--display-state native|legacy|none]");
                    Console.WriteLine("            [--expected-sid S-1-...] [--timeout 30..300] [--kiosk] [--diagnostic-ui] [--url https://...]");
                    Console.WriteLine("--kiosk watches the card reader: removing the card ends the session, closes the");
                    Console.WriteLine("        browser and relaunches to await the next card. Enabled by FidoRun.target-sid=authenticated.");
                    Console.WriteLine("        It shows the full-screen PIN screen and opens Edge right after sign-in.");
                    Console.WriteLine("--diagnostic-ui keeps the diagnostic window (fields, log, Open Edge button) in kiosk mode.");
                    Console.WriteLine("--url sets the page Edge opens (default https://entra.microsoft.com/, or FidoRun.start-url).");
                    Console.WriteLine("Without --mode, FidoRun.target-sid selects launch-edge; use authenticated to follow the signed-in key account.");
                    Console.WriteLine("--target authenticated uses the SID returned by Windows FIDO logon. --expected-sid restricts it to one account.");
                    Console.WriteLine("diagnose tests an internal CTAP RPC export without credential-provider callbacks or authentication.");
                    return 0;
                }
                if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
                    throw new PlatformNotSupportedException("Windows x64 is required.");
                if (!Environment.UserInteractive) throw new InvalidOperationException("An interactive desktop is required.");
                Layout.Check();
                if (options.ExpectedSid != null) options.ExpectedSid = new SecurityIdentifier(options.ExpectedSid).Value;
                WindowsLogon.StartUrl = options.StartUrl;
                using (TraceLog log = new TraceLog())
                using (ManualResetEvent finished = new ManualResetEvent(false))
                {
                    log.Write("FidoRun 0.1.29 experimental; Mode=" + options.Mode + "; Scenario=" + options.Scenario + "; UserArray=" + options.UserArray + "; DisplayState=" + options.DisplayMode);
                    NativeCtapProbe.DescribeCaller(log.Write);
                    log.Write("Native provider only. No password bootstrap, token-cache lookup, S4U, or stored Windows credentials.");
                    log.Write("Log file: " + log.PathName);
                    Thread watchdog = new Thread(delegate()
                    {
                        while (!finished.WaitOne(1000))
                        {
                            long started = Interlocked.Read(ref operationStarted);
                            if (started > 0 && DateTime.UtcNow.Ticks - started > TimeSpan.FromSeconds(options.TimeoutSeconds).Ticks)
                            {
                                log.Write("Outcome=Timeout; a native operation exceeded its deadline.");
                                Environment.Exit(124);
                            }
                        }
                    });
                    watchdog.IsBackground = true;
                    watchdog.Start();
                    try
                    {
                        if (options.Mode == "diagnose") Result = NativeCtapProbe.Run(log.Write);
                        else
                        {
                            if (options.Broker) KioskBroker.Start(options.ClientPid, log.Write);
                            Application.EnableVisualStyles();
                            Application.SetCompatibleTextRenderingDefault(false);
                            using (HostForm form = new HostForm(options, log)) Application.Run(form);
                            // Let the Kiosk read why the sign-in ended before this process goes.
                            if (options.Broker) { KioskBroker.Failed("Logowanie kartą zostało przerwane."); Thread.Sleep(1500); }
                        }
                    }
                    finally { finished.Set(); watchdog.Join(1000); }
                    log.Write("Outcome=" + (options.Mode == "diagnose" ? "DiagnosticCompleted" : "HostClosed") + "; ExitCode=" + Result);
                }
                return Result;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FidoRun: " + error.GetType().Name + ": " + error.Message);
                return 2;
            }
        }
    }
}
