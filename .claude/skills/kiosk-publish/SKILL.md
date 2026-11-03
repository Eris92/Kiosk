---
name: kiosk-publish
description: Build, test and publish a new unsigned Kiosk pilot version as a GitHub pre-release with one quiet script.
disable-model-invocation: true
---
Do not read scripts or logs unless a step fails. Run once from the repository root:

`.\scripts\Publish-Kiosk.ps1`

Use `-NoPublish` for a local package only and `-SkipTests` only if the full suite already passed on this commit. Tracked changes and unpushed commits stop the script. On failure read only the named log. Report the tag and archive path. Signed production packages use /kiosk-release. Never install on the PC from this skill.
