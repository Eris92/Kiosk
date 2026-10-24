namespace Kiosk.Client;

internal static class RdpLaunchTests
{
    internal static void Run()
    {
        if (!Configuration.IsRdpServer("localhost") || !Configuration.IsRdpServer("10.0.0.1") ||
            !Configuration.IsRdpServer("::1") || !Configuration.IsEntraRdpServer("host.example.org"))
            throw new InvalidOperationException("RDP server validation rejected a hostname or IP address.");
        foreach (var authentication in Enum.GetValues<RdpAuthentication>())
        {
            var connection = new RdpConnection
            {
                Name = "Biuro 1", Server = "host.example.org", Port = 3390,
                UserName = @"DOMAIN\Jan Kowalski", Authentication = authentication
            };
            var line = RdpUserHost.ArgumentsFor(connection, 73, true);
            var parts = line.Split(' ');
            var parsed = parts.Length == 2 ? RdpUserHost.Parse(parts[1]) : null;
            if (parts[0] != RdpUserHost.Argument || parsed?.Connection != connection ||
                parsed.Timeout != 73 || !parsed.Prompt || RdpUserHost.WindowName(connection) != "Pulpit zdalny — Biuro 1")
                throw new InvalidOperationException("RDP helper lost an authentication profile or user name.");
        }
        if (RdpUserHost.Parse("not-valid!") != null || RdpUserHost.Parse("") != null)
            throw new InvalidOperationException("RDP helper accepted an invalid request.");
        var oversized = new RdpConnection { Name = new string('N', 129), Server = "host.example.org" };
        if (RdpUserHost.Parse(RdpUserHost.ArgumentsFor(oversized, 60, true).Split(' ')[1]) != null)
            throw new InvalidOperationException("RDP helper accepted an oversized connection name.");
        foreach (var server in new[] { "https://host.example.org", "host.example.org/path", "host.example.org:3389" })
        {
            var invalid = new RdpConnection { Name = "Invalid", Server = server };
            if (RdpUserHost.Parse(RdpUserHost.ArgumentsFor(invalid, 60, true).Split(' ')[1]) != null)
                throw new InvalidOperationException("RDP helper accepted a URI or an inline port as a server name.");
        }
        var entraIp = new RdpConnection { Name = "Entra IP", Server = "10.0.0.1", Authentication = RdpAuthentication.EntraId };
        if (RdpUserHost.Parse(RdpUserHost.ArgumentsFor(entraIp, 60, true).Split(' ')[1]) != null)
            throw new InvalidOperationException("RDP helper accepted an Entra profile without an FQDN.");
    }
}
