# Project Brain: how to use it

This is lightweight **repository memory for AI agents**, not a background daemon, database or runtime component. It starts when a coding agent reads AGENTS.md. There is no service to install.

## Start a coding session

1. Open a local clone of Eris92/Kiosk on the intended branch.
2. Start Codex (or another coding agent) with the repository as its working directory.
3. Give a normal task. AGENTS.md tells the agent to read .ai/index.md, select only relevant .ai/modules/*.md, then verify only the necessary source symbols.

Example on Windows:

~~~powershell
git clone https://github.com/Eris92/Kiosk.git
Set-Location .\Kiosk
git checkout main
git pull --ff-only
codex
~~~

Example first instruction if an agent does not automatically read AGENTS.md:

> Follow AGENTS.md. Use .ai/index.md to route this task; load only needed module docs and source files. Fix the SSO session cleanup and update affected module memory only if the contracts change.

Do not paste all of .ai/ into every prompt. Reuse a persistent workspace/clone; start subsequent sessions in the same repository.

## Cheapest-model code locator

Project custom agent: .codex/agents/code_locator.toml. Codex config: .codex/config.toml enables at most two concurrent subagents and defaults their model to gpt-6-luna (low reasoning). The main session model is not changed. The agent is read-only by instruction, not an enforced sandbox restriction; only the primary agent should apply edits. Codex must have this model available; if not, it falls back according to AGENTS.md.

The primary agent delegates code discovery when it would otherwise need to read multiple files. The locator sends back only evidence, for example:

~~~text
Owner: src/Kiosk.Client/KioskForm.SelfService.cs:18-35 PrepareCardSignIn
Callers: src/Kiosk.Client/KioskForm.cs:xxx-yyy (verify actual current lines)
Related: src/Kiosk.Client/FidoSignIn.cs:xxx-yyy (sign-in adapter)
Conclusion: sign-in owned by SelfService; verify cleanup across identity boundary.
~~~

These example caller line placeholders are not actual source references. For a task with one known method, an rg -n search may be cheaper than spawning an agent.

## Codex hooks and compact test output

Codex loads .codex/hooks.json only in a trusted project. Run /hooks once to review and trust the repository hooks; after modifications the trust review may need to be repeated.
- UserPromptSubmit: adds a compact request-scoping hint (not a literal rewrite of the user question).
- PreToolUse: guards unbounded reads of large C# source and adds -ErrorsOnly to a simple direct scripts/Test.ps1 command.
- PostToolUse: for large repetitive Bash output, groups lines differing only in timestamp while preserving meaningful diagnostics. Avoid hiding data needed for debugging; re-run a narrow command for original evidence.

Windows PowerShell 5.1 is used to run the hook file. These hooks cannot force subagent spawning or override your account's available models. Start Codex from the repository root.

Full test suite with a quiet success and grouped failure output:

~~~powershell
.\\scripts\\Test.ps1 -ErrorsOnly
~~~

The complete raw stdout/stderr is saved in out/test-logs. Exit failures remain failures. Use .\\scripts\\Test.ps1 without -ErrorsOnly for interactive troubleshooting (the agent's default remains -ErrorsOnly).

## How the memory stays current

After changing relevant code, the coding agent reviews `git diff --name-only` and `git diff --cached --name-only` (also `git diff --name-only origin/main...HEAD` for a feature branch when available), then updates only affected module notes. These are **agent instructions**; no automatic Git hook or semantic indexer is installed.

To inspect a suspected stale reference, search targeted symbols rather than reindexing the entire repository:

~~~powershell
rg -n "SelfServiceAuthentication|WindowsSession" src/Kiosk.Client
git diff --name-only
git status --short
~~~

## Run the application (separate from Project Brain)

On Windows x64 with the exact .NET SDK version specified in global.json:

~~~powershell
.\scripts\Test-Prerequisites.ps1
.\scripts\Build.ps1
.\out\client\Kiosk.Client.exe
~~~

To run automated checks instead of starting the GUI:

~~~powershell
.\scripts\Test.ps1
~~~

For installation as a Windows shell, signed production packages, verification and rollback, read README.md and .ai/modules/delivery.md before using the deployment scripts. Do not test shell replacement on your daily workstation.

## Efficiency expectations

- One small routing index on entry; a module note only if needed.
- Current code remains authoritative; never rely on memory instead of reading affected implementation.
- No automatic token quota or guaranteed read limit: agents can expand context for correctness.
- When a module is renamed, added or removed, update its entry in .ai/index.md and the affected module document in the same change.

## Claude Code (Windows local project)

```powershell
git fetch origin
git switch codex/project-brain
git pull --ff-only
claude
```

Claude Code reads CLAUDE.md automatically when opened from the repository root. The project overrides the built-in Explore subagent with .claude/agents/Explore.md using model: haiku; do not assume other subagents are cheap by default. Do not paste the whole index into the chat. If available, review hooks with /hooks and local permission settings.

A normal prompt is enough: "Find where SelfService closes a card session and fix the cleanup." For explicit read-only location use /kiosk-locate, which runs in an Explore fork. Other project skills: /kiosk-test, /kiosk-identity, /kiosk-config, /kiosk-webview, /kiosk-shell, /kiosk-release, /kiosk-brain-refresh. Skills with side effects remain user-invoked.

```powershell
python scripts/project_brain.py route "FIDO2 sign in"
python scripts/project_brain.py check
.\scripts\Test.ps1 -ErrorsOnly
```

Source index maintenance: the index is based on the **Git staging index**, not arbitrary unstaged work. After reviewing a change, stage only the relevant files, run `python scripts/project_brain.py changes`, then `python scripts/project_brain.py update`; stage .ai/source-index.json and run `python scripts/project_brain.py check`. CI rejects an index that does not match the staged/committed source. Generated AI files and .ai/source-index.json are excluded from the inventory to avoid recursive churn. Python 3 and Git are required for the index script. The index is updated on demand, and its correctness is automatically checked by GitHub Actions; no background process runs on your PC.

Windows project build/test tools are not run by index validation. Follow scripts/Test.ps1 and README.md on an isolated Windows test device.
