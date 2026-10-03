# FileMaster Manual Test Checklist

[English](FileMaster-本地手动测试清单_EN.md) | [简体中文](FileMaster-本地手动测试清单.md)

> Applies to: WinToolBox `FileMaster` (renamed and extended from the former FolderCreator)
> Purpose: item-by-item acceptance of the **8 file management features** plus the original folder-structure features.
> Note: the automated unit tests live in `tests/` (`dotnet test`); this checklist is for **manual on-device acceptance** (recycle bin, process termination, real directories and similar behaviour that unit tests cannot fully cover).

---

## 0. Preparation

```powershell
# 1) Build
dotnet build WinToolBox.sln -c Release

# 2) Run
dotnet run --project src/Tools/FileMaster/FileMaster.csproj
# or double-click the published single-file executable: FileMaster.exe
```

After it starts you should see:

- the window title **FileMaster**;
- the top menu **「文件管理(F)」 (File management)**, containing the 8 feature entries plus 「关于 FileMaster」 (About FileMaster);
- the original rule input, templates, check mode, result tree and run log controls in their normal positions, with nothing hidden behind the menu.

**General safety rules (confirm these alongside every item)**

- [ ] Every delete action goes to the **recycle bin** by default, and each one is preceded by a **second confirmation** dialog (whose default button is 「否」 (No)).
- [ ] The window **never freezes** while a long operation runs: the progress bar advances, the 「关闭」 (Close) button stays usable, and the operation can be cancelled.
- [ ] If one feature reports an internal error, the main window stays usable (it does not crash along with it).
- [ ] There is **no** registry read or write anywhere (verify with Process Monitor filtered on RegSetValue, or trust the code-review conclusion).

**Test data (create it once)**

The script below uses the same (Chinese) folder and file names as the Chinese version of this checklist, so both versions can be followed side by side.

```powershell
$root = "$env:TEMP\FileMaster-手动测试"
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$root\源目录\子目录A","$root\源目录\子目录B","$root\空目录1\空子目录","$root\目标目录","$root\left","$root\right" -Force | Out-Null
"hello" | Set-Content "$root\源目录\报告 2024.txt"
"hello" | Set-Content "$root\源目录\报告 2025.txt"
"data"  | Set-Content "$root\源目录\子目录A\a.txt"
"hello" | Set-Content "$root\源目录\子目录A\dup1.txt"
"hello" | Set-Content "$root\源目录\子目录B\dup2.txt"
"same"  | Set-Content "$root\left\same.txt"
"same"  | Set-Content "$root\right\same.txt"
"only-left"  | Set-Content "$root\left\only-left.txt"
"only-right" | Set-Content "$root\right\only-right.txt"
"aaaa" | Set-Content "$root\left\diff.txt"
"bbbb" | Set-Content "$root\right\diff.txt"
```

---

## 1. Batch Rename (files / folders / both)

**Entry**: menu 「文件管理」 (File management) → 「批量重命名（文件 / 文件夹）…」 (Batch rename (files / folders)…)

| # | Step | Expected result |
| --- | --- | --- |
| 1.1 | Set the target directory to `源目录` (source); processing target 「文件」 (Files); 「删除字符」 (Remove characters) = one space; click 「预览」 (Preview) | The list shows `报告 2024.txt` → `报告2024.txt` and so on, with status 「可重命名」 (Renamable, green) |
| 1.2 | Add 「前缀」 (Prefix) `P_` and 「后缀」 (Suffix) `_S`, then preview again | New names look like `P_报告 2024_S.txt` (the suffix is inserted before the extension) |
| 1.3 | 「查找」 (Find) = `报告`, 「替换为」 (Replace with) = `Report`, keep 「区分大小写」 (Match case) ticked | 「报告」 in the new name is replaced with `Report` |
| 1.4 | Tick 「使用正则」 (Use regex), 「查找」 = `(\d{4})`, 「替换为」 = `Y$1` | The result looks like `Y2024` (the regex group works) |
| 1.5 | Tick 「序号」 (Sequence number), start 1, digits 3, separator `_` | Names look like `报告 2024_001.txt` (numbered in list order) |
| 1.6 | Set 「日期格式」 (Date format) to `yyyyMMdd` | An 8-digit date is appended to the name (last-write time by default) |
| 1.7 | Set 「新扩展名」 (New extension) to `.bak`; untick 「保留扩展名」 (Keep extension) and preview again | The extension is replaced with `.bak` everywhere / the base name also takes part in the rules |
| 1.8 | Switch the processing target to 「文件 + 文件夹」 (Files + folders) and tick 「包含子目录」 (Include subdirectories) | `子目录A` and `子目录B` also appear in the list (folders come last) |
| 1.9 | Manually create a file whose target name already exists, then preview | That row shows 「冲突」 (Conflict, red) with the reason 「目标名称已存在」 (the target name already exists), and it is **not** renamed |
| 1.10 | Click 「执行重命名」 (Rename) and confirm | The file names really change in File Explorer; the status bar shows 「成功 N 项」 (N succeeded) |
| 1.11 | Click 「预览」 (Preview) with every rule empty | A message says 「至少需要设置一条规则」 (at least one rule must be set) and nothing is executed |

---

## 2. Empty Folder Cleanup

**Entry**: menu 「文件管理」 (File management) → 「空文件夹清理…」 (Empty folder cleanup…)

| # | Step | Expected result |
| --- | --- | --- |
| 2.1 | Set the root directory to `$root`; tick 「包含子目录」 (Include subdirectories); click 「扫描空文件夹」 (Scan empty folders) | The list shows `空目录1\空子目录` and `空目录1` (**deepest first**), with the level column showing 1/2 |
| 2.2 | Untick 「包含子目录」 and scan again | Only `空目录1` is listed (directories that merely contain empty subdirectories must still be detected; directories that contain files must never be listed) |
| 2.3 | Untick only 「空目录1」, keep `空子目录` ticked, click 「删除选中」 (Delete selected) and confirm | Only the ticked items are deleted; the confirmation dialog states 「放入回收站」 (move to the recycle bin) and the count |
| 2.4 | Open the recycle bin | The deleted empty directories are listed there (proving they went to the recycle bin) |
| 2.5 | Click 「永久删除」 (Delete permanently) (create a new empty directory first), then answer 「是」 (Yes) in the confirmation | The directory disappears immediately and does **not** appear in the recycle bin |
| 2.6 | Pick a directory path that does not exist | The status bar or a dialog reports 「目录不存在」 (the directory does not exist), and the program does not crash |

---

## 3. Batch Timestamp Change

**Entry**: menu 「文件管理」 (File management) → 「时间戳批量修改…」 (Batch timestamp change…)

| # | Step | Expected result |
| --- | --- | --- |
| 3.1 | Set the root directory to `源目录`; processing target 「文件」 (Files); tick 「包含子目录」 (Include subdirectories); mode 「修改时间 = 创建时间」 (last-write = creation time); click 「预览」 (Preview) | The list shows each file's current creation/last-write time, and the rows that need a change show the new time |
| 3.2 | Click 「应用修改」 (Apply changes) and confirm | Check the properties in File Explorer: **the last-write time equals the creation time** |
| 3.3 | Switch the mode to 「统一改成指定时间」 (set a fixed date), set the time to `2024-01-01 08:00:00`, tick only 「修改时间」 (last-write time), then preview | The 「创建时间」 (creation time) column shows `-` (meaning "not changed") |
| 3.4 | Apply, then check the properties | Only the last-write time becomes the given value; the creation time stays as it was |
| 3.5 | Switch the processing target to 「文件 + 文件夹」 (Files + folders), apply, then check the folder times | Folder times are written as well (parent directories are written after their children, so the result is the final value) |
| 3.6 | Click 「应用修改」 (Apply changes) without clicking 「预览」 (Preview) first | A message says 「请先点击预览」 (click Preview first) |

---

## 4. Batch Move / Auto-Classify

**Entry**: menu 「文件管理」 (File management) → 「批量移动 / 自动分类…」 (Batch move / auto-classify…)

| # | Step | Expected result |
| --- | --- | --- |
| 4.1 | Source directory `源目录`, target directory `目标目录`; keep the default rule text; click 「预览」 (Preview) | The list gives every file's 「目标路径」 (target path) (`图片\…`, `视频\…`, `按日期\yyyy-MM\…`, `其它\…`), with status 「可执行」 (Executable, green) |
| 4.2 | Change the rule text to an invalid kind (for example `文件\|\|其它`) and preview | A dialog lists 「未知的规则类型」 (unknown rule kind) and nothing is executed |
| 4.3 | Change the parameter of the `图片` rule to `.txt` and preview again | `.txt` files are classified into the `图片` subdirectory (the rule takes effect) |
| 4.4 | Choose 「移动文件」 (Move files) + 「移动后清理空目录」 (clean up empty folders afterwards), click 「开始分类」 (Start classifying) and confirm | The files are moved into the matching subdirectories of the target directory; empty subdirectories in the source are cleaned up; the status bar shows how many succeeded and failed |
| 4.5 | Switch to 「复制文件」 (Copy files) and repeat once | The source files still exist and copies appear in the target directory |
| 4.6 | A file with the same name already exists in the target and 「覆盖同名文件」 (Overwrite existing files) is not ticked | That row shows status 「跳过」 (Skipped) with the reason 「目标已存在同名文件」 (a file with the same name already exists in the target) |
| 4.7 | Tick 「覆盖同名文件」 (Overwrite existing files), preview again and run it | The existing file is overwritten successfully |

---

## 5. Folder Diff

**Entry**: menu 「文件管理」 (File management) → 「文件夹差异比对…」 (Folder diff…)

| # | Step | Expected result |
| --- | --- | --- |
| 5.1 | Left directory `left`, right directory `right`; comparison mode 「大小 + 修改时间」 (size + last-write time); click 「开始比对」 (Compare) | `same.txt` green (identical), `only-left.txt` blue (left only), `only-right.txt` red (right only), `diff.txt` tan (different content, reason 「大小不同」 (different size)) |
| 5.2 | Switch the comparison mode to 「SHA256 哈希」 (SHA-256 hash) and compare again | `same.txt` is still identical; if both `same.txt` files are changed to the same length but different content, it becomes 「内容不同」 (different content) — the hash takes effect |
| 5.3 | Manually set the right-hand file's last-write time equal to the left-hand one (same content, same size) | Under 「大小 + 修改时间」 (size + last-write time) it must be judged identical; a very small time difference (under 2 seconds) also counts as identical |
| 5.4 | Switch the comparison mode to 「仅大小」 (size only) | Files of the same length but different content are judged 「相同」 (identical) — this is the expected semantics of that mode |
| 5.5 | Enter `diff.txt` under 「忽略名称」 (Ignored names) and compare again | That file no longer appears in the result |
| 5.6 | Click 「导出报告」 (Export report) and save it as a txt file | The file is produced and contains the header and the summary statistics |
| 5.7 | Set the left and right directories to the same directory | A message says 「左右目录相同」 (the left and right directories are the same) and no comparison is run |

---

## 6. Duplicate Finder

**Entry**: menu 「文件管理」 (File management) → 「重复文件查找…」 (Duplicate finder…)

| # | Step | Expected result |
| --- | --- | --- |
| 6.1 | Set the scan directory to `$root\源目录` (「浏览添加目录…」 (Browse to add directories…) can add more); hash algorithm SHA256; click 「开始扫描」 (Start scanning) | Results are grouped: `dup1.txt` / `dup2.txt` (identical content) form one group; inside the group the **oldest** file has status 「保留」 (Keep) and is unticked by default, while the others have status 「重复」 (Duplicate) and are ticked by default |
| 6.2 | Look at the status bar | It shows 「共 N 组重复、可删除 M 个、可回收 X MB」 (N duplicate groups, M deletable, X MB reclaimable) |
| 6.3 | Click 「全选重复项」 (Select all duplicates) | Every 「保留」 (Keep) row stays unticked and all the others become ticked |
| 6.4 | Click 「删除选中」 (Delete selected) | The second confirmation states the count, the reclaimable space and 「放入回收站」 (move to the recycle bin) |
| 6.5 | Confirm, then open the recycle bin | The deleted files are in the recycle bin; the kept copy is still in place |
| 6.6 | Set 「最小文件大小(KB)」 (Minimum file size (KB)) to 1024 and scan again | Small files no longer take part and the duplicate groups disappear |
| 6.7 | Verify with two files that have identical content but different sizes (for example by appending spaces) | They are not reported as duplicates (size is compared first, then the hash) |

---

## 7. Folder Sync / Mirror

**Entry**: menu 「文件管理」 (File management) → 「文件夹同步 / 镜像…」 (Folder sync / mirror…)

| # | Step | Expected result |
| --- | --- | --- |
| 7.1 | Source directory `left`, target directory `right`; mode 「单向复制（不删除目标多余内容）」 (one-way copy, extra target content is kept); click 「生成计划」 (Generate plan) | The plan contains only copy/update actions and **no** delete actions; deletes are red, copies green and directory creation steel blue |
| 7.2 | Click 「开始同步」 (Start sync) and confirm | The target directory is filled in from the source and the extra files stay |
| 7.3 | Switch the mode to 「单向同步（删除目标多余内容）」 (one-way sync, extra target content is deleted) and generate the plan again | A `删除多余文件` (delete extra file) action appears in the plan (for `only-right.txt`) |
| 7.4 | Run it, confirm, then check the recycle bin | The extra file is deleted and **is in the recycle bin** (「删除时放入回收站」 (move deletes to the recycle bin) is ticked by default) |
| 7.5 | Untick 「删除时放入回收站」 (move deletes to the recycle bin) and run a delete again | The file is deleted permanently and the recycle bin has no record (the confirmation text should say 「永久删除」 (permanent delete)) |
| 7.6 | Switch the mode to 「镜像」 (Mirror), change the content of a source file and generate the plan | Every existing file becomes 「更新文件（镜像模式：强制重写）」 (update file — mirror mode: forced rewrite); after running it, target and source are exactly the same |
| 7.7 | Generate the plan again (they are already identical) | A message says 「目标目录已经与源目录一致，无需同步」 (the target directory already matches the source, no sync needed) |
| 7.8 | Set the target directory to a subdirectory of the source | The dialog refuses: 「目标目录不能位于源目录内部」 (the target directory must not be inside the source directory) |
| 7.9 | Tick 「用哈希比较内容」 (compare content by hash) and generate the plan again | Files with the same size, different times but identical content no longer produce an update action |

---

## 8. File Unlock

**Entry**: menu 「文件管理」 (File management) → 「文件占用解锁…」 (File unlock…)

| # | Step | Expected result |
| --- | --- | --- |
| 8.1 | Open `$root\源目录\报告 2024.txt` in Notepad and leave it open | — |
| 8.2 | Use 「选择文件…」 (Choose file…) to select that file → 「查询占用」 (Query locks) | The list shows `notepad` (with the correct PID), type 「桌面应用」 (desktop app) and restartable 「是」 (Yes) |
| 8.3 | Close Notepad and query again | The list is empty and the status bar says 「没有被任何进程占用。」 (not locked by any process.) |
| 8.4 | Use 「选择文件夹…」 (Choose folder…) to select `源目录` and query | If files inside the directory are locked, the locking processes are found; the status bar shows how many files were checked |
| 8.5 | Repeat 8.1, then select the Notepad row in the list → 「结束选中进程」 (Kill selected process) | A second confirmation appears first (with the process name and PID); after confirming, Notepad is killed, the program re-queries automatically and the list becomes empty |
| 8.6 | If a 「关键系统进程」 (critical system process, for example System or a service) appears in the list, try to kill it | It is refused with an explanation (and the system stays healthy) |
| 8.7 | Click 「仅查看（复制信息）」 (View only (copy info)) and paste it into Notepad | You get a text report containing the path, the number of files checked and the list of locking processes |
| 8.8 | Query a path that does not exist | A message says the path does not exist, and the program does not crash |

---

## 9. Regression: the Original Folder-Structure Features (the former FolderCreator features must be preserved)

| # | Step | Expected result |
| --- | --- | --- |
| 9.1 | Set the root directory to an empty directory, load the example rules (「载入示例规则」 (Load example rules)) and click 「创建文件夹」 (Create folders) | The hierarchy is created correctly and the result tree marks it green as 「已创建」 (Created) |
| 9.2 | Click 「创建文件夹」 (Create folders) once more | Everything shows 「已存在，已跳过」 (already exists, skipped) with no error |
| 9.3 | Manually delete one subdirectory, create an extra one, then click 「检查一致性」 (Check consistency) in strict mode | Missing entries are red, extra entries tan; the status bar shows a summary |
| 9.4 | Switch to lenient mode and check again | Only missing entries are reported, no extra ones |
| 9.5 | 「从现有目录生成规则」 (Generate rules from an existing directory) | The rule box fills with dash-indented rules that match the directory hierarchy |
| 9.6 | 「导出目录树」 (Export directory tree) / 「导出检查报告」 (Export check report) | A txt / md file is produced with correct content (Chinese text is not garbled) |
| 9.7 | Pick a template in the 「模板」 (Template) drop-down, save a template, delete a template | Template text is loaded, saved and deleted correctly; the template file lives at `%AppData%\WinToolBox\FileMaster\templates.json` |
| 9.8 | Upgrade scenario: put the old `%AppData%\WinToolBox\FolderCreator\templates.json` back (delete the FileMaster directory, then start the app) | The program migrates the old templates into the FileMaster directory and the template list is not lost |
| 9.9 | Tick 「启用多根目录」 (Enable multi-root), enter one path per line and click 「创建文件夹」 (Create folders) | All roots are created and the result tree is grouped by root |
| 9.10 | Command line `FileMaster.exe --version` / `FileMaster.exe --selftest %TEMP%\fm.txt` | It prints the version / writes a self-test report, and the exit code is 0 |

---

## 10. Acceptance Conclusion

- [ ] All 8 file management features work, and every delete / sync / mirror operation has a preview, a second confirmation and the recycle bin by default.
- [ ] The original folder-structure features show no regression.
- [ ] No registry writes, no frozen UI, and a failure in one feature never affects the main window.
- [ ] Any problems found are recorded (id / reproduction steps / expected / actual) and reported back.
