using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FidoRun
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LsaString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenSource
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Name;
        public Luid Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QuotaLimits
    {
        public UIntPtr PagedPool;
        public UIntPtr NonPagedPool;
        public UIntPtr MinimumWorkingSet;
        public UIntPtr MaximumWorkingSet;
        public UIntPtr PageFile;
        public long TimeLimit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public uint Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, Stdin, Stdout, Stderr;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    internal sealed class LogonOutcome : IDisposable
    {
        public int Status, SubStatus;
        public SafeAccessTokenHandle Token;
        public string Sid, Name;
        public bool Success { get { return Status >= 0 && Token != null && !Token.IsInvalid; } }
        public void Dispose() { if (Token != null) Token.Dispose(); }
    }

    internal static class WindowsLogon
    {
        internal static readonly Guid FidoProvider = new Guid("f8a1793b-7873-4046-b2a7-1f318747f427");

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("secur32.dll", ExactSpelling = true)]
        private static extern int LsaConnectUntrusted(out IntPtr handle);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("secur32.dll", ExactSpelling = true)]
        private static extern int LsaDeregisterLogonProcess(IntPtr handle);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("secur32.dll", ExactSpelling = true)]
        private static extern int LsaFreeReturnBuffer(IntPtr buffer);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("secur32.dll", ExactSpelling = true)]
        private static extern int LsaLookupAuthenticationPackage(IntPtr handle, ref LsaString name, out uint package);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("secur32.dll", ExactSpelling = true)]
        private static extern int LsaLogonUser(IntPtr handle, ref LsaString origin, int logonType,
            uint package, IntPtr authentication, uint length, IntPtr localGroups,
            ref TokenSource source, out IntPtr profile, out uint profileLength,
            out Luid logonId, out IntPtr token, out QuotaLimits quotas, out int subStatus);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint LsaNtStatusToWinError(int status);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocateLocallyUniqueId(out Luid id);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
            IntPtr information, int length, out int returnedLength);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access,
            IntPtr attributes, int impersonationLevel, int type, out SafeAccessTokenHandle duplicate);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags,
            string application, StringBuilder commandLine, uint creationFlags, IntPtr environment,
            string workingDirectory, ref StartupInfo startup, out ProcessInformation process);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static SecurityIdentifier DescribeGuiToken(SafeAccessTokenHandle token, Action<string> log)
        {
            SecurityIdentifier logonSid = null;
            int bytes;
            GetTokenInformation(token, 2, IntPtr.Zero, 0, out bytes);
            if (bytes <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr groups = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!GetTokenInformation(token, 2, groups, bytes, out bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                int count = Marshal.ReadInt32(groups);
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = IntPtr.Add(groups, 8 + i * 16);
                    uint attributes = unchecked((uint)Marshal.ReadInt32(entry, 8));
                    if ((attributes & 0xC0000000) != 0xC0000000) continue;
                    found = true;
                    logonSid = new SecurityIdentifier(Marshal.ReadIntPtr(entry));
                    log("GuiToken: LogonSid=" + logonSid
                        + "; Attributes=0x" + attributes.ToString("X8"));
                }
                if (!found) log("GuiToken: LogonSid=Absent; GroupCount=" + count);
            }
            finally { Marshal.FreeHGlobal(groups); }
            return logonSid;
        }

        internal static void ValidateSerialization(CredentialSerialization serialization)
        {
            if (serialization.Provider != FidoProvider)
                throw new InvalidOperationException("The returned credential provider CLSID is not the native FIDO provider.");
            if (serialization.Buffer == IntPtr.Zero || serialization.Length == 0 || serialization.Length > 1048576)
                throw new InvalidOperationException("The provider returned an invalid serialization buffer.");
        }

        internal static void ClearSerialization(ref CredentialSerialization serialization)
        {
            if (serialization.Buffer != IntPtr.Zero)
            {
                try
                {
                    if (serialization.Length <= 1048576)
                        for (int index = 0; index < (int)serialization.Length; index++)
                            Marshal.WriteByte(serialization.Buffer, index, 0);
                }
                finally { Marshal.FreeCoTaskMem(serialization.Buffer); }
            }
            serialization.Buffer = IntPtr.Zero;
            serialization.Length = 0;
        }

        // Read only fixed, public EX2 header metadata. Never dereference appended
        // usernames, PINs, credential payloads, or package-specific pointers.
        internal static string DescribeCredentialLayout(CredentialSerialization serialization)
        {
            ValidateSerialization(serialization);
            if (serialization.Length < 48 || Marshal.ReadInt32(serialization.Buffer) != 0x201)
                return "CredentialMetadata: Format=Unclassified; RawDataNotLogged=True";
            IntPtr buffer = serialization.Buffer;
            uint header = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            uint size = unchecked((uint)Marshal.ReadInt32(buffer, 8));
            uint packedBytes = unchecked((ushort)Marshal.ReadInt16(buffer, 32));
            bool valid = header >= 48 && header <= size && size <= serialization.Length;
            foreach (int offset in new[] { 12, 20, 28, 40 })
            {
                uint start = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                uint length = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 4));
                if (length != 0 && (start < header || (ulong)start + length > size)) valid = false;
                if (length == 0 && start > size) valid = false;
            }
            return "CredentialMetadata: Format=SEC_WINNT_AUTH_IDENTITY_EX2; HeaderBytes=" + header +
                "; DeclaredBytes=" + size + "; PackedBytes=" + packedBytes + "; RangesValid=" + valid +
                "; DiagnosticOnly=True; BufferUnmodified=True";
        }

        private static LsaString MakeLsaString(string value)
        {
            return new LsaString { Length = checked((ushort)Encoding.ASCII.GetByteCount(value)),
                MaximumLength = checked((ushort)(Encoding.ASCII.GetByteCount(value) + 1)),
                Buffer = Marshal.StringToHGlobalAnsi(value) };
        }

        private static string GetPackageName(IntPtr handle, uint package)
        {
            foreach (string name in new[] { "CloudAP", "Negotiate", "Kerberos", "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0" })
            {
                LsaString nativeName = MakeLsaString(name);
                try
                {
                    uint found;
                    if (LsaLookupAuthenticationPackage(handle, ref nativeName, out found) >= 0 && found == package)
                        return name;
                }
                finally { Marshal.FreeHGlobal(nativeName.Buffer); }
            }
            return "Unresolved";
        }

        internal static LogonOutcome Authenticate(CredentialSerialization serialization, Action<string> log)
        {
            ValidateSerialization(serialization);
            log(DescribeCredentialLayout(serialization));
            IntPtr lsa = IntPtr.Zero, profile = IntPtr.Zero, rawToken = IntPtr.Zero;
            LsaString origin = new LsaString();
            try
            {
                int connected = LsaConnectUntrusted(out lsa);
                log("LsaConnectUntrusted: " + Status.Hex(connected));
                Status.Check(connected, "LsaConnectUntrusted");
                string packageName = GetPackageName(lsa, serialization.AuthenticationPackage);
                log("Authentication package: " + packageName + "; ID=" + serialization.AuthenticationPackage);
                if (packageName != "CloudAP" && packageName != "Negotiate" && packageName != "Kerberos")
                    throw new InvalidOperationException("This prototype does not submit an unresolved authentication package to LSA.");

                TokenSource source = new TokenSource { Name = Encoding.ASCII.GetBytes("FidoRun ") };
                if (!AllocateLocallyUniqueId(out source.Id)) throw new Win32Exception(Marshal.GetLastWin32Error());
                origin = MakeLsaString("FidoRun");
                uint profileLength;
                Luid logonId;
                QuotaLimits quotas;
                int subStatus;
                // Experimental: submit only the system provider's unmodified buffer.
                // AuthenticationInformation is package-specific; compatibility is not assumed.
                int status = LsaLogonUser(lsa, ref origin, 2, serialization.AuthenticationPackage,
                    serialization.Buffer, serialization.Length, IntPtr.Zero, ref source,
                    out profile, out profileLength, out logonId, out rawToken, out quotas, out subStatus);
                log("LsaLogonUser: " + Status.Hex(status) + "; SubStatus=" + Status.Hex(subStatus));
                if (status < 0)
                {
                    uint error = LsaNtStatusToWinError(status);
                    log("Windows logon rejected: Win32=" + error + " (" + new Win32Exception((int)error).Message + ")");
                    uint subError = LsaNtStatusToWinError(subStatus);
                    log("Windows logon substatus: NTSTATUS=" + Status.Hex(subStatus) + "; Win32=" + subError +
                        "; Text=" + TraceLog.DisplayText(new Win32Exception(unchecked((int)subError)).Message));
                    return new LogonOutcome { Status = status, SubStatus = subStatus };
                }
                if (rawToken == IntPtr.Zero)
                    throw new InvalidOperationException("LSA reported success without a token.");

                LogonOutcome outcome = new LogonOutcome { Status = status, SubStatus = subStatus,
                    Token = new SafeAccessTokenHandle(rawToken) };
                rawToken = IntPtr.Zero;
                try
                {
                    using (WindowsIdentity identity = new WindowsIdentity(outcome.Token.DangerousGetHandle()))
                    {
                        outcome.Sid = identity.User.Value;
                        try { outcome.Name = identity.Name; } catch { outcome.Name = "Unresolved account name"; }
                    }
                    int tokenType = ReadTokenInt(outcome.Token, 8);
                    if (tokenType != 1) throw new InvalidOperationException("LSA did not return a primary token.");
                    log("WindowsTokenCreated=True; TokenType=Primary");
                    log("Authenticated account: " + outcome.Name);
                    log("Authenticated SID: " + outcome.Sid);
                    return outcome;
                }
                catch { outcome.Dispose(); throw; }
            }
            finally
            {
                if (rawToken != IntPtr.Zero) CloseHandle(rawToken);
                if (profile != IntPtr.Zero) LsaFreeReturnBuffer(profile);
                if (origin.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(origin.Buffer);
                if (lsa != IntPtr.Zero) LsaDeregisterLogonProcess(lsa);
            }
        }

        private static int ReadTokenInt(SafeAccessTokenHandle token, int informationClass)
        {
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                int required;
                if (!GetTokenInformation(token, informationClass, buffer, 4, out required))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return Marshal.ReadInt32(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // AssignPrimary | Duplicate | Impersonate | Query | AdjustDefault | AdjustSessionId.
        // Handle access only: this does not enable privileges or modify token contents.
        private const uint BrowserTokenAccess = 0x018F;

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags,
            uint disableCount, ref SidAndAttributes disable, uint deleteCount, IntPtr delete,
            uint restrictCount, IntPtr restrict, out SafeAccessTokenHandle result);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CreateProfile(string sid, string username, StringBuilder path, uint length);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length);

        private const int TokenIntegrityLevel = 25;
        private const int SeGroupIntegrity = 0x20;
        private const long MediumIntegrityRid = 0x2000;

        private static long ReadIntegrityRid(SafeAccessTokenHandle token)
        {
            int bytes;
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out bytes);
            if (bytes <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, bytes, out bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                // TOKEN_MANDATORY_LABEL begins with SID_AND_ATTRIBUTES whose first field is the SID pointer.
                SecurityIdentifier level = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                string value = level.Value;
                return Convert.ToInt64(value.Substring(value.LastIndexOf('-') + 1));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // A UAC-filtered administrator token runs at medium integrity. CreateRestrictedToken
        // with LUA_TOKEN removes the Administrators group but leaves the integrity level
        // unchanged, so an admin logon would still start Edge at high integrity. Edge then
        // logs "running elevated" and its sandboxed GPU/renderer children fail to launch.
        // Lower (never raise) the integrity to medium so a cold browser launch matches a
        // normal interactive sign-in. Integrity can only be lowered, so this is safe.
        private static void CapIntegrityToMedium(SafeAccessTokenHandle token, Action<string> log)
        {
            long current = ReadIntegrityRid(token);
            if (current <= MediumIntegrityRid) return;
            SecurityIdentifier medium = new SecurityIdentifier("S-1-16-8192");
            byte[] sidBytes = new byte[medium.BinaryLength];
            medium.GetBinaryForm(sidBytes, 0);
            IntPtr sid = Marshal.AllocHGlobal(sidBytes.Length);
            IntPtr label = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SidAndAttributes)));
            try
            {
                Marshal.Copy(sidBytes, 0, sid, sidBytes.Length);
                SidAndAttributes entry = new SidAndAttributes { Sid = sid, Attributes = SeGroupIntegrity };
                Marshal.StructureToPtr(entry, label, false);
                if (!SetTokenInformation(token, TokenIntegrityLevel, label, Marshal.SizeOf(typeof(SidAndAttributes))))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Lowering browser token integrity to medium failed.");
                if (log != null) log("BrowserTokenIntegrity=Lowered; From=0x" + current.ToString("X") + "; To=0x2000(Medium)");
            }
            finally { Marshal.FreeHGlobal(sid); Marshal.FreeHGlobal(label); }
        }

        private static SafeAccessTokenHandle GetBrowserToken(SafeAccessTokenHandle token, Action<string> log = null)
        {
            SafeAccessTokenHandle linked = null;
            SafeAccessTokenHandle limited = null;
            SafeAccessTokenHandle selected = token;
            try
            {
                if (ReadTokenInt(token, 18) == 2) // TokenElevationTypeFull
                {
                    IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        int required;
                        if (!GetTokenInformation(token, 19, buffer, IntPtr.Size, out required))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        linked = new SafeAccessTokenHandle(Marshal.ReadIntPtr(buffer));
                        selected = linked;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                bool admin;
                using (WindowsIdentity identity = new WindowsIdentity(selected.DangerousGetHandle()))
                    admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                if (admin || ReadTokenInt(selected, 20) != 0)
                {
                    // With UAC disabled an administrator may have no linked token.
                    // Reduce the authenticated token; never substitute another identity.
                    SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    byte[] bytes = new byte[administrators.BinaryLength];
                    administrators.GetBinaryForm(bytes, 0);
                    IntPtr sid = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, sid, bytes.Length);
                        SidAndAttributes deny = new SidAndAttributes { Sid = sid };
                        // DISABLE_MAX_PRIVILEGE | LUA_TOKEN; no SANDBOX_INERT.
                        if (!CreateRestrictedToken(selected, 5, 1, ref deny, 0, IntPtr.Zero, 0, IntPtr.Zero, out limited))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Reducing browser token rights failed.");
                        selected = limited;
                    }
                    finally { Marshal.FreeHGlobal(sid); }
                }
                using (WindowsIdentity identity = new WindowsIdentity(selected.DangerousGetHandle()))
                {
                    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new InvalidOperationException("Refusing to start Edge with an enabled Administrators group. No suitable limited token was available.");
                }
                if (ReadTokenInt(selected, 20) != 0) // TokenElevation
                    throw new InvalidOperationException("Refusing to launch an elevated browser.");
                // Match a UAC-filtered interactive token: medium integrity, so Edge's
                // GPU/renderer sandbox can be created on a cold launch.
                CapIntegrityToMedium(selected, log);
                SafeAccessTokenHandle duplicate;
                if (!DuplicateTokenEx(selected, BrowserTokenAccess, IntPtr.Zero, 2, 1, out duplicate))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return duplicate;
            }
            finally { if (limited != null) limited.Dispose(); if (linked != null) linked.Dispose(); }
        }

        internal static LogonOutcome CloneForSession(LogonOutcome outcome, string expectedSid)
        {
            if (!outcome.Success || outcome.Sid != new SecurityIdentifier(expectedSid).Value)
                throw new InvalidOperationException("A verified FIDO logon for the requested SID is required.");
            return new LogonOutcome { Status = outcome.Status, SubStatus = outcome.SubStatus,
                Sid = outcome.Sid, Name = outcome.Name, Token = GetBrowserToken(outcome.Token) };
        }

        // Kiosk broker: any program as the signed-in person, suspended (the Kiosk resumes it once its window is
        // ready to be embedded).
        internal static ProcessInformation StartAsUser(LogonOutcome outcome, string application, string commandLine,
            string directory, ushort show, Action<string> log)
        {
            if (!outcome.Success) throw new InvalidOperationException("No authenticated Windows token is available.");
            using (SafeAccessTokenHandle token = GetBrowserToken(outcome.Token, log))
            using (GuiAccessLease guiAccess = GuiAccessLease.Acquire(DescribeGuiToken(token, log), log, true))
            {
                StartupInfo startup = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfo)), Desktop = "WinSta0\\Default",
                    Flags = 1, ShowWindow = show }; // STARTF_USESHOWWINDOW
                ProcessInformation process;
                // LOGON_WITH_PROFILE; CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT
                if (!CreateProcessWithTokenW(token, 1, application, new StringBuilder(commandLine), 0x4 | 0x400, IntPtr.Zero,
                    directory ?? Environment.SystemDirectory, ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessWithTokenW failed.");
                try { guiAccess.HoldUntilExit(process.Process, application); }
                catch { TerminateProcess(process.Process, 1); CloseHandle(process.Thread); CloseHandle(process.Process); throw; }
                return process;
            }
        }

        // Kiosk broker sign-out: every process of this person in this Windows session ends.
        internal static void CloseProgramsForSession(LogonOutcome outcome, Action<string> log)
        {
            if (outcome == null || outcome.Sid == null) return;
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId, closed = 0;
            foreach (System.Diagnostics.Process candidate in System.Diagnostics.Process.GetProcesses())
                using (candidate)
                {
                    try
                    {
                        if (candidate.SessionId != session || candidate.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                        SafeAccessTokenHandle candidateToken;
                        if (!OpenProcessToken(candidate.Handle, 0x0008, out candidateToken)) continue;
                        using (candidateToken)
                        using (WindowsIdentity owner = new WindowsIdentity(candidateToken.DangerousGetHandle()))
                            if (owner.User == null || owner.User.Value != outcome.Sid) continue;
                        candidate.Kill();
                        closed++;
                    }
                    catch { /* gone already, or protected */ }
                }
            log("KioskSession=ProgramsClosed; Count=" + closed + "; SID=" + outcome.Sid);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint code);
    }
}
