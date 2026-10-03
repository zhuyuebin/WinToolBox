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
| `AppPaths` | Unified paths: `%AppData%\WinToolBox\...` (configuration), `%LocalAppData%\WinToolBox\logs` (shared logs) and `LogDirectoryFor(tool)` (per-tool logs), plus `EnsureDirectory` |
| `BackupConfig` | Backup configuration model: `BackupTargetDirectory`, `ExcludedExtensions`, `StrictContentVerification` (default `true`), `HistoryRetentionDays` (default `30`, `0` = keep forever); includes `Clone()` (deep copy) and `Normalize()` (trim whitespace, prefix extensions with `.`, lowercase, deduplicate) |
| `ConfigManager` | Reads and writes `config.json`. Reads never throw (a missing file or corrupted content falls back to the default configuration, and the corrupted file is not overwritten); saves write a `.tmp` file first and then replace it, so a half-written file cannot corrupt the configuration |
| `Logger` / `LogLevel` / `LogEntry` | Log files rotated daily as `wintoolbox-yyyyMMdd.log`, written under `logs\{tool}\` via `Logger.ForTool(...)` (30 days retained by default); opens with `FileShare.ReadWrite` and retries on `IOException` so concurrent writers never lose lines; `Info` / `Warn` / `Error`; a failed write records `LastError` and `FailedWriteCount` and never brings down the business logic; the `EntryWritten` event lets the UI display logs in **real time** |
| `INotifier` / `Notifier` | Tray balloon notification wrapper. It can reuse the host's `NotifyIcon`; when called on a background thread it automatically switches back to the UI thread that created it (the host must install a UI `SynchronizationContext` before constructing it) |
| `ExcludeRules` | Copy exclusion rules (directory names / file names / extensions, all case-insensitive). By default it excludes `System Volume Information`, `$RECYCLE.BIN`, `autorun.inf`, `desktop.ini`, `Thumbs.db`, `*.tmp`, and so on |
| `FileCopier` / `CopyResult` / `CopyProgress` | Streaming + content-verified incremental copy engine: a fixed buffer is read and written in a loop to support large files; equal-sized files are compared by content (full SHA256, or "size + mtime + head/tail 64 KiB sampling" above 16 MiB); `CopyResult` reports `VerifiedFiles` / `UnchangedAssumed`; lazily enumerates the source (no full file list in memory), skips reparse points (junctions / symlinks), stops early after too many consecutive failures, writes `{target}.tmp` then atomically replaces, and **never deletes** destination files; progress callbacks and cancellation are supported |
| `HashEngine` | Streaming MD5 / SHA1 / SHA256 with constant memory, progress reporting, cancellation, and `AreEqual` (compare size first, then hash) |
| `BackupMirrorService` / `MirrorResult` | USB drive mirror backup implementing the product semantics "incremental mirror + version history": new files go to `current\`, the old version of a modified file is **moved** into `history\{date}\` before the new one is written, files deleted on the drive are **moved** into `history\{date}\` (soft delete, never erased), expired `history\{yyyy-MM-dd}\` folders are purged, and every version is preserved under a unique name when paths collide |
| `BackupManifest` | Model of `manifest.json`: last backup time, file count, total size, history retention policy, comparison with the previous run, and the list of files archived into `history` |
| `UsbDetector` / `UsbDeviceInfo` | Removable storage device enumeration: volume label, total capacity, free space, **volume serial number**, and file system; the unique identity is "volume label + volume serial number + capacity" (it never uses the drive letter, which changes) |
| `BackupRules` | Backup business rules: the device folder `{backup root}\{volume label}_{serial}\{current | history | manifest.json}` (no date-stamped target directory), the default exclusion rules, directory name sanitizing, history folder parsing, and source/destination same-volume validation |

## Typical Usage

```csharp
using WinToolBox.Core;

var logger = Logger.ForTool("UsbBackup");
var config = new ConfigManager(AppPaths.UsbBackupConfigFile, logger).Load();

// 1) Find the USB drive (unique identity = volume label + serial number + capacity)
var device = new UsbDetector(logger).GetRemovableDrives().FirstOrDefault();
if (device is null) return;

// 2) Mirror backup: current\ holds the latest copy, history\{date}\ keeps older versions
var mirror = new BackupMirrorService(logger);
var result = mirror.Mirror(
    sourceDirectory: device.RootPath,
    backupRootDirectory: config.BackupTargetDirectory,
    device: device,
    excludeRules: BackupRules.CreateDefaultExcludeRules(config.ExcludedExtensions),
    historyRetentionDays: config.HistoryRetentionDays);

logger.Info(result.Copy.Summary);
```

For a plain one-off copy, `FileCopier.CopyDirectory(...)` is still available directly.

## Design Conventions

- **Never throws**: a failed log write → `Logger.LastError`; a corrupted configuration → fall back to the default values; a single file copy failure → `CopyResult.Errors` + `FailedFiles`. Callers only need to check the return values.
- **Testable**: `Logger`, `ConfigManager`, and `FileCopier` all support injected directories / file paths, and unit tests use temporary directories, so they never touch real user data.
- **No external programs are executed**: the copy engine only reads the source and only writes the destination; it never executes any executable file in the source directory.
- **Thread safety**: `Logger` locks around writes; `Notifier` automatically switches back to the UI thread; each `FileCopier` instance is independent and can be used concurrently (with different destination directories).

## Unit Tests

`tests/WinToolBox.Core.Tests` covers:

- `FileCopier`: new files / directory structure, a second run skipping everything, same-size content changes being detected even when the timestamp goes backwards, large-file head/tail sampling, lazy enumeration (the file list is never materialised), skipping junction / symlink directories, and consecutive-failure early stop
- `ConfigManager`: default values, round-trip (including Chinese paths), tolerance of corrupted JSON, normalization, atomic writes leaving no leftovers, and delete and recreate
- `BackupMirrorService`: new / modified / deleted files, soft-delete history, same-day multiple versions all being preserved, history retention and purging, and `manifest.json` content
- `Logger` / `TemplateManager`: per-tool log directories, concurrent writes losing no lines, and template save/delete failures being reported instead of swallowed
- `BackupRules` / `ExcludeRules` / `HashEngine`: rule, exclusion and hashing behavior

```powershell
dotnet test WinToolBox.sln
```
