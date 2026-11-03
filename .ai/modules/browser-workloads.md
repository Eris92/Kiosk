# Browser, apps, local files, commands and RDP

## Source map
- src/Kiosk.Client/BrowserView.cs, BrowserProfileCleanup.cs: WebView2 navigation, tabs, profile ownership, downloads and profile cleanup.
- src/Kiosk.Client/ApplicationView.cs, SessionCommand.cs: embedded/external programs, child-process lifetime, session-scoped commands.
- src/Kiosk.Client/FilesView.cs, FilesHost.cs, NotesView.cs: file browsing, controlled opening and notes.
- src/Kiosk.Client/RdpConfiguration.cs, RdpCredentials.cs, RdpUserHost.cs: embedded RDP policy, authentication and user-context RDP hosting.
- src/Kiosk.Client/PageScripts.cs: configured page actions.
- src/Kiosk.Client/KioskForm.cs: selection, attachment and lifecycle of all the above per session.

## Contracts / security
- BrowserView uses WebView2 with per-session profiles (and a separate shared InPrivate environment for guest/help pages). Inspect profile and cleanup code for any identity change.
- Browser restrictions, protocol/navigation filtering and downloads are defense in depth, not an OS sandbox.
- RDP uses Windows client COM policy: TLS server authentication, CredSSP and redirection choices; inspect RdpConfiguration before touching SSO or credentials.
- Workloads must end or detach safely at card removal, Windows sign-out, crash and shell restart. Check KioskForm and Watchdog as callers.

## Validation
- scripts/Test.ps1 covers selected embedded UI and policy flows; real target RDS (NLA, certificates, SSO) and externally hosted applications need manual acceptance testing.
