using System.ComponentModel;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// The remote desktop of a signed-in person: this program starts again as that person ("Zaloguj") and shows only
/// the configured RDP control. WindowsCurrentUser enables Remote Credential Guard; SmartCard redirects the card;
/// EntraId uses the RDS Entra authentication property. The Kiosk embeds this window like any program.
/// No .rdp file is used; server TLS/NLA validation and any credential prompt still apply.
/// </summary>
internal static class RdpUserHost
{
    internal const string Argument = "--rdp-host";
    internal static string WindowName(RdpConnection connection) => "Pulpit zdalny — " + connection.Name;

    internal sealed record LaunchRequest(RdpConnection Connection, int Timeout, bool Prompt);

    internal static string ArgumentsFor(RdpConnection connection, int timeout, bool prompt)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new LaunchRequest(connection, timeout, prompt));
        return Argument + " " + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static LaunchRequest? Parse(string encoded)
    {
        if (encoded.Length is < 1 or > 8192 || encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return null;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            var payload = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            var request = JsonSerializer.Deserialize<LaunchRequest>(payload);
            var connection = request?.Connection;
            return connection is { Server.Length: > 0 and <= 255, Name.Length: > 0 and <= 128, UserName.Length: <= 1024 } &&
                   !connection.Server.Any(char.IsWhiteSpace) && connection.Port is >= 1 and <= 65535 &&
                   Enum.IsDefined(connection.Authentication) && request!.Timeout is >= 10 and <= 300
                ? request : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException) { return null; }
    }

    internal static int Run(string encoded)
    {
        var request = Parse(encoded);
        if (request == null) return 1;
        var connection = request.Connection;
        var server = connection.Server;
        var timeout = request.Timeout;
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
                RdpConfiguration.Apply(host, connection, form.ClientSize, request.Prompt);
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
