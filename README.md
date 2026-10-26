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

CI builds and verifies an unsigned deployment candidate on `main` and `codex/**` branches. Build locally with `scripts\New-Package.ps1`; the candidate is placed in `out\package`. Updates are installed explicitly from a new versioned package.

For a production package, run `New-Package.ps1 -RequireSigned -SigningCertificateThumbprint <thumbprint>` on a clean checkout, then `Verify-Package.ps1 -PackagePath <package> -RequireSigned -TrustedSignerThumbprint <thumbprint>`. Format 2 includes an Authenticode signed seal for the SHA-256 of `package.json`; the manifest covers configuration, scripts and binaries. Deploy from the verified package with `Install-KioskShell.ps1 -RequireSigned -TrustedSignerThumbprint <thumbprint>` and the same switches on any other Kiosk installer used. Supply site-specific configuration through `-SourceConfig` rather than editing a signed package.

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
| `Install-KioskCardService.ps1` | The **Kiosk Card** service (LocalSystem). It handles the card PIN, card sign-in, shared accounts and the watchdog that restarts a crashed or hung Kiosk. |
| `Set-KioskSignInScreen.ps1` | The Windows sign-in screen in the Kiosk look, with security key sign-in. It also removes the sign-in tile of old test builds. `-SecurityKeyOnly` hides the password tiles. |
| `Set-KioskKeyboardLock.ps1` | Uses Keyboard Filter on Enterprise/Education/IoT or Windows policies on Pro. Saves previous values for `-Uninstall`; on Enterprise, the optional feature remains enabled after rule rollback. |
| `Set-KioskConfigSource.ps1` | Konfiguracja z serwera: one configuration on a share for many PCs. |
| `Set-KioskSharedAccount.ps1` | Shared accounts (`accounts.kiosk`) for pages and network folders. |
| `Install-KioskConfig.ps1` | An administrator's PC for preparing the configuration. It stages a verified client in a versioned directory and updates only its editor/preview shortcuts; Explorer remains the shell. `-Uninstall` removes those shortcuts and retains the configuration and binaries. |

All scripts support `-WhatIf`.

Keyboard Filter needs a restart after the Windows feature is first enabled; run `Set-KioskKeyboardLock.ps1` again to configure its rules. The script fails if a required predefined key or the service is unavailable. On Windows Pro, Ctrl+Alt+Del still opens the Windows security screen with its configured options hidden.

`Install-KioskShell.ps1` keeps an existing `%ProgramData%\Kiosk\client.json` during an ordinary upgrade. To replace it deliberately, pass `-SourceConfig` with the prepared JSON file; this takes precedence even when the installed configuration exists. For a SelfService migration to Windows sign-in, check that the selected file sets `SessionMode=SelfService` and `SelfServiceAuthentication=WindowsSession`, then run the installer with that explicit source. The installer reports its selected source and validates the resulting configuration before changing the shell. Use `-WhatIf` to check source selection without applying the installation.

`Install-KioskConfig.ps1` preserves an existing configuration on an ordinary editor upgrade. Pass `-SourceConfig <client.json>` to replace it deliberately. The installer validates one captured copy using the destination configuration directory for relative logo and news paths, then writes that same copy atomically; it stops if the existing file changes during staging.

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
- Background commands from Menu belong to the current session. Closing that session stops the command and its child processes; a running command is included in the work confirmation.
- The Kiosk Card service accepts requests from the installed Kiosk in Program Files or an elevated
  administrator. Operations that return shared secrets or launch sign-in still require the installed
  Kiosk. Clients trust only the real service (session 0).

## Build

Requires Windows x64 and .NET SDK 10.0.402 (pinned in `global.json`):

```powershell
git clone https://github.com/Eris92/Kiosk.git
Set-Location Kiosk
.\scripts\Build.ps1
.\out\client\Kiosk.Client.exe
```

`out\client\fido\FidoRun.exe` is the card + PIN sign-in. It is shown inside the Kiosk window and started by
the Kiosk Card service.

## Rollback

`Install-KioskShell.ps1 -Uninstall`, `Install-KioskCardService.ps1 -Uninstall` and
`Set-KioskSignInScreen.ps1 -Uninstall` restore Windows' own shell, services and sign-in screen.
`Install-KioskConfig.ps1 -Uninstall` removes only its configuration-editor shortcuts. A previous verified
package can be redeployed into a new versioned directory if an application rollback is needed.
