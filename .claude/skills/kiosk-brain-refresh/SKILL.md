---
name: kiosk-brain-refresh
description: Update and validate Project Brain source index after code, config or script changes, without rescanning source with AI.
disable-model-invocation: true
---
From the repository root: inspect `git diff --name-only` and `git status --short`; stage intended changes, then run `python scripts/project_brain.py changes` and `python scripts/project_brain.py update`; verify with `python scripts/project_brain.py check`. Update only affected .ai/modules/*.md and .ai/index.md when ownership, interfaces or dependency contracts actually changed. Do not stage unrelated work or edit production code just to fix documentation. The source index stores Git blob identifiers, not source copies.
