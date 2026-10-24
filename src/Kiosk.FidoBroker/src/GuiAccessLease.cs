using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.IO;

namespace FidoRun
{
    // Grant only the freshly authenticated logon SID, and remove only our ACEs.
    internal sealed class GuiAccessLease : IDisposable
    {
        private static readonly object poolGate = new object();
        private static readonly Mutex aclMutex = new Mutex(false, "Local\\FidoRun.GuiAccessAcl.v1");
        private static readonly Dictionary<string, GuiAccessLease> pool = new Dictionary<string, GuiAccessLease>();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenWindowStationW(string name, bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseWindowStation(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            out IntPtr target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(
            IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);
        [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int info, IntPtr buffer, int length, out int needed);
        [StructLayout(LayoutKind.Sequential)] private struct UnicodeName { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Flags; public IntPtr Security, Qos; }
        [DllImport("ntdll.dll")] private static extern int NtOpenDirectoryObject(out IntPtr handle, uint access, ref ObjectAttributes attributes);
        [DllImport("ntdll.dll")] private static extern int NtOpenSymbolicLinkObject(out IntPtr handle, uint access, ref ObjectAttributes attributes);
        [DllImport("ntdll.dll")] private static extern int NtQuerySecurityObject(IntPtr handle, uint info, byte[] buffer, uint size, out uint needed);
        [DllImport("ntdll.dll")] private static extern int NtSetSecurityObject(IntPtr handle, uint info, byte[] buffer);
        [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status, Information; }
        [DllImport("ntdll.dll")] private static extern int NtOpenFile(out IntPtr handle, uint access,
            ref ObjectAttributes attributes, out IoStatus io, uint share, uint options);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetUserObjectSecurity(IntPtr handle, ref uint info, byte[] buffer, uint size, out uint needed);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetUserObjectSecurity(IntPtr handle, ref uint info, byte[] buffer);
        private readonly List<Tuple<IntPtr, byte[], bool>> additions = new List<Tuple<IntPtr, byte[], bool>>();
        private readonly Action<string> log;
        private IntPtr station, desktop, objectDirectory, appContainerDirectory, pipeDirectory;
        private readonly List<IntPtr> rendererDirectories = new List<IntPtr>();
        // Stable Edge's renderer namespace, confirmed against the installed renderer token.
        private const string EdgeRendererSid = "S-1-15-2-3251537155-1984446955-2931258699-841473695-1938553385-924012159-129201922";
        private int references = 1;
        private string sidKey;
        private readonly string cleanupReport = Path.Combine(TraceLog.LogRoot(), "FidoRun",
            "gui-access-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
        private GuiAccessLease(Action<string> logger) { log = logger; }
        private sealed class AclLock : IDisposable
        {
            public AclLock()
            {
                try { if (!aclMutex.WaitOne(10000)) throw new TimeoutException("Timed out waiting for GUI ACL access."); }
                catch (AbandonedMutexException) { }
            }
            public void Dispose() { aclMutex.ReleaseMutex(); }
        }

        private static RawSecurityDescriptor Read(IntPtr handle, bool kernel = false)
        {
            uint info = 4, needed;
            if (kernel) NtQuerySecurityObject(handle, info, null, 0, out needed);
            else GetUserObjectSecurity(handle, ref info, null, 0, out needed);
            if (needed == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] bytes = new byte[needed];
            if (kernel ? NtQuerySecurityObject(handle, info, bytes, needed, out needed) < 0
                : !GetUserObjectSecurity(handle, ref info, bytes, needed, out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new RawSecurityDescriptor(bytes, 0);
        }
        private static void Write(IntPtr handle, RawSecurityDescriptor descriptor, bool kernel = false)
        {
            byte[] bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            uint info = 4;
            if (kernel)
            {
                int status = NtSetSecurityObject(handle, info, bytes);
                if (status < 0) throw new InvalidOperationException("NtSetSecurityObject: 0x" + status.ToString("X8"));
            }
            else if (!SetUserObjectSecurity(handle, ref info, bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        private void Grant(IntPtr handle, SecurityIdentifier sid, int mask, bool kernel = false)
        {
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            RawSecurityDescriptor descriptor = Read(handle, kernel);
            if (descriptor.DiscretionaryAcl == null)
                throw new InvalidOperationException("Refusing to replace a null desktop DACL.");
            // Own an ACE even if another host already granted this SID access.
            // That host can exit independently while this browser is still using it.
            CommonAce ace = new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, mask, sid, false, null);
            byte[] identity = new byte[ace.BinaryLength];
            ace.GetBinaryForm(identity, 0);
            int position = 0;
            while (position < descriptor.DiscretionaryAcl.Count
                && (descriptor.DiscretionaryAcl[position].AceFlags & AceFlags.Inherited) == 0) position++;
            descriptor.DiscretionaryAcl.InsertAce(position, ace);
            Write(handle, descriptor, kernel);
            additions.Add(Tuple.Create(handle, identity, kernel));
        }
        private static IntPtr OpenObjectDirectory(string leaf = "BaseNamedObjects", bool optional = false, bool link = false)
        {
            string path = "\\Sessions\\" + System.Diagnostics.Process.GetCurrentProcess().SessionId
                + (String.IsNullOrEmpty(leaf) ? "" : "\\" + leaf);
            UnicodeName name = new UnicodeName { Length = checked((ushort)(path.Length * 2)),
                MaximumLength = checked((ushort)(path.Length * 2 + 2)), Buffer = Marshal.StringToHGlobalUni(path) };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeName)));
            try
            {
                Marshal.StructureToPtr(name, buffer, false);
                ObjectAttributes attributes = new ObjectAttributes { Length = Marshal.SizeOf(typeof(ObjectAttributes)), Name = buffer, Flags = 64 };
                IntPtr handle;
                int status = link ? NtOpenSymbolicLinkObject(out handle, 0x00060000, ref attributes)
                    : NtOpenDirectoryObject(out handle, 0x00060000, ref attributes);
                if (optional && (status == unchecked((int)0xC0000034) || status == unchecked((int)0xC000003A))) return IntPtr.Zero;
                if (status < 0) throw new InvalidOperationException("NtOpenDirectoryObject: 0x" + status.ToString("X8"));
                return handle;
            }
            finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(name.Buffer); }
        }
        private static IntPtr OpenAppContainerPipeDirectory(string leaf = "", bool optional = false)
        {
            string path = "\\Device\\NamedPipe\\Sessions\\"
                + System.Diagnostics.Process.GetCurrentProcess().SessionId + "\\AppContainerNamedObjects" + leaf;
            UnicodeName name = new UnicodeName { Length = checked((ushort)(path.Length * 2)),
                MaximumLength = checked((ushort)(path.Length * 2 + 2)), Buffer = Marshal.StringToHGlobalUni(path) };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeName)));
            try
            {
                Marshal.StructureToPtr(name, buffer, false);
                ObjectAttributes attributes = new ObjectAttributes { Length = Marshal.SizeOf(typeof(ObjectAttributes)), Name = buffer, Flags = 64 };
                IntPtr handle; IoStatus io;
                int status = NtOpenFile(out handle, 0x00060000, ref attributes, out io, 7, 1);
                if (optional && (status == unchecked((int)0xC0000034) || status == unchecked((int)0xC000003A))) return IntPtr.Zero;
                if (status < 0) throw new InvalidOperationException("NtOpenFile(AppContainer pipe directory): 0x" + status.ToString("X8"));
                return handle;
            }
            finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(name.Buffer); }
        }
        internal static GuiAccessLease Acquire(SecurityIdentifier logonSid, Action<string> log, bool fullAccess = false)
        {
            lock (poolGate) using (new AclLock()) { return AcquireCore(logonSid, log, fullAccess); }
        }
        private static GuiAccessLease AcquireCore(SecurityIdentifier logonSid, Action<string> log, bool fullAccess)
        {
            if (logonSid == null || !logonSid.Value.StartsWith("S-1-5-5-", StringComparison.Ordinal))
                throw new InvalidOperationException("An authenticated logon SID is required for desktop access.");
            GuiAccessLease existingLease;
            if (pool.TryGetValue(logonSid.Value, out existingLease))
            {
                Interlocked.Increment(ref existingLease.references);
                log("GuiAccessLease=Shared; LogonSid=" + logonSid);
                return existingLease;
            }
            GuiAccessLease lease = new GuiAccessLease(log) { sidKey = logonSid.Value };
            try
            {
                lease.station = OpenWindowStationW("WinSta0", false, 0x00060000);
                if (lease.station == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                lease.desktop = OpenDesktopW("Default", 0, false, 0x00060000);
                if (lease.desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                lease.Grant(lease.station, logonSid, fullAccess ? 0x000F037F : 0x00020337);
                lease.Grant(lease.desktop, logonSid, fullAccess ? 0x000F01FF : 0x000201CF);
                lease.objectDirectory = OpenObjectDirectory();
                // Match normal interactive-session directory rights: query, traverse,
                // create objects/subdirectories and read control. No WRITE_DAC/owner rights.
                lease.Grant(lease.objectDirectory, logonSid, 0x0002000F, true);
                lease.appContainerDirectory = OpenObjectDirectory("AppContainerNamedObjects");
                // AppContainer token creation needs the same per-logon namespace rights.
                lease.Grant(lease.appContainerDirectory, logonSid, 0x0002000F, true);
                lease.pipeDirectory = OpenAppContainerPipeDirectory();
                // Match the interactive logon SID's pipe-directory rights: create,
                // read control and synchronize, without changing other users' access.
                lease.Grant(lease.pipeDirectory, logonSid, 0x00120006, true);
                foreach (string suffix in new[] { "", "\\RPC Control", "\\Global", "\\Local", "\\Session" })
                {
                    // Windows duplicates these pre-existing handles with full object
                    // access while creating the LowBox environment for another logon.
                    bool link = suffix == "\\Local" || suffix == "\\Session";
                    IntPtr handle = OpenObjectDirectory("AppContainerNamedObjects\\" + EdgeRendererSid + suffix, true, link);
                    if (handle == IntPtr.Zero) continue;
                    lease.rendererDirectories.Add(handle);
                    lease.Grant(handle, logonSid, link ? 0x000F0001 : 0x000F000F, true);
                }
                IntPtr rendererPipe = OpenAppContainerPipeDirectory("\\" + EdgeRendererSid, true);
                if (rendererPipe != IntPtr.Zero)
                {
                    lease.rendererDirectories.Add(rendererPipe);
                    lease.Grant(rendererPipe, logonSid, 0x001F0006, true);
                }
                log("GuiAccessLease=Granted; LogonSid=" + logonSid + "; AddedAces=" + lease.additions.Count);
                pool.Add(lease.sidKey, lease);
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public void Dispose()
        {
            lock (poolGate) using (new AclLock()) { Release(); }
        }
        private void Release()
        {
            if (Interlocked.Decrement(ref references) != 0) return;
            GuiAccessLease registered;
            if (sidKey != null && pool.TryGetValue(sidKey, out registered) && Object.ReferenceEquals(registered, this)) pool.Remove(sidKey);
            bool success = true;
            for (int i = additions.Count - 1; i >= 0; i--)
            {
                try
                {
                    RawSecurityDescriptor descriptor = Read(additions[i].Item1, additions[i].Item3);
                    for (int j = descriptor.DiscretionaryAcl.Count - 1; j >= 0; j--)
                    {
                        GenericAce ace = descriptor.DiscretionaryAcl[j];
                        byte[] bytes = new byte[ace.BinaryLength];
                        ace.GetBinaryForm(bytes, 0);
                        if (Convert.ToBase64String(bytes) != Convert.ToBase64String(additions[i].Item2)) continue;
                        descriptor.DiscretionaryAcl.RemoveAce(j);
                        Write(additions[i].Item1, descriptor, additions[i].Item3);
                        break;
                    }
                }
                catch (Exception error) { success = false; log("GuiAccessLeaseCleanup=Failed; " + error.Message); }
            }
            additions.Clear();
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            if (station != IntPtr.Zero) CloseWindowStation(station);
            if (objectDirectory != IntPtr.Zero) CloseHandle(objectDirectory);
            if (appContainerDirectory != IntPtr.Zero) CloseHandle(appContainerDirectory);
            if (pipeDirectory != IntPtr.Zero) CloseHandle(pipeDirectory);
            foreach (IntPtr handle in rendererDirectories) CloseHandle(handle);
            string result = success ? "GuiAccessLease=Released" : "GuiAccessLease=CleanupFailed";
            log(result);
            try { File.AppendAllText(cleanupReport, DateTime.UtcNow.ToString("o") + " " + result + Environment.NewLine); }
            catch (IOException) { }
        }
        private static string ImagePath(IntPtr process)
        {
            System.Text.StringBuilder buffer = new System.Text.StringBuilder(32768);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        private static bool HasBrowserForLogon(string image, string logonSid)
        {
            foreach (System.Diagnostics.Process candidate in System.Diagnostics.Process.GetProcessesByName("msedge"))
            using (candidate)
            {
                IntPtr process = OpenProcess(0x1000, false, candidate.Id), token = IntPtr.Zero, groups = IntPtr.Zero;
                try
                {
                    if (process == IntPtr.Zero || !String.Equals(image, ImagePath(process), StringComparison.OrdinalIgnoreCase)
                        || !OpenProcessToken(process, 8, out token)) continue;
                    int needed;
                    GetTokenInformation(token, 2, IntPtr.Zero, 0, out needed);
                    if (needed <= 0) continue;
                    groups = Marshal.AllocHGlobal(needed);
                    if (!GetTokenInformation(token, 2, groups, needed, out needed)) continue;
                    for (int index = 0; index < Marshal.ReadInt32(groups); index++)
                    {
                        IntPtr entry = IntPtr.Add(groups, 8 + index * 16);
                        uint flags = unchecked((uint)Marshal.ReadInt32(entry, 8));
                        if ((flags & 0xC0000000) == 0xC0000000
                            && new SecurityIdentifier(Marshal.ReadIntPtr(entry)).Value == logonSid) return true;
                    }
                }
                finally
                {
                    if (groups != IntPtr.Zero) Marshal.FreeHGlobal(groups);
                    if (token != IntPtr.Zero) CloseHandle(token);
                    if (process != IntPtr.Zero) CloseHandle(process);
                }
            }
            return false;
        }
        internal void HoldUntilExit(IntPtr process, string browserImage = null)
        {
            // The caller already knows the executable. The initial process may
            // have exited before its image path can be queried after a handoff.
            string image = browserImage;
            IntPtr duplicate;
            if (!DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), out duplicate, 0x00100000, false, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Interlocked.Increment(ref references);
            Thread monitor = new Thread(delegate()
            {
                try
                {
                    WaitForSingleObject(duplicate, 0xFFFFFFFF);
                    if (image != null)
                    {
                        // Edge can hand off to an existing instance or restart into
                        // background mode. Keep this logon's access until all its
                        // browser processes have gone, with a short handoff grace period.
                        int emptySamples = 0;
                        while (emptySamples < 3)
                        {
                            try { emptySamples = HasBrowserForLogon(image, sidKey) ? 0 : emptySamples + 1; }
                            catch (Exception error)
                            {
                                emptySamples = 0;
                                log("GuiAccessLifetime=RetryingObservation; " + error.Message);
                            }
                            Thread.Sleep(1000);
                        }
                    }
                }
                finally { CloseHandle(duplicate); Dispose(); }
            });
            // Keep ACL cleanup alive even after the credential-provider form closes.
            monitor.IsBackground = false;
            try { monitor.Start(); }
            catch { CloseHandle(duplicate); Dispose(); throw; }
        }
    }
}
