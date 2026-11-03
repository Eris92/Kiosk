---
name: kiosk-locate
description: Locate Kiosk behavior precisely and cheaply; return file line ranges and direct dependencies, without full code dumps.
argument-hint: "[behavior or symbol]"
context: fork
agent: Explore
background: false
---
Locate the implementation for: $ARGUMENTS

Inspect .ai/index.md, select the most relevant module note, then search only related source. Return verified owner path:line-line, symbol, up to four relevant caller/dependency ranges and a concise conclusion. Never edit files or run tests. If the request is vague, derive search terms and narrow scope first; escalate uncertainty rather than inventing paths.
