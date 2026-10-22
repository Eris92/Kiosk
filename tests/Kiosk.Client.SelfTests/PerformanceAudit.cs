using System.Diagnostics;
using System.Text.Json;

namespace Kiosk.Client;

internal sealed partial class KioskForm
{
    internal void MeasureSteadyState(string output)
    {
        timer.Stop();
        var person = new UserSession { Identity = new CardIdentity("test:benchmark", "Benchmark"), Locked = false };
        sessions.Add(person.Identity.Id, person); current = person;
        for (int i = 0; i < 8; i++) AddWindow(person, "bench:" + i, "Window " + i, new Panel());
        for (int i = 0; i < 100; i++) RefreshChrome();
        const int shellIterations = 5000, cardIterations = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < shellIterations; i++) RefreshChrome();
        clock.Stop();
        var shellMs = clock.Elapsed.TotalMilliseconds;
        var shellBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        using var probe = new CardReader();
        probe.Snapshot("Kiosk performance missing reader");
        before = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
        for (int i = 0; i < cardIterations; i++) probe.Snapshot("Kiosk performance missing reader");
        clock.Stop();
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Shell = new { Iterations = shellIterations, Milliseconds = shellMs, AllocatedBytes = shellBytes },
            CardReader = new { Iterations = cardIterations, Milliseconds = clock.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before }
        }, new JsonSerializerOptions { WriteIndented = true }));
        FinishSelfTest();
    }
}
