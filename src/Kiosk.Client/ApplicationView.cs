using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kiosk.Client;

/// <summary>
/// Hosts a desktop application inside the Kiosk. The process runs in a kill-on-close Job; the main
/// window (visible, not a tool window) is embedded. When an application replaces its window — a splash
/// screen, a login window, an Electron restart — the new main window is adopted. Its dialogs stay
/// separate top-level windows (that is how Windows dialogs work) but are centred over the Kiosk.
/// </summary>
internal sealed class ApplicationView : Panel
{
    private SafeFileHandle? job;
    private readonly HashSet<IntPtr> placedDialogs = new();
    private DateTime windowLostAt = DateTime.MaxValue;
    private uint inputThread;
    internal IntPtr HostedWindow { get; private set; }
    /// <summary>True once a window has been embedded; before that the launch is still in progress.</summary>
    internal bool Started { get; private set; }

    internal ApplicationView() { Dock = DockStyle.Fill; BackColor = KioskTheme.Background; }

    /// <param name="logon">When set, the program runs as that person (runas), otherwise as the Kiosk account.</param>
    internal async Task StartAsync(ApplicationEntry entry, UserLogon? logon = null)
    {
        if (!File.Exists(entry.Path)) throw new FileNotFoundException("Nie znaleziono aplikacji.", entry.Path);
        _ = Handle;
        job = AppNative.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new AppNative.JobLimits { Basic = new AppNative.BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!AppNative.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<AppNative.JobLimits>())) throw new Win32Exception();
        // SW_SHOWNOACTIVATE: the window starts normally shown (SW_HIDE or a minimised start makes some apps,
        // e.g. KeePass with "minimize to tray", hide themselves or stay blank). It is adopted within ~50 ms.
        var startup = new AppNative.StartupInfo { Size = Marshal.SizeOf<AppNative.StartupInfo>(), Flags = 1, ShowWindow = 4 };
        // Start suspended: no descendant can escape ownership before job assignment.
        var directory = string.IsNullOrWhiteSpace(entry.WorkingDirectory) ? Path.GetDirectoryName(entry.Path) : entry.WorkingDirectory;
        var commandLine = '"' + entry.Path + "\" " + entry.Arguments;
        AppNative.ProcessInfo process;
        if (logon != null)
        {
            var started = logon.Start(entry.Path, commandLine, directory);
            process = new AppNative.ProcessInfo { Process = started.Process, Thread = started.Thread };
        }
        else if (!AppNative.CreateProcess(entry.Path, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 4, IntPtr.Zero,
            directory, ref startup, out process)) throw new Win32Exception();
        try
        {
            if (!AppNative.AssignProcessToJobObject(job, process.Process)) throw new Win32Exception();
            if (AppNative.ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
        }
        catch { AppNative.TerminateProcess(process.Process, 1); throw; }
        finally { AppNative.CloseHandle(process.Thread); AppNative.CloseHandle(process.Process); }

        var deadline = DateTime.UtcNow.AddSeconds(30); // Office and Electron apps can take a while.
        while (!IsDisposed && DateTime.UtcNow < deadline)
        {
            var window = FindMainWindow();
            if (window != IntPtr.Zero) { Embed(window); return; }
            if (ActiveProcesses() == 0)
                throw new InvalidOperationException("Program zakończył się bez otwierania okna. Zwykle oznacza to, że przekazał pracę do już otwartej kopii — zamknij ją i spróbuj ponownie.");
            await Task.Delay(50);
        }
        if (!IsDisposed) throw new InvalidOperationException("Brak zgodnego okna aplikacji. Program może używać istniejącej instancji, wymagać administratora lub nie obsługiwać osadzania.");
    }

    private void Embed(IntPtr window)
    {
        var style = AppNative.GetWindowLongPtr(window, -16).ToInt64();
        // Remove popup, caption, sizing frame, min/max boxes and the minimised state; make it a child.
        style = (style & ~(0x80000000L | 0x00C00000L | 0x00040000L | 0x00080000L | 0x00030000L | 0x20000000L)) | 0x40000000L;
        Marshal.SetLastPInvokeError(0);
        if (AppNative.SetWindowLongPtr(window, -16, new IntPtr(style)) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception();
        AppNative.SetParent(window, Handle);
        if (AppNative.GetParent(window) != Handle) throw new Win32Exception("Aplikacja nie pozwala osadzić swojego okna w Kiosku.");
        HostedWindow = window;
        Started = true;
        // A child window of another process gets the keyboard only when both threads share input state
        // (otherwise clicks work but typing goes nowhere, e.g. Edge started as another user).
        uint thread = AppNative.GetWindowThreadProcessId(window, out _);
        if (thread != inputThread)
        {
            if (inputThread != 0) AppNative.AttachThreadInput(AppNative.GetCurrentThreadId(), inputThread, false);
            inputThread = AppNative.AttachThreadInput(AppNative.GetCurrentThreadId(), thread, true) ? thread : 0;
        }
        windowLostAt = DateTime.MaxValue;
        AppNative.ShowWindow(window, 9); // SW_RESTORE: leave the minimised start state.
        AppNative.ShowWindow(window, 5); // SW_SHOW
        ResizeChild();
        // Child windows keep a stale frame until told otherwise (the white area some apps showed).
        AppNative.SetWindowPos(window, IntPtr.Zero, 0, 0, ClientSize.Width, ClientSize.Height, 0x0020 | 0x0004 | 0x0040); // FRAMECHANGED | NOZORDER | SHOWWINDOW
        AppNative.RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
    }

    /// <summary>
    /// Called by the shell a few times per second. Returns false once the application has closed
    /// (its window is gone and no replacement appeared), so the Kiosk window can be removed.
    /// </summary>
    internal bool Refresh()
    {
        if (job == null || IsDisposed) return false;
        if (HostedWindow == IntPtr.Zero || !AppNative.IsWindow(HostedWindow))
        {
            HostedWindow = IntPtr.Zero;
            if (windowLostAt == DateTime.MaxValue) windowLostAt = DateTime.UtcNow;
            var replacement = FindMainWindow();
            if (replacement != IntPtr.Zero) { try { Embed(replacement); } catch (Win32Exception) { } }
            else if (ActiveProcesses() == 0 || DateTime.UtcNow - windowLostAt > TimeSpan.FromSeconds(10)) return false;
        }
        else if (Visible && !AppNative.IsWindowVisible(HostedWindow))
            AppNative.ShowWindow(HostedWindow, 5); // An app hiding itself "to the tray" would be lost: the Kiosk has no tray.
        if (Visible) PlaceDialogs();
        return true;
    }

    /// <summary>Centres each new dialog of the application over the Kiosk (once, so it can still be moved).</summary>
    private bool PlaceDialogs()
    {
        var area = RectangleToScreen(ClientRectangle);
        bool any = false;
        foreach (var dialog in JobWindows(includeOwned: true))
        {
            if (dialog == HostedWindow) continue;
            any = true;
            if (!placedDialogs.Add(dialog)) continue;
            if (!AppNative.GetWindowRect(dialog, out var rect)) continue;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            AppNative.SetWindowPos(dialog, IntPtr.Zero, area.Left + Math.Max(0, (area.Width - width) / 2),
                area.Top + Math.Max(0, (area.Height - height) / 2), 0, 0, 0x0001); // NOSIZE; HWND_TOP brings it above the Kiosk
        }
        placedDialogs.RemoveWhere(w => !AppNative.IsWindow(w));
        return any;
    }

    /// <summary>The application's main window: visible, top-level, unowned, titled, not a tool window.</summary>
    private IntPtr FindMainWindow() => JobWindows(includeOwned: false).FirstOrDefault(w => !IsDialog(w) &&
        AppNative.GetWindowTextLength(w) > 0 && (AppNative.IsIconic(w) || // Started minimised: its rectangle is the icon.
            AppNative.GetWindowRect(w, out var r) && r.Right - r.Left >= 200 && r.Bottom - r.Top >= 100));

    /// <summary>Message boxes and dialogs (class #32770) are centred over the Kiosk, never embedded as the main window.</summary>
    private static bool IsDialog(IntPtr window)
    {
        var name = new StringBuilder(64);
        return AppNative.GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32770";
    }

    private List<IntPtr> JobWindows(bool includeOwned)
    {
        var found = new List<IntPtr>();
        if (job == null) return found;
        AppNative.EnumWindows((window, _) =>
        {
            if (!AppNative.IsWindowVisible(window)) return true;
            if (!includeOwned && AppNative.GetWindow(window, 4) != IntPtr.Zero) return true;
            if ((AppNative.GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
            AppNative.GetWindowThreadProcessId(window, out var pid);
            using var process = AppNative.OpenProcess(0x1000, false, pid);
            if (process.IsInvalid || !AppNative.IsProcessInJob(process, job, out var owned) || !owned) return true;
            found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private int ActiveProcesses()
    {
        if (job == null) return 0;
        var buffer = new byte[48]; // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        return AppNative.QueryInformationJobObject(job, 1, buffer, buffer.Length, out _) ? BitConverter.ToInt32(buffer, 40) : 1;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ResizeChild(); }
    /// <summary>The Kiosk focusing this view (taskbar, Menu) puts the keyboard into the hosted program.</summary>
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (HostedWindow != IntPtr.Zero) AppNative.SetFocus(HostedWindow); }
    private void ResizeChild()
    {
        if (HostedWindow != IntPtr.Zero) AppNative.MoveWindow(HostedWindow, 0, 0, ClientSize.Width, ClientSize.Height, true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Terminate only this launch and its descendants, never a pre-existing instance.
            if (inputThread != 0) { AppNative.AttachThreadInput(AppNative.GetCurrentThreadId(), inputThread, false); inputThread = 0; }
            job?.Dispose(); job = null; HostedWindow = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }
}


internal static class AppNative
{
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    internal delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits info, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessInJob(SafeFileHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, byte[] info, int length, out int returned);
}
