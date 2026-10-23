namespace Kiosk.Client;

/// <summary>One credential/redirection policy for embedded and user-context RDP controls.</summary>
internal static class RdpConfiguration
{
    internal static void Apply(RdpHost host, RdpConnection connection, Size size, bool promptForCredentials)
    {
        dynamic client = host.Client;
        client.Server = connection.Server;
        if (connection.UserName.Length > 0) client.UserName = connection.UserName;
        client.DesktopWidth = Math.Max(800, size.Width);
        client.DesktopHeight = Math.Max(600, size.Height);
        dynamic settings = client.AdvancedSettings7;
        settings.RDPPort = connection.Port;
        settings.EnableCredSspSupport = true;
        settings.AuthenticationLevel = 1; // Reject server authentication failures.
        // Only the smart-card profile needs a redirected card. Windows Hello is
        // bound to the local Windows account; it is not a remote smart card.
        settings.RedirectSmartCards = connection.Authentication == RdpAuthentication.SmartCard;
        settings.RedirectDrives = false;
        settings.RedirectPrinters = false;
        settings.RedirectClipboard = false;
        settings.EnableAutoReconnect = false;
        client.FullScreen = false;
        settings.SmartSizing = true;
        settings.DisplayConnectionBar = false;
        settings.HotKeyFullScreen = 0;
        var credentials = (IRdpCredentials)(object)client;
        // RequireCredentialPrompt=false lets Windows use the signed-in (card / Hello / Entra) identity
        // without forcing the dialog; prompting is still allowed when no such credential exists.
        credentials.SetPromptForCredentials(promptForCredentials);
        credentials.SetPromptForCredsOnClient(true);
        credentials.SetAllowPromptingForCredentials(true);
        credentials.SetAllowCredentialSaving(false);
        if (credentials.GetPromptForCredentials() != promptForCredentials || !credentials.GetPromptForCredsOnClient() ||
            !credentials.GetAllowPromptingForCredentials() || credentials.GetAllowCredentialSaving())
            throw new InvalidOperationException("Nie udało się zastosować zasad pytania o poświadczenia RDP.");
        if (connection.Authentication == RdpAuthentication.WindowsCurrentUser)
        {
            var extended = (IRdpExtendedSettings)(object)client;
            object enabled = true;
            extended.SetProperty("RedirectedAuthentication", ref enabled);
        }
        if (connection.Authentication == RdpAuthentication.EntraId)
        {
            var extended = (IRdpExtendedSettings)(object)client;
            object enabled = true;
            // Write-only for this property: the control rejects reading it back (E_UNEXPECTED),
            // so a successful put is the confirmation.
            extended.SetProperty("EnableRdsAadAuth", ref enabled);
        }
    }
}
