---
name: kiosk-release
description: Create and verify Kiosk release candidate or signed package with least-privilege deployment and rollback checks.
disable-model-invocation: true
---
Load .ai/modules/delivery.md and README.md build section. Windows x64: `.\scripts\Test.ps1 -ErrorsOnly`; `.\scripts\New-Package.ps1`; `.\scripts\Verify-Package.ps1 -PackagePath .\out\package`. Production requires certificate-approved `-RequireSigned -SigningCertificateThumbprint`, subsequent verification with trusted signer, explicit SourceConfig and a clean Git worktree. Never deploy/replace shell without explicit user instruction and an isolated pilot. Print only failures and artifact locations.
