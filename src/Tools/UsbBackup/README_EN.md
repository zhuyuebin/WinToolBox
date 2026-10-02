# UsbBackup User Guide

[English](README_EN.md) | [简体中文](README.md)

> A WinToolBox sub-tool: **incrementally backs up** USB drives to a directory of your choice on a local hard disk. After you plug in a USB drive, **click 「立即备份」 (Back up now) manually** — the program never copies anything automatically.

[← Back to WinToolBox overview](../../../README_EN.md)

| Item | Content |
| --- | --- |
| Program name | `UsbBackup.exe` (WinForms window application + resident tray icon, C# / .NET 8) |
| Project location | `src/Tools/UsbBackup/` (outputs `UsbBackup.exe`) |
| Version | `0.4.0`, controlled centrally by the repository root [`Directory.Build.props`](../../../Directory.Build.props) |
| Shared library | References `src/WinToolBox.Core`, reusing its `Logger` / `ConfigManager` / `FileCopier` / `UsbDetector` / `Notifier` |

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
5. **Review and exit**: backed-up files go to `{target directory}\{volume label}_{volume serial number}\{yyyy-MM-dd}\`; click 「打开日志目录」 (Open log directory) to view the logs. **Closing the window only minimizes it to the tray**; to exit completely, click 「退出程序」 (Exit program) or right-click the tray icon → 「退出」 (Exit).

## Main Window Overview

The window title is `WinToolBox - U盘备份`, and from top to bottom it is divided into four blocks:

| Section | Control | Description |
| --- | --- | --- |
| Backup settings | 「备份目标目录」 (Backup target directory) text box | Shows the currently configured backup target directory; it can be edited directly |
| Backup settings | 「浏览…」 (Browse…) | Opens a folder selection dialog to choose the target directory; you must click 「保存设置」 (Save settings) afterwards |
| Backup settings | 「排除的文件后缀」 (Excluded file extensions) text box | Extensions separated by English (Chinese is also accepted) commas, such as `.tmp,.part`; on save they are uniformly lowercased and prefixed with `.` |
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

### Target Path Template

```text
{configured target directory}\{volume label}_{volume serial number}\{yyyy-MM-dd}\
```

For example, with the target directory `D:\UsbBackup`, volume label `MYUSB` and volume serial number `1A2B3C4D`, the backup on 2026-09-30 is written to `D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30\`.

- Devices are identified by **volume label + volume serial number + capacity**, **not by drive letter** (after moving to another USB port the drive letter changes from `E:` to `F:`, yet the backup still lands in the same device directory).
- One date directory per day, so historical backups are never overwritten by new ones.
- An empty volume label uses the `NOLABEL` placeholder, and an unreadable serial number uses `00000000`; illegal characters in the volume label are replaced with `_`.

### Incremental Strategy

- **Copy only, never delete**: the program **never deletes** any file in the target directory, and does not modify anything on the USB drive either.
- **Unmodified files are skipped**: judged by **modification time + size**; if the sizes are equal and the target is not older than the source, the file is treated as unmodified.
- Only **new files** and **modified files** are copied; after copying, the target file's modification time is aligned with the source file so that the next comparison stays accurate.
- Files deleted from the USB drive **remain** in the backup directory (historical snapshot semantics — no synchronized deletion).
- A single file failure does not abort the whole backup run: the log records the failure count and the first 5 details, and the UI summarizes it as 「失败 K 个」 (K failed).

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
| Log file | `wintoolbox-yyyyMMdd.log`, split by day, kept for 30 days by default (earlier logs are cleaned up at startup; the same file is shared with FolderCreator) |

Configuration example (`config.json`, which has only these two fields):

```json
{
  "backupTargetDirectory": "D:\\UsbBackup",
  "excludedExtensions": [
    ".iso",
    ".tmp",
    ".part",
    ".crdownload"
  ]
}
```

| Field | Type | Description |
| --- | --- | --- |
| `backupTargetDirectory` | string | Backup target directory; leaving it empty means it is not configured yet, in which case 「立即备份」 (Back up now) does not run and prompts you to set the target directory first |
| `excludedExtensions` | string[] | Excluded file extensions (case-insensitive; you may write `.tmp` or `tmp`, and on save they are uniformly lowercased and prefixed with `.`) |

> **There is no switch field such as `autoBackupEnabled`**: backups can only be triggered manually. Such a leftover field in an old configuration is simply ignored, and it disappears after you click 「保存设置」 (Save settings) once to rewrite the file.
> When the configuration file is missing, empty or corrupted, the program falls back to the default configuration and writes a WARN log entry, and **does not overwrite** your original file; click 「保存设置」 (Save settings) once in the main window to regenerate the canonical format (when writing it by hand, `#` comments and trailing commas are allowed).

## Command-Line Arguments

```powershell
# Show the version: prints something like “WinToolBox - U盘备份 UsbBackup 0.4.0”
UsbBackup.exe --version

# Local smoke self-test: runs one pass of “first copy → incremental skip → incremental update → exclusion rules → configuration read/write → USB drive enumeration”
# in a temporary directory, without needing a real USB drive; 16 checks in total, and exit code 0 means all of them passed
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
- Manual acceptance testing with a physical USB drive: [Local manual test checklist](../../../MD-files/本地手动测试清单_EN.md)
- Sister tool: [FolderCreator User Guide](../FolderCreator/README_EN.md)
- Source code: `src/Tools/UsbBackup/` ([`Program.cs`](Program.cs), [`TrayApplicationContext.cs`](TrayApplicationContext.cs), [`MainForm.cs`](MainForm.cs), [`BackupService.cs`](BackupService.cs), [`UsbWatcher.cs`](UsbWatcher.cs), [`ProcessIntegrity.cs`](ProcessIntegrity.cs))
