# UsbBackup User Guide

[English](README_EN.md) | [简体中文](README.md)

> A WinToolBox sub-tool: **incrementally backs up** USB drives to a directory of your choice on a local hard disk, **keeping 30 days of version history**. After you plug in a USB drive, **click 「立即备份」 (Back up now) manually** — the program never copies anything automatically.

[← Back to WinToolBox overview](../../../README_EN.md)

| Item | Content |
| --- | --- |
| Program name | `UsbBackup.exe` (WinForms window application + resident tray icon, C# / .NET 8) |
| Project location | `src/Tools/UsbBackup/` (outputs `UsbBackup.exe`) |
| Version | `0.4.8`, controlled centrally by the repository root [`Directory.Build.props`](../../../Directory.Build.props) |
| Shared library | References `src/WinToolBox.Core`, reusing its `Logger` / `ConfigManager` / `FileCopier` / `BackupMirrorService` / `UsbDetector` / `Notifier` |

## Requirements and How to Get It

| How to use | Requirement |
| --- | --- |
| Single-file exe (recommended) | Windows 10 / 11 (x64), just double-click `UsbBackup.exe`; the release package is self-contained with the .NET runtime, so there is **no installation and no need to pre-install .NET** |
| Run from source | Windows 10 / 11 (x64) + .NET 8 SDK: `dotnet run --project src/Tools/UsbBackup/UsbBackup.csproj` |

Download: open <https://github.com/zhuyuebin/WinToolBox/releases/latest>, download **`UsbBackup-win-x64.zip`**, extract it to any directory (for example `D:\Tools\UsbBackup\`), and double-click the `UsbBackup.exe` inside it.

- **No installation**: it does not write to the registry or register a system service; it only writes its configuration file and logs under your own user directory.
- The first run may be blocked by SmartScreen (the program is not code-signed): just click 「更多信息」 (More info) → 「仍要运行」 (Run anyway). A single-file program self-extracts to a temporary directory on first launch, which is normal (see [FAQ](#faq)).

## Quick Start (5 Steps)

1. **Launch**: double-click `UsbBackup.exe`. The program **opens the main window directly**, and also places a resident tray icon in the notification area (bottom-right of the taskbar); by default Windows 11 tucks new icons into the **“^” overflow area**.
2. **Choose a target directory**: in 「备份设置」 (Backup settings), type the **backup target directory** or click 「浏览…」 (Browse…) to pick one (an empty directory on a local hard disk is recommended; **do not choose the USB drive itself**).
3. **Adjust exclusions as needed**: in 「排除的文件后缀」 (Excluded file extensions), append the extensions you do not want to back up, separated by English commas (for example `.iso,.zip`), and then click 「保存设置」 (Save settings) — nothing takes effect unless you save.
4. **Plug in the USB drive and back up manually**: click 「立即备份」 (Back up now) (or right-click the tray icon → 「立即备份」). The program performs one incremental backup for **every currently plugged-in** USB drive; when it finishes, a tray balloon pops up and the result is also written to 「运行日志」 (Run log).
5. **Review and exit**: the latest copy lives in `{target directory}\{volume label}_{volume serial number}\current\`, and deleted / overwritten older versions live in `...\history\{date}\` (**kept for 30 days by default**); click 「打开日志目录」 (Open log directory) to view the logs. **Closing the window only minimizes it to the tray**; to exit completely, click 「退出程序」 (Exit program) or right-click the tray icon → 「退出」 (Exit).

## Main Window Overview

The window title is `WinToolBox - U盘备份`, and from top to bottom it is divided into four blocks:

| Section | Control | Description |
| --- | --- | --- |
| Backup settings | 「备份目标目录」 (Backup target directory) text box | Shows the currently configured backup target directory; it can be edited directly |
| Backup settings | 「浏览…」 (Browse…) | Opens a folder selection dialog to choose the target directory; you must click 「保存设置」 (Save settings) afterwards |
| Backup settings | 「排除的文件后缀」 (Excluded file extensions) text box | Extensions separated by English (Chinese is also accepted) commas, such as `.tmp,.part`; on save they are uniformly lowercased and prefixed with `.` |
| Backup settings | 「历史版本保留」 (History retention) drop-down | Choose `7 days` / `30 days` (default) / `90 days` / `keep forever (no automatic cleanup)`; choosing 「永久保留」 turns the hint on the right into a red risk warning — the `history` directory is never cleaned automatically and disk usage keeps growing |
| Backup settings | 「保存设置」 (Save settings) | Writes the configuration file and fills the normalized result back into the UI; when the target directory is empty it prompts 「请先选择备份目标目录」 (Please choose a backup target directory first) and does not save |
| Actions | 「立即备份」 (Back up now) | One of the two ways to trigger a backup manually (the other is the tray right-click 「立即备份」). During a backup the button is disabled and the cursor becomes a wait cursor; repeated clicks are ignored. It only copies, and never executes any program on the USB drive |
| Actions | 「刷新设备」 (Refresh devices) | Re-enumerates removable disks; the status bar shows 「已检测到 N 个 U 盘」 (Detected N USB drives) or 「未检测到 U 盘」 (No USB drive detected) |
| Actions | 「打开日志目录」 (Open log directory) | Opens `%LocalAppData%\WinToolBox\logs\` in File Explorer |
| Actions | 「隐藏到托盘」 (Hide to tray) | Only hides the window; the program keeps running, and double-clicking the tray icon brings it back |
| Actions | 「退出程序」 (Exit program) | Really exits after a second confirmation (ends the process and removes the tray icon) |
| Actions | 「设备：N 个」 (Devices: N) | Number of removable disks currently detected |
| Run log | Log text box | Scrolls logs in real time (including exception stack traces); when the window opens it first echoes **up to the last 100 lines** of the current day's log; the UI keeps at most 800 lines and trims from the front beyond that |
| Run log | 「清空」 (Clear) | Clears only the UI text, **without affecting** the log files on disk |
| Bottom | Status bar | Shows prompts such as 「就绪」 (Ready), 「正在备份…」 (Backing up…), and 「备份完成：成功 N 个，跳过 M 个，失败 K 个」 (Backup finished: N succeeded, M skipped, K failed) |

## Tray Menu

**Right-click** the tray icon to open the menu (a left single-click does nothing; **left double-click = open the main window**):

| Menu item | Effect |
| --- | --- |
| **打开主界面** (Open main window) | Shows and activates the main window (**double-clicking the tray icon** has the same effect; clicking the balloon notification does too) |
| **立即备份** (Back up now) | Runs one incremental backup for every plugged-in USB drive; if a backup is already running it prompts 「备份正在进行」 (A backup is already in progress) and does not queue another one |
| **打开日志** (Open log) | Opens the log directory `%LocalAppData%\WinToolBox\logs\` in File Explorer, then double-click the current day's `wintoolbox-yyyyMMdd.log` |
| **退出** (Exit) | Stops USB drive monitoring and exits the program (no background process is left behind) |

> The tray icon text is `WinToolBox - U盘备份`. **Closing the main window = minimizing to the tray** (the first time it pops a balloon 「已最小化到托盘，双击托盘图标可重新打开。」 — Minimized to tray, double-click the tray icon to reopen.), and the program keeps running until you click 「退出程序」 (Exit program) or right-click the tray icon → 「退出」 (Exit).

## Backup Rules

### Directory Layout (fixed folders + true incremental + soft-delete history)

```text
{configured target directory}\{volume label}_{volume serial number}\
├─ current\                    ← the latest copy of the USB drive (updated incrementally; fixed path, no date)
│  ├─ Documents\
│  └─ Pictures\
├─ history\                    ← deleted / overwritten older versions
│  ├─ 2026-10-01\
│  │  └─ accidentally-deleted-file.docx
│  └─ 2026-10-03\
│     └─ overwritten-old-version.docx
└─ manifest.json               ← last backup time, file count, total size, history policy and comparison result
```

For example, with the target directory `D:\UsbBackup`, volume label `MYUSB` and volume serial number `1A2B3C4D`, the latest copy of the USB drive lives in `D:\UsbBackup\MYUSB_1A2B3C4D\current\`.

- Devices are identified by **volume label + volume serial number + capacity**, **not by drive letter** (after moving to another USB port the drive letter changes from `E:` to `F:`, yet the backup still lands in the same device directory).
- **Date-stamped target directories are no longer used**: one USB drive maps to exactly one backup root, and `current` always reflects that drive's latest state.
- An empty volume label uses the `NOLABEL` placeholder, and an unreadable serial number uses `00000000`; illegal characters in the volume label are replaced with `_`.

### Incremental Strategy and Version History

| Scenario | Behaviour |
| --- | --- |
| A file is **added** on the USB drive | Copied to `current\` |
| A file is **modified** on the USB drive | The new version is written to `current\`, and the **old version is moved first** into `history\{today}\` (keeping its relative path) || A file is **deleted** on the USB drive | The copy in `current\` is **moved** into `history\{today}\` — it is **not deleted**, so accidental deletions can be recovered |
| The **same file has several older versions** on one day | Each version is archived separately (later ones get an `@time` suffix, e.g. `report@143005.txt`; same-second collisions get `-2`, `-3`), so **no version is ever overwritten or dropped**. If the path would become too long, conflicting versions go to `history\{date}\h{n}\{hash}{ext}` instead (dropping the original folder and readable name to make it actually fit) |
| A file is **unchanged** | Skipped, but only after a content check, so that "same size and timestamp yet different content" is never missed forever |

- **Strict content verification (on by default)**: small files of equal size get a full SHA256 comparison; files larger than 16 MiB use a compromise of "size + modification time + sampled hash of the first and last 64 KiB each". Files skipped by that compromise are explicitly labelled 「未做内容校验」 (not content-verified) in the log summary — the program never claims a verification it did not perform.
- **`history` is kept for 30 days by default**: at the end of every backup, `history\{yyyy-MM-dd}\` folders older than the retention period are cleaned up. You can change this to 7 / 30 / 90 days in 「历史版本保留」; choosing 「永久保留」 (keep forever) shows a red disk-usage risk warning.
- **File content in `current` is never deleted**: deletion semantics always degrade to "move into `history`".
- A single file failure does not abort the whole backup run: at most the first 50 failure details plus the total count are kept, and both log and summary state the failure count.
- Before copying, the program compares `DriveInfo.AvailableFreeSpace` against the total bytes to copy (**reserving extra space for `history`**); when space is insufficient it fails immediately with a "needed X / available Y" message.
- Copying writes `{target name}.tmp` first and then atomically replaces the target, so a failure midway never leaves a truncated target file; `.tmp` leftovers from a forcefully interrupted run are cleaned up automatically on the next backup.

### Default Exclusions

The following are excluded from backups by default (to keep system junk and autorun scripts out of your backups):

| Category | Default exclusions |
| --- | --- |
| File names | `autorun.inf`, `desktop.ini`, `Thumbs.db` |
| Directory names | `System Volume Information`, `$RECYCLE.BIN` (including other capitalizations), `found.000`, `.Trash`, `.Trashes` |
| File extensions | `*.tmp`, `*.part`, `*.crdownload`, plus any extensions you append in the configuration |

> Extension comparison is case-insensitive; appended extensions are **merged** with the default ones (default entries cannot be removed).

### Safety Policy

- **Copy only, never execute**: it does not run any `exe`, `bat`, `cmd`, shortcut or script on the USB drive, performs no autorun, and does not parse `autorun.inf` (that file itself is excluded).
- All copies use streaming reads and writes (1 MiB buffer), so large files do not occupy a lot of memory at once.
- If the target directory is on the **same volume** as the source USB drive (for example, you pick a directory on the USB drive as the target), the program refuses and skips it, prompting 「避免备份回 U 盘自身」 (Avoid backing up back onto the USB drive itself).

### Why Plugging in a USB Drive Does Not Trigger an Automatic Backup (Design Trade-off)

This is a **deliberate design decision**, not something that was left out: `UsbWatcher` still monitors plug and unplug events, but on insertion it does only three things — pops one balloon 「检测到 U 盘」 (USB drive detected, prompting you to click 「立即备份」 manually), writes a log entry (one of which states explicitly 「本工具不会自动备份，请在主界面点「立即备份」或右键托盘图标 →「立即备份」」 — This tool does not back up automatically; please click 「立即备份」 in the main window or right-click the tray icon → 「立即备份」), and updates the main window status bar to 「检测到 U 盘 X:，可点击「立即备份」」 (USB drive X: detected, you can click 「立即备份」).

- It avoids copying someone else's USB drive in full just because it was plugged in, which would cause privacy issues and disk usage.
- The backup timing is entirely up to you: plug in the drive, confirm its contents, and then click 「立即备份」.

## Configuration File and Logs

| Content | Path |
| --- | --- |
| Configuration file | `%AppData%\WinToolBox\UsbBackup\config.json` (usually `C:\Users\<user name>\AppData\Roaming\WinToolBox\UsbBackup\config.json`) |
| Log directory | `%LocalAppData%\WinToolBox\logs\` (usually `C:\Users\<user name>\AppData\Local\WinToolBox\logs\`) |
| Log file | `wintoolbox-yyyyMMdd.log`, split by day, kept for 30 days by default (earlier logs are cleaned up at startup; the same file is shared with FileMaster) |

Configuration example (`config.json`):

```json
{
  "backupTargetDirectory": "D:\\UsbBackup",
  "excludedExtensions": [
    ".iso",
    ".tmp",
    ".part",
    ".crdownload"
  ],
  "strictContentVerification": true,
  "historyRetentionDays": 30
}
```

| Field | Type | Description |
| --- | --- | --- |
| `backupTargetDirectory` | string | Backup target directory; leaving it empty means it is not configured yet, in which case 「立即备份」 (Back up now) does not run and prompts you to set the target directory first |
| `excludedExtensions` | string[] | Excluded file extensions (case-insensitive; you may write `.tmp` or `tmp`, and on save they are uniformly lowercased and prefixed with `.`) |
| `strictContentVerification` | bool | Whether to perform strict content verification, default `true`. When disabled only "size + modification time" is compared, and skipped files are labelled 「未做内容校验」 (not content-verified) in the summary |
| `historyRetentionDays` | int | How many days to keep `history`, default `30`; `0` means **keep forever** (the UI then shows a red disk-usage risk warning). Negative values are normalized back to the default 30 |

> **There is no switch field such as `autoBackupEnabled`**: backups can only be triggered manually. Such a leftover field in an old configuration is simply ignored, and it disappears after you click 「保存设置」 (Save settings) once to rewrite the file.
> When the configuration file is missing, empty or corrupted, the program falls back to the default configuration and writes a WARN log entry, and **does not overwrite** your original file; click 「保存设置」 (Save settings) once in the main window to regenerate the canonical format (when writing it by hand, `#` comments and trailing commas are allowed).

## Command-Line Arguments

```powershell
# Show the version: prints something like “WinToolBox - U盘备份 UsbBackup 0.4.8”
UsbBackup.exe --version

# Local smoke self-test: runs one pass of “first backup → incremental skip → incremental update →
# old version moved to history → deletion on the USB drive soft-deleted into history →
# exclusion rules → configuration read/write → USB drive enumeration”
# in a temporary directory, without needing a real USB drive; 25 checks in total, and exit code 0 means all of them passed
UsbBackup.exe --selftest
UsbBackup.exe --selftest D:\selftest-report.txt   # write the report to the given file
```

- Without a path, `--selftest` writes the report to `%TEMP%\UsbBackup-selftest.txt`; the terminal also prints the full report at the same time (when launched by double-clicking in File Explorer there is no console, so just read the report file).
- When the self-test conclusion is 「存在失败项」 (there are failing items), the exit code is 1, which makes it easy for scripts to check.

## FAQ

| Question | Cause and handling |
| --- | --- |
| After double-clicking there is “no response”, or no icon is visible in the tray | First check Task Manager for `UsbBackup.exe`: **if the process is there, the program has actually started**. ① The icon may have been tucked into the Windows 11 **“^” overflow area** — click it open; ② go to 「设置 → 个性化 → 任务栏 → 其他系统托盘图标」 (Settings → Personalization → Taskbar → Other system tray icons) and set UsbBackup to be shown; ③ if you can see the program entry in that menu, right-click → 「打开主界面」 (Open main window). |
| The process is running but there is **never** an icon in the notification area, and a dialog appeared at startup | The program was started with **low integrity** (sandbox / restricted terminal), and Windows refuses to let it register a tray icon. First end all `UsbBackup.exe` processes in Task Manager, then start it again by double-clicking `UsbBackup.exe` in File Explorer or from a normal terminal. |
| The blue Windows security prompt (SmartScreen) appears every time | The program is **unsigned**, and the file carries the “downloaded from the internet” mark of origin. Handling: right-click `UsbBackup.exe` → 「属性」 (Properties) → check 「解除锁定」 (Unblock) → 「确定」 (OK); a whole extracted directory can be unblocked in bulk: `Get-ChildItem <extract directory> -Recurse -File \| Unblock-File`. The long-term solution is to code-sign the exe. |
| Why is there no automatic backup when I plug in a USB drive? | **By design**: on insertion it only pops one balloon, writes a log entry and updates the status bar; a backup must be triggered manually with 「立即备份」 (see the [design trade-off](#why-plugging-in-a-usb-drive-does-not-trigger-an-automatic-backup-design-trade-off) above). To confirm, you can search the 「运行日志」 (Run log) for `不会自动备份`. |
| Clicking 「立即备份」 (Back up now) says 「没有检测到可移动磁盘（U 盘）」 (No removable disk (USB drive) detected) (notification title 「未检测到 U 盘」 — No USB drive detected) | Make sure the USB drive is properly plugged in and its drive letter is visible in File Explorer, then click 「刷新设备」 (Refresh devices) and check whether 「设备：N 个」 (Devices: N) is greater than 0; enumeration also fails when a card reader has no card inserted, or when the drive letter is occupied by other software. |
| Clicking 「立即备份」 (Back up now) says the target directory is not configured | The backup target directory has not been saved yet. Go to 「备份设置」 (Backup settings) in the main window, choose a directory and click 「保存设置」 (Save settings), then back up again. |
| Can the backup directory be the USB drive itself? | No. When the program detects that the target and the source are on the **same volume**, it skips it and prompts 「避免备份回 U 盘自身」 (Avoid backing up back onto the USB drive itself); please choose a directory on a local hard disk instead. |
| The backup summary shows 「失败 K 个」 (K failed) | A single file failure (insufficient permissions, file in use, path too long, and so on) does not abort the whole backup run. Open the log and search for `失败明细` / `复制失败` to see the specific files and causes; after handling them, click 「立即备份」 (Back up now) again to fill the gaps — files that were copied successfully are skipped. |

## Related Links

- Repository overview: [`README_EN.md`](../../../README_EN.md)
- Manual acceptance testing with a physical USB drive: [UsbBackup manual test checklist](../../../docs/testing/UsbBackup-本地手动测试清单_EN.md)
- Sister tool: [FileMaster User Guide](../FileMaster/README_EN.md)
- Source code: `src/Tools/UsbBackup/` ([`Program.cs`](Program.cs), [`TrayApplicationContext.cs`](TrayApplicationContext.cs), [`MainForm.cs`](MainForm.cs), [`BackupService.cs`](BackupService.cs), [`UsbWatcher.cs`](UsbWatcher.cs), [`ProcessIntegrity.cs`](ProcessIntegrity.cs))
