using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kiosk.Client;

/// <summary>Owns every background command started by one Kiosk session, including child processes.</summary>
internal sealed class SessionCommandScope : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<SafeFileHandle> jobs = [];
    private int active;
    private bool closed;

    internal bool HasRunning { get { lock (gate) return active > 0; } }

    internal bool TryBegin(out CancellationToken cancellation)
    {
        lock (gate)
        {
            if (closed) { cancellation = default; return false; }
            active++;
            cancellation = stop.Token;
            return true;
        }
    }

    internal void Track(SafeFileHandle job, CancellationToken cancellation)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (closed) throw new OperationCanceledException(cancellation);
            jobs.Add(job);
        }
    }

    internal void Release(SafeFileHandle job) { lock (gate) jobs.Remove(job); }

    internal void End()
    {
        lock (gate)
        {
            active--;
            if (closed && active == 0) stop.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            stop.Cancel();
            foreach (var job in jobs) job.Dispose();
            jobs.Clear();
            if (active == 0) stop.Dispose();
        }
    }
}

/// <summary>Runs a menu command inside its session's kill-on-close process group.</summary>
internal static class SessionCommand
{
    internal static async Task<int> RunAsync(string program, string arguments, string? directory,
        UserLogon? logon, SessionCommandScope scope, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var job = AppNative.CreateKillOnCloseJob();
        scope.Track(job, cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
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
                // A script can launch a child and exit. Keep that child in the session until it finishes or the
                // session closes, instead of closing the job as soon as the wrapper process exits.
                await WaitForJobExitAsync(job, cancellation);
                return unchecked((int)exit);
            }
            finally { AppNative.CloseHandle(process.Process); }
        }
        finally { scope.Release(job); }
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

    private static async Task WaitForJobExitAsync(SafeFileHandle job, CancellationToken cancellation)
    {
        var information = new byte[48]; // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION, ActiveProcesses at offset 40 on x64.
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!AppNative.QueryInformationJobObject(job, 1, information, information.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (BitConverter.ToInt32(information, 40) == 0) return;
            await Task.Delay(250, cancellation);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
}
