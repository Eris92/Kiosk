using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FidoRun
{
    // Kiosk mode "--mode kiosk-broker --client-pid N": the Kiosk Card service starts this program as LocalSystem in
    // the Kiosk's own Windows session. It shows the PIN screen, signs the person in to Windows with the card (the same
    // native FIDO logon as FidoRun), keeps the token in this process only and starts programs as that person when the
    // Kiosk asks. Process and thread handles are duplicated into the Kiosk; the token itself never leaves this process.
    //
    // Pipe "KioskFido-<client pid>-<own pid>" (one per sign-in screen: an earlier one still running never answers for
    // this one), one JSON-free line per request, one line back:
    //   wait                    -> "OK <sid>\t<windows name>\t<card account>\t<admin 0|1>" once signed in, or "ERROR <text>"
    //   listening               -> "OK" once Windows' sign-in waits for the card (the Kiosk then wakes a card lying on the
    //                              reader), "ERROR <text>" after 40 s
    //   entered                 -> "OK 1" once the PIN went into Windows' field (the card was found), else "OK 0"
    //   pin\t<PIN>              -> (with --pin-from-client) types the PIN the Kiosk asked for into the native field and
    //                              submits it: "OK ..." as for wait, "RETRY <reason>" when the PIN is asked again
    //                              (wrong PIN), or "ERROR <text>"
    //   start\t<app>\t<command line>\t<directory>\t<show>
    //                           -> "OK <process handle>\t<thread handle>" (suspended, handles valid in the Kiosk process)
    //   close                   -> closes the person's programs started here and ends this process
    // Only the client process named on the command line (checked by Windows for every connection) may talk to it.
    internal static class KioskBroker
    {
        private static readonly object gate = new object();
        private static readonly ManualResetEvent finished = new ManualResetEvent(false);
        private static readonly ManualResetEvent listening = new ManualResetEvent(false);
        private static LogonOutcome retained;
        private static string result = "ERROR Logowanie kartą nie zostało zakończone.";
        private static Action<string> logger;
        private static int clientId;
        private static IntPtr clientProcess;
        // Every program started for the person, and everything those programs start (Edge's own processes), lives in
        // this job; closing it ends them all, whoever can or cannot open their tokens.
        private static IntPtr job;
        private static string clientSid;
        internal static HostForm View;
        internal static IntPtr ViewHandle;
        private static volatile bool stopping;
        private static volatile bool pinEntered;

        private static readonly int self = ProcessIdentity.Id;
        internal static string PipeName(int client) { return "KioskFido-" + client + "-" + self; }

        internal static void Start(int client, Action<string> log)
        {
            logger = log;
            clientId = client;
            clientProcess = OpenProcess(0x0040 | 0x00100000 | 0x1000, false, (uint)client); // DUP_HANDLE | SYNCHRONIZE | QUERY_LIMITED
            if (clientProcess == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "The Kiosk process could not be opened.");
            job = CreateJob();
            // Edge's sandboxed processes refuse even administrators unless SeDebugPrivilege is on; without it a closed
            // sign-in leaves the person's browser running.
            try { Process.EnterDebugMode(); } catch (Exception error) { log("KioskBroker=DebugPrivilege; " + error.Message); }
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            // This program's own account (LocalSystem from the service, an administrator otherwise) must be able to add pipe instances.
            using (WindowsIdentity self = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            SecurityIdentifier clientUser = ClientUser();
            clientSid = clientUser.Value;
            security.AddAccessRule(new PipeAccessRule(clientUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            Thread server = new Thread(delegate() { Serve(security); }) { IsBackground = true, Name = "KioskBroker pipe" };
            server.Start();
            Thread watch = new Thread(delegate()
            {
                // The Kiosk closed or crashed: nothing may keep running as the person.
                WaitForSingleObject(clientProcess, 0xFFFFFFFF);
                log("KioskBroker=ClientExited");
                End();
            }) { IsBackground = true, Name = "KioskBroker client watch" };
            watch.Start();
            ReplaceEarlier(client);
            log("KioskBroker=Ready; Pipe=" + PipeName(client) + "; ClientPid=" + client);
        }

        // One waiting sign-in screen per Kiosk: a new one ends any earlier screen nobody signed in with yet (left over
        // after a failed start or a lost answer, it would stay over the Kiosk and take the keyboard). Screens that
        // signed someone in keep running: they hold that person's programs.
        private static void ReplaceEarlier(int client)
        {
            EventWaitHandle newer;
            try { newer = new EventWaitHandle(false, EventResetMode.ManualReset, @"Global\KioskFidoNew-" + client); }
            catch (Exception error) { logger("KioskBroker=NoReplaceEvent; " + error.Message); return; }
            newer.Set();
            Thread.Sleep(500);
            newer.Reset();
            Thread replaced = new Thread(delegate()
            {
                while (!stopping)
                {
                    newer.WaitOne();
                    bool idle;
                    lock (gate) idle = retained == null && !finished.WaitOne(0);
                    if (idle) { logger("KioskBroker=ReplacedByNewer"); Failed("Zastąpione nowym logowaniem."); End(); return; }
                    Thread.Sleep(1000);   // Signed in: stays; past this signal before waiting again.
                }
            }) { IsBackground = true, Name = "KioskBroker replaced" };
            replaced.Start();
        }

        // Called on the provider thread after LSA created the person's token.
        internal static void SignedIn(LogonOutcome outcome, string targetSid, string account)
        {
            bool admin;
            using (WindowsIdentity identity = new WindowsIdentity(outcome.Token.DangerousGetHandle()))
                admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                    || HasGroup(identity, "S-1-5-32-544");
            lock (gate)
            {
                retained = WindowsLogon.CloneForSession(outcome, targetSid);
                // Programs of the same person left from an earlier sign-in (a crash) have lost their desktop access; a new
                // Edge would hand its page to them and show nothing.
                CloseLeftovers();
                result = "OK " + outcome.Sid + "\t" + Clean(outcome.Name) + "\t" + Clean(account) + "\t" + (admin ? "1" : "0") + "\t" + Clean(PersonName(outcome));
            }
            logger("KioskBroker=SignedIn; SID=" + outcome.Sid);
            BrokerPolicy.SignedIn();
            finished.Set();
        }

        internal static void Failed(string message)
        {
            // The first outcome counts: a later "window closed" must not overwrite the real reason.
            lock (gate) { if (retained == null && !finished.WaitOne(0)) result = "ERROR " + Clean(message); }
            finished.Set();
        }

        // The native tile asked for the card or its PIN, or is ready for input.
        internal static void ProviderListening() { listening.Set(); }

        private static string PersonName(LogonOutcome outcome)
        {
            try
            {
                using (WindowsIdentity identity = new WindowsIdentity(outcome.Token.DangerousGetHandle()))
                using (WindowsImpersonationContext context = identity.Impersonate())
                {
                    uint length = 512;
                    StringBuilder value = new StringBuilder((int)length);
                    if (GetUserNameEx(3, value, ref length)) return value.ToString(); // NameDisplay
                }
            }
            catch (Exception) { }
            return "";
        }

        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);

        // "wait" answers "ERROR UNKNOWN_KEY <Windows' text>": the Kiosk then sets up the card (PIN, registration)
        // instead of leaving Windows' "this security key is probably unknown" on the screen.
        internal const string UnknownKey = "UNKNOWN_KEY ";

        // The Windows credential provider refused to start on this PC. The Kiosk must not retry this session.
        internal const string Unsupported = "UNSUPPORTED ";

        // Windows' own text from the native tile; no status code says it, so the words of its message (Polish or English).
        internal static void ProviderMessage(string text)
        {
            if (View == null || String.IsNullOrEmpty(text)) return;   // Only in the Kiosk's broker mode.
            string lower = text.ToLowerInvariant();
            bool unknown = lower.Contains("nieznany") || lower.Contains("nie jest rozpoznawany") || lower.Contains("unknown") ||
                lower.Contains("isn't recognized") || lower.Contains("not recognized") || lower.Contains("isn't registered");
            if (!unknown || Completed) return;
            if (logger != null) logger("KioskBroker=UnknownKey");
            Failed(UnknownKey + text);
        }

        internal static bool Completed { get { return finished.WaitOne(0); } }

        // Never the Kiosk's own account: when the person signed in with the card is also the Windows user the Kiosk runs
        // as, closing "their programs" would close the Kiosk and the whole desktop.
        private static void CloseLeftovers()
        {
            if (retained == null || String.Equals(retained.Sid, clientSid, StringComparison.OrdinalIgnoreCase)) return;
            try { WindowsLogon.CloseProgramsForSession(retained, logger); } catch { }
        }

        private static bool HasGroup(WindowsIdentity identity, string sid)
        {
            if (identity.Groups == null) return false;
            foreach (IdentityReference group in identity.Groups) if (group.Value == sid) return true;
            return false;
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static SecurityIdentifier ClientUser()
        {
            SafeAccessTokenHandle token;
            if (!OpenProcessToken(clientProcess, 0x0008, out token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The Kiosk account could not be read.");
            using (token)
            using (WindowsIdentity identity = new WindowsIdentity(token.DangerousGetHandle()))
                return identity.User;
        }

        private static void Serve(PipeSecurity security)
        {
            while (!stopping)
            {
                try
                {
                    NamedPipeServerStream pipe = new NamedPipeServerStream(PipeName(clientId), PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
                    try { pipe.WaitForConnection(); }
                    catch { pipe.Dispose(); throw; }
                    uint caller;
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out caller) || caller != (uint)clientId)
                    {
                        logger("KioskBroker=RejectedCaller; Pid=" + caller);
                        pipe.Dispose();
                        continue;
                    }
                    // One request per connection; "wait" may block until the person has typed the PIN.
                    ThreadPool.QueueUserWorkItem(delegate { try { Answer(pipe); } finally { pipe.Dispose(); } });
                }
                catch (Exception error)
                {
                    if (stopping) return;
                    logger("KioskBroker=PipeError; " + error.GetType().Name + "; " + error.Message);
                    Thread.Sleep(200);
                }
            }
        }

        private static void Answer(NamedPipeServerStream pipe)
        {
            try
            {
                StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                string request = reader.ReadLine() ?? "";
                string response;
                try { response = Execute(request); }
                catch (Exception error) { logger("KioskBroker=CommandFailed; " + error.Message); response = "ERROR " + Clean(error.Message); }
                writer.WriteLine(response);
                pipe.WaitForPipeDrain();
                if (request == "close") End();
            }
            catch (Exception error) { logger("KioskBroker=AnswerFailed; " + error.GetType().Name); }
        }

        private static string Execute(string request)
        {
            string[] parts = request.Split('\t');
            switch (parts[0])
            {
                case "wait":
                    finished.WaitOne();
                    lock (gate) return result;
                case "listening":
                    if (WaitHandle.WaitAny(new WaitHandle[] { listening, finished }, 40000) == WaitHandle.WaitTimeout)
                        return "ERROR Logowanie kartą nie wystartowało.";
                    // The Kiosk (in the foreground) hands the foreground to this window: Windows reads the card only for it.
                    lock (gate) return finished.WaitOne(0) ? result : "OK " + ViewHandle.ToInt64();
                case "entered":
                    return pinEntered ? "OK 1" : "OK 0";
                case "pin":
                    if (parts.Length != 2 || parts[1].Length == 0) return "ERROR Bad pin request";
                    return EnterPin(parts[1]);
                case "start":
                    if (parts.Length != 5) return "ERROR Bad start request";
                    return StartProgram(parts[1], parts[2], parts[3].Length == 0 ? null : parts[3], (ushort)Int32.Parse(parts[4]));
                case "close":
                    return "OK";
                default:
                    return "ERROR Unknown command";
            }
        }

        private static string EnterPin(string pin)
        {
            HostForm view = View;
            if (view == null) return "ERROR Logowanie kartą nie przyjmuje PIN-u z Kiosku.";
            // The native tile shows its PIN field once it has found the card.
            DateTime until = DateTime.UtcNow.AddSeconds(45);
            int round = 0;
            while (!view.TryEnterPin(pin))
            {
                if (++round % 8 == 0) view.EnsureForeground("Waiting");
                view.TryAutoRetry();
                if (finished.WaitOne(250)) lock (gate) return result;
                if (DateTime.UtcNow > until) return "ERROR Karta nie odpowiada. Przyłóż ją jeszcze raz.";
            }
            pin = null;
            pinEntered = true;
            // Signed in, failed, or the tile asks for the PIN again (steadily, not just between two native steps).
            DateTime submitted = DateTime.UtcNow, limit = submitted.AddSeconds(120);
            DateTime? waitingSince = null;
            while (!finished.WaitOne(250))
            {
                string again = view.WaitingForPinAgain();
                if (again == null) waitingSince = null;
                else if (waitingSince == null) waitingSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - waitingSince.Value).TotalSeconds >= 2.5 && (DateTime.UtcNow - submitted).TotalSeconds >= 3)
                {
                    logger("KioskBroker=PinAskedAgain");
                    AllowSetForegroundWindow(clientId);   // The Kiosk asks for the PIN again on its own screen.
                    return "RETRY " + Clean(again);
                }
                if (DateTime.UtcNow > limit) return "ERROR Logowanie kartą nie odpowiada.";
            }
            lock (gate) return result;
        }

        private static string StartProgram(string application, string commandLine, string directory, ushort show)
        {
            LogonOutcome outcome;
            lock (gate) outcome = retained;
            if (outcome == null) return "ERROR Nikt nie jest zalogowany kartą.";
            if (!Path.IsPathRooted(application) || !File.Exists(application)) return "ERROR Program nie istnieje: " + application;
            // A parked session (the person walked away) starts nothing; and only programs of this PC's configuration.
            string refused = BrokerPolicy.Check(application, commandLine);
            if (refused != null)
            {
                logger("KioskBroker=StartRefused; Application=" + Path.GetFileName(application) + "; " + refused);
                return "ERROR " + Clean(refused);
            }
            ProcessInformation process = WindowsLogon.StartAsUser(outcome, application, commandLine, directory, show, logger);
            IntPtr me = GetCurrentProcess();
            IntPtr remoteProcess, remoteThread;
            try
            {
                // Still suspended: the program and all its children stay in the job.
                if (job != IntPtr.Zero && !AssignProcessToJobObject(job, process.Process))
                    logger("KioskBroker=JobAssignFailed; Error=" + Marshal.GetLastWin32Error());
                if (!DuplicateHandle(me, process.Process, clientProcess, out remoteProcess, 0, false, 2) ||  // DUPLICATE_SAME_ACCESS
                    !DuplicateHandle(me, process.Thread, clientProcess, out remoteThread, 0, false, 2))
                {
                    int error = Marshal.GetLastWin32Error();
                    TerminateProcess(process.Process, 1);
                    throw new Win32Exception(error, "Handing the program to the Kiosk failed.");
                }
            }
            finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
            logger("KioskBroker=Started; PID=" + process.ProcessId + "; Application=" + Path.GetFileName(application));
            return "OK " + remoteProcess.ToInt64() + "\t" + remoteThread.ToInt64();
        }

        private static void End()
        {
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                if (retained != null)
                {
                    if (job != IntPtr.Zero)
                    {
                        bool ended = TerminateJobObject(job, 1);
                        logger("KioskBroker=JobClosed; Ok=" + ended);
                    }
                    CloseLeftovers();
                    retained.Dispose();
                    retained = null;
                }
            }
            finished.Set();
            logger("KioskBroker=Ended");
            // GUI access leases are foreground threads; the person's programs are gone, so leave now.
            Environment.Exit(0);
        }

        private static IntPtr CreateJob()
        {
            IntPtr created = CreateJobObject(IntPtr.Zero, null);
            if (created == IntPtr.Zero) { logger("KioskBroker=JobCreateFailed; Error=" + Marshal.GetLastWin32Error()); return IntPtr.Zero; }
            JobExtendedLimits limits = new JobExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: also when this program dies
            if (!SetInformationJobObject(created, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobExtendedLimits))))
                logger("KioskBroker=JobLimitFailed; Error=" + Marshal.GetLastWin32Error());
            return created;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimits
        {
            public JobBasicLimits Basic;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint process);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    }
}
