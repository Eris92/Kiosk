# Kiosk Project Brain: routing index

Read this small file first, then only the relevant module documents. Avoid reading the large source files wholesale. Verify against current code; .scriptsTest-ProjectBrain.ps1 checks paths and coverage.

## Project in one paragraph
Windows x64 shared-PC shell: .NET 10 WinForms client with WebView2; modes SelfService, Kiosk, WindowsShell. SelfService/WindowsSession uses the Windows-signed-in user's identity for SSO. A legacy card broker, a LocalSystem card service, a .NET Framework 4.8 FIDO broker and a native Credential Provider also exist. Dedicated configuration/news tools and PowerShell build, test and deployment scripts complete the repository.

## Task -> context

| Task / keywords | Load first | Source entry points |
| --- | --- | --- |
| startup, windows shell, UI, menu, screens, self-service, sessions, timers | [runtime-ui](modules/runtime-ui.md) | src/Kiosk.Client/Program.cs; KioskForm.cs; KioskForm.SelfService.cs |
| FIDO2, PIN, smart card, SSO, Entra, token, login, lock, card reader, broker, Credential Provider | [identity](modules/identity.md) | src/Kiosk.Client/CardService.cs; FidoSignIn.cs; src/Kiosk.FidoBroker/src/Program.cs |
| config, settings, JSON, central share, admin editor, accounts, policy | [configuration](modules/configuration.md) | src/Kiosk.Client/Configuration.cs; Config.cs; CentralConfig.cs |
| browser, WebView2, apps, files, notes, RDP, commands | [browser-workloads](modules/browser-workloads.md) | src/Kiosk.Client/BrowserView.cs; ApplicationView.cs; RdpConfiguration.cs |
| announcements, SharePoint, news, videos, help, portal | [content](modules/content.md) | src/Kiosk.Client/News.cs; SharePointNews.cs; Portal.cs |
| build, SDK, CI, signing, packaging, installer, testing, rollback, Windows security policy | [delivery](modules/delivery.md) | scripts/Build.ps1; scripts/Test.ps1; scripts/New-Package.ps1 |

## Cross-module paths
- UI/session -> identity: KioskForm.cs and KioskForm.SelfService.cs -> CardService.cs / FidoSignIn.cs / WindowsSession.cs.
- UI/session -> workloads: KioskForm.cs -> BrowserView.cs / ApplicationView.cs / RdpConfiguration.cs / FilesView.cs.
- UI/content -> configuration: Configuration.cs, Config.cs -> CentralConfig.cs; KioskForm loads the effective configuration.
- Card service -> broker: CardService.cs -> src/Kiosk.FidoBroker/src/KioskBroker.cs; crossing LocalSystem/user process and named-pipe boundaries.
- Content -> configuration: News.cs and SharePointNews.cs depend on configured data sources and permissions.
- Delivery -> all: build, package, signing, install and tests cover multiple projects.

## Complete tracked-file index and automation

- .ai/source-index.json: machine-readable list of all 131 non-AI tracked files at initialization (path, module, kind, Git blob). It is intended for narrow path lookup, NOT wholesale loading into model context.
- scripts/project_brain.py route "query": deterministic module hints; update/check/changes: staged Git file-index refresh/validation.
- .github/workflows/project-brain.yml: validation on pushes and pull requests, including stale blob references and skills scaffolding.
- CLAUDE.md + .claude/agents/Explore.md + .claude/skills/*: Claude Code specialization with Haiku on Explore and forked search skill.
- .claude/hooks/ContextHook.ps1 + .claude/settings.json: prompt scope, bounded file reads and compact repetitive tool output.

## Search / execution budget

Normalize vague requests before tools. For multi-file discovery, ask the cheap locator (Claude Code: Explore on Haiku, .claude/agents/Explore.md; Codex: code_locator on gpt-6-luna) for exact path:line-line and symbol references, then inspect only those ranges in the primary context. A single known symbol can be searched directly. Avoid full-file output. Tests: scripts/Test.ps1 -ErrorsOnly. Hook configuration: .claude/settings.json, .codex/hooks.json.

## Discovery and memory maintenance
- Prefer targeted symbol search (`rg -n "Symbol" src scripts`) and short source ranges over full-file reads.
- Use `git diff --name-only` for working-tree changes, `git diff --cached --name-only` for staged changes, and `git diff --name-only origin/main...HEAD` for branch changes when that ref exists.
- Update only module records affected by verified code changes; correct stale paths immediately.
- Do not import generated out/, bin/, obj/, media, docs/PRODUCTION_AUDIT.md wholesale or lockfiles into chat context.
- More detailed operational guidance: [USAGE.md](USAGE.md).
