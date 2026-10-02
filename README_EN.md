# WinToolBox

[English](README_EN.md) | [简体中文](README.md)

> A collection of small Windows utilities · Monorepo (one repository, multiple projects)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D4.svg)
[![Release](https://github.com/zhuyuebin/WinToolBox/actions/workflows/release.yml/badge.svg)](https://github.com/zhuyuebin/WinToolBox/actions/workflows/release.yml)

WinToolBox is a **collection of small utilities** for Windows 10/11: every tool is a standalone program under `src/Tools/`,
shared capabilities live in the `src/WinToolBox.Core` library, and unit tests live under `tests/`.

## Tools

| Tool | Type | Description | Docs |
| --- | --- | --- | --- |
| **UsbBackup** | WinForms desktop + tray resident | USB drive backup tool (**manually triggered**): incrementally backs up a USB drive into a target folder using “volume label + volume serial number + date”; the main window manages the backup folder and shows the live log | **[User guide](src/Tools/UsbBackup/README_EN.md)** |
| **FolderCreator** | WinForms desktop | Batch-creates folder structures and checks whether an existing directory matches the rules (strict / loose mode); supports rule templates, generating rules from an existing directory, placeholder files, multi-root batch creation, and check-report / directory-tree export | **[User guide](src/Tools/FolderCreator/README_EN.md)** |
| **WinToolBox.Core** | Class library | Shared building blocks: configuration, logging, tray notifications, incremental copy engine, USB drive detection, backup rules | **[Library guide](src/WinToolBox.Core/README_EN.md)** |

> Requirements: Windows 10 / 11 (x64). Release packages are **self-contained single files**, so the target machine
> **does not need .NET installed**.

---

## Quick start

### Option 1: Use the release packages

1. Open the latest release page: <https://github.com/zhuyuebin/WinToolBox/releases/latest>
2. Download the zip you need (each one is portable and single-file — unzip and run):
   - **`UsbBackup-win-x64.zip`** → unzip, then double-click `UsbBackup.exe`
   - **`FolderCreator-win-x64.zip`** → unzip, then double-click `FolderCreator.exe`
3. For UsbBackup: pick the backup folder in the main window and save → plug in the USB drive → click 「立即备份」 (Back up now)

> The executables are not code-signed, so Windows SmartScreen may block the first launch
> (“Windows protected your PC”): click “More info” → “Run anyway”. If it prompts **every time**, see the
> [UsbBackup FAQ](src/Tools/UsbBackup/README_EN.md#faq) (clear the “downloaded from the internet” mark).

### Option 2: Build from source

```powershell
git clone https://github.com/zhuyuebin/WinToolBox.git
cd WinToolBox

dotnet build WinToolBox.sln -c Release      # build the whole solution
dotnet test  WinToolBox.sln                 # run unit tests

# run locally (for debugging)
dotnet run --project src/Tools/UsbBackup/UsbBackup.csproj
dotnet run --project src/Tools/FolderCreator/FolderCreator.csproj

# publish a self-contained single-file executable
dotnet publish src/Tools/UsbBackup/UsbBackup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The .NET 8 SDK (or a newer SDK) is required.

---

## Repository layout

```text
WinToolBox/
├─ .github/workflows/release.yml   # push a v* tag: test → build → package → publish a Release
├─ src/
│  ├─ WinToolBox.Core/             # shared library (config / logging / notifications / copy / USB detection / backup rules)
│  └─ Tools/
│     ├─ UsbBackup/                # USB backup tool (WinForms, main window + tray resident)
│     └─ FolderCreator/            # batch folder creation + rule consistency check (WinForms)
├─ tests/
│  ├─ WinToolBox.Core.Tests/       # Core unit tests (xUnit)
│  └─ FolderCreator.Tests/         # FolderCreator unit tests (xUnit)
├─ Directory.Build.props           # single source of truth for version and package metadata
├─ WinToolBox.sln
├─ README.md                       # repository overview (Chinese)
└─ README_EN.md                    # repository overview (English)
```

Every tool folder contains its own `README.md` with full usage instructions and troubleshooting.

All documents are bilingual: Chinese lives in `X.md` and English in `X_EN.md` beside it (each document has a language switcher at the top).

---

## Documentation index

| Document | Contents |
| --- | --- |
| [UsbBackup user guide](src/Tools/UsbBackup/README_EN.md) | Main window and tray usage, backup rules, config and log paths, command-line options, FAQ |
| [FolderCreator user guide](src/Tools/FolderCreator/README_EN.md) | Rule syntax, automatic parent creation, strict/loose check modes, six enhanced features (templates / reverse generation / placeholders / multi-root / check report / directory tree), command-line options |
| [WinToolBox.Core library guide](src/WinToolBox.Core/README_EN.md) | Type list, typical usage and design conventions of the shared library |
| [Manual test checklist](MD-files/本地手动测试清单_EN.md) | On-device acceptance steps for USB plug/unplug, backup and incremental copy (performed manually by the user) |
| [FolderCreator manual test checklist](MD-files/FolderCreator-本地手动测试清单_EN.md) | Item-by-item acceptance steps for the six enhanced features (templates / reverse generation / placeholders / multi-root / check report / directory tree) |

---

## Tech stack and conventions

- **Language / framework**: C# · .NET 8 (`net8.0-windows`) · WinForms
- **Tests**: xUnit (`dotnet test`)
- **Dependencies**: no third-party commercial NuGet packages — .NET native APIs only
- **Version**: maintained in one place, [`Directory.Build.props`](Directory.Build.props) (currently `0.4.0`)
- **Continuous integration**: [`.github/workflows/release.yml`](.github/workflows/release.yml) — pushing a `v*` tag makes
  `windows-latest` restore dependencies, build in Release, run `dotnet test`, publish the single-file executables of
  UsbBackup and FileMaster, pack them into `UsbBackup-win-x64.zip` and `FileMaster-win-x64.zip`, and create a
  GitHub Release:

  ```powershell
  git tag v0.4.0
  git push origin v0.4.0
  ```

> Cloud CI has no physical USB drive, so the runtime behaviour of `UsbDetector` / `WM_DEVICECHANGE` is not verified there
> (it must still compile). Real plug/unplug and incremental backup verification should be done on your own machine
> following the [manual test checklist](MD-files/本地手动测试清单_EN.md).

---

## License

This project is released under the **MIT License** — see [LICENSE](LICENSE).

Author: **Zhu Yuebin** · Repository: <https://github.com/zhuyuebin/WinToolBox>

This project uses free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).
