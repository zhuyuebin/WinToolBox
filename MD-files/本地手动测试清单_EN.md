# UsbBackup Manual Test Checklist v0.4.0

[English](本地手动测试清单_EN.md) | [简体中文](本地手动测试清单.md)

> Applies to: `UsbBackup.exe` (WinToolBox's USB drive backup tool, v0.4.0, win-x64 portable single file)
> Systems: Windows 10 / 11
> Performed by: the user (manual operation on a physical machine in person)
> Estimated time: about 35 minutes (6 core rounds)
> Why manual testing is needed: the cloud CI environment has no physical USB drive, so `UsbDetector` and device plug/unplug events cannot be verified in CI and must be tested on a real machine.

---

## 0. Three Things to Remember First

1. **You should plug and unplug the USB drive at least 6 times** (the 6 core rounds, see Section 2); if you also complete the additional rounds, about 10 times in total.
2. Two places to check in every round: **the files in the backup folder** and **the log file for the day**.
3. **This tool does not back up automatically**: plugging in a USB drive only pops up a single balloon notification, and a backup must be triggered manually (「立即备份」 (Back up now) in the main window, or 「立即备份」 in the tray right-click menu); if any round does not meet its “Pass criteria”, collect the information described in Section 6, and do not delete the on-site files first (keep both the logs and the backup folder).

---

## 1. Preparation Before Testing

### 1.1 Hardware and data preparation

| Item | Requirement |
| --- | --- |
| USB drive | 1 working USB drive (FAT32 or exFAT are both fine; NTFS can also be tested); prepare a second, different USB drive if you are doing Additional round B |
| USB drive contents | **Do not put important data on it**. Create a new test folder and put 5–10 small files in it (for example `a.txt`, `b.jpg`, `docs\说明.txt` (i.e. `docs\notes.txt`), including one larger file such as 200 MB to verify large-file copying) |
| USB volume label | It is recommended to set an easy-to-remember name, for example **`MYUSB`** (right-click the USB drive in File Explorer → Properties → rename). All later examples in this checklist use `MYUSB` |
| Backup target folder | An empty folder on the local hard disk, for example **`D:\UsbBackup`**; do **not** choose the USB drive itself, and do not choose the root of the system drive |
| Paper / notepad for records | Recording as you test is recommended (see the “Result record table” in Section 5) |

> **Note in advance**: this tool does **not** back up automatically when a USB drive is plugged in. Every round below follows the rhythm “plug in the USB drive → manually click 「立即备份」 (Back up now)”;
> only Round 6 is deliberately button-free, an observation round used to verify that it really does not back up automatically.

**Write down your USB drive's volume serial number** (you will need it later to check folder names; the program uses it to tell devices apart and does not use drive letters):

```powershell
# Assume the USB drive is currently E: (confirm the real drive letter in “This PC”)
cmd /c vol E:
# Example output: 卷的序列号是 1A2B-3C4D
# When generating folder names the program uses the 8-digit form: 1A2B3C4D (the dash in the middle is removed)
```

You can also take a quick look at the drive letter, volume label and file system:

```powershell
Get-Volume | Where-Object DriveType -eq 'Removable' | Format-Table DriveLetter, FileSystemLabel, FileSystem, SizeRemaining, Size
```

**The expected backup root folder** is therefore: `D:\UsbBackup\MYUSB_1A2B3C4D\` (`{your target folder}\{volume label}_{volume serial number}\`).

### 1.2 Program preparation

1. Unzip `UsbBackup.exe` into any folder (for example `D:\Tools\UsbBackup\`) and **double-click it in File Explorer to run it**.
2. Double-clicking **opens the main window directly** (title 「WinToolBox - U盘备份」 (WinToolBox - USB Backup)), and the window contains three areas:
   - **备份设置** (Backup settings): backup target folder + 「浏览…」 (Browse…), excluded file extensions, 「保存设置」 (Save settings);
   - **操作** (Actions): 「立即备份」 (Back up now), 「刷新设备」 (Refresh devices), 「打开日志目录」 (Open log folder), 「隐藏到托盘」 (Hide to tray), 「退出程序」 (Exit program), with “设备：N 个” (Devices: N) shown on the right;
   - **运行日志** (Runtime log): scrolls the log in real time, with a 「清空」 (Clear) button on the right; the status bar is at the very bottom.
3. Confirm that **the tray icon has appeared**: you can see the `UsbBackup` icon in the notification area at the bottom right of the taskbar (if it is collapsed, click “^” to expand it; if necessary, drag it into the always-visible area so it is easy to watch the balloons).
   - Double-clicking the tray icon = **打开主界面** (Open main window); right-clicking the tray icon = the menu 「打开主界面 / 立即备份 / 打开日志 / 退出」 (Open main window / Back up now / Open log / Exit).
   - **Closing the window only minimizes it to the tray** (the first close pops up a balloon saying “已最小化到托盘” (minimized to tray)), and the program keeps running in the background. To **exit completely**, click 「退出程序」 (Exit program) in the main window, or 「退出」 (Exit) in the tray right-click menu.
4. (Optional, recommended) Run a local smoke self-test first to confirm that the basic capabilities work:

   ```powershell
   D:\Tools\UsbBackup\UsbBackup.exe --selftest "$env:USERPROFILE\Desktop\usbbackup-selftest.txt"
   echo "退出码=$LASTEXITCODE"    # expected: 退出码=0 (all 16 checks pass)
   ```

   If the exit code is not 0, look at the self-test report file and the logs first, rule out environment problems, and only then start the 6 rounds below.

5. In **「备份设置」 (Backup settings)** in the main window, set the **backup target folder** to `D:\UsbBackup` (click 「浏览…」 (Browse…) to pick it, or type the path directly), change the **excluded file extensions** as needed, then click **「保存设置」 (Save settings)**.
   - Separate multiple excluded extensions with English commas, for example `.tmp,.bak`; you can also write just `tmp`; if left empty, the program's built-in default exclusions are used;
   - If the folder does not exist, the program creates it automatically (log line `已创建备份目标目录` (backup target folder created));
   - If the target folder is chosen on a removable disk, a warning “目标目录可能不安全” (the target folder may be unsafe) pops up; choose “否” (No) and pick a folder on the local hard disk instead;
   - After saving successfully the log shows `设置已保存：目标目录=…，排除后缀=…` (settings saved: target folder=…, excluded extensions=…), the status bar shows “设置已保存” (settings saved), and a balloon pops up.

> The program is **single-instance**: double-clicking `UsbBackup.exe` again does not produce a second tray icon; it only shows a dialog saying “已经在运行，请查看通知区域” (already running, please check the notification area). This is normal.
>
> **Cannot see the tray icon?** If the program was started inside a **sandbox / restricted terminal** (a low-integrity process), Windows refuses to let it register a tray icon (the process runs, but the icon never appears). In that case the program automatically shows a dialog at startup explaining that “托盘图标无法注册” (the tray icon cannot be registered) and the two correct ways to start it; first end any leftover `UsbBackup.exe` in Task Manager, then start it again by “double-clicking it in File Explorer”.
>
> **This tool does not back up automatically**: plugging in a USB drive does not start a backup; a backup can only be triggered manually from 「立即备份」 (Back up now) in the main window or 「立即备份」 in the tray right-click menu.

### 1.3 Path quick reference (replace `<username>` with your own)

| Item | Exact path | Notes |
| --- | --- | --- |
| Config file | `C:\Users\<username>\AppData\Roaming\WinToolBox\UsbBackup\config.json` | Equivalent form: `%AppData%\WinToolBox\UsbBackup\config.json` |
| Log folder | `C:\Users\<username>\AppData\Local\WinToolBox\logs\` | Equivalent form: `%LocalAppData%\WinToolBox\logs\` |
| Today's log | `C:\Users\<username>\AppData\Local\WinToolBox\logs\wintoolbox-<today's date yyyyMMdd>.log` | For example `wintoolbox-20260930.log`; rotated daily and kept for 30 days |

**Three ways to open the log** (pick any one):

- **「打开日志目录」 (Open log folder)** in the 「操作」 (Actions) area of the main window: opens the folder holding the logs, then just double-click that day's `wintoolbox-yyyyMMdd.log`;
- Right-click the tray icon → **「打开日志」 (Open log)**: same effect, it also **opens the log folder with File Explorer** (it does not open a specific file directly); **clicking the 「检测到 U 盘」 (USB drive detected) or 「U盘备份完成」 (USB backup complete) tray balloon** has the same effect;
- Go to the folder directly: paste `%LocalAppData%\WinToolBox\logs\` into the File Explorer address bar and press Enter, then open `wintoolbox-yyyyMMdd.log` with **Notepad** or **VS Code**.

- **Tip**: after opening the log in Notepad, press `Ctrl+End` to jump to the end and see the most recent backup; or in VS Code press `Ctrl+F` and search for `复制完成` (copy complete).

---

## 2. Test rounds

### 2.1 Round overview

| Round | Action | USB plug/unplug count | One-line pass criterion |
| --- | --- | --- | --- |
| Round 1 | Start the program → set the backup folder → plug in the USB drive → click 「立即备份」 (first full backup) | Plug 1 / unplug 1 | Backup folder contents match the USB drive (file count and total size) |
| Round 2 | Change nothing, plug in again and click 「立即备份」 | Plug 1 / unplug 1 | The log shows “新增/更新 0，跳过 N” (0 added/updated, N skipped) and the backup folder is unchanged |
| Round 3 | Modify 1 existing file on the USB drive, then plug in and back up | Plug 1 / unplug 1 | Only 1 file is copied (新增/更新 1，跳过 N-1) |
| Round 4 | Add 1 new file, then plug in and back up | Plug 1 / unplug 1 | Only the newly added file is copied |
| Round 5 | Put the excluded items (`autorun.inf`, `test.tmp`, `System Volume Information`) in place, then plug in and back up | Plug 1 / unplug 1 | These items **must not** appear in the backup folder |
| Round 6 | Plug in the USB drive and **click nothing**, wait 1–2 minutes | Plug 1 / unplug 1 | **No** automatic backup; only the “检测到 U 盘” balloon and log entries |
| Additional A | The program is not running at plug-in → start it manually → click 「立即备份」 | Plug 1 / unplug 1 | The manual backup succeeds just the same |
| Additional B | Plug in a second, different USB drive | Plug 1 / unplug 1 | A different `卷标_序列号` (label_serial) folder is created |
| Additional C | Move the same USB drive to a different USB port (drive letter changes), then plug it in | Plug 1 / unplug 1 | Still recognized as the same device and backed up to the same folder |

> **6 core rounds = 6 plug/unplug cycles**; doing everything (including the additional rounds) = about 9 plug/unplug cycles.
>
> **Common rhythm (applies to Rounds 1–5 and to the additional rounds)**: plug in the USB drive → wait for the balloon and the log line “检测到 U 盘” (USB drive detected) → **manually click 「立即备份」 in the main window** (or 「立即备份」 in the tray right-click menu) → wait for the balloon and the runtime log line “备份完成” (backup complete) → check the backup folder → unplug the USB drive.
> The program does **not** back up automatically on plug-in, so “the backup only starts after you click the button” is correct behavior; **only Round 6 is the exception: in that round you deliberately click no buttons**, to verify that no automatic backup happens.

---

### Round 1: First manual backup (full backup)

| Item | Details |
| --- | --- |
| **Steps** | ① Confirm the program is running, **the main window is open** and the tray icon is visible, and that the backup target folder under 「备份设置」 (Backup settings) is `D:\UsbBackup` (click 「保存设置」 (Save settings) if you changed it); ② **plug** the USB drive into a USB port; ③ watch the tray balloon (title 「检测到 U 盘」 (USB drive detected)) and the runtime log; ④ click **「立即备份」 (Back up now)** in the main window; ⑤ wait for the “备份完成” (backup complete) balloon (a large file may take tens of seconds); ⑥ open the backup folder and look at the result; ⑦ **unplug** the USB drive |
| **Expected result** | ① After plugging in there is **no** automatic backup; only one balloon pops up, titled 「检测到 U 盘」 (USB drive detected), reading something like “E: 已插入。点击「立即备份」开始备份，或双击托盘图标打开主界面。” (E: inserted. Click 「立即备份」 (Back up now) to start a backup, or double-click the tray icon to open the main window.), and the status bar shows “检测到 U 盘 E:，可点击「立即备份」。” (USB drive E: detected, you can click 「立即备份」.); ② the backup starts only after you click 「立即备份」; ③ when the backup finishes a balloon pops up, titled 「U盘备份完成」 (USB backup complete), reading something like “‘MYUSB’备份完成：新增/更新 N 个文件，跳过 0 个未修改文件，共 1.2 MB。保存位置：D:\UsbBackup\MYUSB_1A2B3C4D\{today's date}”; ④ the folder `D:\UsbBackup\MYUSB_1A2B3C4D\{today's date}\` is created; ⑤ the relative structure under that folder matches the USB drive |
| **Files / logs to check** | The backup folder `D:\UsbBackup\MYUSB_1A2B3C4D\{today's date}\`; the scrolling content in the 「运行日志」 (Runtime log) panel of the main window; the log file `%LocalAppData%\WinToolBox\logs\wintoolbox-<today>.log` |
| **Pass criteria** | ① After plugging in, neither `开始备份：` (starting backup) nor `复制完成：` (copy complete) appears (that is, there was no automatic backup); ② after 「立即备份」 the balloon appears and says “失败 0” (0 failed); ③ the log contains `复制完成：<drive letter>:\ -> D:\UsbBackup\MYUSB_1A2B3C4D\<date>；新增/更新 N，跳过 0，失败 0`, where N = the number of non-excluded files on the USB drive; ④ **File count comparison**: backup folder file count = USB drive file count − number of excluded items; ⑤ **Total size comparison**: the two total sizes match (use the command below) |

```powershell
# Replace E: with your USB drive letter and the date with today
$src = 'E:\'
$dst = 'D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30'

'--- U 盘 ---'
Get-ChildItem $src -Recurse -File -Force -ErrorAction SilentlyContinue |
  Measure-Object -Property Length -Sum | Select-Object Count, Sum

'--- 备份目录 ---'
Get-ChildItem $dst -Recurse -File -Force -ErrorAction SilentlyContinue |
  Measure-Object -Property Length -Sum | Select-Object Count, Sum
```

> Note: if the USB drive contains `System Volume Information`, `autorun.inf`, `*.tmp`, `Thumbs.db` and the like, these are excluded items and **should not** be in the backup folder, so the “backup folder file count” may be slightly smaller than the “USB drive file count”. This is correct behavior.

---

### Round 2: No changes, back up again (everything should be skipped)

| Item | Details |
| --- | --- |
| **Steps** | ① Change nothing and **plug the same USB drive in again**; ② click **「立即备份」** in the main window; ③ wait for the balloon; ④ open the backup folder; ⑤ unplug the USB drive |
| **Expected result** | ① The backup runs normally, but **no file is copied again**; ② the balloon/log shows “新增/更新 0，跳过 N” (0 added/updated, N skipped); ③ the backup folder's file count, total size and file contents are exactly the same as in Round 1 |
| **Files / logs to check** | The `复制完成` (copy complete) line newly added in this round's runtime log (keywords `新增/更新 0`, `跳过` (skipped)); the **modification times** in the backup folder (old files should not be rewritten) |
| **Pass criteria** | ① That log line reads `新增/更新 0，跳过 N，失败 0`, where N = the number of files copied in Round 1; ② the file count and total size in the backup folder are unchanged; ③ spot-check 2–3 files and confirm their “modification time” was not refreshed to the current time today |

```powershell
# The most recently written files in the backup folder (normally no file should have been rewritten “just now”)
Get-ChildItem 'D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30' -Recurse -File |
  Sort-Object LastWriteTime -Descending | Select-Object -First 5 FullName, LastWriteTime, Length
```

---

### Round 3: Modify one existing file, then back up (only 1 file copied)

| Item | Details |
| --- | --- |
| **Steps** | ① Pick an existing file on the USB drive (for example `a.txt`), **open it, append a line and save** (or replace its contents, but do **not** change the file name); ② plug the USB drive in again and click **「立即备份」** in the main window; ③ wait for the balloon; ④ compare the files |
| **Expected result** | Only the 1 modified file is copied again; every other file is skipped |
| **Files / logs to check** | The log line `新增/更新 1，跳过 N-1`; the contents and modification time of that file in the backup folder; the modification times of the other files are unchanged |
| **Pass criteria** | ① The log clearly shows `新增/更新 1，跳过 <total file count - 1>，失败 0`; ② that file's contents in the backup folder match the USB drive (`CopiedFiles=1`); ③ the other files were not rewritten (their modification times are the same as in Round 1); ④ write down the time of this run so it can be told apart from Round 4 |

```powershell
# Compare the modification time and size of the same file (replace the file name with the one you changed)
Get-Item 'E:\a.txt', 'D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30\a.txt' |
  Select-Object FullName, Length, LastWriteTime
```

---

### Round 4: Add one new file, then back up (only the new file is copied)

| Item | Details |
| --- | --- |
| **Steps** | ① **Create** a file on the USB drive (for example `new-file.txt`, with arbitrary contents); ② plug the USB drive in again and click **「立即备份」** in the main window; ③ wait for the balloon; ④ compare the files |
| **Expected result** | Only the new file is copied into the backup folder; all old files are skipped |
| **Files / logs to check** | The log line `新增/更新 1，跳过 N` (here N includes all files from the previous rounds); `new-file.txt` appears in the backup folder |
| **Pass criteria** | ① The log shows `新增/更新 1，失败 0`; ② a new file with the same name appears in the backup folder and its contents match; ③ the existing files in the backup folder were not copied “all over again”, and no file was deleted |

> **Reverse check (optional, verifies the “never delete” policy)**: **delete** `new-file.txt` from the USB drive, plug it in again and click 「立即备份」 once more.
> Expected: `new-file.txt` **is still kept** in the backup folder (the program only adds, never deletes, keeping historical snapshots), and the log shows `新增/更新 0，跳过 N`.

---

### Round 5: Exclusion rule verification

| Item | Details |
| --- | --- |
| **Steps** | ① Put `autorun.inf` (any contents, for example a single line `[Autorun]`) and `test.tmp` in the **root of the USB drive**; ② try to create a folder named `System Volume Information` in the root of the USB drive; ③ plug the USB drive in again and click **「立即备份」** in the main window; ④ check the backup folder |
| **Expected result** | ① `autorun.inf` and `test.tmp` **do not appear** in the backup folder; ② the `System Volume Information` folder (if it exists) **does not appear** in the backup folder; ③ the other test files are backed up normally; ④ the log contains a “命中排除规则” (exclusion rule matched) entry |
| **Files / logs to check** | Backup folder root: there should be no `autorun.inf` / `test.tmp` / `System Volume Information`; log keyword `命中排除规则，跳过目录` (exclusion rule matched, skipping folder) |
| **Pass criteria** | ① The command below produces no output; ② the log contains `命中排除规则，跳过目录：E:\System Volume Information` (provided that folder exists); ③ neither `autorun.inf` nor `test.tmp` appears in **any subfolder** of the backup folder |

```powershell
# Expected: no output at all
Get-ChildItem 'D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30' -Recurse -Force -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -in @('autorun.inf','test.tmp','System Volume Information') } |
  Select-Object FullName
```

> **About not being able to create `System Volume Information`**: this folder is protected by Windows system permissions, so an ordinary user (even an administrator) usually **cannot create it manually** in File Explorer; an error saying “拒绝访问 / 需要权限” (access denied / permission required) is normal and **does not count as a failure**. Record honestly that you “could not create it”, then instead:
> as long as the folder **exists** on the USB drive (Windows often creates it automatically), just confirm that it does not appear in the backup folder; if the USB drive never had this folder, mark this item as “not applicable (N/A)” for this round.

---

### Round 6: Plug in without clicking any button (**no automatic backup**)

> This round specifically verifies that “this tool does not back up automatically”. **Key requirement: after plugging in the USB drive, do not click any button and do not touch the tray menu**.

| Item | Details |
| --- | --- |
| **Steps** | ① Write down the current time, and confirm the program is running and the main window is open, and note the file count in the backup folder and the latest `复制完成` log line; ② **plug** the USB drive into a USB port; ③ **click nothing** and wait patiently for **1–2 minutes** (do not click 「立即备份」, and do not right-click the tray icon); ④ watch the balloon, the status bar, the runtime log and the log file; ⑤ once you have confirmed there was no automatic backup, **click 「立即备份」 manually once more**, to confirm the backup feature itself works; ⑥ unplug the USB drive |
| **Expected result** | ① On plug-in the balloon pops up **only once**, titled 「检测到 U 盘」 (USB drive detected), reading something like “E: 已插入。点击「立即备份」开始备份，或双击托盘图标打开主界面。”; ② the status bar shows “检测到 U 盘 E:，可点击「立即备份」。”; ③ the runtime log and the log file contain the two lines `U 盘已插入：` (USB drive inserted:) and `本工具不会自动备份…` (this tool does not back up automatically…); ④ during the wait there is **no** `开始备份：` / `复制完成：` / `备份完成：` log line, and the backup folder has **no** additions or rewrites; ⑤ after clicking 「立即备份」 manually the backup completes normally (if no file changed, the log should read `新增/更新 0，跳过 N`) |
| **Files / logs to check** | The runtime log panel; the log file `%LocalAppData%\WinToolBox\logs\wintoolbox-<today>.log`; the file count in the backup folder `D:\UsbBackup\MYUSB_1A2B3C4D\{today's date}\` |
| **Pass criteria** | ① Within 1–2 minutes of plugging in, the log contains **only** `U 盘已插入：…` and that `本工具不会自动备份` line, and **no** `开始备份：`; ② during the wait the backup folder's total size and latest write time do not change; ③ the manual 「立即备份」 completes normally, with “失败 0” in the balloon summary; ④ the “检测到 U 盘” balloon appears exactly once (it does not pop up again every second) |

```powershell
# Use the log to confirm “no automatic backup during the wait”: the command below should list only the lines around the plug-in,
# and between two plug-ins there should be no “开始备份” (starting backup) / “复制完成” (copy complete)
$log = "$env:LocalAppData\WinToolBox\logs\wintoolbox-$(Get-Date -Format yyyyMMdd).log"
Select-String -Path $log -Pattern 'U 盘已插入|本工具不会自动备份|开始备份|复制完成' | Select-Object -Last 10
```

> If `开始备份：` really does appear at this step **without any button being clicked**, that is a defect that does not match expectations; collect the logs and screenshots as described in Section 6.

---

### Additional round A: The program is not running at plug-in

| Item | Details |
| --- | --- |
| **Steps** | ① Click 「退出程序」 (Exit program) in the main window (or right-click the tray icon → 「退出」 (Exit)) and confirm the tray icon disappears (no `UsbBackup.exe` process in Task Manager); ② plug in the USB drive (nothing is listening now, so no backup happens); ③ double-click `UsbBackup.exe` in File Explorer to start the program (it opens the main window directly); ④ click **「立即备份」** in the main window (or right-click the tray icon → 「立即备份」); ⑤ after looking at the result, unplug the USB drive |
| **Expected result** | ① The plug-in event happened before the program started, so **no backup is made retroactively after startup** (this is expected behavior, not a fault); ② clicking 「立即备份」 enumerates all removable disks currently plugged in and backs them up one by one, popping balloons as usual |
| **Files / logs to check** | The `用户在主界面点击「立即备份」` (user clicked 「立即备份」 in the main window), `开始备份：`, `备份完成：` and `本轮备份汇总：` (summary for this backup round) entries in the log; the backup folder is updated normally |
| **Pass criteria** | ① After 「立即备份」 the balloon title is 「U盘备份完成」 (USB backup complete) and the failure count in the summary is 0; ② the backup folder contents match the USB drive (or the log shows everything skipped, depending on whether it was backed up before); ③ if a “未检测到 U 盘” (no USB drive detected) balloon appears, the USB drive was not recognized and this round fails |

---

### Additional round B: Switch to a different USB drive

| Item | Details |
| --- | --- |
| **Steps** | ① Unplug the first USB drive; ② plug in a **second**, different USB drive (a different volume label, for example `MYUSB2`); ③ click **「立即备份」** in the main window; ④ look at the backup root folder |
| **Expected result** | A **new** device folder `D:\UsbBackup\MYUSB2_<serial number of the second drive>\{today's date}\` is created, coexisting with the first drive's folder without affecting it |
| **Files / logs to check** | The list of subfolders under the backup root `D:\UsbBackup\`; the target path in the log points to the new folder |
| **Pass criteria** | ① Two different `卷标_序列号` (label_serial) folders appear; ② the second drive's backed-up contents did not land in the first drive's folder; ③ the first drive's backup files still exist and are unchanged |

> Tip: if the second drive has never been plugged in before, plugging it in likewise only pops up the 「检测到 U 盘」 balloon; **you must click 「立即备份」 manually** for a backup to happen.

---

### Additional round C: Same USB drive, different USB port / drive letter change

| Item | Details |
| --- | --- |
| **Steps** | ① **Move the same USB drive to another USB port** (or plug in another storage device first so that it changes from `E:` to `F:`); ② confirm the drive letter really changed; ③ click **「立即备份」** in the main window; ④ look at the backup folder |
| **Expected result** | The program still recognizes it as the **same device** and backs it up into the **same** `MYUSB_1A2B3C4D` folder instead of creating a new one (when you change USB ports Windows first “removes” and then “inserts” the device, so the plug-in balloon may pop up once more; since no file changed, the log should read `新增/更新 0`, which is normal) |
| **Files / logs to check** | The `卷标_序列号` part of the target path in the log is the same as before; no extra `MYUSB_<other serial number>` folder appeared under `D:\UsbBackup` |
| **Pass criteria** | ① The backup lands back in the original folder; ② all unchanged files are skipped (`新增/更新 0`); ③ the number of device folders under the backup root has not increased |

> How it works: the device's unique identity uses the **volume serial number + volume label + capacity**, not the drive letter.

---

## 3. Logs to Check

**Log file (absolute path)**:

```text
C:\Users\<username>\AppData\Local\WinToolBox\logs\wintoolbox-yyyyMMdd.log
```

For example, if today is 2026-09-30 the file is `wintoolbox-20260930.log`. Open it with **Notepad** or **VS Code**; logs are rotated daily and kept for at most 30 days.

**Log line format**: `时间 [级别] 内容` (time [level] message), where the level is one of `INFO ` / `WARN ` / `ERROR`.

**The two most important lines when you plug in the USB drive** (example; note that the program does **not** back up automatically):

```text
2026-09-30 21:07:55.010 [INFO ] U 盘已插入：MYUSB (E:) 14.9 GB，可用 12.1 GB 序列号=1A2B3C4D
2026-09-30 21:07:55.012 [INFO ] 本工具不会自动备份，请在主界面点「立即备份」或右键托盘图标 →「立即备份」。
```

**The two most important lines after you manually click 「立即备份」** (example):

```text
2026-09-30 21:08:41.123 [INFO ] 复制完成：E:\ -> D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30；新增/更新 12，跳过 0，失败 0，目录 3，字节 1258291
2026-09-30 21:08:41.240 [INFO ] 备份完成：MYUSB -> D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30；新增/更新 12 个文件，跳过 0 个未修改文件，共 1.2 MB。
```

**Keyword quick reference**:

| Keyword | Meaning | Expectation |
| --- | --- | --- |
| `检测到 U 盘` (USB drive detected) | Title of the balloon shown when a USB drive is plugged in (shown only once) | Should appear once per plug-in, in every round |
| `U 盘已插入：` (USB drive inserted:) | Device insertion detected (followed by volume label, drive letter, capacity and serial number) | Should appear once per plug-in, in every round |
| `本工具不会自动备份` (this tool does not back up automatically) | Makes it explicit after plug-in that there is **no** automatic backup and that it must be triggered manually | Should appear once per plug-in, in every round (this is the line to focus on in Round 6) |
| `U 盘已拔出：` (USB drive removed:) | Device removal detected | Should appear once per unplug, in every round |
| `用户在主界面点击「立即备份」` (user clicked 「立即备份」 in the main window) | Manual backup trigger (main-window button) | Should appear once per 「立即备份」 click |
| `开始备份：` (starting backup:) | A backup has started | **Should appear only after 「立即备份」 is clicked**; it **must not appear** in Round 6 while no button is clicked |
| `复制完成：` (copy complete:) | Summary line from the copy engine (added/updated, skipped, failed, folders, bytes) | Should appear once per manual backup |
| `备份完成：` (backup complete:) | Summary line from the backup service (contains a Chinese summary) | Should appear once per manual backup |
| `本轮备份汇总：` (summary for this backup round:) | Overall summary after 「立即备份」 in the main window finishes (success / skipped / failed counts) | Should appear once per manual backup |
| `新增/更新 <数字>` (added/updated <number>) | Number of files copied this time (new or modified) | Round 1 = file count; Rounds 2 and 6 = 0; Rounds 3 and 4 = 1 |
| `跳过 <数字>` (skipped <number>) | Number of files skipped because they were “not modified” | From Round 2 onward it should equal the number of remaining files |
| `失败 0` (0 failed) | Number of files that failed to copy | Must be 0; anything else means that round fails |
| `备份进度（<卷标>）` (backup progress (<volume label>)) | Throttled progress log (written once every few files) | Appears when there are many files; optional to check |
| `命中排除规则，跳过目录：` (exclusion rule matched, skipping folder:) | A folder exclusion rule was matched (such as `System Volume Information`) | Should appear in Round 5 |
| `复制失败` (copy failed) | A single file failed to copy (WARN level, with exception details) | Should not appear |
| `备份部分失败` (backup partially failed) / `失败明细` (failure details) | Some files failed to copy (ERROR level) | Should not appear |
| `备份未执行` (backup not performed) / `尚未配置备份目标目录` (backup target folder not configured yet) | No target folder configured (WARN; in that case save the backup target folder in the main window first) | Should not appear during normal testing |
| `未检测到 U 盘` (no USB drive detected) / `没有检测到可移动磁盘` (no removable disk detected) | There was no removable disk when 「立即备份」 was clicked | Should not appear during normal testing (before clicking, make sure the USB drive is properly plugged in) |
| `设置已保存：` (settings saved:) | 「保存设置」 in the main window succeeded (includes target folder and excluded extensions) | Should appear after clicking 「保存设置」 |
| `已发送通知：` (notification sent:) | The tray balloon has been shown | Should appear each time a backup completes |
| `主界面已打开。` (main window opened.) | The main window has been shown after double-clicking `UsbBackup.exe` | Should appear once per launch |
| `检测到受限运行上下文` (restricted execution context detected) | The program was started in a low-integrity context (sandbox / restricted terminal) and the tray icon may fail to register | **Should not** appear on a normal launch (double-click in File Explorer) |
| `ERROR` | Error-level log entry | Should not appear (if it does, it needs investigation) |
| `无法读取卷序列号` (cannot read volume serial number) | Reading the USB drive's serial number failed (WARN) | Should not appear; if it does, the device folder name becomes `..._00000000` |

**Quickly filter today's log** (PowerShell):

```powershell
$log = "$env:LocalAppData\WinToolBox\logs\wintoolbox-$(Get-Date -Format yyyyMMdd).log"
Select-String -Path $log -Pattern '检测到 U 盘|本工具不会自动备份|开始备份|复制完成|备份完成|命中排除规则|复制失败|ERROR' | Select-Object -Last 20
```

---

## 4. Safety Verification Items

These two items are the **safety baseline**; if either one fails, the whole test counts as failed.

### 4.1 The program never executes executables from the USB drive

| Item | Details |
| --- | --- |
| **Steps** | ① Create a batch file named `should-not-run.bat` on the USB drive, with the contents:<br>`@echo off`<br>`echo RAN > "%USERPROFILE%\Desktop\wintoolbox-should-not-exist.txt"`<br>② also put an executable there (for example copy `C:\Windows\System32\notepad.exe` to the USB drive and rename it `should-not-run.exe`); ③ plug in the USB drive, click **「立即备份」** in the main window and wait for the backup to finish; ④ check the marker file and the processes |
| **Expected result** | ① `wintoolbox-should-not-exist.txt` **does not** appear on the desktop; ② no Notepad window pops up; ③ there is no process in Task Manager started from the USB drive letter; ④ these two files are **only copied** into the backup folder |
| **Pass criteria** | ① The marker file does not exist (the command below produces no output); ② the process query below produces no output; ③ copies of `should-not-run.bat` / `should-not-run.exe` can be found in the backup folder (which shows “copied but not executed”) |

```powershell
# ① the marker file should not exist
Test-Path "$env:USERPROFILE\Desktop\wintoolbox-should-not-exist.txt"   # expected: False

# ② there should be no process started from the USB drive letter (replace E: with your drive letter)
Get-Process | Where-Object { $_.Path -like 'E:\*' } | Select-Object Id, ProcessName, Path   # expected: no output

# ③ but the copies should be in the backup folder
Get-ChildItem 'D:\UsbBackup\MYUSB_1A2B3C4D' -Recurse -Filter 'should-not-run.*' | Select-Object FullName   # expected: output
```

### 4.2 Existing USB drive files in the backup folder are not deleted

| Item | Details |
| --- | --- |
| **Steps** | ① Write down the current file count in the backup folder; ② delete 1 file from the USB drive and add 1 new file; ③ plug in the USB drive (if you unplugged it in between) and click **「立即备份」** in the main window; ④ count the files in the backup folder again |
| **Expected result** | The backup folder's file count **does not decrease** (files deleted from the USB drive are still kept in the backup); it only grows by the newly added files |
| **Pass criteria** | ① Backup folder file count = original file count + newly added count (deleted ones are not removed); ② the log contains no delete-related operations; ③ the contents of previous date folders are completely unchanged |

> Likewise, the program performs **no** write operations on the USB drive (no renaming, no deleting, no creating); if you like, you can record the USB drive's file count before and after the test and compare them to confirm this.

---

## 5. Pass / Fail Determination Table

Tick each item; **ticking them all** means the test passes overall.

**Functionality (6 core rounds)**

- [ ] Round 1 Double-clicking to start shows the **main window** directly; setting the backup folder and clicking 「保存设置」 succeed (log `设置已保存：`)
- [ ] Round 1 After plugging in the USB drive, clicking 「立即备份」 completes the first full backup, the balloon reports completion, and the log shows `新增/更新 N，失败 0`
- [ ] Round 1 The backup folder `D:\UsbBackup\MYUSB_<serial number>\<today's date>\` matches the USB drive in structure, file count and total size (excluding excluded items)
- [ ] Round 2 Clicking 「立即备份」 again without any changes: log `新增/更新 0，跳过 N`, and no file in the backup folder is rewritten
- [ ] Round 3 Modifying 1 existing file: log `新增/更新 1`; only that file is copied and its contents match
- [ ] Round 4 Adding 1 new file: log `新增/更新 1`; only the new file is copied
- [ ] Round 5 None of `autorun.inf`, `test.tmp` or `System Volume Information` appear in the backup folder
- [ ] Round 6 After plugging in, **click nothing** and wait 1–2 minutes: **no** automatic backup (no `开始备份：`), only a single 「检测到 U 盘」 balloon + status bar + log `本工具不会自动备份`
- [ ] Round 6 The subsequent manual 「立即备份」 still completes normally
- [ ] The log contains **no** `复制失败` and **no** `ERROR`

**Additional rounds**

- [ ] Additional A The program was not running at plug-in → after starting it, 「立即备份」 completes normally
- [ ] Additional B Switching to a second USB drive creates a different `卷标_序列号` (label_serial) folder
- [ ] Additional C After moving the same USB drive to another USB port / changing the drive letter, it is still backed up to the same folder

**Safety**

- [ ] 4.1 `should-not-run.bat` / `.exe` on the USB drive were **not** executed (no marker file, no process, no pop-up window) and were only copied
- [ ] 4.2 The USB drive's original files in the backup folder were **not** deleted, and previous date folders were not modified

**Environment**

- [ ] The config file path is correct: `%AppData%\WinToolBox\UsbBackup\config.json`, and its fields are **only** `backupTargetDirectory` / `excludedExtensions` (**no** `autoBackupEnabled`)
- [ ] The log file path is correct: `%LocalAppData%\WinToolBox\logs\wintoolbox-<today>.log`, rotated daily
- [ ] All four tray right-click menu items (**打开主界面** / 立即备份 / 打开日志 / 退出) work properly
- [ ] **Double-clicking the tray icon = 打开主界面**; **closing the window = minimize to tray** (the first close shows an explanatory balloon), and the program keeps running in the background
- [ ] The main window is complete: Backup settings (target folder + 浏览…, excluded extensions, 保存设置), Actions (立即备份 / 刷新设备 / 打开日志目录 / 隐藏到托盘 / 退出程序 + “设备：N 个” (Devices: N)), Runtime log (live scrolling + 清空), and the status bar at the bottom
- [ ] After 「刷新设备」 (Refresh devices), “设备：N 个” (Devices: N) matches the number of USB drives actually plugged in
- [ ] `UsbBackup.exe --selftest <report path>` exits with code `0` (all 16 checks pass; only if you ran this item)
- [ ] (If encountered) Starting in a sandbox / restricted terminal shows a dialog saying “托盘图标无法注册” (the tray icon cannot be registered); after switching to double-clicking in File Explorer the tray icon works normally
- [ ] Test files such as `should-not-run.bat` have been cleaned off the USB drive after testing (optional)

### Result record table

| Round | Date | USB volume label | Volume serial number | 新增/更新 (added/updated) | 跳过 (skipped) | 失败 (failed) | Conclusion (pass/fail) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Round 1 | | | | | | | |
| Round 2 | | | | | | | |
| Round 3 | | | | | | | |
| Round 4 | | | | | | | |
| Round 5 | | | | | | | |
| Round 6 | | | | | | | |
| Additional A | | | | | | | |
| Additional B | | | | | | | |
| Additional C | | | | | | | |

> The “no buttons clicked” phase of Round 6 has no backup activity, so in the `新增/更新` / `跳过` / `失败` columns enter the numbers from the backup **after you manually clicked 「立即备份」**.

---

## 6. What Information to Collect When a Round Fails

Please bundle the following together (**do not delete the logs or the backup folder first**) to make the problem easier to diagnose:

1. **Log file**: `%LocalAppData%\WinToolBox\logs\wintoolbox-<the day the problem occurred>.log` (key: the complete content before and after the failing round, especially the `复制失败` / `ERROR` lines and the exception stack trace that follows them).
2. **Config file**: `%AppData%\WinToolBox\UsbBackup\config.json` (confirm the target folder and excluded extensions; this file has **only** the two fields `backupTargetDirectory` and `excludedExtensions`).
3. **Windows version**: a screenshot of `winver`, or run `Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber`.
4. **USB drive details**: volume label, volume serial number (`cmd /c vol X:`), file system (FAT32/exFAT/NTFS), total capacity, and the current drive letter.
5. **Target folder details**: the backup target folder path and the free space available (`Get-PSDrive D`).
6. **Description of the symptom**: which round, which plug/unplug cycle, roughly how long after plugging in, whether the balloon appeared after clicking 「立即备份」, and which files actually appeared/disappeared in the backup folder; if a backup happened in Round 6 **without any button being clicked**, please call that out specifically.
7. **Screenshots**: the main window (Backup settings / Actions area / Runtime log), the tray balloon, and the relevant lines in the backup folder and the log.
8. **(If relevant) Self-test report**: the report file produced by `UsbBackup.exe --selftest <report file>`.

---

## 7. Notes

- This checklist is **executed manually by the user on a local machine**; the tool **does not require** cloud CI to cover real USB drive behavior (GitHub Actions has no physical USB drive and is only responsible for building, unit tests and packaging releases).
- **Backups can only be triggered manually**: plugging in a USB drive only pops up a single 「检测到 U 盘」 balloon + a status bar hint + a log entry, and does **not** back up automatically. After seeing “检测到 U 盘”, click 「立即备份」 in the main window or 「立即备份」 in the tray right-click menu.
- Between rounds it is recommended to **fully unplug the USB drive and wait more than 10 seconds** before plugging it in again, so that Windows device events are not merged and missed.
- If a round fails, after fixing or reporting it **re-run from Round 1** (incremental backup depends on the target files left by the previous round, so skipping rounds leads to unreliable conclusions).
