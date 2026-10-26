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
            basic with { IdleScreens = [new ScreenContent { Url = "https://a.example" }, new ScreenContent { Url = "https://b.example" }] },
            basic with { Commands = null! },
            basic with { Commands = [null!] },
            basic with { Commands = [new CommandEntry { Name = "relative", Path = "script.ps1" }] },
            basic with { Commands = [new CommandEntry { Name = "unsafe", Path = "C:\\file.txt" }] },
            basic with { Commands = [new CommandEntry { Name = "A", Path = "C:\\tool.exe" }, new CommandEntry { Name = "a", Path = "C:\\other.exe" }] },
            basic with { Applications = [new ApplicationEntry { Name = "invalid", Path = null! }] },
            basic with { RdpConnections = [new RdpConnection { Name = "RDP", Server = new string('s', 256) }] },
            basic with { RdpConnections = [new RdpConnection { Name = "RDP", Server = "host.example", UserName = new string('u', 1025) }] },
            basic with { AdminCards = [new AdminCard { Id = "uid:untrusted" }] }
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
        bar.SetTasks([("a", "b|c=d", false, null, null, false), ("e", "f", false, null, null, false)]);
        bar.SetTasks([("a", "b", false, null, null, false), ("c", "d|e=f", true, null, null, false)]);
        if (!bar.Controls.OfType<TabButton>().Select(t => t.Text).SequenceEqual(new[] { "b", "d|e=f" }))
            throw new InvalidOperationException("Taskbar cache confused different window sets.");

        // A live reader's state must remain stable on a timeout, including identity fields.
        using var probe = new CardReader();
        var first = probe.Snapshot("Kiosk self-test missing reader");
        for (int i = 0; i < 10; i++)
            if (probe.Snapshot("Kiosk self-test missing reader") != first)
                throw new InvalidOperationException("Unchanged reader state changed between polls.");

        // A large banner is stored at display size, and an oversized file on a share leaves its
        // message visible without making the client read the whole file into memory.
        using (var source = new Bitmap(3000, 1000))
        using (var encoded = new MemoryStream())
        {
            using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.Crimson);
            source.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
            using var picture = News.DecodePicture(encoded.ToArray());
            if (picture.Width != 1920 || picture.Height != 640 || ((Bitmap)picture).GetPixel(0, 0).ToArgb() != Color.Crimson.ToArgb())
                throw new InvalidOperationException("News image was not bounded and rendered correctly.");
        }
        var newsFolder = Path.Combine(Path.GetTempPath(), "KioskNewsLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(newsFolder);
        try
        {
            File.WriteAllText(Path.Combine(newsFolder, News.FileName), "[{\"Title\":\"Large image\",\"Image\":\"large.png\"}]");
            using (var large = File.Create(Path.Combine(newsFolder, "large.png"))) large.SetLength(News.MaxPictureBytes + 1L);
            var news = News.Load(newsFolder);
            if (news.Count != 1 || news[0].Picture != null)
                throw new InvalidOperationException("Oversized news picture hid the message or was decoded.");
            using (var small = new Bitmap(12, 12)) small.Save(Path.Combine(newsFolder, "shared.png"), System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(Path.Combine(newsFolder, News.FileName),
                "[{\"Title\":\"First\",\"Image\":\"shared.png\"},{\"Title\":\"Second\",\"Image\":\".\\\\shared.png\"}]");
            var reused = News.Load(newsFolder);
            try
            {
                if (reused.Count != 2 || reused[0].Picture == null || !ReferenceEquals(reused[0].Picture, reused[1].Picture))
                    throw new InvalidOperationException("Repeated news image was decoded more than once.");
            }
            finally { News.DisposePictures(reused); }
        }
        finally { Directory.Delete(newsFolder, recursive: true); }
    }
}
