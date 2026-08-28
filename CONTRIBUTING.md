# Contributing

Thank you for improving OpenRevit Tools.

## Before opening a change

1. Do not commit RVT/RFA/DWG/IFC files, customer names, model screenshots, absolute local paths or credentials.
2. Keep Revit data access, pure geometry/decision logic and Revit write-back separated where practical.
3. Use stable `UniqueId`/owned metadata for destructive operations; never delete by a human-readable name alone.
4. Treat linked-model transforms, units, unloaded links, missing solids and Boolean failures explicitly.
5. Avoid claiming code compliance from nominal BIM parameters. Label screenings and assumptions honestly.

## Build and test

Requirements: Windows, Revit 2024, .NET Framework 4.8 Developer Pack, Visual Studio/Build Tools 2022 and the
.NET SDK.

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test.ps1
.\tests\run-maintenance-ledger-tests.ps1
```

If Revit is installed in a custom directory, pass `-RevitInstallDir` or set `REVIT_2024_INSTALL_DIR`.

## Pull requests

- Keep changes focused and explain user-visible behavior in plain language.
- Include a small deterministic test for pure logic and a concise Revit smoke-test checklist for API behavior.
- Report warnings and errors from a clean Release build.
- Preserve unrelated local/user changes in mixed worktrees.
- Update README/security/limitations whenever a tool's trust or product meaning changes.

By contributing, you agree that your contribution is licensed under the repository's MIT License.
