# Build, testing, packages and production deployment

## Build entry points
- global.json: .NET SDK 10.0.402 with rollForward disabled.
- src/Kiosk.Client/Kiosk.Client.csproj: net10.0-windows x64 WinForms + WebView2, self-contained when published.
- src/Kiosk.FidoBroker/FidoRun.csproj: net48 x64 broker; ConfigTool and NewsEditor are separate .NET 10 tools.
- scripts/Build.ps1: clean staged self-contained client output under out/client; explicit ConfigPath supported.
- scripts/Test.ps1: scripts/Test-Scripts.ps1 checks, scripts/Test-ProjectBrain.ps1 (memory paths, coverage, hooks, compact mode), locked build/self-tests, broker policy, editor and config checks.
- scripts/New-Package.ps1, Verify-Package.ps1, Build-CredentialProvider.ps1: package creation, native provider and inventory/signature verification.
- .github/workflows/build.yml: Windows CI tests and unsigned candidate validation.
- scripts/Deployment.Common.ps1 and scripts/Install-*.ps1, Set-Kiosk*.ps1: common deployment helpers and mutation/rollback.

## Safe commands (Windows x64, from repository root)
- Read-only machine precheck: `.\scripts\Test-Prerequisites.ps1`
- Local build: `.\scripts\Build.ps1`
- Client preview: `.\out\client\Kiosk.Client.exe`
- Automated tests: `.\scripts\Test.ps1 -ErrorsOnly`
- Unsigned package candidate: `.\scripts\New-Package.ps1` then `.\scripts\Verify-Package.ps1 -PackagePath .\out\package`

## Compact test output

scripts/Test.ps1 -ErrorsOnly delegates to scripts/Invoke-CompactTest.ps1, saves complete stdout/stderr under out/test-logs, and prints deduplicated failure lines (ignoring timestamps only). It returns the original exit code. Use the full log for forensic debugging. Agent hooks may additionally shorten repetitive shell output, keeping the raw copy in out/hook-logs; failed Claude Code commands are not filtered.

## Deployment risk
- Running a build does not install/replace the Windows shell. Install-KioskShell, CardService, CredentialProvider, sign-in and keyboard policies modify the host and should be trialed on isolated Windows machines with backups and rollback.
- Production: create signed package using New-Package -RequireSigned -SigningCertificateThumbprint; verify the signer and manifest; use installer signing checks and explicit SourceConfig.
- Rollback includes Install-KioskShell.ps1 -Uninstall, Install-KioskCardService.ps1 -Uninstall and Set-KioskSignInScreen.ps1 -Uninstall (review affected state first).
- CI and offline tests do not demonstrate production readiness. See docs/STATUS.md and docs/PRODUCTION_AUDIT.md for current outstanding acceptance steps.

## Claude and project index CI

- `.github/workflows/project-brain.yml`: checks the staged/committed tracked-file inventory against `.ai/source-index.json`, validates Claude skills/settings and deterministic module routes using Python 3.
- `scripts/project_brain.py`: `route` suggests module notes, `changes` reports modified Git blob entries, `update` writes the reference inventory, and `check` detects staleness.
- `.claude/skills/` contains scoped workflows; `.claude/agents/Explore.md` specifies Haiku for read-only discovery. Production build and sign-in readiness remain under separate testing.
