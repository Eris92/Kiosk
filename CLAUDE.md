# Kiosk: Claude Code entry point

This repo is a Windows x64 kiosk shell with sensitive Windows sign-in, FIDO2, WebView2 and shell-installation paths.
Follow [AGENTS.md](AGENTS.md) for shared rules and [.ai/index.md](.ai/index.md) for module routing. Source and tests are authoritative; status is in docs/STATUS.md.

Before tool calls, translate vague requests into an internal scope: outcome, candidate module, symbol, narrow file/line search, verification. Do not change the user's intended behavior.
For multi-file code discovery delegate to the read-only `Explore` subagent (project override uses Haiku). It must return only path:line-line, symbol, callers/dependencies and uncertainties. If the exact symbol is already known, a single bounded local search can be cheaper.
Read only selected modules and verified source ranges. Never paste large code or diagnostics into the main context.
Run automated tests via `./scripts/Test.ps1 -ErrorsOnly`; the failure exit code is preserved and raw logs are in out/test-logs.
After structural changes run `python scripts/project_brain.py update`; CI requires a fresh `.ai/source-index.json`. The index is a locator, not a substitute for inspecting code.
Do not modify production sign-in, registry, shell, keyboard filters, services or signing settings without a precheck, explicit scope and rollback.

Useful skills: /kiosk-locate, /kiosk-test, /kiosk-identity, /kiosk-config, /kiosk-webview, /kiosk-shell, /kiosk-release, /kiosk-brain-refresh.
Full agent usage: [.ai/USAGE.md](.ai/USAGE.md).
