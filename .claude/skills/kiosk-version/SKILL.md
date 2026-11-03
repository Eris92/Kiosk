---
name: kiosk-version
description: Publish a new signed build-N of Kiosk on GitHub so installed Kiosks can update from Konfiguracja → Aktualizacja.
disable-model-invocation: true
---
Do not read code or logs unless a step fails. A push to main already builds, signs and publishes `build-N` as the latest release, and other branches publish a pre-release for kanał dev.

1. Run `gh workflow run build.yml --repo Eris92/Kiosk --ref <branch>`, default `main`. Skip this after a fresh push to that branch.
2. Wait in the background: `gh run watch <id> --repo Eris92/Kiosk --exit-status`.
3. Report only: `gh release view --repo Eris92/Kiosk --json tagName,assets -q '.tagName + " " + ([.assets[].name]|join(","))'`. The release must have both `Kiosk-win-x64.zip` and `.sig`.

On failure read only `gh run view <id> --log-failed`, last 20 lines. Never print or copy KIOSK_SIGNING_KEY. Never install on the PC from this skill. Authenticode-signed production packages use /kiosk-release.
