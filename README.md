# Kiosk

A Windows shell for shared PCs. Konfiguracja is visible only to Kiosk administrators.

## Operating modes

| Mode | Use |
| --- | --- |
| `SelfService` (default) | Walk-up help station with announcements and Szybka pomoc pages. With `SelfServiceAuthentication=WindowsSession`, the person signs in to Windows with an Entra security key or Windows Hello; the pages run in that Windows account and can use its SSO. There is no RDP/application menu. |
| `Kiosk` | Longer work sessions with an embedded browser, notes, files, configured applications and RDP profiles in one window. Each card has a separate session. |
| `WindowsShell` | The same workstation UI as `Kiosk`, started as the signed-in Windows user in place of Explorer. Windows and Entra SSO depend on that user's Windows sign-in. |

The initial deployment target is `SelfService`. The supported SSO path is `SelfServiceAuthentication=WindowsSession`: deploy Kiosk as the shell for standard-user Windows accounts and let Windows/Entra authenticate the person before Kiosk starts. The signed-in person's WebView2 profile enables OS account SSO; the site and tenant must also permit it. `CardBroker` is retained for older installations and depends on a pinned Windows DLL version, so validate it on the exact Windows image before use. On an unjoined local PC, help pages can be tested but Entra SSO cannot be verified. Password-based domain sign-in paired to a card for the workstation mode remains a separate deployment path.

For an Entra rollout, join or hybrid join the test PC, enable the organization's Windows security key or Hello sign-in policy, install a signed Kiosk build, and deploy `config/client.json` with `SelfServiceAuthentication=WindowsSession`. After the person signs in at the Windows screen, Kiosk shows Szybka pomoc. **Zakończ sesję** signs out of Windows. The inactivity settings disconnect the session and then sign it out. Test two real accounts and the target SSO websites before using it with the public. A local Windows account does not acquire an Entra SSO token merely by opening Kiosk.

## Download

CI builds and verifies an unsigned deployment candidate on `main` and `codex/**` branches. A signed release for automatic updates is published separately after validation. Build locally with `scripts\New-Package.ps1`; the candidate is placed in `out\package`.

## Requirements

- Windows 10/11 x64, with a 64-bit PowerShell for the scripts. The scripts refuse a 32-bit PowerShell.
- The client package carries its own .NET 10 runtime. The separate configuration and news editor tools need .NET 10 Desktop Runtime x64.
- Microsoft Edge WebView2 Runtime (part of current Windows).
- For `SelfService` with Windows sign-in: an Entra joined or hybrid joined PC, an enabled security key or Windows Hello method, and a standard-user account. For the legacy in-app `CardBroker`, a PC/SC reader and FIDO2 card are also required.
- `.\scripts\Test-Prerequisites.ps1` checks the PC and changes nothing.

## Installation (as administrator)

| Script | What it does |
| --- | --- |
| `Install-KioskShell.ps1` | Makes the PC a shared kiosk. The Kiosk becomes the Windows shell, in `C:\Program Files\Kiosk`, with its configuration in `C:\ProgramData\Kiosk\client.json`. `-Uninstall` reverts it. |
| `Install-KioskCardService.ps1` | The **Kiosk Card** service (LocalSystem). It handles the card PIN, card sign-in, shared accounts, updates and the watchdog that restarts a crashed or hung Kiosk. |
| `Set-KioskSignInScreen.ps1` | The Windows sign-in screen in the Kiosk look, with security key sign-in. It also removes the sign-in tile of old test builds. `-SecurityKeyOnly` hides the password tiles. |
| `Set-KioskKeyboardLock.ps1` | Locks Ctrl+Alt+Del options and Windows shortcuts in Windows itself. No program can block Ctrl+Alt+Del. |
| `Set-KioskConfigSource.ps1` | Konfiguracja z serwera: one configuration on a share for many PCs. |
| `Set-KioskSharedAccount.ps1` | Shared accounts (`accounts.kiosk`) for pages and network folders. |
| `Install-KioskConfig.ps1` | An administrator's PC for preparing the configuration. It adds Start menu shortcuts and leaves Explorer as the shell. |

All scripts support `-WhatIf`.

Intune: `config/intune/ShellLauncher.xml` (Shell Launcher CSP, Enterprise/Education) plus the Kiosk as a
Win32 app. Replace its versioned client path with the installed release path before assigning it. Verify it on a test device first. On Windows Pro, the installer can set a shell for future user profiles, but it does not change an existing profile's current Explorer session.

## Configuration

`config/client.json` is the starting configuration. Everything is edited in **Konfiguracja**. A Kiosk
administrator opens it from the Menu, and any Windows administrator with **Ctrl+Alt+K**, which runs it
elevated. Saving reloads the running Kiosk.

- `TestMode` (off unless set) lets everyone close the Kiosk, and makes Uruchom ponownie / Wyłącz do nothing.
  Use it only on a test PC.
- **Konfiguracje Kiosków** (`out\client\KioskKonfiguracje.exe`): copy it into a main folder whose subfolders are
  configurations (the folder name is the configuration's name, e.g. `\\serwer\Kiosk\Recepcja\client.json`, the
  folder a PC's "Konfiguracja z serwera" points at). It lists them, creates new ones (empty or a copy) and edits one in
  the same Konfiguracja as the Kiosk's, as whoever runs it. It needs the Kiosk installed on that PC.
- Company messages are edited with **KioskKomunikaty.exe** (in `out\client\news-example`). It runs with
  normal user rights and points at the server folder with messages and statistics.
- Logs are in `%LOCALAPPDATA%\Kiosk\Logs\YYYY-MM-DD.jsonl`. Konfiguracja → Statystyki → "Logi" sets a size
  limit per folder. No PIN, password or card ID is logged.

## Security notes

- The Kiosk is not an OS security boundary on its own. Use it together with `Install-KioskShell.ps1`,
  `Set-KioskKeyboardLock.ps1` and standard (non-administrator) accounts.
- The browser blocks downloads of programs and scripts, and has no download panel. It has no print, save
  or open-file dialogs (Ctrl+P / Ctrl+S / Ctrl+O). With "tylko strony z listy" it opens only the configured
  sites.
- The browser cancels HTTP authentication challenges before a password can be entered. A manual Windows
  username and password prompt is available only for HTTPS sites and closes when its page closes.
- Pliki opens documents, never programs or scripts.
- The Kiosk Card service accepts requests only from the installed Kiosk in Program Files. Clients trust
  only the real service (session 0).

## Build

Requires Windows x64 and the .NET 10 SDK:

```powershell
git clone https://github.com/Eris92/Kiosk.git
Set-Location Kiosk
.\scripts\Build.ps1
.\out\client\Kiosk.Client.exe
```

`out\client\fido\FidoRun.exe` is the card + PIN sign-in. It is shown inside the Kiosk window and started by
the Kiosk Card service.

## Applications in an RDS session

`scripts/Start-SessionLauncher.ps1` with `config/apps.json` is a small launcher for a Remote Desktop
Session Host. It is not part of the kiosk package. Run it as the RDS user, without elevation:

```powershell
powershell.exe -NoProfile -STA -File C:\Kiosk\scripts\Start-SessionLauncher.ps1 -ConfigPath C:\Kiosk\config\apps.json
```

## Rollback

`Install-KioskShell.ps1 -Uninstall`, `Install-KioskCardService.ps1 -Uninstall` and
`Set-KioskSignInScreen.ps1 -Uninstall` restore Windows' own shell, services and sign-in screen.
An older Kiosk version can be installed from its release package (`build-N`).
