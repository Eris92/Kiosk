using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Kiosk.Client;

internal sealed partial class KioskForm
{
    internal void CoreRegressionTests()
    {
        var basic = new Config { ConfigVersion = Config.CurrentVersion };
        foreach (var invalid in new[]
        {
            basic with { IdleScreens = null! },
            basic with { IdleScreens = [null!] },
            basic with { IdleScreens = [new ScreenContent { Url = null! }] },
            basic with { IdleScreens = [new ScreenContent { Path = "relative.exe" }] },
            basic with { IdleScreens = [new ScreenContent { Url = "https://example.com", Path = "C:\\app.exe" }] },
            basic with { IdleScreens = [new ScreenContent { Url = "https://a.example" }, new ScreenContent { Url = "https://b.example" }] }
        })
        {
            try { invalid.Validate(); throw new Exception("Invalid screen configuration was accepted."); }
            catch (InvalidDataException) { }
        }
        foreach (var invalid in new[] { basic with { ConfigVersion = Config.CurrentVersion + 1 }, basic with { Applications = [null!] } })
        {
            try { invalid.Migrate(); throw new Exception("Unsafe migration was accepted."); }
            catch (InvalidDataException) { }
        }

        var file = new FileInfo(Path.Combine(AppContext.BaseDirectory, "self-test-acl.json"));
        Configuration.Save(file.FullName, basic);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Allow));
        file.SetAccessControl(acl);
        string before = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Configuration.Save(file.FullName, basic with { LockAfterSeconds = 120 });
        if (file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) != before || Configuration.Load(file.FullName).LockAfterSeconds != 120)
            throw new InvalidOperationException("Configuration save replaced access permissions or lost changes.");

        // Delimiters in user-supplied names must not collide in the taskbar cache.
        using var bar = new Taskbar();
        bar.SetTasks([("a", "b|c=d", false), ("e", "f", false)]);
        bar.SetTasks([("a", "b", false), ("c", "d|e=f", true)]);
        if (!bar.Controls.OfType<TabButton>().Select(t => t.Text).SequenceEqual(new[] { "b", "d|e=f" }))
            throw new InvalidOperationException("Taskbar cache confused different window sets.");

        // A live reader's state must remain stable on a timeout, including identity fields.
        using var probe = new CardReader();
        var first = probe.Snapshot("Kiosk self-test missing reader");
        for (int i = 0; i < 10; i++)
            if (probe.Snapshot("Kiosk self-test missing reader") != first)
                throw new InvalidOperationException("Unchanged reader state changed between polls.");
    }
}
