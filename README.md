# Kiosk - smart card / RDS test client

Fullscreen Windows launcher hosting Microsoft's RDP ActiveX control. Insert a
certificate smart card, select Connect, choose the smart-card credential and
enter your PIN in the Windows dialog. Card removal, reader failure, another card,
manual disconnect and 300 seconds of inactivity close the connection. Remote
applications are not terminated and the remote user is not logged off.

This is a test implementation. COM/PCSC, credential dialogs, PIN caching and
reconnection must be tested with your reader, middleware, PKI and RDS.
Fullscreen is not an OS security boundary. No OS lockdown, auto-logon,
firewall/registry changes or credential storage are performed by these files.

## Prerequisites

- Windows 11 x64 client with a PC/SC reader and certificate smart card, such as
  YubiKey PIV. UID-only RFID and FIDO2-only credentials cannot authenticate this
  RDP workflow. ATR is a change signal, not a user identity.
- Configured Windows Server RDS Session Host, appropriate RDS CALs and individual
  AD accounts. Windows 11 Pro cannot provide 3-4 concurrent local user sessions.
- AD/PKI smart-card logon certificates, account mapping, trusted certificate
  chain and reachable revocation endpoints. Check with `certutil -scinfo`.
- Trusted RDS TLS certificate matching the configured server FQDN; NLA enabled.
  The client rejects server authentication failures.
- Direct Session Host connection on the configured TCP port (default 3389).
  RD Gateway and broker/farm routing are not implemented in this version.
- Set ReaderName to the exact physical PC/SC reader name when Windows Hello,
  UICC or other virtual readers are present. Only that reader is monitored.
  With ReaderName empty, exactly one usable card across all readers is required.

Microsoft documentation:
https://learn.microsoft.com/en-us/windows/security/identity-protection/smart-cards/smart-card-and-remote-desktop-services

ActiveX API:
https://learn.microsoft.com/en-us/windows/win32/termserv/msrdpclient9notsafeforscripting

## Quick test - ready EXE

1. Open Actions -> Build Windows client, open a successful run and download
   Kiosk-win-x64. Extract the ZIP to C:\Kiosk. The client is self-contained;
   installing a separate .NET runtime is not required.
2. Edit client/client.json: set Server to your Session Host FQDN.
   Keep TestMode true during testing. IdleSeconds defaults to 300.
3. Run the read-only checks, then the client:

```powershell
Set-Location C:\Kiosk
.\scripts\Test-Prerequisites.ps1 -ConfigPath .\client\client.json
certutil.exe -scinfo
.\client\Kiosk.Client.exe
```

The PIN belongs in the Windows credential dialog. Select the smart-card tile
if multiple methods are shown. Authentication is performed by Windows/RDS,
not by a local card UID lookup. Enforce smart-card-only authentication on the
relevant AD accounts with Smart card is required for interactive logon. Scope
and test that policy first: it affects other interactive logons too. Without
that enforcement a password credential may also be offered.

After disconnect, remove and reinsert the card before another connection.
No automatic reconnect or credential reuse is requested by the client.
TestMode false removes the window border and test exit; it does not restrict
Alt+Tab, Ctrl+Alt+Del or Windows access. Production OS lockdown is separate.

## Build locally

Requires Windows x64 and .NET 8 SDK or a newer SDK that can target .NET 8:

```powershell
git clone https://github.com/Eris92/Kiosk.git
Set-Location Kiosk
.\scripts\Build.ps1
notepad.exe .\out\client\client.json
.\out\client\Kiosk.Client.exe
```

Scripts support Windows PowerShell 5.1 and PowerShell 7. The session launcher
requires STA. Sign scripts or use your approved execution policy; no policy
bypass is included. The EXE is unsigned; production signing/deployment is separate.

## Applications in the authenticated RDS user session

Copy scripts/Start-SessionLauncher.ps1 and config/apps.json to the Session Host.
Edit application paths and URL. The Enova path is only an example. Run as the
actual RDS user, without elevation:

```powershell
powershell.exe -NoProfile -STA -File C:\Kiosk\scripts\Start-SessionLauncher.ps1 -ConfigPath C:\Kiosk\config\apps.json
```

Buttons launch Notepad, Enova or Edge as the remote user. Web SSO depends on the
application and the RDS user's identity configuration; the launcher does not
implement SSO itself. Edge uses the user's normal profile to preserve context.
Closing the launcher does not close apps. Disconnect uses tsdiscon for its own
RDP session. No logoff or server-side process termination is used.

For production, put scripts/config in an administrator-managed directory with
read-only user access. After manual validation, deploy startup using a scoped
user logon policy or approved shortcut. No scheduled task or Explorer replacement
is installed automatically.

## RDS policy review

| Setting | Required behavior |
| --- | --- |
| Restrict each user to a single RDS session | Enabled: resume the existing user session |
| Do not allow smart card device redirection | Disabled: allow smart cards |
| Require NLA | Enabled |
| Time limit for disconnected sessions | Preserve for the required workflow |
| End session when time limits are reached | Disabled for this preservation workflow |
| Interactive logon: smart card removal behavior | Review on RDS; logoff conflicts with keeping apps |
| Credential saving / delegation | Do not save; review existing credentials and delegation policy |

Use scoped RDS collection/GPO settings. Check existing idle/disconnected-session
limits to avoid unexpected logoff. The client disables drive, printer and
clipboard redirection and automatic reconnect; enforce equivalent policies on
the server. Standard users and individual certificates are required.

## Acceptance tests

1. User A connects with card/PIN, opens apps, records session ID with quser.
   Remove card: desktop disappears locally; remote session becomes Disc and
   apps survive.
2. User B connects with their own card. Verify B cannot see A's desktop/apps.
3. A reconnects with PIN: verify the same session ID and application state.
4. Wait 300 seconds without local input: disconnect while applications survive.
5. Unplug the configured reader, stop Smart Card service or rapidly
   remove/reinsert: connection must close. PCSC event counters detect removal
   between polling cycles when supported by the reader stack.
6. Remove card while entering PIN, cancel credentials, use a wrong PIN, unplug
   network and reject TLS trust: no desktop should become visible without fresh
   authentication. Cancellation is cleaned up within ConnectTimeoutSeconds.
7. Restart client with another card. Verify no prior user identity is reused.
   Windows/provider PIN caching is outside this app: test middleware policy.
8. Inspect client logs: %LOCALAPPDATA%\Kiosk\Logs\YYYY-MM-DD.jsonl and server
   Security/TerminalServices logs. No PIN, password or card ID is logged.

## Limitations and rollback

Polling is 250 ms, not an instantaneous hardware interlock. Credential modal
dialogs and reader-specific event counters require hardware testing. Client
inactivity uses input in the local Windows session. The connection timeout is
bounded, but interactive provider behavior requires testing on Windows.

No machine installation changes: close using Exit test, remove any manually
deployed startup shortcut and delete the directory. Restore independently made
RDS/GPO changes from your export. Avoid logoff or killing remote apps when
preserving state. Runtime verification on Windows/RDS is required before rollout.

## Selecting the physical reader

For ACS ACR1252, set the following value in your client.local.json:

```json
"ReaderName": "ACS ACR1252 Dual Reader PICC 0"
```

Use the exact name shown on your machine. Windows Hello/UICC presence does not
block that configured reader. Removing the selected card or reader still closes
the connection even when virtual cards remain PRESENT. Selecting a reader scopes
monitoring; it does not filter Windows/RDP credential providers. Choose the
certificate belonging to the intended physical card in the Windows dialog.
