using System.Diagnostics;

namespace Kiosk.Client;

internal sealed partial class KioskForm
{
    internal async Task CommandLifecycleTestsAsync()
    {
        var parentMarker = Path.Combine(AppContext.BaseDirectory, "command-parent.pid");
        var childMarker = Path.Combine(AppContext.BaseDirectory, "command-child.pid");
        var session = new UserSession { Identity = new CardIdentity("test:command", "Command fixture") };
        sessions.Add(session.Identity.Id, session);
        Process? parent = null, child = null;
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var exit = await SessionCommand.RunAsync(cmd, "/c exit 17", AppContext.BaseDirectory,
                null, session.CommandJobs, session.Lifetime.Token);
            if (exit != 17 || session.CommandJobs.Count != 0)
                throw new InvalidOperationException("A completed command lost its exit code or retained its process group.");

            var arguments = "--command-parent \"" + parentMarker + "\" \"" + childMarker + "\"";
            var command = SessionCommand.RunAsync(Environment.ProcessPath!, arguments, AppContext.BaseDirectory,
                null, session.CommandJobs, session.Lifetime.Token);
            for (int attempt = 0; attempt < 200 && (!File.Exists(parentMarker) || !File.Exists(childMarker)); attempt++)
            {
                if (command.IsCompleted) await command;
                await Task.Delay(50);
            }
            if (!File.Exists(parentMarker) || !File.Exists(childMarker))
                throw new InvalidOperationException("The command process tree did not start.");
            parent = Process.GetProcessById(int.Parse(File.ReadAllText(parentMarker)));
            child = Process.GetProcessById(int.Parse(File.ReadAllText(childMarker)));
            if (parent.HasExited || child.HasExited || session.CommandJobs.Count != 1 || !session.HasWork)
                throw new InvalidOperationException("The command process tree was not owned by its session.");

            CloseSession(session, "command_test_closed");
            if (session.CommandJobs.Count != 0) throw new InvalidOperationException("Session retained command handles.");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!parent.HasExited || !child.HasExited)
                throw new InvalidOperationException("A command or its child survived session closure.");
        }
        finally
        {
            if (!session.Closed) CloseSession(session, "command_test_cleanup");
            if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
            if (parent is { HasExited: false }) parent.Kill(entireProcessTree: true);
            child?.Dispose(); parent?.Dispose();
            if (File.Exists(parentMarker)) File.Delete(parentMarker);
            if (File.Exists(childMarker)) File.Delete(childMarker);
        }
    }
}
