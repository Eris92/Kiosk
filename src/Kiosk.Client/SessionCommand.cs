using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kiosk.Client;

/// <summary>Runs a menu command inside the card session's kill-on-close process group.</summary>
internal static class SessionCommand
{
    internal static async Task<int> RunAsync(string program, string arguments, string? directory,
        UserLogon? logon, ISet<SafeFileHandle> jobs, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var job = AppNative.CreateKillOnCloseJob();
        jobs.Add(job);
        try
        {
            var line = "\"" + program + "\" " + arguments;
            AppNative.ProcessInfo process;
            if (logon != null)
            {
                var started = logon.Start(program, line, directory, show: 0); // SW_HIDE
                process = new AppNative.ProcessInfo { Process = started.Process, Thread = started.Thread };
            }
            else
            {
                var startup = new AppNative.StartupInfo
                {
                    Size = Marshal.SizeOf<AppNative.StartupInfo>(), Flags = 1, ShowWindow = 0
                };
                if (!AppNative.CreateProcess(program, new StringBuilder(line), IntPtr.Zero, IntPtr.Zero,
                        false, 0x4 | 0x08000000, IntPtr.Zero, directory, ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                try
                {
                    if (!AppNative.AssignProcessToJobObject(job, process.Process))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (AppNative.ResumeThread(process.Thread) == uint.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                catch
                {
                    AppNative.TerminateProcess(process.Process, 1);
                    throw;
                }
                finally { AppNative.CloseHandle(process.Thread); }

                await WaitForProcessAsync(process.Process, cancellation);
                if (!GetExitCodeProcess(process.Process, out var exit))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)exit);
            }
            finally { AppNative.CloseHandle(process.Process); }
        }
        finally { jobs.Remove(job); }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(IntPtr process) =>
            SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false);
    }

    private static async Task WaitForProcessAsync(IntPtr process, CancellationToken cancellation)
    {
        using var wait = new ProcessWaitHandle(process);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => completed.TrySetResult(), null, Timeout.Infinite, true);
        try { await completed.Task.WaitAsync(cancellation); }
        finally { registration.Unregister(null); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
}
