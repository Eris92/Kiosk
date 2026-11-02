---
name: kiosk-shell
description: Review Shell Launcher, Windows sign-in UI, keyboard filter and shared-PC policy changes with precheck/rollback.
---
Read .ai/modules/delivery.md and runtime-ui.md. PRECHECK -> BACKUP/EXPORT -> WHATIF -> APPLY (only if explicitly requested, on isolated test device) -> VERIFY -> ROLLBACK. Scripts/Install-KioskShell.ps1, Set-KioskSignInScreen.ps1, Set-KioskKeyboardLock.ps1 and CredentialProvider installer mutate host security and recovery paths. Do not silently install services or change shell on an operator workstation. Document exact rollback commands and required privileges.
