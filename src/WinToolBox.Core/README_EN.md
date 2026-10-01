# WinToolBox.Core

[English](README_EN.md) | [简体中文](README.md)

[← Back to WinToolBox overview](../../README_EN.md)

`WinToolBox.Core` is the shared foundational class library (`net8.0-windows`) for all WinToolBox tools.
It consolidates the common capabilities — configuration, logging, notifications, copying, USB drive detection, and backup rules — in one place, so that every tool does not have to implement its own set.

> It depends on WinForms (`Notifier` requires `NotifyIcon`), so the target framework is `net8.0-windows`.
> Apart from argument validation, no disk / configuration operation throws an exception to the caller; errors are surfaced as return values or log entries.

## Types at a Glance

| Type | Purpose |
| --- | --- |
| `AppPaths` | Unified paths: `%AppData%\WinToolBox\...` (configuration), `%LocalAppData%\WinToolBox\logs` (logs), plus `EnsureDirectory` |
| `BackupConfig` | Backup configuration model: `BackupTargetDirectory`, `ExcludedExtensions`; includes `Clone()` (deep copy) and `Normalize()` (trim whitespace, prefix extensions with `.`, lowercase, deduplicate) |
| `ConfigManager` | Reads and writes `config.json`. Reads never throw (a missing file or corrupted content falls back to the default configuration, and the corrupted file is not overwritten); saves write a `.tmp` file first and then replace it, so a half-written file cannot corrupt the configuration |
| `Logger` / `LogLevel` / `LogEntry` | Log files rotated daily as `wintoolbox-yyyyMMdd.log` (30 days retained by default); `Info` / `Warn` / `Error`; a failed write only records `LastError` and never brings down the business logic; the `EntryWritten` event lets the UI display logs in **real time** |
| `INotifier` / `Notifier` | Tray balloon notification wrapper. It can reuse the host's `NotifyIcon`; when called on a background thread it automatically switches back to the UI thread that created it |
| `ExcludeRules` | Copy exclusion rules (directory names / file names / extensions, all case-insensitive). By default it excludes `System Volume Information`, `$RECYCLE.BIN`, `autorun.inf`, `desktop.ini`, `Thumbs.db`, `*.tmp`, and so on |
| `FileCopier` / `CopyResult` / `CopyProgress` | Streaming + incremental copy engine: a fixed buffer is read and written in a loop to support large files; files are skipped when "size + last write time" are unchanged; files that already exist in the destination are **never deleted**; progress callbacks and cancellation are supported; a single file failure is recorded in `Errors` and copying continues |
| `UsbDetector` / `UsbDeviceInfo` | Removable storage device enumeration: volume label, total capacity, free space, **volume serial number**, and file system; the unique identity is "volume label + volume serial number + capacity" (it never uses the drive letter, which changes) |
| `BackupRules` | Backup business rules: the target path template `{backup root}\{volume label}_{serial}\{yyyy-MM-dd}`, the default exclusion rules, directory name sanitizing, and source/destination same-volume validation |

## Typical Usage

```csharp
using WinToolBox.Core;

var logger = Logger.Instance;
var config = new ConfigManager(AppPaths.UsbBackupConfigFile, logger).Load();

// 1) Find the USB drive (unique identity = volume label + serial number + capacity)
var device = new UsbDetector(logger).GetRemovableDrives().FirstOrDefault();
if (device is null) return;

// 2) Compute this backup's destination directory according to the rules
var target = BackupRules.BuildTargetDirectory(config.BackupTargetDirectory, device, DateTime.Now);

// 3) Incremental copy (skip unchanged files, never delete destination files, progress callback)
var copier = new FileCopier(logger);
var result = copier.CopyDirectory(
    device.RootPath,
    target,
    progressCallback: p => Console.WriteLine($"{p.Percent}% {p.CurrentFile}"),
    excludeRules: BackupRules.CreateDefaultExcludeRules(config.ExcludedExtensions));

logger.Info(result.Summary);
```

## Design Conventions

- **Never throws**: a failed log write → `Logger.LastError`; a corrupted configuration → fall back to the default values; a single file copy failure → `CopyResult.Errors` + `FailedFiles`. Callers only need to check the return values.
- **Testable**: `Logger`, `ConfigManager`, and `FileCopier` all support injected directories / file paths, and unit tests use temporary directories, so they never touch real user data.
- **No external programs are executed**: the copy engine only reads the source and only writes the destination; it never executes any executable file in the source directory.
- **Thread safety**: `Logger` locks around writes; `Notifier` automatically switches back to the UI thread; each `FileCopier` instance is independent and can be used concurrently (with different destination directories).

## Unit Tests

`tests/WinToolBox.Core.Tests` covers:

- `FileCopier`: new files / directory structure, a second run skipping everything, a source update re-copying only the changed files, extra files in the destination not being deleted, default and custom exclusion rules, binary consistency of large files, cancellation, and error scenarios
- `ConfigManager`: default values, round-trip (including Chinese paths), tolerance of corrupted JSON, normalization, atomic writes leaving no leftovers, and delete and recreate
- `BackupRules` / `Logger` / `ExcludeRules`: rule and logging behavior

```powershell
dotnet test WinToolBox.sln
```
