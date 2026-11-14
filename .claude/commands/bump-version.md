---
description: Bump the Grably version (patch, minor or major) in Directory.Build.props
argument-hint: "[patch|minor|major]"
---

Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/bump-version.ps1 $ARGUMENTS` from the repository root (`pwsh` works too where installed; the default part is `patch`). Report the old and new version it prints. Do not commit; the change is committed together with the work it belongs to.
