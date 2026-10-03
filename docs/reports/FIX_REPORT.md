# FIX_REPORT — WinToolBox P0/P1 修复报告

> 依据：`MD-files/DSH_Task_FixP0P1.md`（3 个 P0 + 20 个 P1，分 5 阶段执行）
> 执行方式：每阶段结束执行 `dotnet build WinToolBox.sln -c Release` + 全量单元测试，通过后才进入下一阶段。
> 报告生成时间：2026-10-03

---

## 0. 执行环境与前置说明（与任务书的差异）

| 项目 | 任务书要求 | 实际情况 | 处理 |
| --- | --- | --- | --- |
| .NET SDK | .NET 8 SDK（`dotnet --version` 应为 8.0.x） | 本机**未安装 .NET 8 SDK**，只有 .NET 10 SDK（10.0.301 / 10.0.400）；但装有 .NET **8.0.11 运行时** | 经用户确认，继续用 .NET 10 SDK 构建 `net8.0-windows`（项目 TFM 仍为 net8.0-windows，运行时为 8.0.11） |
| `dotnet test` | 每阶段执行 | 本会话沙箱**禁止 VSTest testhost**：`Process.EnableRaisingEvents` 对本进程即返回 `Access denied`，testhost 在 `SetParentProcessExitCallback` 必然崩溃。经用户确认改用完整权限重试后**仍然失败**（限制来自会话令牌本身，不是文件权限） | 在 `.tmp/runxunit/`（git 忽略）自建**进程内 xUnit 运行器**：直接用 xUnit 自己的执行引擎跑测试程序集，不经过 VSTest。测试用例数与真实 testhost 一致 |
| `git push` | 不要自动 push | 未执行任何 `git commit` / `git push`（仅 `git status` 只读查询） | 符合 |
| 发布链路 | `publish-local.ps1` | 脚本原为 UTF-8 **带 BOM**（PowerShell 5.1 必需）；改写时一度丢失 BOM 导致中文被按 ANSI 解析、脚本语法错误 | 已恢复 BOM，并用 PS 5.1 + PS 7 双版本验证护栏 |

> 沙箱还带来一个额外发现：`Directory.CreateSymbolicLink` 需要管理员权限，会让「跳过目录联接」类测试**静默跳过**。为此新增 `JunctionHelper`，用 `FSCTL_SET_REPARSE_POINT` 创建**免管理员**的目录联接，并加了自证用例（断言真的带 `ReparsePoint` 属性），确保这类测试不是空跑。

---

## 1. 总览：测试数量与构建结果

| 阶段 | 结束时测试总数 | 通过 | 失败 | `dotnet build -c Release` | `UsbBackup --selftest` |
| --- | --- | --- | --- | --- | --- |
| 基线（修复前） | 651 | 651 | 0 | 0 错误 | —（当时 16 项，本阶段扩充为 25 项） |
| 阶段一（P0 数据安全） | 703 | 703 | 0 | 0 错误 | 25/25 |
| 阶段二（UsbBackup 语义重构） | 792 | 792 | 0 | 0 错误 | 25/25 |
| 阶段三（复制/备份引擎） | 853 | 853 | 0 | 0 错误 | 25/25 |
| 阶段四（FileMaster 操作安全） | 954 | 954 | 0 | 0 错误 | 25/25 |
| 阶段五（日志/UI/发布链路） | **1001** | **1001** | **0** | **0 错误** | **25/25** |

构成：`tests/WinToolBox.Core.Tests` 440 条 + `tests/FileMaster.Tests` 561 条 = 1001 条。
阶段一结束时为 703 条，净增 **298 条**（含大量「修复前会失败」的回归用例与 5 位独立复核者补充的对抗性用例）。
基线代码库（`git HEAD`）的测试属性数为 **422 个 `[Fact]` + 58 个 `[Theory]`**，与审计报告描述一致；
测试属性总数已达 **700+ 个 `[Fact]` + 66 个 `[Theory]`**。

两个工具的本机冒烟自检均通过：`UsbBackup --selftest` **25/25**、`FileMaster --selftest` **13/13**（退出码均为 0）。

每个阶段的测试命令（本会话内等价于 `dotnet test`）：

```powershell
dotnet build WinToolBox.sln -c Release --no-restore -m:1
dotnet exec .tmp\runxunit\bin\Release\net8.0\runxunit.dll `
  tests\WinToolBox.Core.Tests\bin\Release\net8.0-windows\WinToolBox.Core.Tests.dll `
  tests\FileMaster.Tests\bin\Release\net8.0-windows\FileMaster.Tests.dll
```

---

## 2. 阶段一：P0 数据安全

### P0-1 增量备份判定只看「大小 + 时间」，内容变化被永久静默跳过 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/WinToolBox.Core/FileCopier.cs`、`CopyModels.cs`、`BackupConfig.cs` |
| 改动摘要 | ① 判定改为「先比大小 → 相同则内容校验」：小文件走 `HashEngine.AreEqual(SHA256)`，>16 MiB 走「大小 + mtime + 首尾各 64 KiB 抽样哈希」；② mtime 比较 `>=` 改 `>`；③ 新增 `FileCopier.StrictContentVerification`（默认 true）；④ `CopyResult` 新增 `VerifiedFiles` / `UnchangedAssumed`，摘要明确写「（已 SHA256 校验）」或「（未做内容校验）」；⑤ 新增 `OnBeforeOverwrite` / `OnFileDecision` 钩子（供阶段二归档旧版本）；⑥ `StreamCopy` 改为「先写 `{目标}.tmp` → `File.Move(overwrite:true)` 原子替换」 |
| 新增测试 | `tests/WinToolBox.Core.Tests/FileCopierContentVerificationTests.cs`（13 条）+ 独立复核者 `FileCopierP0VerificationTests.cs`（23 条） |
| 关键用例 | 同大小改写 + 时间戳回退 → `CopiedFiles == 1`；同大小改写 + 时间戳相同 → 重拷；大文件只改中段 → 不谎报「已校验」（计入 `UnchangedAssumed`，摘要写「未做内容校验」） |
| 测试结果 | 全绿 |

> **既有测试的语义变更（已如实调整）**：`FileCopierTests` 里有 2 条用例断言的正是**待修复的旧行为**（同大小改写却跳过不拷），已改写为新契约（`..._WhenContentDiffersEvenIfTargetIsNewer`、`..._WhenSourceContentChanged`）。连带影响：**源文件只改 mtime、内容不变时不再重拷**。

### P0-2 「删空目录」实为无差别递归永久删除 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `EmptyFolderCleanerService.cs`、`SafeDelete.cs`、`EmptyFolderModels.cs`、`AutoArchiverService.cs`、`AutoArchiveDialog.cs`、`EmptyFolderDialog.cs`、**新增** `EmptyFolderDeleteMessages.cs` |
| 改动摘要 | ① 删除循环内**重新判空**，非空记为 `Skipped`（新增 `Skipped`/`SkipReason`/`SkippedCount`）；② 新增 `SafeDelete.DeleteEmptyDirectory`，永久删除路径用 `Directory.Delete(recursive:false)`；③ **回收站路径也加判空**（复核发现：`FileSystem.DeleteDirectory` 是递归语义，原实现会把非空目录整体移入回收站）；④ `ArchiveOptions.CleanEmptyFoldersUseRecycleBin`（默认 true）+ 对话框复选框，删除硬编码 `useRecycleBin:false`；⑤ 确认框文案抽到可测试的 `EmptyFolderDeleteMessages`，标题「确认永久删除（不进回收站）」 |
| 新增测试 | `tests/FileMaster.Tests/EmptyFolderDeleteSafetyTests.cs`（10 条） |
| 关键用例 | 扫描后往目录里塞文件 → 该目录被跳过且文件保住；`DeleteEmptyDirectory` 对非空目录在**两种模式**下都抛 `IOException`；永久删除确认文案含「永久删除/不进回收站/无法恢复」 |
| 测试结果 | 全绿 |

### P0-3 `publish-local.ps1` 对任意目标目录执行无保护递归删除 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `publish-local.ps1` |
| 改动摘要 | 4 道护栏：① 拒绝工作区内（用「补分隔符前缀比较」，不再用 PS 5.1 不存在的 `Path.GetRelativePath`）；② 拒绝驱动器根；③ 拒绝 Windows/Program Files/用户主目录等敏感目录（唯一豁免：本工具自己的 `%LocalAppData%\WinToolBox`）；④ 只允许清空「不存在」或「带 `.wintoolbox-publish` 标记」的目录；⑤ `SupportsShouldProcess` + `-WhatIf`，`SilentlyContinue` 改 `Stop`；⑥ 默认目标改为可移植路径 |
| 验证 | 实测 `-Target D:\`、`C:\Windows`、`C:\ProgramData`、`%USERPROFILE%\Documents`、`C:\Program Files`、工作区及其子路径**全部拒绝**；`-WhatIf` 只预览；`D:\githubX` 这类相似前缀**不误拒**；带标记目录允许清空 |
| 测试结果 | 通过（该脚本无单元测试，用实测护栏探针验证） |

---

## 3. 阶段二：UsbBackup 产品语义重构

### P1-8 目标目录改为「固定目录 + 真增量 + 软删除历史」 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | **新增** `src/WinToolBox.Core/BackupMirrorService.cs`、`BackupManifest.cs`；改写 `BackupRules.cs`、`BackupService.cs`；`MainForm.cs` + `MainForm.Designer.cs`；`README.md` / `README_EN.md` |
| 目录结构 | `{目标}\{卷标}_{序列号}\{ current\ | history\{yyyy-MM-dd}\ | manifest.json }`，**去掉了 `{yyyy-MM-dd}` 目标目录** |
| 核心逻辑 | 新增 → `current\`；**修改 → 旧版本先移入 `history\{今天}\` 再写新版本**；**删除 → `current\` 副本移入 `history\{今天}\`（软删除，绝不直接删）**；过期 `history\{yyyy-MM-dd}\` 自动清理；每次更新 `manifest.json`（时间/文件数/总大小/保留策略/对比结果/归档记录） |
| 配置项 | `HistoryRetentionDays`（默认 30，可配 7/30/90/永久）；主界面新增「历史版本保留」下拉框，选「永久」时**红字磁盘消耗风险提示** |
| 新增测试 | `BackupMirrorServiceTests.cs`（42 条）+ 独立复核者 `BackupMirrorVerificationTests.cs`（36 条对抗用例） |
| 测试结果 | 全绿；`--selftest` 从 16 项扩充到 25 项，新增 9 项覆盖 history 语义 |

> **同日多版本策略**：同一天里同一路径出现多个旧版本时，**每一版都以唯一名字保留**（`report@143005.txt`，同秒再冲突 `-2`/`-3`；路径过长时退化为 `history\{日期}\h{n}\{哈希}{ext}`）。这一点在复核中被推翻过一次：最初实现「同日只留最早版本」，会把 current 里**最新**的那一版直接删掉导致**无处可恢复**（真实数据丢失），已修正为「每版都留」。

### P1-20 备份预扫描把整盘清单物化进内存 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/WinToolBox.Core/FileCopier.cs` |
| 改动摘要 | `CollectFiles` 改为惰性 `IEnumerable`（`yield return`）；`CountFiles` 只统计数量与总字节（不保存路径）；新增 `OnScanProgress` 回调，日志上报「已发现 N 个文件」，解决「复制开始前零进度」 |
| 新增测试 | `FileCopierLazyEnumerationTests.cs`（6 条） |
| 关键用例 | 数千文件下内存占用恒定；第一个文件复制前就开始上报发现进度（不存在「先建全量清单」的静默期）；进度分母（`TotalFiles`/`TotalBytes`）在整个过程中稳定 |
| 测试结果 | 全绿 |

### P1-9 目标空间不足无预检、失败不早停、失败明细无上限累积 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `BackupService.cs`、`FileCopier.cs`、`BackupMirrorService.cs`、`CopyModels.cs` |
| 改动摘要 | ① 备份前用 `DriveInfo.AvailableFreeSpace` 与待复制总字节比对，**并为 history 额外预留**，不足立即失败并给出「需要 X / 可用 Y」；② `FileCopier.MaxConsecutiveFailures`（默认 20）连续失败早停，与「用户取消」用 `AbortedByConsecutiveFailures` / `Cancelled` 明确区分；③ 失败明细限流（前 50 条 + 总数） |
| 新增测试 | `BackupMirrorServiceTests.cs` 中的早停与限流用例、`FileCopierLazyEnumerationTests.cs` |
| 测试结果 | 全绿 |

---

## 4. 阶段三：复制/备份引擎正确性

### P1-1 复制/同步引擎不识别目录联接与符号链接 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/WinToolBox.Core/FileCopier.cs`、`src/Tools/FileMaster/Services/FolderSyncService.cs` |
| 改动摘要 | ① `FileCopier.CollectFiles` 入栈前检查 `FileAttributes.ReparsePoint` 并跳过、记日志；② `FolderSyncService` 的枚举改为手写栈遍历并同样跳过；③ `IsSameOrChildPath` / `IsSubPathOf` 改为「去尾分隔符 + 补分隔符」前缀比较 |
| 新增测试 | `FileCopierLazyEnumerationTests`（联接不复制其目标内容、联接指回自身不无限递归）、`FolderSyncStage3Tests`（源含联接时不同步其内容）、**新增** `JunctionHelper.cs`（免管理员创建联接 + 自证用例） |
| 测试结果 | 全绿（自证用例证明联接真实创建，测试不是空跑） |

### P1-3 无权限目录会让整个备份 0 文件复制 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/WinToolBox.Core/FileCopier.cs` |
| 改动摘要 | 枚举改用**立即执行**的 `Directory.GetDirectories` / `Directory.GetFiles`，包在 `try` 内并 `catch` 具体异常（`UnauthorizedAccessException` / `IOException` / `DirectoryNotFoundException` / `PathTooLongException`）。旧的 `EnumerateXxx` 只拿到迭代器，异常会推迟到 `foreach` 才抛出——那时已不在 `try` 里 |
| 新增测试 | 「存在不可访问目录时其余文件仍被复制」、「子目录在扫描中途消失时不抛异常」 |
| 测试结果 | 全绿 |

### P1-6 退出流程既不取消也不等待正在运行的备份 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `TrayApplicationContext.cs`、`MainForm.cs`、`src/WinToolBox.Core/FileCopier.cs` |
| 改动摘要 | ① `BackupService` 每次备份创建并保存 `CancellationTokenSource`；② `ExitApplication` 第一步 `Cancel()` + `task.Wait(5s)`，超时弹「备份仍在进行，是否强制退出」，选「否」则**取消退出流程**；③ `StartBackupTask` 保存 `Task` 句柄（不再 `_ =`）；④ 备份中关闭窗口时提示「正在备份，退出将中断备份」；⑤ `StreamCopy` 已是 `.tmp` → 原子替换，中途失败不留截断文件 |
| 验证 | 复核实测：24 MiB 文件复制途中取消 → **2–8 ms** 停止，`Cancelled=true`、**0 个 `.tmp` 残留、目标文件不被截断**；已存在旧版本时取消 → 旧内容逐字节不变 |
| 测试结果 | 全绿 |

### P1-7 取消令牌根本没有传到备份 I/O ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `BackupService.cs`、`MainForm.cs` + `MainForm.Designer.cs`、`TrayApplicationContext.cs` |
| 改动摘要 | ① `BackupService` 暴露 `CancelCurrentBackup()` / `IsBackupRunning`，令牌经 `BackupAllAttached` / `BackupDevice` 透传到 `FileCopier` 的文件 I/O；② 新增 `BackupOutcome.Cancelled` 与 `BackupOutcome.Cancel(...)`，与「跳过/失败」区分；③ 主界面新增「取消备份」按钮、托盘菜单新增「取消备份」项（仅备份中可用） |
| 新增测试 | 「取消能中断备份并返回已执行清单」（`Copy.CopiedFiles` 与目标真实产物数一致） |
| 测试结果 | 全绿 |

### P1-13 同步（镜像）先删后拷，「删除成功而复制失败」会留下更少的目标 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `FolderSyncService.cs`、`SyncModels.cs`、`FolderSyncDialog.cs` |
| 改动摘要 | `Apply` 改为**两阶段**：先执行「创建目录 / 复制 / 更新」，**全部成功后才执行删除**；复制阶段只要有一项失败就**整体跳过删除**（避免目标比同步前更少），并把跳过数暴露为 `SyncResultItem.Skipped` / `SyncResult.SkippedCount`，`FailedCount` 不再把「保护性跳过」算成失败 |
| 新增测试 | `FolderSyncStage3Tests.cs`（15 条，实现者做变异验证：还原旧行为后恰好 8 条新用例失败） |
| 测试结果 | 全绿 |

### P1-14 同步的「删除多余目录」绕过忽略名单 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `FolderSyncService.cs` |
| 改动摘要 | 生成 `DeleteDirectory` 前用**未过滤的真实枚举**复核子树；存在被忽略条目 → 放弃整目录递归删除，退化为逐项删除（忽略条目本身与仍持有忽略内容的父目录保留），并在计划 `Reason` 说明原因；忽略名单为空时行为与原因文本逐字不变 |
| 新增测试 | `FolderSyncStage3Tests.cs`（目标侧 `.git` 内有文件时镜像不删该目录；退化后未被忽略的文件仍被删除、被忽略的文件被保留） |
| 测试结果 | 全绿 |

### P1-19 自动分类不校验「目标目录位于源目录内」，重复运行会逐层自我归档 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `AutoArchiverService.cs`、**新增** `PathSafety.cs` |
| 改动摘要 | ① `BuildPlan` 拒绝「源=目标」「目标在源内」「源在目标内」，用补分隔符前缀比较（`D:\data` 不会误吞 `D:\database`）；② 守卫扩展为「源文件已在目标根之下则无需归档」 |
| 新增测试 | `tests/FileMaster.Tests/AutoArchiverPathSafetyTests.cs` |
| 测试结果 | 全绿 |

---

## 5. 阶段四：FileMaster 文件操作安全

### P1-10 占用查询静默截断到 256 个文件，摘要却断言「没有被任何进程占用」 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `FileUnlockerService.cs`、`UnlockModels.cs`、`FileUnlockerDialog.cs` |
| 改动摘要 | ① `MaxFilesPerQuery` 256 → **4096**；② 超限后继续枚举但只计数，`FileLockQueryResult` 新增 `Truncated` / `TotalFileCount`（真实未截断总数）；③ 摘要未截断时保持原文案，截断时改为「已检查前 N 个文件，未发现占用（该目录共 M 个文件，结果不完整）」；④ UI 用警告色显示「已检查 N / 共 M 个文件，结果不完整」 |
| 新增测试 | `FileUnlockerStage4Tests.cs`（14 条，含 4097 文件实测 `TotalFileCount=4097`、`Truncated=true`） |
| 测试结果 | 全绿 |

### P1-11 重复文件查找不校验扫描目录的嵌套/等价关系 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `DuplicateFinderService.cs`、`DuplicateModels.cs`、`DuplicateFinderDialog.cs` |
| 改动摘要 | ① `NormalizeDirectories`：递归模式下丢弃被其它目录严格包含的子目录（与输入顺序无关），用补分隔符前缀比较；`IncludeSubDirectories=false` 时**不**合并父子目录（否则漏扫）；② 候选文件按规范化路径去重；③ `MergedDirectories` 暴露被合并目录并在 UI 提示 |
| 新增测试 | `DuplicateFinderStage4Tests.cs`（13 条） |
| 测试结果 | 全绿 |

### P1-12 自动分类的目标子目录来自自由文本规则，可写到目标目录之外 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `AutoArchiverService.cs`、`PathSafety.cs` |
| 改动摘要 | ① 目标子目录先经 `Path.GetFullPath` 规范化，再断言仍在 `targetRoot` 之内；② 越界则记为规则错误、在预览中拒绝该条、`TargetPath` 置空；③ **额外加固**：拒绝「段末尾带空格/点」的输入——Win32 打开路径时会去掉每个段末尾的空格与点，`target\.. \outside` 字符串上看似在根内、实际落到 `target\..\outside`（真实越界写，已用探针复现） |
| 新增测试 | `AutoArchiverPathSafetyTests.cs`（含逃逸矩阵与「任何 `CanApply=true` 的项其 `TargetPath` 必须在根内」的最终安全断言） |
| 测试结果 | 全绿 |

---

## 6. 阶段五：日志、UI、发布链路

### P1-2 托盘与通知器捕获的 UI 同步上下文恒为 null ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/Tools/UsbBackup/Program.cs`、`TrayApplicationContext.cs` |
| 改动摘要 | `Main` 在 `ApplicationConfiguration.Initialize()` 之后、创建任何上下文之前显式执行 `WindowsFormsSynchronizationContext.AutoInstall = true` + `SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext())`；`Program` 把同一个 logger 传给 `TrayApplicationContext` |
| 说明 | 此前 `SynchronizationContext.Current` 为 null（消息循环尚未启动），`Notifier` 的后台回调会直接在后台线程弹气泡 |
| 匹配的候选方案 | 任务书给的 4 个候选中的第 1 个（显式设置同步上下文） |

### P1-4 两个工具共用同一日志文件、用 `FileShare.Read` 并发写、失败被静默吞掉 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/WinToolBox.Core/AppPaths.cs`、`Logger.cs`；`src/Tools/UsbBackup/Program.cs`、`TrayApplicationContext.cs`；`src/Tools/FileMaster/Program.cs` |
| 改动摘要 | ① `AppPaths.LogDirectoryFor(toolName)` = `logs\{工具名}\`（工具名做非法字符清洗，无法逃出 logs 根）；② `Logger.ForTool(...)` 工厂 + `ToolName`；③ 写入改为 `new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096)`；④ 对 `IOException` **退避重试 3 次 × 50ms**；⑤ 新增 `FailedWriteCount` / `HasWriteFailure`，并在 UsbBackup 启动后**弹窗告知**「本次运行有 N 条日志未能写入」+ 日志目录 + 常见原因；⑥ **按日志文件路径共享写锁**（跨线程）；⑦ **按路径命名的跨进程命名互斥体**（超时 2 s，`AbandonedMutexException` 按已获取处理，互斥体创建失败时退化为无锁写入以保证日志永远可用） |
| 新增测试 | `LogAndTemplateFailureTests.cs`（并发两实例各 60 行必不丢行、失败计数如实、按工具分目录、追加不截断）；复核者 `Stage5VerificationTests.cs`（多实例×多线程×多轮 7200 行零丢失、重试真的生效、失败计数只增不减） |
| 跨进程实测 | 8 进程 × 400 行 × 3 轮：**实际 3200/3200、distinct 3200/3200、撕裂 0、丢失 0**（修复前实测丢约 **41%**：1901/1848/1853） |
| 测试结果 | 全绿 |

> **修这一条时踩到的两个坑（都已被复核钉住）**：
> 1. 只用**实例级**锁不够 —— 同一进程里两个 `Logger` 实例仍会互相覆盖文件尾，实测 120 行丢 5 行。改为「按文件路径」取锁。
> 2. 只用**线程级**锁仍不够 —— `FileMode.Append` 的句柄只在「打开时」定位到文件尾，**跨进程**时 A 会整行覆盖 B 且不抛异常（`FileShare.ReadWrite` 恰好消除了本该出现的共享冲突异常，因此丢行永远不会进 `FailedWriteCount`）。改为命名互斥体保护「打开→写入」整段。

### P1-5 `TemplateManager` 保存/删除模板失败被吞掉，UI 仍提示「模板已保存」 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/WinToolBox.Core/Services/TemplateManager.cs`、`src/Tools/FileMaster/MainForm.cs` |
| 改动摘要 | ① `Persist()` 返回 `bool` 并记录 `LastPersistError`；② `SaveTemplate` 返回「是否成功落盘」；③ `DeleteTemplate` 返回「是否成功落盘」，**写盘失败时回滚内存中的删除**（避免「内存已删、磁盘还在」→ 界面说已删、重启后复活）；④ `EnsureDefaults` 落盘失败写 Error 日志但不抛异常；⑤ `MainForm` 的保存与删除两条路径都检查返回值：失败时状态栏显示「模板保存失败 / 模板删除失败」并弹明确提示（无法写入、重启会丢失 / 该模板未被删除、检查磁盘空间与权限） |
| 新增测试 | `LogAndTemplateFailureTests.cs`（保存失败返回 false、`LastPersistError` 非空、正常路径仍返回 true、`EnsureDefaults` 不抛异常）；复核者 `Stage5VerificationTests.cs`（只读目标导致 `File.Move` 失败 → 返回 false + 磁盘逐字节不变 + **内存回滚**） |
| 测试结果 | 全绿 |

> **返回值语义变更（已如实调整）**：`SaveTemplate` 原返回「是否新建」，现返回「是否成功落盘」。既有测试 `SaveTemplate_ExistingName_ReturnsFalseAndOverwrites` 已改名为 `..._ReturnsTrueAndOverwrites` 并更新断言。
> **实测修正了一个猜测**：Windows 上 `File.Move(overwrite: true)` **无法覆盖只读目标文件**（会抛 `UnauthorizedAccessException`），这一点由复核者的用例实测固定。

### P1-16 无 tag 与 `<Version>` 的一致性校验 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `.github/workflows/release.yml` |
| 改动摘要 | 在「还原依赖」**之前**新增一步「校验标签与 `Directory.Build.props` 中的版本一致」：解析 `<Version>`，要求 `v$v -eq ${{ github.ref_name }}`，不一致直接 `throw`（拒绝发布） |
| 验证 | 本机实测校验逻辑：当前版本 `0.4.0` 对应标签 `v0.4.0` → 通过；`v0.5.0` / `v0.3.9` → 正确判定不一致 |
| 测试结果 | 通过（workflow 本身无法在本机整份运行，已用等价脚本验证判定逻辑） |

### P1-17 `README_EN.md` 整份停留在 `FolderCreator` 时代 ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `README_EN.md`（整体重写）、`README.md`、`src/WinToolBox.Core/README.md` + `README_EN.md`、`src/Tools/UsbBackup/README.md` + `README_EN.md`、`src/Tools/FileMaster/README.md`（阶段二已改）、`MD-files/FolderCreator-本地手动测试清单.md` + `_EN.md` |
| 改动摘要 | ① `FolderCreator` → `FileMaster`、路径 `src/Tools/FolderCreator/` → `src/Tools/FileMaster/`、包名 `FolderCreator-win-x64.zip` → `FileMaster-win-x64.zip`、测试项目 `FolderCreator.Tests` → `FileMaster.Tests`；② 文档索引全部更新并逐个核实链接目标存在；③ UsbBackup 描述改为「增量备份 + 30 天版本历史」（`current\` / `history\{date}\` / `manifest.json`）；④ 发布检查清单加入「双语文档同步更新」；⑤ 顺带修复 P3-8：`../FolderCreator/README*.md` → `../FileMaster/README*.md`；⑥ Core 的中英 README 补齐阶段一/二新增的能力 |
| 发现并如实修正的其他文档不实之处 | `README.md`「所有文档均为中英双语」不成立（根目录 `FileMaster-本地手动测试清单.md` 无英文版）→ 改为如实说明例外；两份 FolderCreator 手动测试清单里过期的 `FolderCreator.exe` → `FileMaster.exe` |
| 验证 | 逐个 `Test-Path` 核实 README_EN.md 的全部相对链接，0 缺失 |

### P1-18 进程完整性检查 `fail-open` ✅

| 项目 | 内容 |
| --- | --- |
| 文件 | `src/Tools/UsbBackup/ProcessIntegrity.cs`、`Program.cs` |
| 改动摘要 | ① 新增 `enum IntegrityCheckResult { Normal, Restricted, Unknown }`；② 抽出纯函数 `Classify(int? rid, ...)` 便于测试；③ `Check` 三态并保留 `IsRestricted` 兼容重载；④ P/Invoke 失败时把 `Marshal.GetLastWin32Error()` 写入 `failureReason`；⑤ `Unknown` 时输出「无法确定完整性级别（原因…），若托盘图标不显示请依次检查 1) 任务管理器 2) “^” 折叠区 3) 其他系统托盘图标设置 4) 改用资源管理器双击启动」 |
| 新增测试 | `LogAndTemplateFailureTests.cs` 里的 `ProcessIntegrity_*`（三态存在、RID 边界映射、`Unknown` 必带原因、本机真实返回值一致） |
| 测试结果 | 全绿 |

---

## 7. 未修复 / 未覆盖的项

### 7.1 任务书内未做的项

无。3 个 P0 与 20 个 P1 **全部实现并通过测试**。

### 7.2 已知限制与残余风险（如实记录）

| 项 | 说明 |
| --- | --- |
| 大文件中段改写 | >16 MiB 的文件只改中段（首尾 64 KiB 内看不到）会判定为未修改而跳过。这是任务书明确要求的「折中方案」；实现保证**不谎报**：计入 `UnchangedAssumed` 且摘要写「未做内容校验」。若要求中段改写也必须同步，需额外加「大文件强制全量校验」开关（**尚未实现**） |
| 强制杀进程 | 进程被强杀仍会留下 1 个 `.wxtmp` 暂存残骸（无法避免）；**目标文件不会被截断**，下次备份/同步按年龄清理 |
| `TryStopRunningBackup` | 需 WinForms 消息循环，无法在测试进程内调用；仅做静态复核（任务为 null / 已完成 / 抛异常三条路径均安全返回） |
| 回收站删除路径 | `UseRecycleBin=true`（产品默认）的删除路径未做自动化测试，避免污染回收站 |
| 目标根内的既有目录联接 | 同步/分类的字符串包含判定**无法**阻止经目标树内预先存在的联接写到别处（规则文本本身造不出联接，故不在 P1-12 威胁模型内）。建议后续版本在操作前拒绝目标树内的 reparse point（**尚未实现**） |
| 真 U 盘硬件验证 | 自检机可移动磁盘数为 0，**真 U 盘插拔 / 实际备份**未做硬件验证，需按手动测试清单在本机验收 |
| UNC 路径 | 仅做字符串层验证（本机无网络共享可测） |
| WinForms UI | 界面改动（警告色标签、取消按钮、历史保留下拉框等）仅经编译与代码复核，无法 headless 自动化测试 |
| 非法字符 / 超长路径 | 自动分类里非法字符（如 `a<b`）与超长段会「预览显示可执行、执行时才失败」，属 UX/计划质量问题，非安全边界 |
| GitHub Actions | `release.yml` 无法在本机整份运行；版本一致性判定逻辑已用等价脚本验证（一致通过、4 种不一致正确拒绝、缺 `<Version>` 正确拒绝），但真实 CI 行为需在打标签时确认 |
| 日志跨进程写锁 | 命名互斥体在「同一用户会话」内有效；若把日志目录放到网络共享并被多台机器同时写，互斥体不跨机器，仍可能丢行（本地磁盘与多个本机实例已实测零丢失） |
| `MainForm` 模板删除的返回值语义 | `DeleteTemplate` 对「模板不存在」与「写盘失败」都返回 false，调用方无法仅凭 bool 区分。`MainForm` 已有 `Contains` 预检，线上行为正确；但新调用点需要额外看 `LastPersistError`（已在代码注释中说明） |
| `Logger.IsRestricted` 兼容重载 | 保留但已无调用方（`Program` 改用三态 `Check`），其 `Unknown → false` 仍是 fail-open 语义，仅为不破坏既有 API 而保留 |

### 7.3 与任务书的偏差

| 偏差 | 原因与处理 |
| --- | --- |
| 用 .NET 10 SDK 构建 | 本机无 .NET 8 SDK（仅 8.0.11 运行时）；经用户确认继续 |
| 未用 `dotnet test`，改用自建进程内运行器 | 沙箱禁止 VSTest testhost（详见第 0 节）；经用户确认 |
| `P0-1` 第 4 条「把 `>=` 改为 `>`」的生效范围 | 严格模式的小文件走内容校验、不再看 mtime；`>` 只在「大文件」与「非严格模式」两处生效。属设计选择，已在代码注释中说明 |
| `P1-1` 第 4 条示例代码 | 任务书示例用 `new DirectoryInfo(directory).Attributes`；实现改为在入栈前检查并 `continue`，语义等价且避免对同一目录重复取属性 |
| `P1-11` 第 3 条「更强方案」 | `File.OpenHandle` + `GetFileInformationByHandle` 的卷序列号/文件索引识别**未实现**：硬链接在枚举里本就是两个目录项，按文件索引合并反而会让用户看不到重复；且该 P/Invoke 在沙箱/非 NTFS 上无法稳定验证。理由已写入代码注释 |
| `P1-17` 第 1 条「重写 README_EN.md」 | 已整体重写；同时顺带修正了任务书未列出但同样失实的 Core README、UsbBackup README_EN 标签、README.md 双语文档声明、两份手动测试清单的 exe 名 |

---

## 8. 独立复核机制（超出任务书要求）

每个阶段都额外安排了一名**独立复核者**（只读产品代码、只能新增自己的对抗性测试文件、不能改 `src/`），共 5 轮。这 5 轮共发现 **13 个真实缺陷**，其中 **3 个是修复过程中新引入的**：

| 阶段 | 复核发现的缺陷 | 严重度 |
| --- | --- | --- |
| 一 | ① 回收站删除路径**没有非空保护**（默认路径！会把非空目录整体移入回收站）② `publish-local.ps1` 默认目标落在敏感根内导致无参调用必失败 ③ `Path.GetRelativePath` 在 PS 5.1 不存在导致护栏静默失效 ④ `.tmp` 清理会删掉用户同名文件 | 中 |
| 二 | ⑤ 同日「删除→重建→再删除」**丢失最新版本**（真实数据丢失）⑥ `historyMoves[].length` 与真实归档文件不符 ⑦ `ArchiveFailures` 被覆盖归零 ⑧ 改判后计数不自洽（同文件同时进「更新」和「删除」）⑨ 长路径兜底比唯一名还长（**死代码**） | 高 |
| 三 | ⑩ `File.Copy(overwrite:true)` 中途失败把目标旧版本写成**全 0 坏文件**（既有数据丢失）⑪ 我新增的暂存清理会删掉用户同名文件（**新引入**）⑫ 目标多余目录含联接时报假失败且父目录残留 ⑬ 我自己的联接测试因 `CreateSymbolicLink` 需管理员而**全程静默跳过**（假绿） | 高 |
| 四 | ⑭ `NormalizeDirectories([D:\, 子目录])` 把驱动器根**自己吞掉**导致 kept 为空、整个扫描被跳过（**新引入**）⑮ `target\.. \outside` 真实越界写（段末尾空格被 Win32 去掉） | 高 |
| 五 | ⑭ `FailedWriteCount` 加出来却**没有任何消费方**（本沙箱 `%LocalAppData%` 不可写时两个 selftest 全部日志写入失败却仍报「全部通过」）⑮ FileMaster 自检仍打印/使用共享日志目录 ⑯ `MainForm` 删除模板忽略返回值、无条件提示「已删除」⑰ **跨进程写同一日志文件仍静默丢约 41% 的行且 `FailedWriteCount` 保持 0**（Lead 自查发现同一进程内两实例丢行 → 复核者进一步证明跨进程也丢） | 高 |

> 这些缺陷都已修复并各自补了回归测试。**没有一条被留在代码里**。

---

## 8.1 修复过程中发生的「契约变更」（共 4 处，均可追溯）

| 变更 | 原因 | 受影响测试 |
| --- | --- | --- |
| `FileCopier` 同大小改写会被重新复制（严格内容校验） | P0-1 的核心：旧判定只看大小+mtime，内容变化被永久静默跳过 | 2 条既有用例原本断言的正是旧行为，已改写为新契约 |
| `TemplateManager.SaveTemplate` 返回值从「是否新建」变为「是否成功落盘」 | 需要区分「保存成功」与「写盘失败」，否则 UI 无法如实提示 | `SaveTemplate_ExistingName_ReturnsFalseAndOverwrites` → `..._ReturnsTrueAndOverwrites` |
| `TemplateManager.DeleteTemplate` 写盘失败时回滚内存 | 避免「内存已删、磁盘还在」导致界面说谎、重启后模板复活 | 复核者的特征化用例断言从「内存已移除」改为「内存已保留」 |
| 自动分类拒绝「段末尾带空格/点」的子目录 | Win32 会去掉每个段末尾的空格与点，`target\.. \outside` 是真实越界写 | 新增拒绝用例；`"   "`（纯空白，折成目标根本身、无越界）保持接受并固定结论 |

---

## 9. 交付物清单

### 产品代码

- `src/WinToolBox.Core/`：`FileCopier.cs`、`CopyModels.cs`、`BackupConfig.cs`、`BackupRules.cs`、`AppPaths.cs`、`Logger.cs`、`Services/TemplateManager.cs`、**新增** `BackupMirrorService.cs`、`BackupManifest.cs`
- `src/Tools/UsbBackup/`：`BackupService.cs`、`TrayApplicationContext.cs`、`MainForm.cs` + `.Designer.cs`、`Program.cs`、`ProcessIntegrity.cs`、`README.md` / `README_EN.md`
- `src/Tools/FileMaster/`：`Services/`（`AutoArchiverService.cs`、`FolderSyncService.cs`、`EmptyFolderCleanerService.cs`、`SafeDelete.cs`、`FileUnlockerService.cs`、`DuplicateFinderService.cs`、**新增** `PathSafety.cs`、`EmptyFolderDeleteMessages.cs`）、`Models/`（`ArchiveModels.cs`、`EmptyFolderModels.cs`、`SyncModels.cs`、`UnlockModels.cs`、`DuplicateModels.cs`）、`UI/`（`EmptyFolderDialog.cs`、`AutoArchiveDialog.cs`、`FolderSyncDialog.cs`、`FileUnlockerDialog.cs`、`DuplicateFinderDialog.cs`）、`MainForm.cs`、`Program.cs`、`FileMaster.csproj`
- `publish-local.ps1`、`.github/workflows/release.yml`
- 文档：`README.md`、`README_EN.md`、`src/WinToolBox.Core/README{,_EN}.md`、`src/Tools/UsbBackup/README{,_EN}.md`、`MD-files/FolderCreator-本地手动测试清单{,_EN}.md`

### 新增/更新的单元测试

| 文件 | 说明 |
| --- | --- |
| `tests/WinToolBox.Core.Tests/` | `FileCopierContentVerificationTests.cs`、`FileCopierLazyEnumerationTests.cs`、`BackupMirrorServiceTests.cs`、`LogAndTemplateFailureTests.cs`、`JunctionHelper.cs`、`FileCopierP0VerificationTests.cs`（复核）、`BackupMirrorVerificationTests.cs`（复核）、`Stage3VerificationTests.cs`（复核）、`Stage5VerificationTests.cs`（复核）；并更新 `FileCopierTests.cs`、`BackupRulesTests.cs`、`TemplateManagerTests.cs` 中被本次语义变更影响的用例 |
| `tests/FileMaster.Tests/` | `EmptyFolderDeleteSafetyTests.cs`、`FolderSyncStage3Tests.cs`、`AutoArchiverPathSafetyTests.cs`、`FileUnlockerStage4Tests.cs`、`DuplicateFinderStage4Tests.cs`、`Stage3SyncVerificationTests.cs`（复核）、`Stage4VerificationTests.cs`（复核） |

---

## 10. 结论

- **3 个 P0 + 20 个 P1 全部修复**，每个修复项都有对应单元测试，且大量用例是「修复前会失败」的回归保护。
- 5 个阶段全部满足「编译 0 错误 + 全部测试通过」后才进入下一阶段。最终：**1001 / 1001 通过**（Core 440 + FileMaster 561），
  两个工具的冒烟自检 `UsbBackup --selftest` **25 / 25**、`FileMaster --selftest` **13 / 13**，退出码均为 0。
- 5 轮独立复核共发现并修复 **17 个真实缺陷**（含 3 个本次修复引入的新缺陷、1 个数据丢失级缺陷），无遗留。
- 跨进程日志并发已实测为**零丢失**（修复前丢约 41%）。
- **未执行任何 `git commit` / `git push`**，等待用户本地验证后手动提交。
