# Kiosk agent instructions

Use this as a routing policy, not as a substitute for source inspection.

0. Normalize each request internally: goal, likely owner, candidate symbols, minimal read scope, acceptance test and rollback. Do not weaken or silently reinterpret user constraints; a vague request is not permission to scan the repo.
1. Read .ai/index.md first. Pick only the module documents relevant to the task.
2. Read those .ai/modules/*.md files, then inspect the referenced symbols and specific source files. Search for symbols before opening large files such as KioskForm.cs, Program.cs, SettingsView.cs or CentralConfig.cs.
2a. Prefer cheapest evidence first: path list -> rg -n focused symbol -> bounded source lines (Select-Object -Skip/-First). Never dump large source files or repeatedly read the same content.
3. Expand to neighboring modules only for a demonstrated call, contract or security dependency. Never re-scan the entire repository by default.
4. Source and tests are authoritative. Memory is a locator, not proof that a behavior works. Check docs/STATUS.md for pilot readiness and limitations.
5. Before editing, inspect `git status --short` and relevant `git diff`; keep unrelated and user changes intact.
5a. For the full test suite, run .\\scripts\\Test.ps1 -ErrorsOnly. It keeps raw logs on disk and only shows grouped failures. Never hide a nonzero exit status.
6. After editing, run the smallest relevant check, then broader tests when warranted. If behavior, ownership, entry points, configuration contracts or dependencies changed, update only the affected module documents and .ai/index.md. Do not copy source code into the memory.
7. For new modules, add one concise mapping entry and a module document; for ordinary small fixes without structural changes, no documentation churn is required.
8. Prefer least privilege, fail-closed authentication, narrow IPC permissions, verified packages and explicit production rollback. Do not weaken Windows security controls to make a test pass.
9. Do not install shell, Credential Provider, keyboard filter or services on a production device as a routine test.

## Cheapest-agent source location (mandatory)
Before source-code exploration, the primary agent must delegate targeted read-only discovery to the custom `code_locator` subagent (configured in .codex/agents/code_locator.toml), unless the exact single file and symbol are already known and one bounded read is clearly cheaper than spawning a subagent.
- Spawn once per focused task, not once per file; provide the user's goal and selected module from .ai/index.md.
- The locator uses a cheaper model (gpt-6-luna). It reads .ai/index.md, then only the relevant module note, searches symbols, checks bounded source ranges and caller/callee references. Never edit code.
- The locator must return compact evidence only: primary path:line-line + symbol, related path:line-line + relationship, 1-2 sentence conclusion and confidence/gaps; no source dumps, duplicate search results, secrets or full logs. Limit normal replies to about 15 lines.
- The primary agent receives ONLY this evidence, opens narrow source ranges for verification, makes changes, verifies security implications and runs tests. Escalate exploration to the primary model when evidence conflicts, security-critical trust boundaries are unclear or cheap model access is unavailable.
- If the chosen cheap model is unavailable to the installed Codex/account, use the cheapest available read-only subagent or a single bounded local search. Never claim the model was switched if no subagent ran.

Codex and Claude Code command hooks (.codex/hooks.json, .claude/settings.json) inject prompt routing, guard wide source dumps and group redundant long output. Review them via /hooks before relying on them. Claude Code mapping: CLAUDE.md.

Claude Code uses CLAUDE.md, .claude/agents/Explore.md (Haiku), scoped .claude/skills and .claude/settings.json hooks. In Claude, delegate read-only multi-file discovery to Explore and return path:line ranges only. Keep feature implementation and verification in the primary agent. Never paste the full file catalog into model context: .ai/source-index.json is for deterministic local lookup.

After source/config/script changes, inspect tracked changes and run python scripts/project_brain.py update with intended changes staged, then python scripts/project_brain.py check. CI validates this automatically. Do not stage unrelated local work or weaken the test to hide stale entries.

Development commands and the AI usage workflow: .ai/USAGE.md. Source build and release details: README.md.
