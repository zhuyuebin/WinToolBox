# FolderCreator User Guide

[English](README_EN.md) | [简体中文](README.md)

> A WinToolBox sub-tool: describe a folder hierarchy with a piece of "hyphen-indented" text to **batch-create a folder structure** in one go, or to **check whether an existing directory matches the rules**.

[← Back to WinToolBox overview](../../../README_EN.md)

- Program name: `FolderCreator.exe` (a WinForms desktop application, C# / .NET 8)
- Project location: `src/Tools/FolderCreator/`; it references the shared library `src/WinToolBox.Core` and reuses its `Logger`

## Requirements

| How to run | Requirement |
| --- | --- |
| From source | Windows 10 / 11 (x64) + .NET 8 SDK |
| Single-file exe | Windows 10 / 11 (x64); just double-click `FolderCreator.exe`. The release package is self-contained with the .NET runtime — **no installation, and no need to pre-install .NET** |

Download: open <https://github.com/zhuyuebin/WinToolBox/releases/latest>, download **`FolderCreator-win-x64.zip`**, unzip it to any directory, and double-click `FolderCreator.exe` inside it.

## Quick start (5 steps)

1. **Launch**: run `FolderCreator.exe`; to debug from source, run `dotnet run --project src/Tools/FolderCreator/FolderCreator.csproj`.
2. **Choose the root directory**: type the target directory into the text box at the top, or click 「浏览…」 (Browse…) to select it — every folder is created under this root directory.
3. **Paste the rules**: paste the hyphen-indented hierarchy text into the rules box on the left; if you are unsure about the format, click 「载入示例规则」 (Load sample rules) to fill in a sample you can run right away.
4. **Choose the mode**: when checking, choose **严格模式** (Strict mode, the default, which also reports 「多余」/ Extra) or **宽松模式** (Lenient mode, which only reports 「缺失」/ Missing).
5. **Run**: 「创建文件夹」 (Create folders) starts batch creation, and 「检查一致性」 (Check consistency) compares the target directory; results are shown in the result tree on the right, and progress and hints are shown in the status bar at the bottom.

## Enhanced features (6 new in v0.2.2)

| Feature | How to use it | Notes |
| --- | --- | --- |
| **Rule template management** | Load from the 「模板」 (Template) combo box; save or remove with 「保存为模板」 (Save as template) / 「删除模板」 (Delete template) | 「保存为模板」 opens a **name input dialog** prefilled with the current combo box content: type a **new name to create a template**, and only a duplicate name asks whether to overwrite; built-in templates are 「Web 项目」 and 「Python 项目」; the library lives in `%AppData%\WinToolBox\FolderCreator\templates.json` and the built-ins are written on first run |
| **Generate rules from an existing directory** | 「从现有目录生成规则」 (Generate rules from directory) | Scans the root directory and produces hyphen-indented rules, sorted by name at every level and stable across runs; `.git`, `node_modules`, `bin`, `obj` and `.vs` are excluded by default |
| **Placeholder files** | Tick 「为空目录创建占位文件（.gitkeep）」 (Create placeholder files for empty directories) | Creates a 0-byte `.gitkeep` only in **truly empty** directories (no files and no subdirectories); an existing placeholder is skipped without error |
| **Multi-root batch creation** | Tick 「启用多根目录」 (Enable multi-root), one path per line | Duplicates are removed (case-insensitive) and blank/invalid paths are ignored; per-root progress shows 「当前处理第 X / 共 Y 个」; one failing root does not stop the others |
| **Check report export** | 「导出检查报告」 (Export check report), after running a check | Produces Markdown with the check time, target directory, mode, a statistics table and per-category details; any category with more than 100 items is folded into `<details>` |
| **Directory tree export** | 「导出目录树」 (Export directory tree) | Produces standard `tree`-style text (`├─`/`└─`/`│`) plus a directory count at the end, with the same default exclusions |

> A 「运行日志」 (Run log) panel at the bottom of the window shows operation logs live (they are also written to `%LocalAppData%\WinToolBox\logs\`).
> Step-by-step acceptance steps for all six features are in the [FolderCreator manual test checklist (Chinese)](../../../MD-files/FolderCreator-本地手动测试清单.md).

## Rule syntax

Use consecutive `-` characters at the start of a line to express hierarchy: one `-` is level 1, `--` is level 2, `---` is level 3, and so on.

```text
# 这是一个注释行，以 # 开头将被忽略
根目录名称

-一级目录
--二级目录
---三级目录
-一级目录2
--二级目录2
```

Parsing rules:

| # | Rule | Description |
| --- | --- | --- |
| 1 | The number of `-` characters = the nesting depth | one `-` is level 1, `--` is level 2, up to 32 levels |
| 2 | The space after the hyphens may be omitted | `-一级目录` and `- 一级目录` are equivalent; leading spaces are stripped |
| 3 | A line starting with `#` is a comment | the whole line is ignored; you can write explanatory text there |
| 4 | A line with no leading `-` that does not start with `#` is ignored | for example `根目录名称`; you can use such a line as a root-directory reminder, and it takes no part in creation |

The example above creates: `一级目录\二级目录\三级目录` and `一级目录2\二级目录2`.

## Automatic parent completion and name validation

- **Automatic parent completion**: if `---三级目录` appears while there is no preceding `--二级目录`, the parser **automatically fills in the missing parent levels**; the generated folder name is always **`未命名目录`** (Unnamed folder), and a **warning with the line number** is shown in the UI. Follow the warning to go back to the rules and change `未命名目录` into the name you want.
- **Invalid-character validation**: a folder name must not contain any of the following, otherwise the parsing stage reports an error straight away and lists the "第 N 行" (line N) — in that case **no creation is performed**:
  - Invalid characters: `\`, `/`, `:`, `*`, `?`, `"`, `<`, `>`, `|`
  - Windows reserved device names: `CON`, `PRN`, `AUX`, `NUL`, `COM1`~`COM9`, `LPT1`~`LPT9`
  - A folder name must not end with `.` or a space
  - A single level may be at most 255 characters long, and nesting may be at most 32 levels deep

## Creating folders

- Folders are created recursively under the chosen root directory with `Directory.CreateDirectory`, and **parent directories are created automatically**.
- **Folders that already exist are skipped without an error**; only the missing parts are created, so the operation can be repeated.
- Creation runs on a **background thread**, so the UI does not freeze; there is a progress bar and a status hint at the bottom.
- Failures are **caught one by one**: insufficient permissions, a path that is too long, invalid characters and so on all produce a friendly message; failed items are highlighted in red in the result tree while the remaining directories continue to be created.

## Checking consistency

After choosing the target root directory, click 「检查一致性」 (Check consistency); the result is displayed as a colored tree:

| State | Meaning | Color |
| --- | --- | --- |
| **匹配** (Matched) | present in the rules and present in the target directory | Green |
| **缺失** (Missing) | present in the rules but not in the target directory (a failed creation is marked red too) | Red |
| **多余** (Extra) | present in the target directory but not in the rules (strict mode only) | Yellow |
| **已存在（跳过）** (Already exists — skipped) | the directory already existed at creation time and was skipped | Gray |

| Check mode | Behavior |
| --- | --- |
| **严格模式** (Strict mode, the default) | besides 「匹配 / 缺失」 (Matched / Missing), it also reports folders that exist in the target directory but are not in the rules (「多余」/ Extra) |
| **宽松模式** (Lenient mode) | only checks whether the folders required by the rules exist, and ignores extra folders |

## Command-line arguments

```powershell
# Show the version
FolderCreator.exe --version

# Local smoke self-test: runs "parse rules → generate directories → generate again (everything should be skipped) → check consistency (strict/lenient)"
# once in a temporary directory; it is for local self-checking only and needs no real data. Exit code 0 means it passed
# (when no path is given, the report is written to %TEMP%\FolderCreator-selftest.txt)
FolderCreator.exe --selftest
FolderCreator.exe --selftest D:\selftest-report.txt   # also write the report to the specified file
```

## Logs

FolderCreator reuses the WinToolBox shared logging component; logs are written to:

```text
%LocalAppData%\WinToolBox\logs\wintoolbox-yyyyMMdd.log
```

(usually `C:\Users\<username>\AppData\Local\WinToolBox\logs\`; it shares the same log file with UsbBackup, split by day.)

## FAQ

| Question | Cause and how to handle it |
| --- | --- |
| Why wasn't one of my lines created? | That line **has no leading `-`** (so it is treated as a root-directory reminder and ignored) or **starts with `#`** (a comment line). Add the corresponding number of `-` characters to the lines you want created. |
| Why did `未命名目录` appear? | The rules skipped a level (for example writing `---三级目录` while `--二级目录` is missing), so the parser filled in the parent level automatically and the generated name is exactly `未命名目录` (Unnamed folder). Just complete the parent level at the line number given in the warning. |
| The check reported a pile of 「多余」 (Extra) — what should I do? | The target directory contains folders that the rules do not describe. If those folders are legitimate, switch to **宽松模式** (Lenient mode) to check only for missing ones; if they really should not exist, delete them from the target directory and check again. |
| Creation reports insufficient permissions / a path that is too long? | Use a root directory that the current user has write permission for, or shorten the target path (the Windows default path-length limit is about 260 characters); the reason for each failure is shown one by one in the result tree and the status bar, and is written to the log. |
