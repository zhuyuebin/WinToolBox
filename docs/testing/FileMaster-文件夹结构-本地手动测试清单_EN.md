# FileMaster Folder-Structure Manual Test Checklist (v0.4.0 · Six enhanced features)

[English](FileMaster-文件夹结构-本地手动测试清单_EN.md) | [简体中文](FileMaster-文件夹结构-本地手动测试清单.md)

> Applies to: `FileMaster.exe` (WinToolBox's batch folder creation tool; the former `FolderCreator.exe` was merged into FileMaster in v0.4.0, win-x64 portable single file)
> Systems: Windows 10 / 11
> Performed by: the user (manual operation on a physical machine in person)
> Estimated time: about 20 minutes (6 core rounds)

This checklist verifies the **6 features** added in this round: rule template management, reverse rule generation, placeholder file creation, multi-root batch creation, inspection report export, and directory tree text export.
For the existing "Create Folders / Check Consistency" (including strict / lenient mode), keep referring to the relevant rounds in [UsbBackup and FileMaster General Acceptance](UsbBackup-本地手动测试清单.md).

---

## 0. Three Things to Remember First

1. **This tool really creates folders and files**: always work in a **dedicated test directory** first, and never experiment on important directories.
2. **Placeholder files are only created in "truly empty" directories**: that is, a directory that has neither files nor subdirectories. Once a parent directory has a subdirectory, no `.gitkeep` is added to it.
3. **The configuration files live in the user directory**: templates are stored in `%AppData%\WinToolBox\FileMaster\templates.json`, and logs in `%LocalAppData%\WinToolBox\logs\`.

---

## 1. Preparation Before Testing

### 1.1 Prepare the program

- [ ] Download `FileMaster-win-x64.zip` from the release page and extract it to the **desktop or another non-system directory** (for example `D:\Tools\FileMaster\`).
- [ ] **Double-click** `FileMaster.exe` to start it; the main window should appear normally, then open the feature page this checklist covers via the menu 「文件管理」 (File management) → 「文件夹结构创建」 (Create folder structure).
- [ ] Note the interface structure, which every later step will use:
  - Top toolbar: `模板：[下拉框]` (Template: [drop-down]) `[保存为模板]` (Save as Template) `[删除模板]` (Delete Template) …… `[从现有目录生成规则]` (Generate Rules from Existing Directory) `[导出检查报告]` (Export Inspection Report) `[导出目录树]` (Export Directory Tree)
  - Target area: `根目录：[输入框] [浏览…]` (Root directory: [text box] [Browse…]), `[ ] 启用多根目录` (Enable multiple root directories), `[ ] 为空目录创建占位文件（.gitkeep）` (Create placeholder file (.gitkeep) for empty directories)
  - Middle: the rules input box on the left (labelled `文件夹规则`, "folder rules"), the result tree on the right
  - Lower part: `检查模式` (Check mode), `[创建文件夹] [检查一致性] [载入示例规则]` (Create Folders / Check Consistency / Load Sample Rules)
  - Bottom: the `运行日志` (Run log) area + status bar

### 1.2 Prepare the test directories

Create two empty directories on `D:\` or on the desktop, for example:

```text
D:\FCTest\项目A
D:\FCTest\项目B
```

### 1.3 Path quick reference

| Item | Path |
| --- | --- |
| Directory template library | `%AppData%\WinToolBox\FileMaster\templates.json` |
| Run log | `%LocalAppData%\WinToolBox\logs\wintoolbox-yyyyMMdd.log` |
| Exported directory tree | Chosen by you in the save dialog (default file name `目录树.txt`) |
| Exported inspection report | Chosen by you in the save dialog (default file name `目录检查报告.md`) |

---

## 2. Test Rounds

> General rhythm: fill in the root directory and the rules → click the corresponding button → look at the **result tree** (colors and the notes in brackets) and the **run log** → verify the actual files in File Explorer.

### Round 1: Rule template management (save / load / delete / built-in templates)

**Steps**

1. Enter any content in the rules box, for example:

   ```text
   # 测试模板
   -文档
   --图片
   ```

2. In the top `模板` (Template) drop-down, **type directly** `我的测试模板` (My Test Template) (the drop-down is editable), then click `[保存为模板]` (Save as Template).
3. Open `%AppData%\WinToolBox\FileMaster\templates.json` in Notepad.
4. Clear the rules box, then select `我的测试模板` in the `模板` drop-down.
5. Select the built-in template `Web 项目` (Web Project) (a dialog asks you to confirm overwriting) → then select `Python 项目` (Python Project).
6. Select `我的测试模板` and click `[删除模板]` (Delete Template) → confirm.

**Expected results**

- [ ] After saving, the status bar shows 「模板已保存：我的测试模板（新建）」 ("Template saved: 我的测试模板 (new)").
- [ ] The template is visible in `templates.json` (of the form `"我的测试模板": "# 测试模板\r\n-文档\r\n--图片"`), and the two built-in templates `Web 项目` and `Python 项目` are **written automatically on first launch**.
- [ ] After a template is selected, the contents of the rules box are replaced with that template's contents.
- [ ] Selecting `Web 项目` shows a confirmation box asking 「是否覆盖当前规则内容」 ("Overwrite the current rule contents?"); selecting `Python 项目` does the same.
- [ ] After deletion, `我的测试模板` is no longer in the drop-down, and the corresponding entry disappears from `templates.json`.

**Verdict**: all 4 items satisfied → pass.

---

### Round 2: Reverse rule generation from an existing directory

**Steps**

1. Using File Explorer, manually create this tree under `D:\FCTest\项目A`:

   ```text
   项目A\
   ├─.git\            （模拟版本库目录，应被默认排除）
   ├─node_modules\    （应被默认排除）
   ├─src\
   │  ├─components\
   │  └─pages\
   └─tests\
   ```

2. In the program, set `根目录` (Root directory) to `D:\FCTest\项目A`.
3. Click `[从现有目录生成规则]` (Generate Rules from Existing Directory) (if the rules box is not empty, it asks for confirmation to overwrite first).

**Expected results**

- [ ] The generated content in the rules box is **sorted by name** and looks like this:

  ```text
  -src
  --components
  --pages
  -tests
  ```

- [ ] `.git` and `node_modules` **do not appear** (the defaults exclude `.git`, `node_modules`, `bin`, `obj`, `.vs`).
- [ ] The status bar shows 「已生成规则：N 个目录」 ("Rules generated: N directories").
- [ ] Clicking `[从现有目录生成规则]` once more immediately gives **exactly the same** result (repeatable and stable).

**Verdict**: all 4 items satisfied → pass.

---

### Round 3: Creating placeholder files for empty directories

**Steps**

1. Check `[√] 为空目录创建占位文件（.gitkeep）` (Create placeholder file (.gitkeep) for empty directories).
2. Set the root directory to `D:\FCTest\项目A` (or create another empty directory `D:\FCTest\占位测试`), and enter the rules:

   ```text
   -文档
   --图片
   -源码
   --模块
   ```

3. Click `[创建文件夹]` (Create Folders).

**Expected results**

- [ ] In File Explorer: a **0-byte** file appears at each of `文档\图片\.gitkeep` and `源码\模块\.gitkeep`.
- [ ] **The `文档` and `源码` directories themselves have no `.gitkeep`** (they already have subdirectories, so they do not count as empty directories).
- [ ] These nodes in the result tree show 「（已创建）（已创建 .gitkeep）」 ("(created) (created .gitkeep)").
- [ ] Clicking `[创建文件夹]` again: the directories become 「已存在，已跳过」 ("already exists, skipped"), and `.gitkeep` **is neither created again nor reported as an error**.
- [ ] After unchecking the option, create another empty directory (for example `-空目录`); `.gitkeep` **should not** appear in the result.

**Verdict**: all 5 items satisfied → pass.

---

### Round 4: Multi-root batch creation

**Steps**

1. Check `[√] 启用多根目录` (Enable multiple root directories): a multi-line input box appears in the lower part of the window, and the single-root input box above it is greyed out.
2. Enter in the multi-line box (**line 3 deliberately differs from line 1 in letter case, to verify de-duplication**):

   ```text
   D:\FCTest\项目A
   D:\FCTest\项目B
   D:\FCTest\项目A
   ```

   > While you are at it, try an invalid path too: add another line `D:\FCTest\非法|路径`.
3. Enter the rules:

   ```text
   -文档
   --图片
   -源码
   ```

4. Click `[创建文件夹]` (Create Folders), watching the status bar and the progress bar during the run.

**Expected results**

- [ ] During creation the status bar shows 「**当前处理第 1 / 共 2 个**：…」 → 「第 2 / 共 2 个」 ("currently processing 1 of 2" → "2 of 2"), and the progress bar advances to 100% accordingly.
- [ ] The result tree shows **2 root nodes**, labelled 「第 1 / 2 个根目录：…」 and 「第 2 / 2 个根目录：…」 ("root directory 1 / 2: …"), each carrying its own directory details.
- [ ] `项目A` and `项目B` **both get** the same structure created (the duplicate line and the invalid line are ignored).
- [ ] The status bar / log reports 「已忽略 N 个无效或重复的路径」 ("ignored N invalid or duplicate paths").
- [ ] **Fault-tolerance check**: delete `项目B` and occupy its place with an **existing file of the same name** (for example, create a file with no extension named `项目B` under `D:\FCTest\`), then run once more → that root directory reports a failure, while **the other root directory is still created normally** (a single-point failure does not abort the whole run).

**Verdict**: all 5 items satisfied → pass.

---

### Round 5: Inspection report export (Markdown)

**Steps**

1. Verify with a **deliberately inconsistent** directory (for example, inside `D:\FCTest\项目A` delete `源码\模块` by hand, and create a `多余目录` that is not in the rules).
2. Set the root directory to that directory and fill in the rules:

   ```text
   -文档
   --图片
   -源码
   --模块
   ```

3. Select `严格模式（检查多余文件夹）` (Strict mode (check for extra folders)) and click `[检查一致性]` (Check Consistency).
4. Click `[导出检查报告]` (Export Inspection Report) and save it as `检查报告.md` in the save dialog (saving to the desktop is suggested).
5. Open this report with Notepad or VS Code.

**Expected results**

- [ ] Before the check, `[导出检查报告]` is **greyed out (not clickable)**; after a successful check it becomes **clickable**.
- [ ] The result tree distinguishes by color: green = match, red = missing, **tan = extra**, and 「模块（缺失）」 and 「多余目录（多余）」 appear **at the same time**.
- [ ] The report contains: title, check time, target directory, check mode, check conclusion (differences exist), and a statistics table (matched / missing / extra / total).
- [ ] The report lists the details by category: `## 缺失的文件夹（1）` → `- 源码\模块`; `## 多余的文件夹（1）` → `- 多余目录`; `## 匹配的文件夹（3）`.
- [ ] The Chinese text and the paths in the report display correctly (UTF-8), and backslashes in paths render as a single `\` in a Markdown preview.
- [ ] When there are **more than 100** missing/extra entries, the corresponding section is collapsed into `<details>…</details>` (you can verify this with a script that creates 120 extra directories in bulk).

**Verdict**: all 6 items satisfied → pass.

---

### Round 6: Directory tree text export

**Steps**

1. Set the root directory to `D:\FCTest\项目A` (its existing structure is enough).
2. Click `[导出目录树]` (Export Directory Tree) and save it as `目录树.txt`.
3. Open it with Notepad.

**Expected results**

- [ ] The first line is the full path of the root directory.
- [ ] What follows is standard `tree`-style tree text, for example:

  ```text
  D:\FCTest\项目A
  ├─src
  │  ├─components
  │  └─pages
  └─tests
  ```

- [ ] It uses the characters `├─` / `└─` / `│`, and **Chinese text and indentation are aligned correctly**.
- [ ] The last line is a statistics line of the form `3 个目录` ("3 directories").
- [ ] `.git`, `node_modules`, `bin`, `obj`, `.vs` **do not appear** (excluded by default).
- [ ] Exporting once more gives content **exactly the same** as the previous run (stable and repeatable).

**Verdict**: all 5 items satisfied → pass.

---

## 3. Pass / Fail Verdict Table

| Round | Feature | Pass criteria |
| --- | --- | --- |
| 1 | Rule template management | All four of save / load / delete / built-in templates work, and the contents of `templates.json` are correct |
| 2 | Reverse rule generation | Generation is correct, sorted by name, default exclusions take effect, repeatable and consistent |
| 3 | Placeholder files | A 0-byte `.gitkeep` is created only in truly empty directories, and repeated runs do not error |
| 4 | Multiple root directories | De-duplication and invalid-path ignoring take effect, per-root progress is correct, one root failing does not abort the run |
| 5 | Inspection report | Button availability is correct, the report structure is complete, missing and extra entries are listed together, and more than 100 entries are collapsed |
| 6 | Directory tree export | Tree characters are correct, trailing statistics are present, default exclusions take effect, results are stable |

**All pass** → the 6 enhanced features in this round are accepted.

---

## 4. What Information to Collect on Failure

1. **Run log**: the contents of the `运行日志` (Run log) area at the bottom of the window, or that day's records in the log file `%LocalAppData%\WinToolBox\logs\wintoolbox-yyyyMMdd.log` (it states which directories failed and why).
2. **Template library**: the contents of `%AppData%\WinToolBox\FileMaster\templates.json` (for template-related problems).
3. **Steps to reproduce**: which button was clicked, and the **exact text** of the root directory and the rules (especially whether 「多根目录」 or 「占位文件」 was checked).
4. **Screenshots of the interface**: the result tree + the status bar + the run log.
5. **The actual on-disk result**: a screenshot taken in File Explorer for that directory after running 「显示 → 隐藏的项目」 (View → Hidden items).

---

## 5. Notes

- The **core logic** of all 6 features (template read/write, rule generation, placeholder files, multi-root de-duplication and fault tolerance, report and directory tree text generation) is covered by **unit tests**, 322 test cases in total; this checklist is used to verify **the interface interaction and the real on-disk results**.
- This checklist covers the **folder-structure features** of FileMaster (`src/Tools/FileMaster/`) and introduces no dependency on any main application.
