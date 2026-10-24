# Kiosk

A Windows shell for shared PCs. People sign in with a FIDO2 card (or Windows Hello) and get a full-screen
Kiosk instead of Explorer: remote desktops (RDP), programs, a browser limited to the sites an administrator
allows, company messages, Szybka pomoc pages and a lock that never shows another person's work.
Konfiguracja is visible only to Kiosk administrators.

## Download

Every push builds a GitHub release `build-N` with the package:
https://github.com/Eris92/Kiosk/releases/latest/download/Kiosk-win-x64.zip

Unzip it to e.g. `C:\Kiosk`. It keeps the repository layout: `out\client` (the program),
`scripts`, `config`. Installed Kiosks update themselves to a newer `build-N`
(Konfiguracja → Aktualizacja, Ctrl+Alt+U).

## Requirements

- Windows 10/11 x64, with a 64-bit PowerShell for the scripts. The scripts refuse a 32-bit PowerShell.
- **.NET 10 Desktop Runtime x64.** The Kiosk does not carry its own .NET. The `Install-*.ps1` scripts and
  the in-app update install it when it is missing. They use Microsoft's installer from the .NET release list,
  checked against the SHA-512 in that list. winget is not used: it does not work in the kiosk shell.
- Microsoft Edge WebView2 Runtime (part of current Windows).
- A PC/SC card reader and FIDO2 cards. For card sign-in to Windows, Entra ID or domain accounts with
  security key sign-in.
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
Win32 app. Verify it on a test device first.

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
