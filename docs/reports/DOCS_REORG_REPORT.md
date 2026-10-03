# DOCS_REORG_REPORT — WinToolBox 仓库 Markdown 文档整理报告

- **任务书**：[`docs/tasks/DSH_Task_DocsReorganize.md`](../tasks/DSH_Task_DocsReorganize.md)（原 `MD-files/DSH_Task_DocsReorganize.md`）
- **执行时间**：2026-10-03
- **执行范围**：仅 Markdown 文档的**移动 / 重命名 / 链接与索引更新**。未修改任何源码、`.csproj`、`.sln`、CI 脚本、`publish-local.ps1`。
- **Git 操作**：只做了 `git add` 与 `git mv`，**未执行 `git commit` / `git push`**。
- **用户确认的决策**（执行前逐条确认）：
  1. 英文版后缀统一用 `_EN.md`（与模块 README 的既有约定一致）；
  2. 手动测试清单**彻底按当前工具名统一**（含把已并入 FileMaster 的 FolderCreator 清单改名）；
  3. 两份非 DSH 命名的早期任务书归入 `docs/archive/` 并规范文件名；
  4. 历史文档正文中的旧路径引用**保持原样**（历史事实），在 `docs/README.md` 中统一说明；
  5. 未跟踪文件用 `git add` + `git mv`（保留重命名记录）；
  6. 顺带修复：`zhiyuebin` → `zhuyuebin`（2 处）、两份清单的过期路径；**不**新建 FileMaster 清单英文版。

---

## 1. 扫描结果（步骤 1）

扫描范围：全仓库 `.md`（排除 `.git`、`.nuget` 本地包缓存、`bin`、`obj`）。共 **22 份**：**保留原位 8 份**、**移动 14 份**、**删除文档 0 份**。

### 1.1 保留原位（8 份，含模块级 README）

| 路径 | 大小 | 首行标题 |
| --- | --- | --- |
| `README.md` | 9,807 B | `# WinToolBox` |
| `README_EN.md` | 10,545 B | `# WinToolBox` |
| `src/WinToolBox.Core/README.md` | 6,641 B | `# WinToolBox.Core` |
| `src/WinToolBox.Core/README_EN.md` | 7,419 B | `# WinToolBox.Core` |
| `src/Tools/FileMaster/README.md` | 12,646 B | `# FileMaster 使用说明` |
| `src/Tools/FileMaster/README_EN.md` | 15,099 B | `# FileMaster User Guide` |
| `src/Tools/UsbBackup/README.md` | 16,845 B | `# UsbBackup 使用说明` |
| `src/Tools/UsbBackup/README_EN.md` | 21,735 B | `# UsbBackup User Guide` |

`LICENSE` 保留在根目录（非 Markdown）；根目录无 `CHANGELOG.md`，按约束未新建。

### 1.2 移动的 14 份

| # | 原路径 | 新路径 | 大小 | 移动前 Git 状态 |
| --- | --- | --- | --- | --- |
| 1 | `AUDIT_REPORT.md` | `docs/audit/AUDIT_REPORT.md` | 141,490 B | 未跟踪 |
| 2 | `FIX_REPORT.md` | `docs/reports/FIX_REPORT.md` | 36,544 B | 未跟踪 |
| 3 | `FileMaster-本地手动测试清单.md` | `docs/testing/FileMaster-本地手动测试清单.md` | 14,525 B | 已跟踪 |
| 4 | `MD-files/本地手动测试清单.md` | `docs/testing/UsbBackup-本地手动测试清单.md` | 36,327 B | 已跟踪 |
| 5 | `MD-files/本地手动测试清单_EN.md` | `docs/testing/UsbBackup-本地手动测试清单_EN.md` | 45,040 B | 已跟踪 |
| 6 | `MD-files/FolderCreator-本地手动测试清单.md` | `docs/testing/FileMaster-文件夹结构-本地手动测试清单.md` | 11,936 B | 已跟踪（有未提交改动） |
| 7 | `MD-files/FolderCreator-本地手动测试清单_EN.md` | `docs/testing/FileMaster-文件夹结构-本地手动测试清单_EN.md` | 14,998 B | 已跟踪（有未提交改动） |
| 8 | `MD-files/DSH_Audit_Task.md` | `docs/tasks/DSH_Audit_Task.md` | 9,055 B | 未跟踪 |
| 9 | `MD-files/DSH_Task_DocsReorganize.md` | `docs/tasks/DSH_Task_DocsReorganize.md` | 7,943 B | 未跟踪 |
| 10 | `MD-files/DSH_Task_FileMaster.md` | `docs/tasks/DSH_Task_FileMaster.md` | 7,812 B | 未跟踪（HEAD 中为 `.md.md`） |
| 11 | `MD-files/DSH_Task_FixP0P1.md` | `docs/tasks/DSH_Task_FixP0P1.md` | 25,345 B | 未跟踪 |
| 12 | `MD-files/DSH_Task_FolderCreator_Upgrade.md` | `docs/tasks/DSH_Task_FolderCreator_Upgrade.md` | 7,161 B | 未跟踪（HEAD 中为 `.md.md`） |
| 13 | `MD-files/WinToolBox 子工具开发任务书：FolderCreator.md` | `docs/archive/WinToolBox-子工具开发任务书-FolderCreator.md` | 5,837 B | 已跟踪 |
| 14 | `MD-files/WinToolBox 项目开发任务书 (DSH 自动化指令).md` | `docs/archive/WinToolBox-项目开发任务书-DSH-自动化指令.md` | 6,885 B | 已跟踪 |

### 1.3 扫描出的问题与处理

| 类别 | 发现 | 处理 |
| --- | --- | --- |
| `.md.md` 双扩展名 | HEAD 中跟踪 `MD-files/DSH_Task_FileMaster.md.md`、`MD-files/DSH_Task_FolderCreator_Upgrade.md.md`；磁盘上已是正确名（未跟踪），`git status` 显示 2 个 ` D` + 2 个 `??` | 把磁盘上的正确名入库并移动，同时暂存旧名删除；最终在暂存区**配对为 rename**（`R MD-files/DSH_Task_FileMaster.md.md -> docs/tasks/DSH_Task_FileMaster.md`） |
| 文件名拼写错误 | 文件名本身无拼错；正文 2 处仓库 URL 用户名写 `zhiyuebin`（应为 `zhuyuebin`，会 404） | 已按确认修复：`docs/tasks/DSH_Audit_Task.md:15`、`docs/tasks/DSH_Task_FileMaster.md:4` |
| 内容重复 | 无完全相同副本（22 份 MD5 互不相同）；3 份清单内容有重叠（审计报告 P3-7 已记录），英文侧无 FileMaster 版 | **未删除任何文档**；是否补英文版由用户后续决定 |
| 内容过期 | 两份 FolderCreator 清单仍写 `%AppData%\WinToolBox\FolderCreator\templates.json`（当前实际为 `FileMaster`，见 `src/WinToolBox.Core/AppPaths.cs`）、`src/Tools/FolderCreator/`、`WinToolBox.App` | 已按确认修复（各 4 处路径 + 第 281 行说明段）；历史任务书正文按决策 4 保持原样 |
| 空文件 / 占位文件 | 无 | — |
| `docs/` 目录 | 不存在 | 本次新建（`.gitignore` 第 32 行已有 `!docs/**/*.zip`，说明 `docs/` 本就是预期目录） |
| `.gitignore` | 无任何 `MD-files/` 规则 | 无需改动 |

---

## 2. 实际执行的移动操作

先 `mkdir`，再对未跟踪文件 `git add` 后统一 `git mv`（全部成功，无命名冲突、无覆盖）：

```powershell
# 0) 目录
mkdir docs/audit docs/reports docs/testing docs/tasks docs/archive

# 1) 未跟踪的 7 份先入库，使重命名可被记录
git add -- AUDIT_REPORT.md FIX_REPORT.md
git add -- MD-files/DSH_Audit_Task.md MD-files/DSH_Task_DocsReorganize.md MD-files/DSH_Task_FileMaster.md
git add -- MD-files/DSH_Task_FixP0P1.md MD-files/DSH_Task_FolderCreator_Upgrade.md

# 2) 14 次 git mv
git mv -- AUDIT_REPORT.md docs/audit/AUDIT_REPORT.md
git mv -- FIX_REPORT.md docs/reports/FIX_REPORT.md
git mv -- FileMaster-本地手动测试清单.md docs/testing/FileMaster-本地手动测试清单.md
git mv -- MD-files/本地手动测试清单.md docs/testing/UsbBackup-本地手动测试清单.md
git mv -- MD-files/本地手动测试清单_EN.md docs/testing/UsbBackup-本地手动测试清单_EN.md
git mv -- MD-files/FolderCreator-本地手动测试清单.md docs/testing/FileMaster-文件夹结构-本地手动测试清单.md
git mv -- MD-files/FolderCreator-本地手动测试清单_EN.md docs/testing/FileMaster-文件夹结构-本地手动测试清单_EN.md
git mv -- MD-files/DSH_Audit_Task.md docs/tasks/DSH_Audit_Task.md
git mv -- MD-files/DSH_Task_DocsReorganize.md docs/tasks/DSH_Task_DocsReorganize.md
git mv -- MD-files/DSH_Task_FileMaster.md docs/tasks/DSH_Task_FileMaster.md
git mv -- MD-files/DSH_Task_FixP0P1.md docs/tasks/DSH_Task_FixP0P1.md
git mv -- MD-files/DSH_Task_FolderCreator_Upgrade.md docs/tasks/DSH_Task_FolderCreator_Upgrade.md
git mv -- "MD-files/WinToolBox 子工具开发任务书：FolderCreator.md" docs/archive/WinToolBox-子工具开发任务书-FolderCreator.md
git mv -- "MD-files/WinToolBox 项目开发任务书 (DSH 自动化指令).md" docs/archive/WinToolBox-项目开发任务书-DSH-自动化指令.md

# 3) 暂存 HEAD 中两个 .md.md 旧名的删除
git add -A -- MD-files/DSH_Task_FileMaster.md.md MD-files/DSH_Task_FolderCreator_Upgrade.md.md
```

> 说明：`git mv` 会同时暂存源文件已有的未提交内容改动（#6、#7 两份清单），这是 `git mv` 的固有行为，不额外新增内容变更。

---

## 3. 链接与引用更新清单（含行号，行号为**更新后**的行号）

移动前基线：全仓库 66 条 Markdown 相对链接、0 条失效；其中 **25 条**指向本次移动的文件（15 条来自保留文件，10 条是移动文件之间的互链）。全部已更新，无遗漏。

### 3.1 保留文件中的链接（15 条）

| 文件 | 行 | 原链接 | 新链接 |
| --- | --- | --- | --- |
| `README.md` | 106 | `FileMaster-本地手动测试清单.md` | `docs/testing/FileMaster-本地手动测试清单.md` |
| `README.md` | 118 | `FileMaster-本地手动测试清单.md` | `docs/testing/FileMaster-本地手动测试清单.md` |
| `README.md` | 119 | `MD-files/本地手动测试清单.md` | `docs/testing/UsbBackup-本地手动测试清单.md` |
| `README.md` | 120 | `MD-files/FolderCreator-本地手动测试清单.md` | `docs/testing/FileMaster-文件夹结构-本地手动测试清单.md` |
| `README.md` | 148 | `MD-files/本地手动测试清单.md` | `docs/testing/UsbBackup-本地手动测试清单.md` |
| `README_EN.md` | 116 | `FileMaster-本地手动测试清单.md` | `docs/testing/FileMaster-本地手动测试清单.md` |
| `README_EN.md` | 117 | `MD-files/本地手动测试清单_EN.md` | `docs/testing/UsbBackup-本地手动测试清单_EN.md` |
| `README_EN.md` | 118 | `MD-files/FolderCreator-本地手动测试清单_EN.md` | `docs/testing/FileMaster-文件夹结构-本地手动测试清单_EN.md` |
| `README_EN.md` | 146 | `MD-files/本地手动测试清单_EN.md` | `docs/testing/UsbBackup-本地手动测试清单_EN.md` |
| `src/Tools/FileMaster/README.md` | 42 | `../../../MD-files/FolderCreator-本地手动测试清单.md` | `../../../docs/testing/FileMaster-文件夹结构-本地手动测试清单.md` |
| `src/Tools/FileMaster/README.md` | 59 | `../../../FileMaster-本地手动测试清单.md` | `../../../docs/testing/FileMaster-本地手动测试清单.md` |
| `src/Tools/FileMaster/README_EN.md` | 42 | `../../../MD-files/FolderCreator-本地手动测试清单_EN.md` | `../../../docs/testing/FileMaster-文件夹结构-本地手动测试清单_EN.md` |
| `src/Tools/FileMaster/README_EN.md` | 59 | `../../../FileMaster-本地手动测试清单.md` | `../../../docs/testing/FileMaster-本地手动测试清单.md` |
| `src/Tools/UsbBackup/README.md` | 201 | `../../../MD-files/本地手动测试清单.md` | `../../../docs/testing/UsbBackup-本地手动测试清单.md` |
| `src/Tools/UsbBackup/README_EN.md` | 201 | `../../../MD-files/本地手动测试清单_EN.md` | `../../../docs/testing/UsbBackup-本地手动测试清单_EN.md` |

### 3.2 被移动文件之间的互链（10 条）

| 文件 | 行 | 更新内容 |
| --- | --- | --- |
| `docs/testing/FileMaster-文件夹结构-本地手动测试清单.md` | 3 | 语言切换：指向自身与 `_EN` 的新文件名 |
| 同上 | 11 | 交叉引用 → `UsbBackup-本地手动测试清单.md`，链接文字改为「UsbBackup 与 FileMaster 通用验收」 |
| `docs/testing/FileMaster-文件夹结构-本地手动测试清单_EN.md` | 3 / 11 | 同上（英文） |
| `docs/testing/UsbBackup-本地手动测试清单.md` | 3 | 语言切换：指向 `UsbBackup-本地手动测试清单.md` / `_EN.md` |
| `docs/testing/UsbBackup-本地手动测试清单_EN.md` | 3 | 同上 |

### 3.3 新增链接

| 文件 | 行 | 内容 |
| --- | --- | --- |
| `README.md` | 114 | 文档索引新增「文档中心」行 → `docs/README.md` |
| `README_EN.md` | 112 | Documentation index 新增 Documentation hub 行 → `docs/README.md` |
| `docs/README.md` | 22–32 | 新建索引本身包含 8 条 `.md` 直达链接 + 若干目录链接 |

### 3.4 非链接的正文改动

- `README.md:96` / `README_EN.md:94`：结构树新增 `docs/` 行。
- `README.md:105` / `README_EN.md:103`：「唯一没有英文版」的例外表述更新（该清单已不在仓库根目录）。
- `README.md:118–120` 与 `README_EN.md:116–118`：三行清单条目的链接文字与说明对齐新文件名。
- `docs/testing/FileMaster-文件夹结构-本地手动测试清单{, _EN}.md`：H1 标题由 `FolderCreator …` 改为 `FileMaster 文件夹结构 …`；`%AppData%\WinToolBox\FolderCreator\templates.json` → `%AppData%\WinToolBox\FileMaster\templates.json`（各 4 处）；第 281 行「基于独立 FolderCreator 项目（`src/Tools/FolderCreator/`）…（`WinToolBox.App`）」改为与现状一致的表述。原文第 5 行「原 `FolderCreator.exe` 已在 v0.4.0 并入 FileMaster」作为历史沿革**保留**。
- `docs/tasks/DSH_Audit_Task.md:15`、`docs/tasks/DSH_Task_FileMaster.md:4`：`zhiyuebin` → `zhuyuebin`。
- `.csproj` / `.sln` / `publish-local.ps1` / `.github/workflows/*.yml` / 源码注释：经全仓库检索，**0 处**引用被移动的 Markdown 文件，无需改动（源码中出现的 `*.md` 仅为文件对话框过滤器与测试数据）。

---

## 4. 删除的文件

**未删除任何文档。** 仅删除了迁移后已清空的目录：

| 目标 | 结果 |
| --- | --- |
| `MD-files/` | 迁移后为空 → 已删除（删除前校验：递归项数 0；路径确认为 `D:\github\WinToolBox\MD-files`） |
| `.gitignore` | 无 `MD-files/` 规则，未做任何改动 |

---

## 5. 最终目录结构

```text
docs/
├─ README.md                                    # 文档总索引（本次新建）
├─ archive/                                     # 已被取代的早期任务书
│  ├─ WinToolBox-子工具开发任务书-FolderCreator.md
│  └─ WinToolBox-项目开发任务书-DSH-自动化指令.md
├─ audit/                                       # 审计报告
│  └─ AUDIT_REPORT.md
├─ reports/                                     # 修复报告 / 技术报告
│  ├─ DOCS_REORG_REPORT.md                      # 本文件
│  └─ FIX_REPORT.md
├─ tasks/                                       # DSH 任务书（历史归档）
│  ├─ DSH_Audit_Task.md
│  ├─ DSH_Task_DocsReorganize.md
│  ├─ DSH_Task_FileMaster.md
│  ├─ DSH_Task_FixP0P1.md
│  └─ DSH_Task_FolderCreator_Upgrade.md
└─ testing/                                     # 手动测试清单
   ├─ FileMaster-文件夹结构-本地手动测试清单.md
   ├─ FileMaster-文件夹结构-本地手动测试清单_EN.md
   ├─ FileMaster-本地手动测试清单.md
   ├─ UsbBackup-本地手动测试清单.md
   └─ UsbBackup-本地手动测试清单_EN.md
```

未创建 `docs/guides/`（仓库暂无独立的用户指南文档，工具使用说明即各模块 README，按规则保留原位）。

---

## 6. 验证结果

| 验证项 | 结果 |
| --- | --- |
| 文件确实落在目标位置 | ✅ 14/14（逐个 `Test-Path` 校验，见第 5 节结构） |
| Markdown 相对链接存在性 | ✅ 23 个 `.md` 文件、87 条相对链接、**0 条失效** |
| `git status` 显示重命名而非删除+新增 | ✅ `R`(4) + `RM`(5) + `A`(4) + `AM`(1)；两个 `.md.md` 旧名与 `docs/tasks/` 新文件配对为 rename |
| 构建验证 | ✅ **Build succeeded, 0 Error(s)**（19 warnings，均为既有代码告警与联网告警） |
| `git commit` / `git push` | ✅ 未执行 |

**构建命令说明**：`dotnet build WinToolBox.sln -c Release` 在本沙箱内**无法直接运行**，原因与文档改动无关：

1. `dotnet restore` 需要访问 `api.nuget.org`，本环境无外网（同样原因，仓库根目录既有的 `.build-baseline.log`（15:44）也是 restore 失败）；
2. 沙箱禁止命名管道，MSBuild 多节点 / Roslyn 编译服务器（`VBCSCompiler`）通信失败，表现为 `Build FAILED. 0 Warning(s) 0 Error(s)` 的静默失败。

因此改用等价的**全量重建**（复用仓库内已有的 `.nuget/packages` 还原产物）：

```powershell
$env:DOTNET_CLI_HOME = 'D:\github\WinToolBox\.dotnet'
$env:NUGET_PACKAGES  = 'D:\github\WinToolBox\.nuget\packages'
dotnet build WinToolBox.sln -c Release --no-restore --no-incremental -m:1 -p:UseSharedCompilation=false
# → Build succeeded.  0 Error(s)  19 Warning(s)
```

即：**只改动 Markdown，编译链路不受影响**（5 个项目全部重新编译通过）。

---

## 7. 未完成项与遗留问题

1. **新建文件尚未入库**：`docs/README.md` 与 `docs/reports/DOCS_REORG_REPORT.md` 是本次新建，目前为未跟踪（`??`），需要时 `git add` 即可；`git commit` / `git push` 按约定未执行。
2. **历史归档文档中的旧路径**：`docs/audit/AUDIT_REPORT.md`、`docs/reports/FIX_REPORT.md`、`docs/tasks/DSH_*.md` 正文里仍保留 `MD-files/...`、`src/Tools/FolderCreator/` 等当时表述（按确认的决策 4 保持原样），已在 `docs/README.md` 的「归档说明」中统一提示其可能过期。
3. **FileMaster 手动测试清单仍无英文版**：`docs/testing/FileMaster-本地手动测试清单.md` 只有中文；根 README 已如实声明该例外。本次未新建英文版（用户决策）。
4. **三份清单内容仍有重叠**（文件夹结构 6 项 / 文件管理 8 项 / UsbBackup 通用验收），本次只做了归类与改名，未做内容合并——合并属于内容改写，超出「移动/改名/链接」范围。
5. **工作区的既有未提交改动**：45 个已修改 + 21 个未跟踪文件是本次任务之前就存在的成果（源码、测试、CI 等），本次只触碰 Markdown；`git mv` 会把两份清单自身的未提交改动一并暂存。
6. **沙箱操作说明**：`git add` / `git mv` 需要写 `.git/index`，本次为这一批命令申请了一次性提权（已获批准）；`git commit` / `git push` 请你在正常 shell 中自行执行。
7. 根目录 `.build-baseline.log`（已被 `.gitignore` 的 `*.log` 忽略）与 `.tmp/` 是本地临时文件，非 Markdown，未处理。
