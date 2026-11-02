---
name: kiosk-test
description: Run Kiosk build/self-tests with compact, deduplicated failure output and preserve complete logs.
disable-model-invocation: true
---
On Windows x64, from the repo root, use `.\scripts\Test.ps1 -ErrorsOnly`. Never pipe output to a command that loses the exit code. If it fails, inspect the reported full log only for distinct error signatures and affected code ranges; do not print 100 duplicate errors. For precheck only use `.\scripts\Test-Prerequisites.ps1`. State if Windows/.NET SDK 10.0.402 cannot run in the current environment. Do not report success without the actual exit code.
