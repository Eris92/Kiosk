---
name: kiosk-identity
description: Review FIDO2, PIN, Entra SSO, WindowsSession, KioskCard service, broker or Credential Provider changes with trust-boundary checks.
---
Load .ai/modules/identity.md, then narrow code locations. Check attacker prerequisites -> trust boundaries -> evidence -> detection -> impact -> mitigation. Preserve SID/process origin checks, named-pipe ACLs, token isolation, card removal, cleanup and fail-closed behavior. Do not conflate Windows sign-in with verified PRT/tenant SSO or RDP identity. Use scripts/Test-BrokerPolicy.ps1 and relevant tests; physical Windows/Entra/FIDO acceptance remains separate.
