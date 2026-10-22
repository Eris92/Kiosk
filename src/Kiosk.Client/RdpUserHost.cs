using System.ComponentModel;

namespace Kiosk.Client;

/// <summary>
/// The remote desktop of a signed-in person: this program started again as that person ("Zaloguj"), showing only the
/// RDP control with Remote Credential Guard. Their own Kerberos sign-in reaches the server and no password leaves
/// this PC (Credential Guard here would refuse to pass one anyway). The Kiosk embeds this window like any program.
/// No .rdp file is used, so Windows shows no "unknown remote connection" warning and does not read Default.rdp.
/// </summary>
internal static class RdpUserHost
{
    internal const string Argument = "--rdp-host";

    internal static int Run(string server, int port, int timeout, bool prompt)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Any(char.IsWhiteSpace) || port is < 1 or > 65535 || timeout is < 10 or > 300) return 1;
        using var form = new Form
        {
            Text = "Pulpit zdalny – " + server, FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized,
            BackColor = Color.Black, StartPosition = FormStartPosition.Manual
        };
        var host = new RdpHost { Dock = DockStyle.Fill };
        ((ISupportInitialize)host).BeginInit();
        form.Controls.Add(host);
        ((ISupportInitialize)host).EndInit();
        bool wasConnected = false;
        var connectingSince = DateTime.UtcNow;
        using var watch = new System.Windows.Forms.Timer { Interval = 1000 };
        form.Shown += (_, _) =>
        {
            try
            {
                host.CreateControl();
                var connection = new RdpConnection { Name = server, Server = server, Port = port, Authentication = RdpAuthentication.WindowsCurrentUser };
                RdpConfiguration.Apply(host, connection, form.ClientSize, prompt);
                connectingSince = DateTime.UtcNow;
                dynamic client = host.Client;
                client.Connect();
                Audit.Write("rdp_user_connect", server);
                watch.Start();
            }
            catch (Exception ex)
            {
                Audit.Write("rdp_user_error", ex.Message);
                MessageBox.Show(form, "Nie udało się połączyć z „" + server + "”: " + ex.Message, "Pulpit zdalny", MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.Close();
            }
        };
        // Disconnected (signed out, closed, network): this window goes away and the Kiosk removes its taskbar button.
        watch.Tick += (_, _) =>
        {
            try
            {
                int state = (int)host.Client.Connected;
                if (state == 1) wasConnected = true;
                else if (wasConnected && state == 0) form.Close();
                else if (DateTime.UtcNow - connectingSince > TimeSpan.FromSeconds(timeout))
                {
                    Audit.Write("rdp_user_timeout", server);
                    form.Close();
                }
            }
            catch (Exception ex) { Audit.Write("rdp_user_monitor_error", ex.Message); form.Close(); }
        };
        form.FormClosed += (_, _) =>
        {
            watch.Stop();
            try { if ((int)host.Client.Connected != 0) host.Client.Disconnect(); }
            catch (Exception ex) { Audit.Write("rdp_user_disconnect_error", ex.Message); }
        };
        Application.Run(form);
        return 0;
    }
}
