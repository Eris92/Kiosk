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

    internal static int Run(string server, int port)
    {
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
        var watch = new System.Windows.Forms.Timer { Interval = 1000 };
        form.Shown += (_, _) =>
        {
            try
            {
                host.CreateControl();
                dynamic client = host.Client;
                client.Server = server;
                client.DesktopWidth = Math.Max(800, form.ClientSize.Width);
                client.DesktopHeight = Math.Max(600, form.ClientSize.Height);
                dynamic settings = client.AdvancedSettings7;
                settings.RDPPort = port;
                settings.EnableCredSspSupport = true;
                settings.AuthenticationLevel = 1; // Reject server authentication failures.
                settings.RedirectSmartCards = true;
                settings.RedirectDrives = false;
                settings.RedirectPrinters = false;
                settings.RedirectClipboard = false;
                settings.EnableAutoReconnect = false;
                settings.SmartSizing = true;
                settings.DisplayConnectionBar = false;
                client.FullScreen = false;
                var credentials = (IRdpCredentials)(object)client;
                credentials.SetPromptForCredentials(false);
                credentials.SetPromptForCredsOnClient(true);
                credentials.SetAllowPromptingForCredentials(true); // Only if the server refuses Remote Credential Guard.
                credentials.SetAllowCredentialSaving(false);
                var extended = (IRdpExtendedSettings)(object)client;
                object on = true;
                extended.SetProperty("DisableCredentialsDelegation", ref on);
                on = true;
                extended.SetProperty("RedirectedAuthentication", ref on); // Remote Credential Guard
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
            int state = (int)host.Client.Connected;
            if (state == 1) wasConnected = true;
            else if (wasConnected && state == 0) form.Close();
        };
        form.FormClosed += (_, _) => watch.Dispose();
        Application.Run(form);
        return 0;
    }
}
