# Identity, card, broker and Windows session boundaries

## Source map
- src/Kiosk.Client/CardService.cs: KioskCard LocalSystem service; bounded local named-pipe requests, card operations, configuration/secret mediation and watchdog integration.
- src/Kiosk.Client/Ctap.cs, CardPin.cs, CardVault.cs: CTAP/PCSC/card PIN and local credential logic.
- src/Kiosk.Client/FidoSignIn.cs and KioskForm.SelfService.cs: legacy embedded card-broker sign-in.
- src/Kiosk.Client/WindowsSession.cs, WindowsHello.cs, UserLogon.cs, FirstSignIn.cs: Windows sign-in, session state and identity helpers.
- src/Kiosk.Client/SharedAccounts.cs, NewsAccount.cs: protected shared-account use; inspect secret access paths before touching.
- src/Kiosk.Client/CardPageHost.cs (hidden Program.cs argument), LocalAdministrators.cs, BoundedPipeLine.cs, src/Common/BoundedLineReader.cs: card page host, administrator checks and bounded IPC reads.
- src/Kiosk.FidoBroker/src/Program.cs, KioskBroker.cs, BrokerPolicy.cs, WindowsLogon.cs, ProviderHost.cs, NativeCtapProbe.cs: .NET Framework 4.8 broker, native provider interop, process/policy checks.
- src/Kiosk.FidoBroker/src/: remaining broker support files (HostForm.cs is large; locate symbols first).
- src/Kiosk.CredentialProvider/KioskCredentialProvider.cpp: Windows native sign-in tile wrapper of the OS FIDO provider.

## Authentication paths
- Preferred SelfService SSO: Windows/Entra signs the user in first, then Kiosk runs as that Windows user and the WebView2 account may use SSO depending on tenant/site policy.
- Legacy CardBroker: LocalSystem service starts FidoRun and mediates PIN/sign-in; specific Windows DLL behavior and physical card removal need target-image testing.
- Credential Provider changes the sign-in tile presentation; it does not implement a new Entra authentication protocol.

## Boundaries / evidence
- Trust boundaries: normal-user GUI <-> LocalSystem KioskCard pipe; KioskCard <-> broker process; local Windows logon <-> Entra/site authentication; machine secrets <-> user sessions.
- Treat SID, process origin, pipe ACL, token lifetime, executable allowlist, card removal, session cleanup and log redaction as security invariants.
- Use scripts/Test-BrokerPolicy.ps1 and scripts/Test.ps1 for code-level checks; real FIDO2, PRT/SSO and multi-user acceptance require dedicated hardware/tenant tests.
- Never infer that RDP/Edge gains the correct identity merely because a local card PIN succeeds; see docs/STATUS.md.
