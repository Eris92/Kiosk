# Kiosk

A Windows shell for shared PCs. Konfiguracja is visible only to Kiosk administrators.

## Operating modes

| Mode | Use |
| --- | --- |
| `Announcements` | Only announcements and the screensaver; no person can unlock applications or the Windows shell from this mode. |
| `Kiosk` | Workstation with the application menu and taskbar. A person signs in inside Kiosk with a card; ending their session keeps the workstation's Windows account signed in. |
| `WindowsShell` | Kiosk replaces Explorer for the person already signed in to Windows. Ending the session signs that Windows account out. There is no in-app authentication choice. |
| `SelfService` | Walk-up help station with announcements and Szybka pomoc. A person signs in inside Kiosk with a card; ending their session keeps the workstation's Windows account signed in. |

`Kiosk` and `SelfService` offer `CardAuthentication=Sso` or `CardAuthentication=Local`. SSO uses the card and PIN to obtain the person's Windows token for their pages and applications; the target site and tenant must also permit SSO. Local uses the card PIN and an assigned Windows login/password, protected with the card's FIDO2 hmac-secret, and runs the person's programs in that assigned account. The Local and SSO paths are still pilot features pending acceptance on the target image. An actual PC/SC insertion/removal is required; removing the card closes the person's pages and then their Kiosk session after `CardRemovedSeconds`. The separate workstation Windows session remains signed in. The legacy `SelfServiceAuthentication` field is read for old configurations but does not select the current sign-in flow. The older CardBroker adapter must not be considered verified without a test on the exact Windows image.

For an Entra rollout, join or hybrid join the test PC, enable the organization's security key sign-in policy, install a signed Kiosk build, and deploy an explicit `-SourceConfig` with `SessionMode=SelfService` and `CardAuthentication=Sso`. **Zakończ sesję** closes only the person's Kiosk session. Test two ordinary accounts, their process SID and browser profiles, and the target SSO websites before using it with the public. A local Windows account does not acquire an Entra SSO token merely by opening Kiosk.

## Download

CI builds and verifies an unsigned deployment candidate on `main` and `codex/**` branches. Build locally with `scripts\New-Package.ps1`; the candidate is placed in `out\package`. Updates are installed explicitly from a new versioned package.

For a production package, run `New-Package.ps1 -RequireSigned -SigningCertificateThumbprint <thumbprint>` on a clean checkout, then `Verify-Package.ps1 -PackagePath <package> -RequireSigned -TrustedSignerThumbprint <thumbprint>`. Format 2 includes an Authenticode signed seal for the SHA-256 of `package.json`; the manifest covers configuration, scripts and binaries. Deploy from the verified package with `Install-KioskShell.ps1 -RequireSigned -TrustedSignerThumbprint <thumbprint>` and the same switches on any other Kiosk installer used. Supply site-specific configuration through `-SourceConfig` rather than editing a signed package.

## Requirements

- Windows 10/11 x64, with a 64-bit PowerShell for the scripts. The scripts refuse a 32-bit PowerShell.
- The client package carries its own .NET 10 runtime. The separate configuration and news editor tools need .NET 10 Desktop Runtime x64.
- Microsoft Edge WebView2 Runtime (part of current Windows).
- For card sign-in inside `SelfService` or `Kiosk`: a PC/SC reader and FIDO2 card. Entra SSO additionally needs an Entra joined or hybrid joined PC, an enabled security key sign-in method, and ordinary test accounts.
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

`Install-KioskShell.ps1` keeps an existing `%ProgramData%\Kiosk\client.json` during an ordinary upgrade. To replace it deliberately, pass `-SourceConfig` with the prepared JSON file; this takes precedence even when the installed configuration exists. For a SelfService SSO pilot, check that the selected file sets `SessionMode=SelfService` and `CardAuthentication=Sso`, then run the installer with that explicit source. The installer reports its selected source and validates the resulting configuration before changing the shell. Use `-WhatIf` to check source selection without applying the installation.

`Install-KioskConfig.ps1` preserves an existing configuration on an ordinary editor upgrade. Pass `-SourceConfig <client.json>` to replace it deliberately. The installer validates one captured copy using the destination configuration directory for relative logo and news paths, then writes that same copy atomically; it stops if the existing file changes during staging.

Intune: `config/intune/ShellLauncher.xml` (Shell Launcher CSP, Enterprise/Education) plus the Kiosk as a
Win32 app. Replace its versioned client path with the installed release path before assigning it. Verify it on a test device first. On Windows Pro, the installer can set a shell for future user profiles, but it does not change an existing profile's current Explorer session.

## Configuration

`config/client.json` is the starting configuration. Everything is edited in **Konfiguracja**. A Kiosk
administrator opens it from the Menu, and any Windows administrator with **Ctrl+Alt+K**, which runs it
elevated. Saving reloads the running Kiosk. The `client.json` file is limited to 4 MB.

- `TestMode` (off unless set) lets everyone close the Kiosk, and makes Uruchom ponownie / Wyłącz do nothing.
  Use it only on a test PC.
- **Konfiguracje Kiosków** (`out\client\KioskKonfiguracje.exe`): copy it into a main folder whose subfolders are
  configurations (the folder name is the configuration's name, e.g. `\\serwer\Kiosk\Recepcja\client.json`, the
  folder a PC's "Konfiguracja z serwera" points at). It lists them, creates new ones (empty or a copy) and edits one in
  the same Konfiguracja as the Kiosk's, as whoever runs it. A copied configuration keeps its assets and messages but
  starts without the source configuration's `Stats` history. Saving checks for edits made by someone else and keeps the
  previous `client.json` as `client.json.bak`. It needs the Kiosk installed on that PC.
- Company messages are edited with **KioskKomunikaty.exe** (in `out\client\news-example`). It runs with
  normal user rights and points at the server folder with messages and statistics. `kiosk-news.json` supports
  up to 4 MB and 2048 messages; pictures and videos are separate files.
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
