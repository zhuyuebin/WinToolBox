# COMMIT_PUSH_REPORT — WinToolBox 提交与推送报告

- **任务书**：DSH 任务书：WinToolBox 提交并推送至 GitHub + Gitee
- **执行时间**：2026-10-04（Asia/Shanghai）
- **执行范围**：仅 git 操作（暂存 / 提交 / 推送），**未修改任何源码或文档内容**
- **约束遵守**：第一次提交未用 `git add -A`（按路径暂存并程序化校验）、全程未用 `--force`、未修改远程配置、未执行 `git reset --hard` / `git clean`

---

## 1. 体检摘要（步骤 1）

| 项 | 结果 |
| --- | --- |
| 当前分支 | `main`（`git symbolic-ref --short HEAD` → main，**非** detached HEAD） |
| 远程仓库 | `origin` → `git@github.com:zhuyuebin/WinToolBox.git`（GitHub）；`gitee` → `git@gitee.com:zhuyuebin/win-tool-box.git`（Gitee） |
| SSH 配置 | `~/.ssh/config`：github.com → `ssh.github.com:443`；gitee.com 使用 `~/.ssh/id_ed25519_gitee` |
| 远程跟踪分支 | `origin/main`、`gitee/main` 均存在 |
| 未提交改动（体检时） | 45 个修改 + 22 个未跟踪 + 14 项已暂存（文档 rename/新增）= 81 项 |
| 最近一次提交 | `581be1c feat(FileMaster): FolderCreator 重命名扩展为 FileMaster，新增 8 项文件管理功能 (v0.4.0)` |
| 是否有 P0/P1 修复未提交 | **是**（57 个代码文件；`Directory.Build.props` 本次无改动） |
| 是否有文档整理未提交 | **是**（`docs/` 16 项 + 8 份 README 的链接更新） |
| 体检时与远程差距 | 领先 `origin/main` 0、领先 `gitee/main` 0（无 non-fast-forward 风险） |

---

## 2. 构建与测试（步骤 2）

### 2.1 构建

```
> dotnet build WinToolBox.sln -c Release -m:1
Build succeeded.
    6 Warning(s)      ← 全部为 NU1900（无法联网查询包漏洞数据）
    0 Error(s)
```

**环境限制（与本仓库代码无关，已与用户确认后采用等价命令）**：裸命令 `dotnet build WinToolBox.sln -c Release` 在本会话沙箱内 `exit 1`，且只输出 `Build FAILED. / 0 Warning(s) / 0 Error(s)`，**没有任何 error CSxxxx / NUxxxx 诊断**。逐级定位结论：

| 变体 | 结果 |
| --- | --- |
| `dotnet build WinToolBox.sln -c Release` | ❌ exit 1（静默失败） |
| 加 `DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1` + `MSBUILDDISABLENODEREUSE=1` | ❌ exit 1 |
| **加 `-m:1`** | ✅ **Build succeeded，0 Error** |
| `--no-restore --no-incremental -m:1 -p:UseSharedCompilation=false`（全量重建） | ✅ **Build succeeded，0 Error**（5 个项目全部重新编译） |

根因：DSH 沙箱（workspace-write）禁止命名管道，MSBuild 多节点 / 共享编译进程（`VBCSCompiler`）通信被拦截。CI（`.github/workflows/release.yml`）与正常 shell 不受此限制。

### 2.2 测试

`dotnet test` 在本会话不可用：Windows 令牌禁止 testhost 所需的 `Process.EnableRaisingEvents`，VSTest 宿主必然崩溃（此为构建期既有结论）。改用仓库 `.tmp/` 下**既有的**进程内 xUnit 运行器（`runxunit`，位于 git 忽略目录，不修改任何产品代码与测试项目配置）：

| 测试程序集 | 总计 | 通过 | 失败 | 跳过 |
| --- | --- | --- | --- | --- |
| `WinToolBox.Core.Tests.dll` | 440 | 440 | 0 | 0 |
| `FileMaster.Tests.dll` | 561 | 561 | 0 | 0 |
| **合计** | **1001** | **1001** | **0** | **0** |

---

## 3. 两次提交（步骤 3、4）

| # | commit | message | 规模 |
| --- | --- | --- | --- |
| ① | **`4723e62`** | `fix: 完成 P0/P1 全部修复（1001/1001 测试通过）` | 57 files changed, 15354 insertions(+), 365 deletions(-) |
| ② | **`9e65e24`** | `docs: 整理仓库 Markdown 文档结构` | 24 files changed, 2915 insertions(+), 139 deletions(-) |

### 提交① 内容（代码 / 配置 / CI，37 修改 + 20 新增）

`.github/workflows/release.yml`、`publish-local.ps1`、`src/Tools/FileMaster/**`（含 `Services/EmptyFolderDeleteMessages.cs`、`Services/PathSafety.cs` 等新增）、`src/Tools/UsbBackup/**`、`src/WinToolBox.Core/**`（含 `BackupManifest.cs`、`BackupMirrorService.cs` 等新增）、`tests/**`（20 个新增测试/辅助文件）。

**程序化校验**（提交前由脚本执行，不通过即中止）：暂存项必须恰为 57 项，且不得出现任何 `.md` 或 `docs/` 路径 → 实测 57 项、0 项文档，校验通过后才提交。

### 提交② 内容（文档，`git diff --cached --name-status` 原样输出）

```
M	README.md
M	README_EN.md
A	docs/README.md
R100	MD-files/WinToolBox 子工具开发任务书：FolderCreator.md	docs/archive/WinToolBox-子工具开发任务书-FolderCreator.md
R100	MD-files/WinToolBox 项目开发任务书 (DSH 自动化指令).md	docs/archive/WinToolBox-项目开发任务书-DSH-自动化指令.md
A	docs/audit/AUDIT_REPORT.md
A	docs/reports/DOCS_REORG_REPORT.md
A	docs/reports/FIX_REPORT.md
A	docs/tasks/DSH_Audit_Task.md
A	docs/tasks/DSH_Task_DocsReorganize.md
R099	MD-files/DSH_Task_FileMaster.md.md	docs/tasks/DSH_Task_FileMaster.md
A	docs/tasks/DSH_Task_FixP0P1.md
R100	MD-files/DSH_Task_FolderCreator_Upgrade.md.md	docs/tasks/DSH_Task_FolderCreator_Upgrade.md
R089	MD-files/FolderCreator-本地手动测试清单.md	docs/testing/FileMaster-文件夹结构-本地手动测试清单.md
R090	MD-files/FolderCreator-本地手动测试清单_EN.md	docs/testing/FileMaster-文件夹结构-本地手动测试清单_EN.md
R100	FileMaster-本地手动测试清单.md	docs/testing/FileMaster-本地手动测试清单.md
R099	MD-files/本地手动测试清单.md	docs/testing/UsbBackup-本地手动测试清单.md
R099	MD-files/本地手动测试清单_EN.md	docs/testing/UsbBackup-本地手动测试清单_EN.md
M	src/Tools/FileMaster/README.md
M	src/Tools/FileMaster/README_EN.md
M	src/Tools/UsbBackup/README.md
M	src/Tools/UsbBackup/README_EN.md
M	src/WinToolBox.Core/README.md
M	src/WinToolBox.Core/README_EN.md
```

**程序化校验**：`git add -A --dry-run` 的每一项必须落在文档集合内（`docs/**`、`MD-files/**`、根 `FileMaster-本地手动测试清单.md`、根与模块 `README(_EN).md`），暂存项数必须恰为 24 → 实测通过后才提交。

### 提交信息原文

```
fix: 完成 P0/P1 全部修复（1001/1001 测试通过）

- P0-1: 增量备份增加内容校验（SHA256 + 大文件抽样哈希）
- P0-2: 修复空目录永久误删（删除前重新判空 + 非空即拒绝）
- P0-3: publish-local.ps1 加安全护栏（拒绝驱动器根/系统目录）
- P1-1~P1-20: 按审计报告修复全部 P1 问题
- 新增/更新单元测试覆盖所有修复项
```

```
docs: 整理仓库 Markdown 文档结构

- 移动报告到 docs/audit/、docs/reports/
- 移动测试清单到 docs/testing/（按工具名统一命名）
- 移动任务书到 docs/tasks/，历史任务书到 docs/archive/
- 新建 docs/README.md 索引
- 更新所有引用链接，0 处失效
```

---

## 4. 推送结果（步骤 5）

| 远程 | 地址 | 结果 |
| --- | --- | --- |
| `origin`（GitHub） | `git@github.com:zhuyuebin/WinToolBox.git` | ✅ `581be1c..9e65e24  main -> main`（退出码 0） |
| `gitee`（Gitee） | `git@gitee.com:zhuyuebin/win-tool-box.git` | ✅ `581be1c..9e65e24  main -> main`（退出码 0） |

推送使用 `GIT_TERMINAL_PROMPT=0` + `ssh -o BatchMode=yes -o ConnectTimeout=20`，避免交互式挂起；未使用 `--force`，未改动远程配置。

---

## 5. 推送后同步验证（步骤 6）

| 项 | 结果 |
| --- | --- |
| `git status` | **clean**（两次提交后、报告写入前为 0 项） |
| 提交历史（前 3 条） | `9e65e24 docs:` → `4723e62 fix:` → `581be1c feat(FileMaster):` |
| `HEAD` | `9e65e24` |
| `origin/main` | `9e65e24` |
| `gitee/main` | `9e65e24` |
| `git rev-list --count origin/main..main` | **0**（已同步） |
| `git rev-list --count gitee/main..main` | **0**（已同步） |
| `git remote -v` | 与体检时完全一致（未改动） |

---

## 6. 未完成项与遗留问题

1. **本报告文件本身未入库**：`docs/reports/COMMIT_PUSH_REPORT.md` 在两次提交与推送之后生成，目前为**未跟踪**（`??`）。因此当前 `git status` 会显示这 1 个未跟踪文件（提交与推送当时是 clean 的）。如需入库，需追加一次提交并再次推送——未擅自执行。
2. **环境限制（非仓库问题，建议在正常 shell / CI 复核）**：
   - 裸 `dotnet build WinToolBox.sln -c Release` 在本会话沙箱内静默失败（命名管道被禁），已用仅多 `-m:1` 的等价命令验证 0 错误；
   - `dotnet test` 在本会话不可用（testhost 受令牌限制），已用进程内 xUnit 运行器验证 1001/1001 通过。CI 上两者均按标准命令执行不受影响。
3. **提交① 的 message 沿用任务书原文**：其中 P0/P1 条目描述与 `docs/reports/FIX_REPORT.md` 一致；`Directory.Build.props` 本次无改动，故未产生 diff。
4. **过程记录**：提交① 之后的 `git add -A` 预演校验曾因把 9 条 `remove 'MD-files/…'`（旧路径删除）误判为非文档项而中止（校验器过严的**假阳性**）；中止发生在 `git add -A` 之前，**未产生任何副作用**，修正校验规则后重跑通过。
5. **CRLF 提示**：`core.autocrlf=true`，git 对若干文本文件提示 “LF will be replaced by CRLF the next time Git touches it”，属 Windows 正常提示，不影响提交内容。
6. **本次会话的临时脚本/日志**保留在 git 忽略目录 `.tmp/`（`step3-commit1.ps1`、`step4-commit2-push.ps1`、`commit1-msg.txt`、`commit2-msg.txt`、`step2-tests.log`），未清理，可作为操作证据。
