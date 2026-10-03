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
| **UsbBackup** | WinForms desktop + tray resident | USB drive backup tool (**manually triggered**): incrementally backs up a USB drive into a target folder with **30 days of version history**, keyed by “volume label + volume serial number” (`current\` holds the latest copy; deleted or overwritten older versions are kept in `history\{date}\`); the main window manages the backup folder, the history retention policy and the run log | **[User guide](src/Tools/UsbBackup/README_EN.md)** |
| **FileMaster** | WinForms desktop | File and folder management tool: batch-creates folder structures and checks an existing directory against the rules (every original FolderCreator feature is preserved), plus **8 file management features**: batch rename, empty folder cleanup, batch timestamp change, batch move / auto-classify, folder diff, duplicate finder, folder sync / mirror and file unlock | **[User guide](src/Tools/FileMaster/README_EN.md)** |
| **WinToolBox.Core** | Class library | Shared building blocks: configuration, logging, tray notifications, an incremental copy engine with content verification, USB mirror backup with version history, USB drive detection, backup business rules and a hash engine | **[Library guide](src/WinToolBox.Core/README_EN.md)** |

Optional UsbBackup settings (stored in `%AppData%\WinToolBox\UsbBackup\config.json`): `strictContentVerification` (whether incremental copies verify file content, default `true`) and `historyRetentionDays` (how long `history\` is kept, default `30`; `0` means keep forever).

> Requirements: Windows 10 / 11 (x64). Release packages are **self-contained single files**, so the target machine
> **does not need .NET installed**.

### FileMaster file management features

After launching FileMaster, open the 「文件管理」 (File management) menu; every feature is an independent dialog, so they never affect one another, and all of them support background execution, progress display and cancellation:

| Feature | What it does | Safety policy |
| --- | --- | --- |
| Batch rename | Files / folders / both; remove characters, find & replace (plain text or regex), prefix and suffix, sequence numbers, dates, unify extension | Preview first (with conflict detection); never overwrites existing files |
| Empty folder cleanup | Recursively finds directories that contain no files anywhere below them | Preview + second confirmation; deletes to the recycle bin by default |
| Batch timestamp change | Last-write = creation time, or set a fixed date (creation / last-write / last-access times are each optional) | Preview before applying |
| Batch move / auto-classify | Classify files into a target folder by extension, date (for example `yyyy-MM`), first letter or a fallback rule; move or copy | Preview + conflict detection + second confirmation |
| Folder diff | Green = identical, blue = left only, red = right only, tan = content differs; supports “size + time / size only / SHA256” | Read-only — never modifies any file |
| Duplicate finder | Groups by size first, then hashes the candidates with MD5 / SHA1 / SHA256, shows the groups and keeps the oldest copy | Checkbox selection + second confirmation; deletes to the recycle bin by default |
| Folder sync / mirror | One-way copy / one-way sync (deletes extra content in the target) / mirror; a plan is generated before anything runs | Preview + second confirmation; deletions go to the recycle bin by default |
| File unlock | Uses the Windows Restart Manager to list the processes holding a file (PID / type / whether they can be restarted) | Second confirmation before ending a process; critical system processes are always refused |

> This tool **does not read or write the registry** (it also offers no Explorer context-menu integration); every deletion goes to the recycle bin by default and requires the user to confirm it a second time.

---

## Quick start

### Option 1: Use the release packages

1. Open the latest release page: <https://github.com/zhuyuebin/WinToolBox/releases/latest>
2. Download the zip you need (each one is portable and single-file — unzip and run):
   - **`UsbBackup-win-x64.zip`** → unzip, then double-click `UsbBackup.exe`
   - **`FileMaster-win-x64.zip`** → unzip, then double-click `FileMaster.exe`
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
dotnet run --project src/Tools/FileMaster/FileMaster.csproj

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
│  ├─ WinToolBox.Core/             # shared library (config / logging / notifications / copy / USB detection / backup rules / hash engine)
│  └─ Tools/
│     ├─ UsbBackup/                # USB backup tool (WinForms, main window + tray resident)
│     └─ FileMaster/               # batch folder creation + 8 file management features (WinForms)
├─ tests/
│  ├─ WinToolBox.Core.Tests/       # Core unit tests (xUnit, including HashEngine tests)
│  └─ FileMaster.Tests/            # FileMaster unit tests (xUnit)
├─ docs/                           # documentation hub (entry point: docs/README.md)
├─ Directory.Build.props           # single source of truth for version and package metadata
├─ WinToolBox.sln
├─ README.md                       # repository overview (Chinese)
└─ README_EN.md                    # repository overview (English)
```

Every tool folder contains its own `README.md` with full usage instructions and troubleshooting.

Every user-facing document is bilingual: Chinese lives in `X.md` and English in `X_EN.md` beside it (each one has a language switcher at the top). Process documents (the audit report, the fix reports and the historical task books) and the documentation hub [docs/README.md](docs/README.md) are Chinese only.

---

## Documentation index

| Document | Contents |
| --- | --- |
| [Documentation hub](docs/README.md) | Index of every document: audit report / fix report / manual test checklists / historical task-book archive |
| [UsbBackup user guide](src/Tools/UsbBackup/README_EN.md) | Main window and tray usage, backup rules, configuration and log paths, command-line options, FAQ |
| [FileMaster user guide](src/Tools/FileMaster/README_EN.md) | Rule syntax, automatic parent completion, strict/loose check modes, the six folder-structure enhancements, and the 8 file management features (rename / empty folders / timestamps / auto-classify / diff / duplicates / sync & mirror / file unlock) |
| [WinToolBox.Core library guide](src/WinToolBox.Core/README_EN.md) | Type list, typical usage and design conventions of the shared library |
| [FileMaster manual test checklist](docs/testing/FileMaster-本地手动测试清单_EN.md) | Item-by-item acceptance steps for the 8 file management features (best run on a real machine) |
| [UsbBackup manual test checklist](docs/testing/UsbBackup-本地手动测试清单_EN.md) | On-device acceptance steps for USB plug/unplug, backup and version history (performed manually by the user) |
| [FileMaster folder-structure manual test checklist](docs/testing/FileMaster-文件夹结构-本地手动测试清单_EN.md) | Item-by-item acceptance steps for the six folder-structure enhancements |

---

## Tech stack and conventions

- **Language / framework**: C# · .NET 8 (`net8.0-windows`) · WinForms
- **Tests**: xUnit (`dotnet test`)
- **Dependencies**: no third-party commercial NuGet packages — .NET native APIs only
- **Version**: maintained in one place, [`Directory.Build.props`](Directory.Build.props) (currently `0.4.0`)
- **Continuous integration**: [`.github/workflows/release.yml`](.github/workflows/release.yml) — pushing a `v*` tag makes
  `windows-latest` check that the tag matches the version in `Directory.Build.props`, restore dependencies, build in
  Release, run `dotnet test`, publish the single-file executables of UsbBackup and FileMaster, pack them into
  `UsbBackup-win-x64.zip` and `FileMaster-win-x64.zip`, and create a GitHub Release:

  ```powershell
  git tag v0.4.0
  git push origin v0.4.0
  ```

Release checklist:

- [ ] Update `README.md` and `README_EN.md` together, so the two language versions stay in sync
- [ ] Bump `<Version>` in `Directory.Build.props`
- [ ] Tag `v<version>` so that it matches `Directory.Build.props` (the workflow fails if it does not)

> Cloud CI has no physical USB drive, so the runtime behaviour of `UsbDetector` / `WM_DEVICECHANGE` is not verified there
> (it must still compile). Real plug/unplug and incremental backup verification should be done on your own machine
> following the [UsbBackup manual test checklist](docs/testing/UsbBackup-本地手动测试清单_EN.md).

---

## License

This project is released under the **MIT License** — see [LICENSE](LICENSE).

Author: **Zhu Yuebin** · Repository: <https://github.com/zhuyuebin/WinToolBox>

This project uses free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).
