using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace FidoRun
{
    // Diagnostic for an INTERNAL Windows export, not a supported authentication API.
    // The no-argument HRESULT signatures were checked in Microsoft's 26100.9549
    // binaries. A separate process keeps this subscription apart from the provider.
    internal static class NativeCtapProbe
    {
        internal const int RpcAccessDenied = unchecked((int)0x8001011B);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("webauthn.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern int WebAuthNCtapStartDeviceChangeNotify();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("webauthn.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        private static extern int WebAuthNCtapStopDeviceChangeNotify();

        internal static void DescribeCaller(Action<string> log)
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                bool present = false;
                if (identity.Groups != null)
                    foreach (IdentityReference group in identity.Groups)
                        if (group.Value == "S-1-5-32-544") { present = true; break; }
                log("Caller=" + identity.Name + "; SID=" + identity.User.Value);
                log("CallerContext: AdminSidPresent=" + present + "; AdminEnabled=" +
                    principal.IsInRole(WindowsBuiltInRole.Administrator) + "; IsSystem=" + identity.IsSystem +
                    "; SessionId=" + Process.GetCurrentProcess().SessionId);
            }
            foreach (string name in new[] { "fidocredprov.dll", "webauthn.dll", "aadauthhelper.dll" })
            {
                string path = Path.Combine(Environment.SystemDirectory, name);
                try
                {
                    FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                    log("SystemFile: Name=" + name + "; Version=" + version.FileVersion);
                }
                catch (Exception error) { log("SystemFile: Name=" + name + "; Unavailable=" + error.GetType().Name); }
            }
        }

        internal static int Interpret(int start, int stop, Action<string> log)
        {
            if (start == RpcAccessDenied)
            {
                log("Outcome=NativeCtapRpcAccessDenied; the native CTAP request was denied without any credential-provider callback.");
                log("NextStage=PrivilegedHostInvestigation; this is not a key PIN, account, or LSA authentication result.");
                return 6;
            }
            if (start < 0)
            {
                log("Outcome=NativeCtapRpcFailed; Start=" + Status.Hex(start));
                return 7;
            }
            if (stop < 0)
            {
                log("Outcome=NativeCtapCleanupFailed; Stop=" + Status.Hex(stop));
                return 7;
            }
            log("Outcome=NativeCtapRpcReachable; device-change notification started and stopped.");
            log("This is a transport diagnostic, not FIDO authentication or Windows token creation.");
            return 0;
        }

        internal static int Run(Action<string> log)
        {
            log("DiagnosticOnly=True; NoProviderCallbacks=True; NoAuthentication=True; NoLsaLogon=True; NoProcessLaunch=True");
            log("Internal Windows CTAP export: its contract can change between Windows builds.");
            bool started = false;
            int start = Status.NotImplemented, stop = Status.Ok;
            Program.BeginOperation();
            try
            {
                try
                {
                    log("WebAuthNCtapStartDeviceChangeNotify: starting");
                    start = WebAuthNCtapStartDeviceChangeNotify();
                    started = start >= 0;
                    log("WebAuthNCtapStartDeviceChangeNotify: " + Status.Hex(start));
                }
                finally
                {
                    if (started)
                    {
                        stop = WebAuthNCtapStopDeviceChangeNotify();
                        log("WebAuthNCtapStopDeviceChangeNotify: " + Status.Hex(stop));
                    }
                }
                return Interpret(start, stop, log);
            }
            catch (Exception error)
            {
                log("Outcome=NativeCtapProbeUnavailable; " + error.GetType().Name + "; HRESULT=" + Status.Hex(error.HResult) + "; " + error.Message);
                return 7;
            }
            finally { Program.EndOperation(); }
        }
    }
}
