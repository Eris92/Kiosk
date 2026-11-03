# Runtime, UI and session orchestration

## Owned code
- src/Kiosk.Client/Program.cs: WinForms entry point and command-line modes, including configuration validation and service dispatch.
- src/Kiosk.Client/KioskForm.cs: main shell, user/card session dictionaries, window ownership, lifecycle, timers, RDP/application orchestration. Large file: locate symbols first.
- src/Kiosk.Client/KioskForm.SelfService.cs: SelfService home/help screen, WindowsSession vs legacy CardBroker flow, sign-out and card removal behavior.
- src/Kiosk.Client/DesktopView.cs, Shell.cs, ScreenHost.cs, IdleScreen.cs, InputWatch.cs, DisplayRotation.cs: desktop, taskbar/menu, windows/screens, idle and input behavior.
- src/Kiosk.Client/FirstRunSetup.cs, FirstSignIn.cs: first-run/sign-in UI; KioskIcons.cs / ColorPicker.cs: visual helpers.
- src/Kiosk.Client/People.cs, WindowsKey.cs, BuildVersion.cs: small UI and runtime helpers; locate symbols before assuming behavior.

## Modes and flow
- SessionMode.SelfService (set in config/client.json; the Config default is Kiosk): help station, no workstation menu/RDP; WindowsSession uses existing Windows logon for WebView2/SSO.
- SessionMode.Kiosk: per-card workstation sessions, browser, notes, files, apps and RDP.
- SessionMode.WindowsShell: workstation UI as the user's Windows shell; Windows controls the sign-in/lock boundary.
- Typical flow: Program.Main -> KioskForm -> active Config -> mode-specific UI -> user session / windows / external actions.
- Behavior and readiness are not equivalent: read docs/STATUS.md before claiming SSO or card sign-in is validated.

## Contract / risk points
- Session ownership, removal, sign-out, lock/unlock, idle timeout and orphaned child processes cross into identity and workloads.
- Before changing timer/lock logic, inspect WindowsSession.cs, Watchdog.cs and the relevant CardService interaction.
- Test: scripts/Test.ps1 with UI self-tests; physical Windows/Entra/reader tests are separate.
