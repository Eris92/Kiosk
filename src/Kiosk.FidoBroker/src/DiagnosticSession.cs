using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace FidoRun
{
    // The authenticated token remains in this process; IPC never exports handles or credentials.
    internal static class DiagnosticSession
    {
        private static readonly object gate = new object();
        private static LogonOutcome retained;
        private static string expectedSid;
        private static Action<string> logger;
        private static NamedPipeServerStream activePipe;
        private static Timer expiryTimer;
        private static DateTime expires;
        private static bool stopping;

        internal static string Start(LogonOutcome outcome, string target, Action<string> log)
        {
            string caller;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) caller = identity.User.Value;
            string pipeName = "FidoRun-Session-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (string sid in new[] { caller, "S-1-5-18", "S-1-5-32-544" })
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
            lock (gate)
            {
                if (retained != null) throw new InvalidOperationException("A diagnostic token session already exists.");
                retained = WindowsLogon.CloneForSession(outcome, target);
                expectedSid = target; logger = log; stopping = false;
                expires = DateTime.UtcNow.AddHours(1);
                activePipe = CreatePipe(pipeName, security);
                expiryTimer = new Timer(delegate { Stop(); }, null, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);
            }
            Thread worker = new Thread(delegate() { Serve(pipeName, security); });
            worker.IsBackground = true;
            worker.Start();
            log("DiagnosticSession=Ready; Pipe=" + pipeName + "; SID=" + retained.Sid
                + "; ExpiresUtc=" + expires.ToString("o") + "; TokenStorage=ProcessMemoryOnly");
            return pipeName;
        }

        private static NamedPipeServerStream CreatePipe(string name, PipeSecurity security)
        {
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        private static void Serve(string name, PipeSecurity security)
        {
            while (true)
            {
                NamedPipeServerStream pipe;
                lock (gate) { if (stopping || retained == null) return; pipe = activePipe; }
                try
                {
                    pipe.WaitForConnection();
                    using (StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                    using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                    {
                        writer.AutoFlush = true;
                        string request = reader.ReadLine();
                        string response;
                        lock (gate)
                        {
                            if (stopping || retained == null) return;
                            try { response = Execute(request); }
                            catch (Exception error) { logger("DiagnosticCommand=Failed; " + error.Message); response = "ERROR " + error.Message; }
                        }
                        writer.WriteLine(response.Replace('\r', ' ').Replace('\n', ' '));
                        if (request == "close") { Stop(); return; }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { return; }
                finally { pipe.Dispose(); }
                lock (gate)
                {
                    if (stopping || retained == null) return;
                    activePipe = CreatePipe(name, security);
                }
            }
        }

        private static string Execute(string command)
        {
            if (DateTime.UtcNow >= expires) return "ERROR Session expired";
            if (command == "status") return "OK SID=" + retained.Sid + "; ExpiresUtc=" + expires.ToString("o");
            if (command == "prt-status")
            {
                logger("DiagnosticCommand=prt-status; ReusingAuthenticatedToken=True");
                return WindowsLogon.ReportPrtStatus(retained, logger);
            }
            if (command == "close") return "OK Session closed";
            if (command != "edge-default" && command != "edge-diagnostic") return "ERROR Unknown command";
            return RequestEdge(command == "edge-diagnostic");
        }

        // Launch Edge with the retained token. Callable from the pipe (edge-default /
        // edge-diagnostic) and from the host's "Open Edge" button so the browser opens
        // only on demand, never automatically right after authentication.
        internal static string RequestEdge(bool diagnosticProfile)
        {
            lock (gate)
            {
                if (retained == null) return "ERROR No retained token session is available.";
                if (DateTime.UtcNow >= expires) return "ERROR Session expired";
                string previous = Environment.GetEnvironmentVariable("FIDORUN_EDGE_DIAGNOSTIC_PROFILE");
                try
                {
                    Environment.SetEnvironmentVariable("FIDORUN_EDGE_DIAGNOSTIC_PROFILE", diagnosticProfile ? "1" : null);
                    logger("DiagnosticCommand=edge-" + (diagnosticProfile ? "diagnostic" : "default") + "; ReusingAuthenticatedToken=True");
                    WindowsLogon.LaunchEdge(retained, expectedSid, logger);
                    return "OK Launch attempted; inspect the FidoRun and Edge logs";
                }
                finally { Environment.SetEnvironmentVariable("FIDORUN_EDGE_DIAGNOSTIC_PROFILE", previous); }
            }
        }

        // Kiosk: close the user's browser, then release the retained token.
        internal static void EndSessionForKiosk(Action<string> log)
        {
            lock (gate)
            {
                if (retained != null)
                    try { WindowsLogon.CloseBrowsersForSession(retained, log); } catch { }
            }
            Stop();
        }

        internal static void Stop()
        {
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                if (activePipe != null) { activePipe.Dispose(); activePipe = null; }
                if (expiryTimer != null) { expiryTimer.Dispose(); expiryTimer = null; }
                if (retained != null) { retained.Dispose(); retained = null; }
                if (logger != null) logger("DiagnosticSession=Closed; RetainedTokenDisposed=True");
            }
        }
    }
}
