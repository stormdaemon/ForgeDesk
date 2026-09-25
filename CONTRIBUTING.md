# Contributing to ForgeDesk

Thanks for helping make ForgeDesk better! This guide explains how to build, test and submit
changes.

## Prerequisites

* Windows 10 1809+ or Windows 11 (the app is WPF). Core and presentation code also builds and
  tests on Linux/macOS.
* [.NET SDK 10](https://dotnet.microsoft.com/download) (the exact feature band is pinned in `global.json`).
* [Git for Windows](https://git-scm.com/download/win) 2.31 or newer.
* Any editor: Visual Studio 2022 17.14+ (open `ForgeDesk.slnx`), JetBrains Rider or VS Code with C# Dev Kit.

## Build and run

```powershell
git clone https://github.com/stormdaemon/ForgeDesk.git
cd ForgeDesk
dotnet build ForgeDesk.slnx
dotnet run --project src/ForgeDesk.App
```

To keep your real data separate while developing, point ForgeDesk at a scratch data folder:

```powershell
$env:FORGEDESK_DATA_DIR = "$env:TEMP\forgedesk-dev"
dotnet run --project src/ForgeDesk.App
```

## Tests

```powershell
dotnet test --project tests/ForgeDesk.Core.Tests          # services against real git repos & SQLite
dotnet test --project tests/ForgeDesk.Presentation.Tests  # view models
```

End-to-end UI smoke tests drive the published app with FlaUI (Windows only):

```powershell
./build/package.ps1 -Version 0.1.0-dev -SkipTests
$env:FORGEDESK_APP_PATH = "$PWD\artifacts\publish\win-x64\ForgeDesk.exe"
dotnet test --project tests/ForgeDesk.UITests
```

Tests use xUnit v3 on Microsoft.Testing.Platform (configured in `global.json`). Filter with
`-- --filter-class "ForgeDesk.Core.Tests.Git.GitStatusTests"`.

## Packaging

```powershell
./build/package.ps1 -Version 1.2.3
```

produces `artifacts/Releases/ForgeDesk-win-Setup.exe` (Velopack installer with automatic updates)
and `ForgeDesk-win-Portable.zip`. Releases are built by the **Release** GitHub Actions workflow
when a `v*.*.*` tag is pushed (or when it is started manually with a version).

## Where things go

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before your first change and
[docs/DESIGN.md](docs/DESIGN.md) before touching the UI. In short:

* Domain logic → `src/ForgeDesk.Core/<Domain>` + tests in `tests/ForgeDesk.Core.Tests/<Domain>`.
* View models → `src/ForgeDesk.Presentation/<Feature>` (no WPF types) + tests.
* Views → `src/ForgeDesk.App/Features/<Feature>` (XAML, DataTemplates in the feature's `*Templates.xaml`).

## Coding guidelines

* The build treats warnings as errors; keep it at zero warnings.
* Follow `.editorconfig` (file-scoped namespaces, `_camelCase` private fields, braces always).
* Throw `ForgeException` with a user-readable message and a hint for expected failures.
* Every view handles loading, empty, error and content states.
* No blocking calls on the UI thread; long work goes through `ViewModelBase.RunAsync`.
* Add or update tests with every behavior change.

## Pull requests

1. Fork and create a branch from the default branch.
2. Keep changes focused; describe what and why in the PR, with screenshots for UI changes.
3. Make sure CI is green (Linux tests + Windows build, tests, packaging and UI smoke tests).
4. Add an entry under **Unreleased** in [CHANGELOG.md](CHANGELOG.md) for user-visible changes.

## Releasing (maintainers)

1. Move the **Unreleased** entries of `CHANGELOG.md` under a new `## [x.y.z] - YYYY-MM-DD` heading
   and bump `<Version>` in `Directory.Build.props`.
2. Commit, then push a tag `vx.y.z` (or run the **Release** workflow with the version).
3. The workflow tests, packages, and publishes the GitHub release with the installer,
   portable zip, update packages and checksums. Installed copies update themselves.
