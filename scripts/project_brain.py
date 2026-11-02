#!/usr/bin/env python3
"""Deterministic, content-free Git blob index for the Kiosk agent memory."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / ".ai" / "source-index.json"
VERSION = 1
MODULES = (
    "runtime-ui", "identity", "configuration", "browser-workloads",
    "content", "delivery",
)
CLIENT_IDENTITY = {
    "CardService.cs", "CardPin.cs", "CardVault.cs", "Ctap.cs", "FidoSignIn.cs",
    "FirstSignIn.cs", "UserLogon.cs", "WindowsHello.cs", "WindowsKey.cs",
    "WindowsSession.cs", "SharedAccounts.cs", "NewsAccount.cs",
    "LocalAdministrators.cs",
}
CLIENT_CONFIG = {
    "Config.cs", "Configuration.cs", "ConfigManager.cs", "CentralConfig.cs",
    "AdminSettings.cs", "SettingsView.cs", "FirstRunSetup.cs",
}
CLIENT_WORKLOADS = {
    "ApplicationView.cs", "BrowserView.cs", "BrowserProfileCleanup.cs",
    "FilesView.cs", "FilesHost.cs", "NotesView.cs", "RdpConfiguration.cs",
    "RdpCredentials.cs", "RdpUserHost.cs", "SessionCommand.cs", "PageScripts.cs",
    "BoundedPipeLine.cs",
}
CLIENT_CONTENT = {
    "News.cs", "NewsSlides.cs", "NewsVideo.cs", "SharePointNews.cs",
    "Portal.cs", "Help.cs", "Stats.cs", "Retention.cs",
}
TERMS = {
    "identity": ("login", "sign-in", "signin", "sso", "fido", "ctap", "smartcard",
                 "card", "pin", "entra", "hello", "token", "credential", "auth"),
    "configuration": ("config", "json", "settings", "central", "share",
                      "policy", "account", "intune", "konfigur"),
    "browser-workloads": ("browser", "webview", "edge", "rdp", "file",
                          "download", "application", "process", "notes"),
    "content": ("news", "sharepoint", "communicat", "komunikat", "help",
                "portal", "slide", "video", "media"),
    "delivery": ("build", "package", "sign", "install", "deploy", "script",
                 "test", "rollback", "pipeline", "release", "workflow"),
    "runtime-ui": ("shell", "session", "form", "ui", "menu", "screen",
                   "desktop", "idle", "window", "timer"),
}


def run_git(*args: str) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(ROOT), *args],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
    )
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace").strip())
    return result.stdout


def module_for(path: str) -> str:
    if path.startswith("src/Kiosk.FidoBroker/") or path.startswith("src/Kiosk.CredentialProvider/"):
        return "identity"
    if path.startswith("src/Kiosk.ConfigTool/"):
        return "configuration"
    if path.startswith("src/Kiosk.NewsEditor/") or path.startswith("config/news/"):
        return "content"
    if path.startswith("src/Kiosk.Client/"):
        name = path.rsplit("/", 1)[-1]
        if name in CLIENT_IDENTITY:
            return "identity"
        if name in CLIENT_CONFIG:
            return "configuration"
        if name in CLIENT_WORKLOADS:
            return "browser-workloads"
        if name in CLIENT_CONTENT:
            return "content"
        if name.endswith(".csproj") or name == "packages.lock.json":
            return "delivery"
        return "runtime-ui"
    if path.startswith("config/"):
        return "delivery" if path.startswith("config/intune/") else "configuration"
    if path.startswith("scripts/") or path.startswith(".github/"):
        return "delivery"
    if path.startswith("docs/") or path == "README.md":
        return "delivery"
    return "delivery" if path in (".gitignore", "global.json", "Directory.Build.props") else "runtime-ui"


def included(path: str) -> bool:
    # AI instructions are intentionally excluded to avoid self-referential index churn.
    return not (
        path.startswith((".ai/", ".claude/", ".codex/"))
        or path in ("AGENTS.md", "CLAUDE.md")
        or path.startswith(("out/", "bin/", "obj/", ".git/"))
    )


def kind_for(path: str) -> str:
    suffix = Path(path).suffix.lower()
    if suffix in (".png", ".jpg", ".jpeg", ".gif", ".mp4", ".ico"):
        return "asset"
    if suffix in (".cs", ".cpp", ".ps1", ".py", ".mjs"):
        return "source"
    if suffix in (".json", ".xml", ".yml", ".yaml", ".props", ".csproj", ".def"):
        return "configuration"
    if suffix == ".md":
        return "documentation"
    return "other"


def build_index() -> dict:
    # Index entries use Git's staged blob IDs. No source content enters an AI context.
    raw = run_git("ls-files", "--stage", "-z")
    files = []
    for entry in raw.split(b"\0"):
        if not entry:
            continue
        metadata, raw_name = entry.split(b"\t", 1)
        mode, blob, stage = metadata.decode("ascii").split(" ")
        if stage != "0":
            raise RuntimeError("Unmerged Git index entry: " + raw_name.decode("utf-8", errors="replace"))
        name = raw_name.decode("utf-8")
        if not included(name):
            continue
        files.append({
            "path": name.replace("\\", "/"),
            "module": module_for(name),
            "kind": kind_for(name),
            "blob": blob,
        })
    files.sort(key=lambda x: x["path"])
    counts = Counter(x["module"] for x in files)
    return {"version": VERSION, "modules": {m: counts.get(m, 0) for m in MODULES}, "files": files}


def load_index() -> dict | None:
    if not OUTPUT.is_file():
        return None
    with OUTPUT.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def changed_files(stored: dict | None, current: dict) -> list[str]:
    previous = {e["path"]: (e["blob"], e["module"], e["kind"])
                for e in (stored or {}).get("files", [])}
    actual = {e["path"]: (e["blob"], e["module"], e["kind"])
              for e in current["files"]}
    return [p for p in sorted(previous.keys() | actual.keys())
            if previous.get(p) != actual.get(p)]


def validate_scaffold() -> list[str]:
    errors = []
    for name in MODULES:
        if not (ROOT / ".ai" / "modules" / (name + ".md")).is_file():
            errors.append("Missing .ai/modules/" + name + ".md")
    for path in ("CLAUDE.md", "AGENTS.md", ".claude/settings.json"):
        if not (ROOT / path).is_file():
            errors.append("Missing " + path)
    settings = ROOT / ".claude" / "settings.json"
    if settings.is_file():
        try:
            json.loads(settings.read_text(encoding="utf-8-sig"))
        except (ValueError, UnicodeError) as exc:
            errors.append("Invalid Claude settings.json: " + str(exc))
    skills = ROOT / ".claude" / "skills"
    if not skills.is_dir():
        errors.append("No .claude/skills")
    else:
        for skill in sorted(skills.iterdir()):
            if not skill.is_dir():
                continue
            definition = skill / "SKILL.md"
            if not definition.is_file():
                errors.append("Missing " + str(definition.relative_to(ROOT)))
                continue
            text = definition.read_text(encoding="utf-8")
            if not re.match(r"\A---\r?\n[\s\S]+?\r?\n---\r?\n", text):
                errors.append("Missing YAML frontmatter: " + str(definition.relative_to(ROOT)))
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("update", "check", "changes", "route"))
    parser.add_argument("query", nargs="*")
    args = parser.parse_args()
    if args.command == "route":
        q = " ".join(args.query).casefold()
        scores = [(sum(q.count(word) for word in words), name) for name, words in TERMS.items()]
        selected = [name for count, name in sorted(scores, reverse=True) if count > 0][:3]
        if not selected:
            print("No confident module: start with .ai/index.md and narrow a symbol.")
        else:
            for name in selected:
                print(".ai/modules/" + name + ".md")
        return 0

    current = build_index()
    stored = load_index()
    changes = changed_files(stored, current)
    if args.command == "changes":
        for path in changes[:60]:
            print(path + " -> " + module_for(path))
        if len(changes) > 60:
            print("... plus " + str(len(changes) - 60) + " other changes")
        return 0

    if args.command == "update":
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(json.dumps(current, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
        print("Updated " + str(OUTPUT.relative_to(ROOT)) + " with " + str(len(current["files"])) + " tracked paths")
        return 0

    errors = validate_scaffold()
    if stored != current:
        errors.append("Source index stale; stage intended changes and run: python scripts/project_brain.py update")
        errors.extend("  " + path for path in changes[:25])
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print("Project Brain OK: " + str(len(current["files"])) + " indexed paths, "
          + str(len(MODULES)) + " modules; Claude skills/settings present")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, ValueError) as exc:
        print("Project Brain error: " + str(exc), file=sys.stderr)
        sys.exit(2)
