namespace Kiosk.Client;

internal static class SelfTestEntry
{
    internal static bool TryRun(string[] args, out int testResult)
    {
        testResult = 1;
        if (args.Length == 2 && args[0] == "--multi-window-fixture")
        {
            testResult = MultiWindowFixture.Run(args[1]); return true;
        }
        if (args.Length == 3 && args[0] == "--command-parent")
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add("--command-child");
            start.ArgumentList.Add(args[2]);
            using var child = System.Diagnostics.Process.Start(start)!;
            File.WriteAllText(args[1], Environment.ProcessId.ToString());
            Thread.Sleep(30000);
            testResult = 0; return true;
        }
        if (args.Length == 2 && args[0] == "--command-child")
        {
            File.WriteAllText(args[1], Environment.ProcessId.ToString());
            Thread.Sleep(30000);
            testResult = 0; return true;
        }
        if (args.Length == 1 && args[0] == "--benchmark")
        {
            using var benchmark = new KioskForm(new Config());
            benchmark.Shown += (_, _) =>
            {
                try { benchmark.MeasureSteadyState(Path.Combine(AppContext.BaseDirectory, "performance.json")); }
                finally { benchmark.Close(); }
            };
            Application.Run(benchmark);
            testResult = 0; return true;
        }
        if (args.Length > 0 && args[0] == "--app-host-fixture")
        {
            if (args.Length == 2 && int.TryParse(args[1], out var delay) && delay is >= 0 and <= 10000)
                Thread.Sleep(delay);
            using var fixture = new Form { Text = "Kiosk host test", Size = new Size(480, 320) };
            fixture.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = "Hosted application test" });
            Application.Run(fixture);
            testResult = 0; return true;
        }
        if (args.Length == 1 && args[0] == "--self-test")
        {
                using var form = new KioskForm(new Config { RdpConnections = [new RdpConnection { Name = "Local", Server = "localhost" }] });
                var result = 1;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        SecuritySelfTests.Run();
                        RdpLaunchTests.Run();
                        WindowsSessionTests.Run();
                        form.SmokeTest();
                        form.CoreRegressionTests();
                        await CardPinLifecycleTests.RunAsync();
                        await form.CommandLifecycleTestsAsync();
                        await form.EmbeddedSmokeTestAsync();
                        await BrowserLifecycleTests.RunAsync(form);
                        await form.ApplicationSmokeTestAsync();
                        await form.ApplicationOwnershipTestsAsync();
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), "Config migration, RDP sign-in modes, PCSC, taskbar, menu, lock/resume, per-card sessions (park/switch/return/expire), cancelled PIN helper termination, browser, notes, settings round-trip, owned application window, command process tree and session cleanup passed.");
                        result = 0;
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), ex.ToString()); }
                    finally { form.FinishSelfTest(); form.Close(); }
                };
                Application.Run(form);
                testResult = result; return true;
        }
        return false;
    }
}
