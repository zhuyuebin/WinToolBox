# WinToolBox 代码审计报告

- 审计对象：`D:\github\WinToolBox`（分支 `main`，HEAD `581be1c`）
- 审计日期：2026-10-03
- 审计性质：**只读代码审计**（未修改任何源码、配置、文档；未执行 git 写操作）
- 交付物：本文件（仓库根目录 `AUDIT_REPORT.md`）

---

## 一、执行摘要

### 审计范围

| 范围 | 内容 | 实测规模 |
|---|---|---|
| 共享库 | `src/WinToolBox.Core/**` | 17 个手写 `.cs` |
| 工具 A | `src/Tools/FileMaster/**` | 41 个源文件（37 `.cs` + csproj + manifest + 2 README），12,767 行 |
| 工具 B | `src/Tools/UsbBackup/**` | 11 个文件，3,098 行 |
| 测试 | `tests/WinToolBox.Core.Tests`、`tests/FileMaster.Tests` | 2 个测试项目、25 个 `.cs`、9,127 行；**422** 个 `[Fact]` + **58** 个 `[Theory]`（含 229 个 `[InlineData]`） |
| 构建/发布 | `WinToolBox.sln`、`Directory.Build.props`、`.github/workflows/release.yml`、`publish-local.ps1`、`.gitignore`、2 份 `app.manifest` | 全部通读 |
| 文档 | 根 `README.md`/`README_EN.md`、各模块 README、`MD-files/**` | 全部通读 |

**实测总计**：`git ls-files` 115 个文件；`.cs/.csproj/.props/.sln/.ps1/.yml/.manifest` 合计 **28,317 物理行**（其中 `.cs` 85 个文件 27,836 行）。

### 审计方法

1. 以 `git ls-files` 与磁盘实际内容建立真实文件清单（不采信任务书描述）。
2. 五路并行深审：跨工具一致性/重复、Core 抽象质量、正确性/并发/性能/安全、FileMaster 深审、UsbBackup + 构建发布链路。
3. 关键结论由独立复核员**回到源码逐行核对行号与事实**（对抗式验证），本报告中的行号以复核后的值为准。
4. Lead 亲自实测的高风险项：并发日志写入实验、`FileCopier.Decide`/`SafeDelete`/`EmptyFolderCleanerService`/`AutoArchiverService`/`TrayApplicationContext` 源码复核、构建与测试执行尝试。
5. 严重度按任务书 §4 标准分级。

### 主要发现（最重要 5 条）

1. **备份的"是否需要重新复制"判定只看文件大小与时间戳，从不校验内容**（`src/WinToolBox.Core/FileCopier.cs:190-L195`）。源文件被同大小改写且时间戳未前进时（7-Zip 解压、`robocopy /COPY:DAT`、`git checkout`、FAT32 2 秒粒度）会被**永久跳过**，而 `CopyResult.Success` 仍为 `true`、摘要显示"跳过 N 个未修改文件"。U 盘备份工具的核心承诺在此失效且用户无从察觉。
2. **存在一条"不重新判空 + 永久删除"的数据销毁路径**：`EmptyFolderCleanerService.Delete` 只检查 `Directory.Exists`、不重新判空（L128-L140），`SafeDelete.DeleteDirectory` 以 `Directory.Delete(path, recursive: true)` 永久删除（`SafeDelete.cs:49`），而 `AutoArchiverService` 把 `useRecycleBin: false` **硬编码**（`AutoArchiverService.cs:455`）。扫描与删除之间落入该目录的任何文件会被一并永久删除，回收站无副本。
3. **`publish-local.ps1` 对调用方指定的任意目录执行无保护递归删除**（L39），唯一护栏只排除"工作区内"（L34-L36）；`-Target D:\` 会清空整盘。这是本次审计范围内唯一具备"一键数据销毁"能力的代码路径。
4. **两个工具共用同一个日志文件**（`AppPaths.cs:58` 无工具名），`Logger` 用 `File.AppendAllText`（`FileShare.Read`，L133）写入。Lead 实测：4 进程并发追加时 **22.57% 的写入抛 `IOException`**，异常被 L136-L140 吞进 `LastError`，而 `LastError` 在**生产代码中无人读取**（仅测试读取）→ 两个工具同时运行时日志静默丢行，丢的恰恰是失败/错误日志。
5. **一个"共享库"实际上不是共享库**：`WinToolBox.Core` 的 30 个公共类型（`grep` 实测，见 §3.3）中，只有 `AppPaths`、`Logger`/`LogEntry`/`LogLevel`、`INotifier` 被两个工具共同引用，**26 个只有单一消费者**。其中 `Notifier` 还把 `NotifyIcon`/`ToolTipIcon` 放进 Core 的**公共 API**，迫使 `WinToolBox.Core.Tests` 必须声明 `net8.0-windows` + `UseWindowsForms=true`，纯算法测试无法在非 Windows 宿主运行。

### 问题数量分布

| 严重度 | 数量 | 说明 |
|---|---|---|
| **P0 致命** | **3** | 数据静默丢失 / 永久误删 / 一键数据销毁 |
| **P1 高** | **20** | 功能不可靠、并发错误、跨工具冲突、发布链路缺陷 |
| **P2 中** | **35** | 技术债、可维护性、重复实现、可测试性缺口 |
| **P3 低** | **26** | 命名、注释、组织、微小不一致 |
| 合计 | **84** | 去重合并后（五路原始条目 158 条 → 合并同源条目，并经 4 组独立复核员对抗式验证修正 13 处） |

### 整体健康度评分

**4.5 / 10**

评分依据（分项）：

| 维度 | 评分 | 理由 |
|---|---|---|
| 正确性与数据安全 | 3/10 | 5 条静默失败路径 + 2 条永久删除路径 + 3 处取消令牌未贯通 |
| 并发与进程模型 | 4/10 | 共享日志文件无协调、托盘上下文捕获时机错误、无 TaskScheduler 兜底 |
| 安全 | 6/10 | **未发现注册表操作**、删除有二次确认、`Process.Start` 使用克制；但脚本递归删除与 `fail-open` 判定拉低分数 |
| 架构与抽象 | 4/10 | Core 名义共享实质分裂；`MainForm` 是 God Object；Service 层下沉做得不错 |
| 可测试性 | 6/10 | Service 层 9 个类对 WinForms **零依赖**、480 个测试方法（422 `[Fact]` + 58 `[Theory]`）；但 **UI 层 4,707 行零测试**，UsbBackup **整个工具零测试** |
| 可维护性 | 5/10 | 文档双语文档齐全、注释质量高；但重复实现 15 处、`MainForm.cs` 1,828 行、测试基建两份副本 |
| 构建与发布 | 4/10 | CI 覆盖两个工具且跑测试；但无 tag↔版本校验、无签名、本地发布形态与 CI 不一致、脚本默认参数必失败 |
| 工程卫生 | 6/10 | 无构建产物入库、无残留已删除项目、`.gitignore` 基本完备；但根英文 README 整份过期 |

---

## 二、按严重度分组的问题清单

> **行号口径**：本报告全部行号为磁盘物理行号（含空行），由 `[System.IO.File]::ReadAllLines` / `read` 工具计数。任务书中的 `MainForm.cs = 1522 行` 等数字来自 `Get-Content | Measure-Object -Line` 口径（**丢弃空行**），与磁盘不符，差异见 §七。

---

### P0 致命问题

#### [P0-1] 增量备份判定只看"大小 + 时间"，内容变化会被永久静默跳过

- **文件**：`src/WinToolBox.Core/FileCopier.cs`
- **位置**：`Decide` L175-L198（核心判定 L190-L195）；`StreamCopy` L201-L240（时间戳对齐 L228-L239）；消费点 `src/Tools/UsbBackup/BackupService.cs` L204-L209
- **现象**：
  ```csharp
  // FileCopier.cs L187-L195
  var sourceInfo = new FileInfo(file.FullPath);
  var targetInfo = new FileInfo(destinationPath);

  // 增量判定：大小相同且目标不比源旧 => 认为未修改，跳过
  if (sourceInfo.Length == targetInfo.Length &&
      targetInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
  {
      return CopyDecision.SkipUnchanged;
  }
  ```
  复制成功后 L232 执行 `File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourceFile))`，把目标时间强制对齐为源时间，使上述判定在下次运行时继续成立。
- **为什么是问题**：判据完全是**元数据**，整个备份路径上没有任何内容校验（`FileCopier` 无哈希入口，`HashEngine` 就在同一程序集但备份侧引用不到）。以下常见情形会让"内容已变"的文件永远不被重拷：
  1. 用解压/同步工具写回 U 盘：7-Zip、`robocopy /COPY:DAT`、`git checkout` 都会保留归档里的**旧**修改时间，只要新旧文件大小相同即命中；
  2. 绝大多数 U 盘使用 FAT32/exFAT，其 `LastWriteTime` 只有 **2 秒粒度**且按本地时间存储，同一时间桶内的两次写入得到完全相同的时间戳；
  3. 任何显式把时间戳设回旧值的工具或脚本。
  该分支只累加 `SkippedFiles` 计数，不进 `result.Errors`，`CopyResult.Success` 仍为 `true`。
- **影响**：`UsbBackup` 的增量备份静默过期，用户在"备份完成"的提示下丢失最新版本；还原时拿到旧文件。反向场景同样存在：当备份目标位于 FAT32/exFAT 外置盘时，时间戳被向下取整可能使 `>=` 不成立 → 每次都全量重拷，增量失效。
- **修复建议**：
  1. 判定改为内容校验：先比大小，大小相同再调 `HashEngine.AreEqual(src, dst, HashAlgorithmKind.SHA256, ct)`；对大文件采用"大小 + mtime + 首尾 64 KiB 抽样哈希"折中，并在 `BackupConfig` 暴露"严格校验"开关。
  2. 至少把 `>=` 改为 `>`，让同刻文件重拷一次。
  3. `CopyResult` 增加 `VerifiedFiles` / `UnchangedAssumed` 计数，摘要明确写"（未做内容校验）"。
  4. 补一条自检用例：**同大小改写 + 时间戳回退**，断言 `CopiedFiles == 1`（当前实现必然失败）。
- **证据命令**：`grep -n "SkipUnchanged\|LastWriteTimeUtc" src/WinToolBox.Core/FileCopier.cs`（命中 L192、L194、L232）
- **交叉印证**：T1（P1-01）、T2、T3（P0）三路独立确认；Lead 已复核源码。

#### [P0-2] "删空目录"实为无差别递归永久删除，且在扫描与删除之间不重新判空

- **文件**：`src/Tools/FileMaster/Services/EmptyFolderCleanerService.cs`、`src/Tools/FileMaster/Services/SafeDelete.cs`、`src/Tools/FileMaster/Services/AutoArchiverService.cs`
- **位置**：`EmptyFolderCleanerService.Delete` L107-L161（判定 L128-L140）；`SafeDelete.DeleteDirectory` L34-L50（递归删除 L49）；`AutoArchiverService.Apply` L443-L470（硬编码 L455）；UI 入口 `UI/EmptyFolderDialog.cs` L85（「永久删除」按钮）、L172（默认全选）
- **现象**：
  ```csharp
  // EmptyFolderCleanerService.cs L128-L140：唯一的检查是"目录还在"
  if (!Directory.Exists(directory)) { items.Add(... Success = true); }
  else { SafeDelete.DeleteDirectory(directory, useRecycleBin); ... }

  // SafeDelete.cs L48-L49：永久删除键
  ClearReadOnlyAttributes(path);
  Directory.Delete(path, recursive: true);

  // AutoArchiverService.cs L453-L457：硬编码永久删除
  var deleted = cleaner.Delete(
      scan.Items.Select(static item => item.FullPath),
      useRecycleBin: false,      // ← 无开关、无 UI 选项
      progress: null, cancellationToken);
  ```
  判空只发生在 `Scan` 阶段（L66）。`Delete` 阶段与 `Scan` 之间存在**任意长的时间窗**（对话框要求用户逐项复核后点按钮，且结果默认全选）。
- **为什么是问题**：`Directory.Delete(path, recursive: true)` 的语义是"无差别递归删除"，而调用方的业务语义是"删除空目录"。窗口期内任何进程（构建脚本重建 `bin/obj`、同步客户端、下载工具、用户自己）在待删目录中创建文件后，`Delete` 无从得知，新文件被一并永久删除。`AutoArchiverService` L449-L457 在**刚移动完文件之后**立刻走同一条路径，窗口更贴近文件活动期。此外该调用完全绕过工具自身的"回收站优先"约定（其他 3 个删除入口默认走回收站），并会删除用户**故意保留**的、分类开始前就存在的空目录占位。
- **影响**：FileMaster 的空文件夹清理（「永久删除」入口）与自动分类的"移动后清理空目录"→ **永久性用户数据丢失，回收站无副本**。**定级说明**：`SafeDelete.DeleteDirectory(path, useRecycleBin: true)` 同样是**递归**删除（`Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory` 会移走整棵树，只是可恢复），因此"删除空目录"的语义偏差在两种模式下都存在；升级为 P0 的决定性因素是 `AutoArchiverService.cs:455` 与「永久删除」按钮这两条**不可恢复**的路径，叠加"不重新判空"的窗口期。
- **修复建议**：
  1. `Delete` 循环内删除前重新调用已存在的 `IsEmptyDirectory(directory)`（`public static`，L26-L61），返回 false 时记为 `Skipped` 而不是删除。
  2. 把 `SafeDelete` 的空目录场景改用 `Directory.Delete(path, recursive: false)`——非空即抛异常，天然消除误删。
  3. `ArchiveOptions` 增加 `CleanEmptyFoldersUseRecycleBin`（默认 `true`），由对话框复选框控制，去掉 L455 的硬编码。
  4. 确认框文案补充"永久删除、不进回收站"的明确提示（当前仅在括号里一句带过）。
- **证据命令**：`grep -n "Directory.Exists(directory)\|useRecycleBin" src/Tools/FileMaster/Services/EmptyFolderCleanerService.cs src/Tools/FileMaster/Services/AutoArchiverService.cs`
- **交叉印证**：T4（两条 P1）与 T3（P1）分别从"竞态"与"永久删除"角度命中；Lead 已复核三处源码，合并升级为 P0。

#### [P0-3] `publish-local.ps1` 对任意目标目录执行无保护递归删除

- **文件**：`publish-local.ps1`
- **位置**：L30（`$ErrorActionPreference = 'Stop'`）、L32（`$targetFull = [System.IO.Path]::GetFullPath($Target)`）、L34-L36（唯一护栏）、L39（`Remove-Item $targetFull -Recurse -Force -ErrorAction SilentlyContinue`）
- **现象**：
  ```powershell
  $targetFull = [System.IO.Path]::GetFullPath($Target)
  if ($targetFull.StartsWith([System.IO.Path]::GetFullPath($root), [System.StringComparison]::OrdinalIgnoreCase)) {
      throw "目标目录不能位于工作区内…"      # 唯一的安全检查
  }
  ...
  Remove-Item $targetFull -Recurse -Force -ErrorAction SilentlyContinue   # L39
  ```
  护栏只排除"工作区内"。`-Target D:\`、`-Target C:\Users\<用户>\Documents`、或任何已有数据目录一律放行；删除动作没有 `-WhatIf`/`-Confirm`/`ShouldProcess`、不检查是否为驱动器根、不检查目录是否为空、不检查重解析点；`-ErrorAction SilentlyContinue` 还会把删除失败静默吞掉，让脚本继续向"没删干净"的目录发布（新旧 DLL 混合）。
- **为什么是问题**：脚本注释说明它的用途是"一键把 WinToolBox 工具发布到工作区之外的可运行目录"（L2-L9），即它被设计为**可以指向用户自己的目录**。一旦指向一个装有手写配置或旧版本的目录，整个目录树被强制删除；指向驱动器根则递归清空整盘。`$ErrorActionPreference='Stop'` 无法拦住这一行，因为该行被显式覆盖为 `SilentlyContinue`。
- **影响**：**数据丢失**。这是审计范围内唯一具备"一键数据销毁"能力的代码路径。
- **修复建议**：
  1. 加白名单/结构校验：拒绝驱动器根（`$targetFull -eq [IO.Path]::GetPathRoot($targetFull)`）、拒绝 `%SystemRoot%`/`%ProgramFiles%`/`%USERPROFILE%` 顶层；要求目标"不存在"或"带本脚本生成的标记文件（如 `.wintoolbox-publish`）"才允许清空。
  2. 用 `[CmdletBinding(SupportsShouldProcess)]` + `$PSCmdlet.ShouldProcess(...)`，并默认 `-Confirm`。
  3. L39 的 `-ErrorAction SilentlyContinue` 改为 `Stop`。
  4. 更安全的替代：发布到 `$targetFull` 下的时间戳子目录，不删除任何既有内容。
- **证据命令**：`grep -n "Remove-Item\|StartsWith\|GetFullPath\|ShouldProcess" publish-local.ps1`
- **交叉印证**：T5（P0）；Lead 已复核脚本全文。

---

### P1 高优先级问题

#### [P1-1] 复制/同步引擎完全不识别目录联接与符号链接（同仓库其他模块却都识别）

- **文件**：`src/WinToolBox.Core/FileCopier.cs`、`src/Tools/FileMaster/Services/FolderSyncService.cs`
- **位置**：`FileCopier.CollectFiles` L243-L304（入栈 L289-L299）；`IsSameOrChildPath` L307-L315；`FolderSyncService.EnumerateFiles/EnumerateDirectories` L341-L382、删除计划 L77-L117
- **现象**：`CollectFiles` 只按**名字**判断 `rules.IsExcludedDirectory(name)`，从不检查 `FileAttributes.ReparsePoint`：
  ```csharp
  subDirectories = Directory.EnumerateDirectories(current);   // L258
  ...
  foreach (var directory in subDirectories) { ... pending.Push(directory); }  // L289-L299
  ```
  `grep -n "ReparsePoint" src/WinToolBox.Core/FileCopier.cs` → **0 命中**，`FolderSyncService.cs` 同样 **0 命中**；而同仓库 `Core/Services/RuleGenerator.cs:246`、`L293`、`L297`、`FileMaster/Services/FolderChecker.cs:280`、`EmptyFolderCleanerService.cs:39`、`L201` 共 7 处**显式跳过** ReparsePoint。**同一个安全不变式在仓库内的实现并不一致——这正是"重复实现未提取"的直接代价。**
- **为什么是问题**：
  1. `D:\U盘\link -> C:\Users\me`：备份会把用户目录内容当作 U 盘内容复制进备份（污染备份并把隐私数据复制到备份盘）；
  2. `D:\U盘\loop -> D:\`：递归深度受 MAX_PATH 约束（本仓库未声明 `longPathAware`，超长路径抛异常并被 L261-L265 的按目录 catch 吞掉），因此实际后果是**深层级重复复制 + 树外内容混入 + 逐文件失败**，而非严格意义的无限递归/OOM；
  3. 镜像/同步模式下，目标树中的联接会被判为"源侧不存在的多余内容"，导致 `DeleteDirectory` 计划把**树外的真实文件**删除（`SyncModels.cs:54` 与 `FolderSyncDialog.cs:108` 允许关闭回收站 → 永久删除）；
  4. `IsSameOrChildPath`（L307-L315）是纯字符串前缀比较，检测不到联接造成的"自我复制"。
- **影响**：`UsbBackup` 备份内容越界、磁盘写满、进程 OOM；`FileMaster` 文件夹同步/镜像误删目标树外数据。
- **修复建议**：在 `CollectFiles` 入栈前加 `if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0) { logger?.Warn(...); continue; }`；`FolderSyncService` 同样处理，并把 `IsSameOrChildPath` 改为带分隔符补全的比较或 `Path.GetRelativePath` 判定；补单测"源目录含 Junction 时不复制其目标内容"。
- **证据命令**：`grep -rn "ReparsePoint" src/`（命中 RuleGenerator:246、EmptyFolderCleanerService:39、FolderChecker:280；FileCopier/FolderSyncService 均 0）

#### [P1-2] 托盘与通知器捕获的 UI 同步上下文恒为 null，"切回 UI 线程"实际不成立

- **文件**：`src/Tools/UsbBackup/TrayApplicationContext.cs`、`src/WinToolBox.Core/Notifier.cs`
- **位置**：`TrayApplicationContext.cs:42`（`_uiContext = SynchronizationContext.Current;`）、L55（`new Notifier(...)`）、`PostToUi` L285-L302（回退分支 L296）、`CompleteBackupRequest` L234-L243；`Notifier.cs:34/L43`
- **现象**：构造函数在 L42 捕获同步上下文，L55 构造 `Notifier`（其内部同样捕获一次），而本进程**第一个 `Control`** 直到 L84 `new MainForm(...)` 才创建。WinForms 的 `WindowsFormsSynchronizationContext` 只在创建第一个 `Control` 时安装，因此两处捕获值都是 `null`，`PostToUi` 永远走 L296 的 `action()` 内联分支。
- **为什么是问题**：备份完成后的收尾（`_backupMenuItem.Enabled = true`、气泡通知）在**线程池线程**上直接操作 WinForms 控件——`NotifyIcon` 及其 `ContextMenuStrip` 的创建线程是 UI 线程，而跨线程访问 WinForms 控件在无调试器时 `CheckForIllegalCrossThreadCalls` 为 `false`（不抛异常，但属 WinForms 明确不支持的访问，与 `NotifyIcon` 内部窗口消息、菜单状态构成数据竞争）；一旦附加调试器该值为 `true`，L243 的赋值会抛 `InvalidOperationException`，被本方法 catch（L277-L281）后**重试同一赋值并再次抛出**，最终由 `PostToUi` 的 catch（L298-L301）吞掉。
- **影响**：① 正常状态下用户仍能收到气泡，但 UI 状态更新与 `NotifyIcon` 内部状态在后台线程上竞争，属latent 并发缺陷（表现为偶发的气泡不显示/菜单状态不一致）；② 附加调试器或 `CheckForIllegalCrossThreadCalls=true` 时，收尾流程会在 L243 中断，**本轮"备份完成/失败"的气泡全部不显示**（`_backupRequestRunning` 已在 L236-L239 复位、菜单也在 L280 的重试中恢复，因此托盘入口不会永久失效——这一点经复核修正，见 §七"对抗式验证发现并已修正的问题"）。
- **修复建议**：三选一保持一致——① L42 之前显式 `SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext())`；② 把 `_uiContext`/`Notifier` 延迟到 `_mainForm` 构造之后创建（或改用 `Lazy<T>`/`EnsureUiContext()`）；③ `PostToUi` 改为 `_mainForm`（或专用隐藏 `Control`）的 `BeginInvoke`，并在 `InvokeRequired == false` 时直接执行。同时把 `PostToUi` 的 catch 从"仅记日志"改为降级提示，避免静默丢回调。
- **证据命令**：`grep -n "_uiContext\|PostToUi\|SynchronizationContext.Current" src/Tools/UsbBackup/TrayApplicationContext.cs src/WinToolBox.Core/Notifier.cs`

#### [P1-3] `FileCopier` 预扫描把惰性枚举包在 try 内，一个无权限目录会让整个备份 0 文件复制

- **文件**：`src/WinToolBox.Core/FileCopier.cs`
- **位置**：`CollectFiles` L243-L304，关键 L253-L267 与 L289；消费点 `src/Tools/UsbBackup/BackupService.cs` L108-L114、L204-L209
- **现象**：
  ```csharp
  try {
      subDirectories = Directory.EnumerateDirectories(current);   // 只拿到迭代器，不执行 I/O
      files = Directory.EnumerateFiles(current);
  } catch (Exception ex) {
      logger?.Warn($"读取目录失败，已跳过：{current}", ex);        // 实际上永远进不来
      continue;
  }
  foreach (var file in files) { ... }        // ← 真正的枚举发生在这里，已在 try 之外
  ```
- **为什么是问题**：`Directory.EnumerateFiles/EnumerateDirectories` 返回惰性 `IEnumerable`，目录枚举与异常都发生在第一次 `MoveNext()`，即 L267/L289 的 `foreach`——**已在 `catch` 作用域之外**，异常直接穿透 `CollectFiles` → `CopyDirectory` → `BackupService` 的 `catch(Exception)`。作者在 `Core/Services/RuleGenerator.cs` L229-L237 用的是立即执行的 `Directory.GetDirectories`，说明知道正确写法。
- **影响**：预扫描在复制任何文件之前执行，因此 U 盘里只要存在**一个** ACL 受限/损坏/被独占的目录，用户得到"备份失败"，**本次备份 0 个文件被复制**。U 盘越"脏"越容易命中，而这正是备份工具最需要容错的场景；"跳过并继续"的承诺完全失效。
- **修复建议**：改为物化 + 缩小 try 范围（`Directory.GetDirectories`/`GetFiles` 包在 `try` 内并 `catch` 具体异常类型），或把两个 `foreach` 整体移入 `try`；补一条"存在不可访问目录时其余文件仍被复制"的单测（当前 `FileCopierTests.cs` 无此用例）。
- **证据命令**：`grep -n "EnumerateDirectories\|EnumerateFiles\|foreach (var file in files)" src/WinToolBox.Core/FileCopier.cs`

#### [P1-4] 两个工具共用同一日志文件、用 `FileShare.Read` 并发写，失败被静默吞掉且无人读取

- **文件**：`src/WinToolBox.Core/AppPaths.cs:57-L58`、`src/WinToolBox.Core/Logger.cs:88-L99`、L111-L158（关键 L133、L136-L140）
- **位置**：`AppPaths.LogDirectory`（无工具名）；`Logger.Write` L128-L141；`Logger.LastError` L91
- **现象**：
  ```csharp
  // AppPaths.cs L58：产品级唯一目录，不含工具名
  public static string LogDirectory => Path.Combine(LocalAppDataRoot, "logs");
  // Logger.cs L99：文件名同样不含工具名
  public static string BuildFileName(DateTime date) => $"{FileNamePrefix}{date:yyyyMMdd}.log";
  // Logger.cs L133-L140
  File.AppendAllText(CurrentLogFilePath, fileText + Environment.NewLine, new UTF8Encoding(false));
  ...
  catch (Exception ex) { LastError = ex; }     // 只存属性，无重试、无对外信号
  ```
  两个工具都取同一单例：`FileMaster/Program.cs:49`、`UsbBackup/Program.cs:35`、`UsbBackup/TrayApplicationContext.cs:39`。
- **为什么是问题**：`File.AppendAllText` 内部以 `FileMode.Append, FileAccess.Write, FileShare.Read` 打开。Lead 实测（4 个进程并发向同一文件 `File.AppendAllText` 5 秒）：**成功 9,911 次 / 失败 2,889 次 = 失败率 22.57%**，异常为 `IOException`「文件正由另一进程使用」；写入本身是原子的（成功次数 == 文件行数，未发现半行/损坏），但失败被 L136-L140 吞进 `LastError`，而 `grep LastError src/` 显示生产代码**从未读取**该属性（仅 `Logger` 自身赋值 + 测试读取）。
- **影响**：UsbBackup 是常驻托盘程序、FileMaster 是随手打开的工具，"同时运行"是默认场景 → 任一方在该毫秒内写日志会静默丢失。日志是这两个工具**唯一的审计轨迹**（"备份是否执行过""某次失败原因"），且失败/错误日志写入最频繁、冲突概率最高。用户与工具都无法察觉。
- **修复建议**：
  1. 日志文件名带工具名：`AppPaths` 增加 `LogDirectoryFor(string toolName)`，`Logger` 构造接收工具名（默认值保持兼容）。
  2. 写入改为 `new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096)` 并对 `IOException` 做 3 次 × 50ms 退避重试。
  3. `Logger` 增加 `HasWriteFailure` 对外信号，或让 `Program.cs` 退出时提示"本次运行有 N 条日志未能写入"。
- **证据命令**：`grep -rn "LastError" src/`（仅 `Logger.cs` 自身）；实测数据见 §七。

#### [P1-5] `TemplateManager` 保存/删除模板失败被吞掉，UI 仍提示"模板已保存"

- **文件**：`src/WinToolBox.Core/Services/TemplateManager.cs` L212-L227、L356-L379；消费点 `src/Tools/FileMaster/MainForm.cs` L363-L366
- **现象**：
  ```csharp
  // TemplateManager.cs：Persist() 内部吞异常
  224    Persist();
  226    return isNew;                 // 只告诉调用方"新建/覆盖"，不告诉是否落盘成功
  ...
  364    try { File.WriteAllText(tempFile, json, ...); File.Move(tempFile, FilePath, overwrite: true); }
  374    catch (Exception ex) { _logger?.Warn($"保存模板失败：{FilePath}", ex); }
  // MainForm.cs
  365    SetStatus($"模板已保存：{name}（{(isNew ? "新建" : "覆盖")}）");
  ```
- **为什么是问题**：`Persist` 是"尽力而为"实现，异常只进日志；`SaveTemplate` 的返回语义是"新建/覆盖"。`MainForm` 的 `catch`（L368-L372）永远不会触发，因为异常根本没抛出——Core 单方面关掉了错误提示路径。与同库 `ConfigManager.Save`（失败直接抛）契约相反。
- **影响**：`%AppData%` 卷满、被安全软件拦截、`.tmp` 被上次崩溃实例占用时，用户点"保存为模板"→ 状态栏显示"模板已保存"→ 下次启动模板消失。用户编写的规则文本丢失且无任何提示。`DeleteTemplate` 同理（报"已删除"，重启后又出现）。
- **修复建议**：`Persist()` 返回 `bool`（或 `out string? error`），`SaveTemplate`/`DeleteTemplate`/`EnsureDefaults` 把写盘结果暴露给调用方；最简做法是让 `Persist` 直接抛 `IOException`，由 `MainForm` 现有 `catch` 显示失败。
- **证据命令**：`grep -n "Persist()\|return isNew" src/WinToolBox.Core/Services/TemplateManager.cs`

#### [P1-6] 退出流程既不取消也不等待正在运行的备份，进程带着截断的目标文件结束

- **文件**：`src/Tools/UsbBackup/TrayApplicationContext.cs`、`src/WinToolBox.Core/FileCopier.cs`
- **位置**：`ExitApplication` L340-L377（`ExitThread()` L376）、`StartBackupTask` L194-L231（`_ = Task.Run(...)` L211、L219 未传 token）；`FileCopier.StreamCopy` L212-L218（`FileMode.Create`）
- **现象**：`StartBackupTask` 用 `_ = Task.Run(...)` 起一个**不持有句柄、不可取消**的任务；`ExitApplication` 依次关主界面、`Dispose` 托盘图标、停止 watcher，最后 `ExitThread()` 让 `Main` 返回 0，全程没有"是否正在备份"判断、没有 `CancellationTokenSource`、没有等待。
- **为什么是问题**：`FileCopier.StreamCopy` 对目标文件使用 `FileMode.Create`（**先截断再逐块写**）。线程池线程在进程退出时不会被执行完毕，用户在大文件复制途中点「退出程序」（确认框只问"确定要退出吗"，不提示备份正在进行）→ 目标目录留下一个长度不完整的文件，且**没有任何标记**说明它不完整；日志最后一条仍是"备份开始"。
- **影响**：备份目标出现被截断的文件；在"备份已成功过"的错觉下用户可能删除 U 盘原件。
- **修复建议**：`BackupService` 暴露 `CancellationTokenSource`（每次备份创建并保存，`BackupDevice` 传递该 token）；`ExitApplication` 第一步 `cts.Cancel()` 并 `task.Wait(TimeSpan.FromSeconds(5))`，超时提示"备份仍在进行，是否强制退出"；`StartBackupTask` 保存 `Task` 句柄；退出确认文案在 `_backupRunning` 时改为"正在备份，退出将中断备份"。
- **证据命令**：`grep -n "Task.Run\|ExitThread" src/Tools/UsbBackup/TrayApplicationContext.cs`；`grep -n "FileMode.Create" src/WinToolBox.Core/FileCopier.cs`

#### [P1-7] 取消令牌根本没有传到备份 I/O

- **文件**：`src/Tools/UsbBackup/TrayApplicationContext.cs`、`src/Tools/UsbBackup/BackupService.cs`、`src/Tools/UsbBackup/MainForm.cs`
- **位置**：`TrayApplicationContext.cs:219`（`BackupAllAttached()` 无参数）、`BackupService.BackupAllAttached` L62-L92、`BackupDevice` L96-L130；`MainForm.cs` 备份按钮调用点
- **现象**：所有调用点传入 `CancellationToken.None` 或使用默认值；`BackupService` 内的 `SemaphoreSlim` 只用于"同时只允许一次备份"，不承载取消。UI 上不存在任何取消入口。
- **为什么是问题**：`FileCopier.StreamCopy` L221-L225 **已经**正确实现了 `cancellationToken.ThrowIfCancellationRequested()`（按缓冲区粒度），`CopyModels` 也有进度模型——底层能力齐备，但上层从未把 token 传下去。于是取消链路是**死代码**：长备份一旦开始只能靠杀进程中止（并命中 P1-6 的截断问题）。
- **影响**：用户误点"立即备份"后无法中止；大 U 盘全量备份期间无法退出或切换设备。
- **修复建议**：`BackupService` 持有每次任务的 `CancellationTokenSource`，`BackupAllAttached`/`BackupDevice` 显式接收 token 并透传到 `FileCopier`；主界面与托盘菜单增加"取消备份"入口（备份中启用）。
- **证据命令**：`grep -rn "CancellationToken.None\|BackupAllAttached(" src/Tools/UsbBackup/`

#### [P1-8] "增量备份"在跨天时退化为全量，且没有保留策略

- **文件**：`src/Tools/UsbBackup/BackupService.cs`、`src/WinToolBox.Core/BackupRules.cs`
- **位置**：`BackupService` L191（目标目录含 `{yyyy-MM-dd}`）、L204-L209；`FileCopier.Decide` L175-L198
- **现象**：每次备份的目标目录按当天日期生成，而增量跳过判定只在**当天目录内**比较。跨天后目标目录为空，所有文件都被判为"新增"并全量重拷；仓库内没有任何清理/保留旧日期目录的逻辑。
- **为什么是问题**：README 与界面宣称"增量备份"，实际效果是"每天一份整盘副本"，且旧副本无限累积。按 U 盘 64 GB、保留 30 天计算，磁盘需求是单份的 30 倍——用户会在某一天突然遇到"目标空间不足"。
- **影响**：UsbBackup 的核心卖点与实现不符；磁盘被快速吃满；备份耗时远超预期。
- **修复建议**：二选一——① 目标目录固定（不带日期），改为在目录内做真正的增量 + 定期快照（`BackupRules` 增加保留策略：保留最近 N 份 / 按周月轮转）；② 保留日期目录但明确文档化为"每日全量快照"，并实现保留策略（删除超过 N 天的日期目录，删除失败要提示）。同时在 README 与界面文案中把"增量"改为与实现一致的表述。
- **证据命令**：`grep -n "yyyy-MM-dd" src/Tools/UsbBackup/BackupService.cs`

#### [P1-9] 目标空间不足无预检、失败不早停、失败明细无上限累积

- **文件**：`src/Tools/UsbBackup/BackupService.cs`
- **位置**：L191-L245（备份主循环）
- **现象**：备份开始前不检查目标卷剩余空间；单个文件失败只记入失败列表并继续；失败列表无上限（`List` 持续增长），且整轮结束后才汇总提示。
- **为什么是问题**：与 P1-8 叠加后，"磁盘写满"是可预期的常态而非异常。空间不足时每一个文件都会失败一次，浪费大量 I/O 与时间；失败明细在整盘规模下可达数十万条，占用内存并使汇总提示失去可读性。
- **影响**：备份长时间"空转"；用户等到最后才看到"失败 N 个"；极端情况内存膨胀。
- **修复建议**：备份前用 `DriveInfo.AvailableFreeSpace` 与待复制总字节比对（`CopyPlan` 已能算出总量），不足时立即失败并给出"需要 X / 可用 Y"；连续失败超过阈值（如 20 个）时中止并提示可能原因；失败明细限制条数（保留前 N 条 + 总数）。
- **证据命令**：`grep -n "AvailableFreeSpace\|FailedFiles\|Errors.Add" src/Tools/UsbBackup/BackupService.cs src/WinToolBox.Core/FileCopier.cs`

#### [P1-10] 文件占用查询静默截断到 256 个文件，摘要却断言"没有被任何进程占用"

- **文件**：`src/Tools/FileMaster/Services/FileUnlockerService.cs`、`src/Tools/FileMaster/Models/UnlockModels.cs`、`src/Tools/FileMaster/UI/FileUnlockerDialog.cs`
- **位置**：`MaxFilesPerQuery` L22；`ExpandDirectoryFiles` L240-L263（截断点 L250-L254）；`FileLockQueryResult.Summary` L111-L113；`FileUnlockerDialog.FillResult` L254-L256
- **现象**：
  ```csharp
  foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
      files.Add(file);
      if (files.Count >= MaxFilesPerQuery) { _logger?.Warn($"目录内文件过多，仅检查前 {MaxFilesPerQuery} 个文件：{directory}"); break; }
  }
  // UnlockModels L112：LockCount == 0 时没有任何限定词
  return LockCount == 0 ? "没有被任何进程占用。" : ...;
  ```
  截断只写日志，不改变结果模型；UI 直接显示该摘要与"占用进程 0 个"。
- **为什么是问题**：Restart Manager 只接受文件路径，目录查询本就是抽样，但抽样给出了**全称结论**。用户据此去删除/重命名文件仍会失败，会认为工具在撒谎。前 256 个文件的顺序还依赖目录遍历顺序，结果不稳定。
- **影响**：FileMaster 文件占用解锁 → 误报"未占用"，把排查引向错误方向；这是同类工具最容易损伤信任的点。
- **修复建议**：`FileUnlockQueryResult` 增加 `Truncated`/`TotalFileCount`；`Summary` 在截断时改为"已检查前 256 个文件，未发现占用（该目录文件更多，结果不完整）"；UI 在 `Truncated` 时用警告色显示"已检查 256 / 共 N 个文件"。治本方案是提高上限并在超限时**明确失败**而非静默截断。
- **证据命令**：`grep -n "MaxFilesPerQuery" src/Tools/FileMaster/Services/FileUnlockerService.cs`；`grep -n "没有被任何进程占用" src/Tools/FileMaster/Models/UnlockModels.cs`

#### [P1-11] 重复文件查找不校验扫描目录的嵌套/等价关系

- **文件**：`src/Tools/FileMaster/Services/DuplicateFinderService.cs`、`src/Tools/FileMaster/UI/DuplicateFinderDialog.cs`
- **位置**：`Find` L29-L33（目录级去重）、L46-L96（按目录逐个全量收集，文件级不去重）、L129-L148（按 `(Length, Hash)` 分组）；对话框 `TryGetScanDirectories` L223-L268
- **现象**：去重只发生在目录字符串层面（`Distinct(OrdinalIgnoreCase)`），不处理嵌套（`D:\A` 与 `D:\A\B`）与尾随分隔符（`D:\A` vs `D:\A\`）等价情形；同一文件被收集两次后，同 `(Length, Hash)` 且同 `FullName` → 成为"重复组"。
- **为什么是问题**：① `DuplicateGroup.WastedBytes`（`Models/DuplicateModels.cs:41`）与 `Summary` 会**报告并不存在的重复与可回收空间**；② 更危险的是"路径字符串不同、实为同一文件"的情形（映射盘/UNC/目录联接/硬链接）：两行 `FullName` 不同，只有一行被判"保留"，另一行状态为"重复"且**默认勾选**，用户点「删除选中」会删掉唯一副本（走回收站可恢复，但用户以为还有一份）。
- **影响**：结果可信度（组数、可回收空间）虚高；在映射盘/联接场景提供"删除唯一副本"的默认路径。**需要澄清的一点**（复核修正）：存在"路径相同"的自重复组时，对话框会让**两行都判为 `isKeep`、默认都不勾选**，「全选重复项」也会跳过它们，因此不会一键误删；真正的风险是**映射盘/UNC/联接导致路径字符串不同**的情形——那时只有一行被判"保留"，另一行默认勾选。同时确认框已包含可回收空间与"删除后这一组将不再保留任何文件"的警示语。
- **修复建议**：对**文件**做规范化去重（`Path.GetFullPath` + 去尾随分隔符 + `OrdinalIgnoreCase`）；对扫描目录做"若 A 是 B 的父路径则只保留 A"的归一化；更强可用 `File.OpenHandle` + `GetFileInformationByHandle` 的卷序列号/文件索引识别同一文件；对话框在添加目录时提示已自动合并。
- **证据命令**：`grep -n "Distinct(StringComparer.OrdinalIgnoreCase)\|WastedBytes" src/Tools/FileMaster/Services/DuplicateFinderService.cs src/Tools/FileMaster/Models/DuplicateModels.cs`

#### [P1-12] 自动分类的目标子目录来自自由文本规则，路径未规范化校验 → 可写到目标目录之外

- **文件**：`src/Tools/FileMaster/Services/AutoArchiverService.cs`
- **位置**：规则解析与目标路径拼装 L313 附近；对照 `src/WinToolBox.Core/Services/FolderCreatorService.cs`（同类场景有校验）
- **现象**：分类规则的第三列（目标子目录）完全无校验，直接参与 `Path.Combine(targetRoot, ruleValue)`。`RuleParser`（L27、L204-L229）对文件夹结构规则已拦截 `\ / : ..`，但 AutoArchiver 走的是自己的规则解析路径，未复用该校验。
- **为什么是问题**：`..\..\Windows\Temp` 之类的规则值会让文件被移动/复制到目标根目录之外；绝对路径（`D:\elsewhere`）会**覆盖 `Path.Combine` 的前缀**（`Path.Combine("C:\a", "D:\b")` 返回 `D:\b`），直接把用户文件搬离预期范围。
- **影响**：FileMaster 自动归档/批量移动 → 文件被移动到非预期位置（可能覆盖同名文件），用户难以定位；属安全与数据完整性缺陷。
- **修复建议**：目标子目录先经 `Path.GetFullPath(Path.Combine(targetRoot, value))` 规范化，再用"带分隔符补全的前缀比较"或 `Path.GetRelativePath` 断言其仍在 `targetRoot` 之内，否则记为规则错误并在预览中拒绝该条；非法字符复用 `RuleParser` 的校验逻辑。
- **证据命令**：`grep -n "Path.Combine" src/Tools/FileMaster/Services/AutoArchiverService.cs`

#### [P1-13] 同步（镜像）在复制前先删，"删除成功而复制失败"会留下比同步前更少的目标

- **文件**：`src/Tools/FileMaster/Services/FolderSyncService.cs`
- **位置**：`Apply` L178-L240（删除计划先执行）、`BuildPlan` L27-L117
- **现象**：计划项按"先删除（多余文件/目录）→ 再复制/更新"的顺序执行；任一复制失败只记入结果，不回滚已执行的删除。
- **为什么是问题**：镜像模式的语义是"目标 == 源"。先删后拷在这条路径上等价于"先把目标削到比源更少，再尝试补齐"；复制阶段失败（权限、占用、磁盘满、设备掉线）会留下一个**既不是旧状态也不是新状态**的目标，且用户看到的是"N 个失败"。
- **影响**：FileMaster 文件夹同步/镜像 → 目标数据在失败后处于更差的状态；若源侧文件同时不可用，可能造成净数据丢失。
- **修复建议**：改为"先复制/更新，全部成功后再执行删除"，或在删除前把待删项移入本工具管理的暂存目录（`.wintoolbox-trash`）并在整轮成功后清理、失败时回滚；至少要在同一计划项内保证"删除某文件"之前其替代副本已就位。
- **证据命令**：`grep -n "SyncActionKind.Delete\|foreach (var item in plan" src/Tools/FileMaster/Services/FolderSyncService.cs`

#### [P1-14] 同步的"删除多余目录"绕过忽略名单

- **文件**：`src/Tools/FileMaster/Services/FolderSyncService.cs`、`src/Tools/FileMaster/Services/SafeDelete.cs`
- **位置**：`BuildPlan` L77-L117（删除目录计划 L101-L114）、`EnumerateFiles` L341-L360（忽略名单只在文件枚举生效）、`SafeDelete.DeleteDirectory` L34-L50
- **现象**：`DeleteDirectory` 计划只判断"该相对目录是否存在于源侧"，从不检查目录内部是否含被忽略的内容；而 `EnumerateFiles` 对命中忽略名单的文件直接 `continue`，这些文件既不出现在 `targetFiles`，也就不产生 `DeleteFile` 计划项。
- **为什么是问题**：忽略名单（`.git`/`node_modules`）在"逐文件删除"路径生效，在"删除整个目录"路径**完全失效**。源侧已删除目录 `X` 时，`DeleteDirectory(X)` 会把 `X\.git\...` 一并递归删除（`useRecycleBin=false` 时永久）。用户以为"我忽略了 .git，工具不会动它"。
- **影响**：FileMaster 单向同步/镜像 → 忽略名单被绕过，目标侧被忽略目录内的数据被删除。
- **修复建议**：生成 `DeleteDirectory` 前先判定该目录子树内是否存在被忽略条目（让枚举额外返回 `ignoredRelativePaths`，或按目录删除前用 `Directory.EnumerateFileSystemEntries` 复核）；存在则**放弃整目录删除**，退化为逐文件删除，并在计划 `Reason` 中说明。
- **证据命令**：`grep -n "DeleteDirectory\|IsExcluded" src/Tools/FileMaster/Services/FolderSyncService.cs`

#### [P1-15] 空目录清理：扫描与删除之间不复检"是否仍为空"

- **文件**：`src/Tools/FileMaster/Services/EmptyFolderCleanerService.cs`
- **位置**：`Delete` L107-L161（判定 L128-L140），`Scan` L66
- **现象**：`Delete` 阶段唯一检查是 `Directory.Exists`。详见 **P0-2**——同一根因；P0-2 是从"永久删除后果"角度升级定级，此处从"竞态机制"角度单列以便修复时两处都改。
- **修复建议**：见 P0-2 的修复建议第 1、2 条。

#### [P1-16] 无 tag 与 `<Version>` 的一致性校验，任意 `v*` 标签都会直接发布

- **文件**：`.github/workflows/release.yml`、`Directory.Build.props`
- **位置**：`release.yml` L5-L8（触发 `tags: ['v*']`）、L47-L51、L102-L109；`Directory.Build.props:6`（`<Version>0.4.0</Version>`）
- **现象**：工作流由任意 `v*` 标签触发，直接使用仓库内写死的 `<Version>0.4.0</Version>` 构建并创建 Release（`name: WinToolBox ${{ github.ref_name }}`），全程没有任何 "标签版本 == 程序集版本" 的校验。
- **为什么是问题**：推送 `v0.9.0` 标签时，Release 标题写 `v0.9.0`，而 exe 的 `FileVersion`/`InformationalVersion` 仍是 `0.4.0`；`--version` 输出、Windows 文件属性、崩溃报告全部与实际发布版本不符。发布产物的可追溯性被破坏，且这个错误**不会被任何步骤发现**。
- **影响**：发布链路可信度；用户报障时版本号对不上；回滚/审计困难。
- **修复建议**：在 `release.yml` 的 restore 之前加一步校验：
  ```powershell
  [xml]$props = Get-Content Directory.Build.props
  $v = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
  if ("v$v" -ne "${{ github.ref_name }}") { throw "标签 ${{ github.ref_name }} 与 Directory.Build.props 的版本 $v 不一致" }
  ```
  或改用 `-p:Version=${{ github.ref_name }}`（去 `v` 前缀）让标签成为唯一版本源。
- **证据命令**：`grep -n "tags:\|Version\|ref_name" .github/workflows/release.yml Directory.Build.props`

#### [P1-17] `README_EN.md` 整份停留在 `FolderCreator` 时代（英文用户按文档必然失败）

- **文件**：`README_EN.md`
- **位置**：L20（工具表仍写 `FolderCreator`，链接指向 `src/Tools/FolderCreator/README_EN.md`）、L35（`FolderCreator-win-x64.zip`）、L53（`dotnet run --project src/Tools/FolderCreator/FolderCreator.csproj`）、L72（结构树 `FolderCreator/`）、L75（`FolderCreator.Tests/`）、L93/L96（文档索引）
- **现象**：中文 `README.md` 已在 v0.4.0 全面更新为 FileMaster，英文版**整份未同步**：工具名、目录路径、发布包名、`dotnet run` 命令、测试项目名、文档链接全部过期。
- **为什么是问题**：`src/Tools/FolderCreator/` 在磁盘上**不存在**（已重命名为 `src/Tools/FileMaster/`）。英文用户按 README 下载 `FolderCreator-win-x64.zip`（不存在，实际为 `FileMaster-win-x64.zip`）或执行 `dotnet run --project src/Tools/FolderCreator/...`（必然报错）都会失败。Lead 实测全仓库有 4 条 markdown 相对链接指向不存在的路径，其中 2 条来自 `README_EN.md`。
- **影响**：英文用户无法按文档使用或构建；仓库对外形象受损。
- **修复建议**：把 `README_EN.md` 与 `README.md` 对齐重写（工具名 FileMaster、路径 `src/Tools/FileMaster/`、包名 `FileMaster-win-x64.zip`、测试项目 `FileMaster.Tests`、文档索引）；把"双语文档同步"加入发布检查清单。
- **证据命令**：`grep -n "FolderCreator" README_EN.md`；markdown 链接存在性校验见 §七。

#### [P1-18] 进程完整性检查 `fail-open`：读不到完整性级别时按"正常"处理

- **文件**：`src/Tools/UsbBackup/ProcessIntegrity.cs`、`src/Tools/UsbBackup/Program.cs`
- **位置**：`ProcessIntegrity.cs` L56-L102（P/Invoke L112-L128）；`Program.cs:103`（`WarnIfRestrictedContext`）
- **现象**：`GetTokenInformation` 等调用失败（或被 `catch` 兜住）时，`IsRestricted` 返回 `false`，即"未受限"。
- **为什么是问题**：这个检查存在的**唯一目的**是提示用户"当前进程处于低完整性/受限上下文，托盘图标可能不显示"（`publish-local.ps1` 的注释也说明这是本仓库的已知坑）。`fail-open` 恰好漏掉它要提示的场景：用户遇到"进程在跑但托盘没图标"时得不到任何提示，只能自己猜。
- **影响**：UsbBackup 最常见的环境类问题失去唯一的诊断线索。
- **修复建议**：把"查询失败"与"确认正常"区分开（如 `enum IntegrityCheckResult { Normal, Restricted, Unknown }`），`Unknown` 时输出"无法确定完整性级别（原因），若托盘图标不显示请…"的提示；`P/Invoke` 声明了 `SetLastError = true` 却从不读 `Marshal.GetLastWin32Error()`，应把错误码写入日志。
- **证据命令**：`grep -n "SetLastError\|IsRestricted\|GetLastWin32Error" src/Tools/UsbBackup/ProcessIntegrity.cs`

#### [P1-19] 自动分类不校验"目标目录位于源目录内"，重复运行会逐层自我归档

- **文件**：`src/Tools/FileMaster/Services/AutoArchiverService.cs`
- **位置**：扫描 L280（`Directory.EnumerateFiles(options.SourceDirectory, "*", searchOption)`）；唯一守卫 L319-L323；目标路径拼装 L313
- **现象**：允许把分类目标目录设在源目录内部（例如源 `D:\Data`、目标 `D:\Data\Sorted`）。第一轮运行把文件移入 `D:\Data\Sorted`；第二轮扫描 `D:\Data` 时（`searchOption` 含子目录）会再次遇到这些文件，`targetPath` 变成 `D:\Data\Sorted\Sorted\...`，按规则再下沉一层。
- **为什么是问题**：代码里唯一的守卫是 L319-L323 的 `Path.GetFullPath(targetPath) == Path.GetFullPath(file)` → `canApply = false`，它只能识别"**该文件本身已经在目标路径上**"（即文件已在目标目录内的情形）；当目标目录嵌套在源目录内时，上一轮归档后的文件其 `file` 与 `targetPath` 并不相等，守卫不触发，于是形成"每一轮都把上一轮结果再归类一次"的自我纠缠。用户观察到的现象是"目录越跑越深、文件位置反复变化"。
- **影响**：FileMaster 自动归档 → 目录结构被逐层套娃，用户难以恢复原有组织；配合"移动后清理空目录"（P0-2）还会反复删除/重建空目录。
- **修复建议**：`BuildPlan` 中拒绝"目标根位于源根内"或"源根位于目标根内"的组合（用带分隔符补全的前缀比较，不要用裸 `StartsWith`，可复用 `FileCopier.IsSameOrChildPath` 的思路），给出明确错误；若产品确实需要允许嵌套，则在扫描时排除目标子树（`L280` 的枚举结果过滤掉 `targetRoot` 之下的路径），并把 L319 的守卫扩展为"目标位于目标根之下且不等于源文件"的完整判定。
- **证据命令**：`grep -n "EnumerateFiles\|GetFullPath\|SourceDirectory\|TargetDirectory" src/Tools/FileMaster/Services/AutoArchiverService.cs`（命中 L259、L280、L313、L319）

#### [P1-20] 备份预扫描把整盘文件清单物化进内存，复制开始前长时间无进度

- **文件**：`src/WinToolBox.Core/FileCopier.cs`、`src/Tools/UsbBackup/BackupService.cs`
- **位置**：`FileCopier.CollectFiles` L243-L304（`List<FileEntry> results`）、`CopyDirectory` L80-L123（预扫描先于复制）；`BackupService` L191-L245
- **现象**：`CollectFiles` 返回完整 `List<FileEntry>`，每个条目持有 `FullPath`/`RelativePath`/`Length`；大 U 盘（数十万文件）下这是"全量元数据入内存 + 复制前零进度"。
- **为什么是问题**：① 内存占用与文件数线性增长（数十万条目 × 路径字符串，可达数十 MB）；② 预扫描阶段 UI 显示"正在扫描"，进度条不动，用户容易误判卡死并强杀进程（→ 命中 P1-6 的截断问题）。
- **影响**：大容量 U 盘首次备份体验差、内存峰值高。
- **修复建议**：改为惰性遍历（`EnumerateFiles` + `yield return`，或分批处理），让复制与遍历交替进行；预扫描阶段也上报"已发现 N 个文件"的进度；若要保留总量估算，先做一次轻量的 `EnumerateFiles` 计数（只计数不存路径）。
- **证据命令**：`grep -n "List<FileEntry>\|results.Add" src/WinToolBox.Core/FileCopier.cs`

---

### P2 中优先级问题

#### [P2-1] Core 名义共享、实质分裂：30 个公共类型中只有 4 个被两个工具共同引用

- **文件**：`src/WinToolBox.Core/**`
- **位置**：典型锚点 `ConfigManager.cs:24`（写死 `AppPaths.UsbBackupConfigFile`）、`AppPaths.cs:15-L18`、`Core/Services/*`；公共类型计数命令见 §3.3
- **现象**：`grep` 实测 Core 共有 **30 个公共类型**，两工具真正共用的只有 `AppPaths`、`Logger`/`LogEntry`/`LogLevel`、`INotifier`（`INotifier` 在 FileMaster 仅作为恒 `null` 的构造参数出现，属名义共享）；`HashEngine` 与 `Services/*`（FolderCreatorService/RuleGenerator/TemplateManager/FolderCheckReport/FolderCreateModels）仅 FileMaster 使用；`ConfigManager`/`Notifier`/`UsbDetector`/`UsbDeviceInfo`/`FileCopier`/`CopyModels`/`BackupConfig`/`BackupRules`/`ExcludeRules` 仅 UsbBackup 使用（`ExcludeRules` 在 UsbBackup 侧不由名字直接引用，而是经 `BackupRules.CreateDefaultExcludeRules()` 创建后传给 `FileCopier`）。**26 个类型只有单一消费者。**
- **为什么是问题**："提取到 Core"的收益（复用、单一修改点）在这 20 余个类型上不存在，只剩成本：Core 的每次改动都要评估两个不相关工具；`ConfigManager`/`Notifier` 这类通用名 + 通用命名空间会误导下一个工具作者——沿用 `ConfigManager` 会拿到 `%AppData%\WinToolBox\UsbBackup\config.json`，沿用 `Notifier` 会拿到托盘气泡。
- **影响**：维护成本随工具数量线性增长而收益为零；新工具极易误用 UsbBackup 专属类型。
- **修复建议**：**物理拆分**为 `WinToolBox.Core`（真共享：`Logger`/`AppPaths`/`HashEngine`/通用文件比较与路径校验）+ `WinToolBox.Tools.UsbBackup.Core` + `WinToolBox.Tools.FileMaster.Core`，测试工程分别引用；若暂不拆分，至少在 Core README 与每个类型上标注"仅 XX 使用"，并把 `ConfigManager` 重命名为 `UsbBackupConfigStore`。
- **证据命令**：`grep -rn "HashEngine\|FileCopier\|ConfigManager\|Notifier\|UsbDetector\|TemplateManager\|FolderCreatorService" src/Tools/FileMaster src/Tools/UsbBackup`

#### [P2-2] Core 公共 API 泄漏 WinForms 类型，把 Core 与全部消费者绑死在 Windows Desktop

- **文件**：`src/WinToolBox.Core/WinToolBox.Core.csproj:5`、`src/WinToolBox.Core/Notifier.cs`
- **位置**：`Notifier.cs:1-L2`（using）、L27（`NotifyIcon` 字段）、L39-L45（**公共构造函数**参数 `NotifyIcon`）、L52（公共方法参数 `ToolTipIcon`）、L95-L110（`new NotifyIcon`）
- **现象**：一个"共享类库"设置了 `<UseWindowsForms>true</UseWindowsForms>`，理由是 `Notifier` 需要 `NotifyIcon`；该类型把 WinForms 控件放进**公共 API**。
- **为什么是问题**：`UseWindowsForms` 向所有消费者传递 `Microsoft.WindowsDesktop.App.WindowsForms` 框架引用：`tests/WinToolBox.Core.Tests.csproj:4-L5` 必须写 `net8.0-windows` + `UseWindowsForms=true` 才能编译，于是 `HashEngineTests`/`BackupRulesTests`/`ConfigManagerTests` 这些纯算法测试也被绑死在 Windows Desktop 运行时，无法在 Linux CI 或普通 `net8.0` 宿主运行。`Directory.Build.props:18` 的 `EnableWindowsTargeting` 只能让**还原**通过，覆盖不到运行测试。`INotifier` 本意是隔离 UI（`Notifier.cs:7` 注释称"便于单元测试使用假实现"），但全仓库**没有任何假实现、没有任何测试使用它**（`tests/**` 中 `Notifier` 命中 0 处）；`INotifier` 唯一被 FileMaster 出现的地方是 `MainForm.cs:53/89` 的构造参数，而 `Program.cs:60` 恒传 `null`。
- **影响**：Core 的可移植性与可测试性被一个 130 行的托盘通知类绑架；跨平台 CI、纯逻辑测试、未来 CLI/服务形态扩展全部受阻。
- **修复建议**：把 `Notifier`（含 WinForms 实现）移出 Core 放进 UsbBackup；Core 只保留纯 BCL 的 `INotifier` 接口或干脆不要；从 `WinToolBox.Core.csproj` 删除 `UseWindowsForms`，并把 `WinToolBox.Core.Tests` 改回 `net8.0`。
- **证据命令**：`grep -n "UseWindowsForms" src/WinToolBox.Core/WinToolBox.Core.csproj tests/*/*.csproj`

#### [P2-3] FileMaster 无单实例互斥；UsbBackup 的互斥体不是 `Global\`

- **文件**：`src/Tools/FileMaster/Program.cs`、`src/Tools/UsbBackup/Program.cs:87`
- **现象**：FileMaster 可多开（`Program.cs` 无 `Mutex`）；UsbBackup 使用 `new Mutex(true, "WinToolBox.UsbBackup.SingleInstance")`（无 `Global\` 前缀）。
- **为什么是问题**：FileMaster 多开时两个实例并发写同一 `templates.json`（固定 `.tmp` 名 + 无锁，见 P2-4），会静默丢失其中一个模板；UsbBackup 的 `Local\` 命名空间互斥体在 RDP/快速用户切换的**不同会话**中不互斥，两个实例可同时向同一备份目标写入。
- **影响**：模板丢失（FileMaster）；跨会话双实例并发备份（UsbBackup）。
- **修复建议**：FileMaster 增加与 UsbBackup 同规格的命名互斥体；把 UsbBackup 的互斥体名改为 `Global\WinToolBox.UsbBackup.SingleInstance`（注意不同用户会话下需处理权限）。
- **证据命令**：`grep -rn "Mutex" src/Tools/`

#### [P2-4] 两套 JSON 持久化各自实现"临时文件 + 原子替换"，固定 `.tmp` 名且无跨进程锁

- **文件**：`src/WinToolBox.Core/ConfigManager.cs`、`src/WinToolBox.Core/Services/TemplateManager.cs`
- **位置**：`ConfigManager.cs:103-L120`（`tempFile = ConfigFilePath + ".tmp"` L114、写盘+替换 L116-L117）；`TemplateManager.cs:359-L378`（`lock` 只用于快照字典 L359-L362，`.tmp` 写盘与替换 L369/L371-L372 在锁外）
- **现象**：两处"原子写"逐行等价（写 `<目标>.tmp` → `File.Move(..., overwrite: true)`），都使用**固定** `.tmp` 后缀、都没有文件锁或重试。
- **为什么是问题**：同进程内两个消费者同时保存会互相覆盖 `.tmp`；跨进程（两个 FileMaster 实例，见 P2-3）同样冲突。故障难以复现，日志里只留一条 `IOException`。这是同一种模式的两份独立实现，未来修一处不会修到另一处。
- **影响**：并发保存时静默丢失一次用户修改（模板/配置）。
- **修复建议**：Core 抽 `AtomicJsonFile.Save<T>(path, value)`：使用 `Path.GetRandomFileName()` 生成唯一临时名（或加 `Environment.ProcessId`）+ `File.Move(..., overwrite: true)` + 有限次重试，`ConfigManager` 与 `TemplateManager` 都改为调用它。
- **证据命令**：`grep -n "\.tmp\|File.Move(" src/WinToolBox.Core/ConfigManager.cs src/WinToolBox.Core/Services/TemplateManager.cs`

#### [P2-5] 覆盖式写入没有原子性：取消/失败会在目标留下截断文件

- **文件**：`src/Tools/FileMaster/MainForm.cs:527`、`UI/FolderDiffDialog.cs:290`、`Services/FolderSyncService.cs:228`、`Services/AutoArchiverService.cs:405`/`L409`
- **现象**：四处都"直接以最终路径为写入目标"，没有"写临时文件 → 校验 → 原子替换"：`File.WriteAllTextAsync(path, text, encoding, token)`（两处）以 `FileMode.Create` 打开，`File.Copy(src, dst, overwrite: true)`，`File.Move(..., overwrite)` / `File.Copy(..., overwrite)`。
- **为什么是问题**：`WriteAllTextAsync` 与 `File.Copy` 都会**先截断目标再写**。取消（用户关窗触发 token，`MainForm.cs:958` 会 cancel）、磁盘写满、U 盘/网络盘掉线时，目标停留在部分内容状态，而**原有内容已被截断清除**；`SaveFileDialog` 的 `OverwritePrompt = true` 只保证"覆盖前问过用户"，不保证可回退。**两点精确化**（复核修正）：① `File.Copy`/`File.Move` 本身**不可取消**，只有 I/O 失败才会造成截断；② 同一卷内的 `File.Move(overwrite: true)` 是单次原子 `MoveFileEx(REPLACE_EXISTING)`，因此 `AutoArchiverService.cs:405` 的截断风险**仅存在于跨卷移动**（跨卷时 `File.Move` 退化为"复制+删除"）。真正必然暴露截断风险的是两处 `File.WriteAllTextAsync`（可被取消）与 `File.Copy`。
- **影响**：FileMaster 导出/同步/归档 → 目标文件损坏且原内容不可恢复。
- **修复建议**：导出与复制统一改为"写 `<目标>.tmp` → 校验 → `File.Move(tmp, 目标, overwrite: true)`"，取消/失败时删除临时文件；跨卷移动场景显式采用"复制到临时文件 → 替换 → 再删源"，避免中途失败留下半成品。
- **证据命令**：`grep -rn "WriteAllTextAsync\|File.Copy(\|File.Move(" src/Tools/FileMaster/`

#### [P2-6] 取消后不返回"已执行清单"，半完成状态被吞进 `OperationCanceledException`

- **文件**：`src/Tools/FileMaster/Services/*.cs`（5 个服务）、`UI/FeatureDialogBase.cs`
- **现象**：重命名/归档/时间戳/同步/空目录等服务在 `Apply` 循环中响应取消并抛出/冒泡 `OperationCanceledException`，但已完成的部分没有作为结果返回给 UI。
- **为什么是问题**：用户点"取消"后，磁盘上已经改了一半（文件已改名/已移动/时间戳已改），而 UI 显示"已取消"，两边状态不一致；用户无法知道哪些文件已被处理。
- **影响**：FileMaster 8 项功能的取消路径 → 用户对"取消"的预期与实际不符，可能重复执行导致二次改名。
- **修复建议**：`Apply` 在 `finally` 中返回带 `Canceled = true` 的部分结果（已完成项列表），UI 展示"已取消：已处理 N 项（列出前若干项）"；取消后再执行前应支持基于结果续做或提示。
- **证据命令**：`grep -rn "OperationCanceledException" src/Tools/FileMaster/`

#### [P2-7] `FeatureDialogBase.SafeHandler` 覆盖不了 `async` 续体，7 个对话框各自复制 `AsyncHandler`

- **文件**：`src/Tools/FileMaster/UI/FeatureDialogBase.cs`、`UI/*Dialog.cs`（7 个）
- **现象**：基类提供 `SafeHandler`（`FeatureDialogBase.cs:520`、L529-L540）包装同步事件处理器，但 `async void` 事件处理器的异常发生在**续体**中，`SafeHandler` 的 try/catch 无法覆盖；于是 7 个子类各自复制了一份**完全相同**的 12 行 `AsyncHandler`（`AutoArchiveDialog.cs:166`、`DuplicateFinderDialog.cs:160`、`EmptyFolderDialog.cs:97`、`FileUnlockerDialog.cs:102`、`FolderDiffDialog.cs:118`、`FolderSyncDialog.cs:132`、`TimestampDialog.cs:146`，合计 84 行重复代码；`RenameDialog` 没有复制，而是直接用 `async void`（L312/L358），成为唯一没有该兜底的对话框）。
- **为什么是问题**：同一兜底逻辑被复制 7 份（修改时必然漏改）；`async void` 中未捕获的异常会直接进入 `AppDomain.UnhandledException`（两工具均未注册 `TaskScheduler.UnobservedTaskException`，见 P2-9），表现为进程级崩溃或静默丢失。唯一未复制 `AsyncHandler` 的 `RenameDialog` 因此成为该抽象漏洞的直接受害样本。
- **影响**：UI 异常兜底不可靠；重复代码维护成本。
- **修复建议**：把 `AsyncHandler` 提升到 `FeatureDialogBase` 作为受保护的统一实现（`async void` 包裹 `try/catch` 并按 `IsDisposed` 判断后提示），子类只保留业务 lambda；同时注册 `TaskScheduler.UnobservedTaskException`。
- **证据命令**：`grep -rn "AsyncHandler\|SafeHandler" src/Tools/FileMaster/UI/`

#### [P2-8] 预览计划失效策略不一致：部分功能在用户改规则后继续执行旧计划

- **文件**：`src/Tools/FileMaster/UI/RenameDialog.cs`、`UI/TimestampDialog.cs`、`UI/AutoArchiveDialog.cs`
- **现象**：`RenameDialog`/`TimestampDialog` 在用户修改规则后仍可点击"执行"并沿用之前生成的计划；`AutoArchiveDialog` 另有"预览失败（`plan.Error != null`）仍保留可执行计划"的情况。
- **为什么是问题**：预览与执行的内容不是同一份计划，等于绕过了"先预览后执行"的安全设计；用户在预览中看到的 10 个改名，执行时可能是另一批。
- **影响**：FileMaster 批量重命名/时间戳/自动归档 → 执行结果与用户确认的内容不一致。
- **修复建议**：任何影响计划的输入发生变化时立即使计划失效并清空结果区（按钮置灰，提示"规则已变更，请重新预览"）；`plan.Error != null` 时禁止执行。
- **证据命令**：`grep -n "plan\|Preview" src/Tools/FileMaster/UI/RenameDialog.cs src/Tools/FileMaster/UI/TimestampDialog.cs`

#### [P2-9] 两个工具都未注册 `TaskScheduler.UnobservedTaskException`，且全局异常兜底策略不一致

- **文件**：`src/Tools/FileMaster/Program.cs:54-L55`、`src/Tools/UsbBackup/Program.cs:132-L156`
- **现象**：`UsbBackup` 先 `Application.SetUnhandledExceptionMode(CatchException)`（L134）再订阅 `Application.ThreadException`（L136-L152），`AppDomain.UnhandledException`（L154-L155）**只写日志不弹框**；`FileMaster` 直接订阅 `Application.ThreadException`（L54）而**未**设置模式，两个事件都指向同一个 `HandleFatal`（写日志 + `MessageBox`）。两者都**没有**注册 `TaskScheduler.UnobservedTaskException`（全仓库 grep 0 命中），而两工具都大量使用 `Task.Run`。
- **为什么是问题**：① 未设置 `UnhandledExceptionMode` 时行为取决于 `Automatic` 默认值与调试器是否附加，FileMaster 的"兜底"是否生效不确定；② 未观察的任务异常不会被记录，与本项目"任何异常都要写日志"的既定约定冲突，后台任务失败将无任何痕迹。
- **影响**：FileMaster 在特定宿主下弹框行为与预期不符；两工具的后台任务异常无日志可查。
- **修复建议**：在 Core 抽 `GlobalExceptionHandler.Install(toolName, logger)`，统一固定 `SetUnhandledExceptionMode(CatchException)` + 订阅三个事件（含 `TaskScheduler.UnobservedTaskException` 并调用 `e.SetObserved()`），两个 `Program.cs` 只传入工具名。
- **证据命令**：`grep -rn "TaskScheduler\|SetUnhandledExceptionMode" src/Tools/`

#### [P2-10] 两个 `.csproj` 的发布参数逐字复制且无条件生效

- **文件**：`src/Tools/FileMaster/FileMaster.csproj:13-L18`、`src/Tools/UsbBackup/UsbBackup.csproj:13-L18`
- **现象**：两个工程各 25 行，逐字节比对后**只有 3 行不同**——L7 `RootNamespace`（`WinToolBox.Tools.FileMaster` vs `WinToolBox.Tools.UsbBackup`）、L8 `AssemblyName`（`FileMaster` vs `UsbBackup`）、L13 注释（`（发布时使用）` vs `（GitHub Actions 发布时使用）`；字节数 1041 vs 1054，差值 13 = 命名空间 −1 + 程序集名 −1 + "GitHub Actions " +15，可证无其他字符差异）。也就是说 `RuntimeIdentifiers`/`PublishSingleFile`/`SelfContained`/`IncludeNativeLibrariesForSelfExtract`/`EnableCompressionInSingleFile`/`ApplicationHighDpiMode`/`ApplicationManifest`/`UseWindowsForms`/`OutputType`/`TargetFramework`/`IsPackable` 共 11 条属性 + `ProjectReference` 全部逐字相同，且其中 5 条发布参数无条件生效，`Directory.Build.props` 中没有任何发布相关属性。
- **为什么是问题**：① 逐字复制意味着新增第三个工具时任何漏改（漏 `PerMonitorV2`、漏 `SelfContained`）都不会编译报错，只会在发布产物里体现；② `SelfContained=true` + 单一 RID 会被 SDK 推断为默认 RID，使**每次 `dotnet build`（含 Debug）**都产出 RID 专属自包含输出（磁盘上 `src/Tools/*/bin/Debug/net8.0-windows/win-x64/` 下确实存在整套运行时），放大构建时间与 `bin/` 体积。
- **影响**：构建变慢、工作区膨胀；发布参数随时间漂移。
- **修复建议**：把两个工具共享的属性上提到 `Directory.Build.props`，用 `Condition="'$(OutputType)' == 'WinExe'"` 区分库与工具，用 `Condition="'$(Configuration)' == 'Release'"` 限定发布参数；两个 csproj 只保留 `OutputType`/`RootNamespace`/`AssemblyName`/`ApplicationManifest`/`ProjectReference`。
- **证据命令**：`grep -n "PublishSingleFile\|SelfContained\|RuntimeIdentifiers" src/Tools/*/*.csproj Directory.Build.props`

#### [P2-11] 同一条命令行的开关解析、版本输出与 `AttachConsole` 互操作签名两工具各写一套且已漂移

- **文件**：`src/Tools/FileMaster/Program.cs`、`src/Tools/UsbBackup/Program.cs`
- **位置**：`FileMaster/Program.cs:16`（`const int AttachParentProcess = -1`）、L83-L99、L71-L81、L268-L269；`UsbBackup/Program.cs:24`（`const uint = 0xFFFFFFFF`）、L194-L216、L394-L402、L407-L409
- **现象**：`--selftest` 取值在两工具契约不同（FileMaster 返回**原样字符串**，UsbBackup 立即 `Path.GetFullPath`）；`AttachConsole` 有 `int`/`uint` 两套 P/Invoke 与两个常量；`--version` 输出格式不同，且 FileMaster 未复用 `AppPaths.ProductName`；`HasSwitch`、版本号读取为逐字重复。
- **为什么是问题**：这些是进程外壳的公共面，用户与脚本（CI 的 `--selftest`、发布校验的 `--version`）会直接依赖；同一开关语义不同，任何共享脚本都要写两套分支。
- **影响**：跨工具脚本复杂度；命令行契约无法作为统一 API 承诺。
- **修复建议**：抽一个共享源文件（`src/WinToolBox.Core/CliSwitchParser.cs` 或 `Directory.Build.targets` 中的 `Compile Include` 链接文件），集中提供 `HasSwitch`/`GetSwitchValue`/`GetInformationalVersion`/`AttachConsole` 声明，两个 `Program.cs` 只保留各自文案。
- **证据命令**：`grep -n "AttachParentProcess\|HasSwitch\|GetInformationalVersion" src/Tools/*/Program.cs`

#### [P2-12] 日志保留清理只被 UsbBackup 调用，FileMaster 日志无界增长

- **文件**：`src/WinToolBox.Core/Logger.cs:182-L221`、`src/Tools/UsbBackup/TrayApplicationContext.cs:78`、`src/Tools/FileMaster/Program.cs`
- **现象**：`CleanupOldLogs`（`RetentionDays` 默认 30，`Logger.cs:54`）在全仓库只有 `TrayApplicationContext.cs:78` 一处调用；FileMaster 启动路径从不调用。
- **为什么是问题**：`Logger` 文档承诺"按天切割、保留 30 天"，但契约的执行依赖"用户装了并在运行 UsbBackup"。只用 FileMaster 的用户，`%LocalAppData%\WinToolBox\logs\` 无限增长。清理逻辑内嵌在调用方而非 `Logger` 自身，是契约与实现分离的设计问题。
- **影响**：长期占用磁盘；日志目录体积影响"打开日志目录"体验。
- **修复建议**：把清理移到 `Logger` 内部（首次写入时惰性执行一次，带"当天已清理"标志），或在两个 `Program.cs` 的启动路径都显式调用。
- **证据命令**：`grep -rn "CleanupOldLogs" src/`

#### [P2-13] 复制结束不做完整性校验，中断残留的半成品在 `OverwriteExistingFiles=false` 下被永久保留

- **文件**：`src/WinToolBox.Core/FileCopier.cs`
- **位置**：`Decide` L182-L185、`StreamCopy` L201-L240
- **现象**：复制完成后不校验目标文件大小/哈希；当 `OverwriteExistingFiles=false` 时，`Decide` 对已存在的目标一律返回 `SkipExisting`——包括上次中断留下的截断文件。
- **为什么是问题**：一次中断会永久污染该目标：此后每次备份都"跳过已存在"，用户永远拿不到完整副本，而工具一直报告成功。
- **影响**：备份目标存在不可自愈的损坏文件。
- **修复建议**：复制后校验目标大小（廉价）并计入 `VerifiedFiles`；对 `SkipExisting` 分支在下次运行时比较大小，不一致则重拷或标记为冲突；配合 P1-6 的取消机制，取消时删除未完成的临时文件而不是留下截断的最终文件。
- **证据命令**：`grep -n "SkipExisting\|OverwriteExistingFiles" src/WinToolBox.Core/FileCopier.cs`

#### [P2-14] `BackupRules.IsTargetOnSameVolume` 校验失败时 `fail-open`（返回"不同卷"）

- **文件**：`src/WinToolBox.Core/BackupRules.cs`
- **现象**：判定目标是否与源在同一卷时，异常路径返回 `false`（= 不同卷），即"允许备份"。
- **为什么是问题**：这条检查是防止"把 U 盘备份回 U 盘自身"的安全红线。`fail-open` 使红线在异常（卷信息读不到、路径异常）时静默失效。
- **影响**：极端情况下备份写入源盘，用户以为已备份。
- **修复建议**：改为三态（`SameVolume` / `DifferentVolume` / `Unknown`），`Unknown` 时**拒绝备份**并提示原因；同时把异常写入日志与结果。
- **证据命令**：`grep -n "IsTargetOnSameVolume" -A 20 src/WinToolBox.Core/BackupRules.cs`

#### [P2-15] `FolderSyncService` 用精确 UTC 时间戳判定"是否需要更新"，FAT 目标每次全量重拷

- **文件**：`src/Tools/FileMaster/Services/FolderSyncService.cs:310-L323`
- **现象**：默认比较是 `source.LastWriteTimeUtc != target.LastWriteTimeUtc`（精确不等）。
- **为什么是问题**：目标位于 FAT32/exFAT（U 盘、外置盘）时时间戳按 2 秒粒度取整，"写入后读回"的时间必然不等于源时间 → 每次同步都重拷全部文件；同时与备份侧 `>=` 的语义相反（见 P0-1）。
- **影响**：跨设备同步性能退化到全量；两个工具对"文件是否变了"的答案互相矛盾。
- **修复建议**：改为带容差的比较（如 |Δ| ≤ 2 秒视为相同），或提供并默认启用 `CompareByHash`；两个工具统一采用同一套 `FileComparisonPolicy`（放 Core）以免再次漂移。
- **证据命令**：`grep -n "LastWriteTimeUtc\|CompareByHash" src/Tools/FileMaster/Services/FolderSyncService.cs`

#### [P2-16] 大目录进度洪泛：逐项 `IProgress.Report` 被 `Progress<T>` 转成 UI 线程消息

- **文件**：`src/Tools/FileMaster/Services/*.cs`、`src/Tools/UsbBackup/BackupService.cs`
- **现象**：多个服务在逐项循环里对每个条目调用 `progress?.Report(...)`，而 `Progress<T>` 会通过捕获的 `SynchronizationContext` 把每次回调 `Post` 到 UI 线程。
- **为什么是问题**：数十万条目会产生数十万条 UI 消息，UI 线程被淹没；用户看到的是界面卡顿（进度条反而更不流畅），且消息队列积压使"取消"响应变慢。
- **影响**：大目录下 UI 明显卡顿；取消延迟。
- **修复建议**：进度按时间节流（如最快 50-100 ms 一次）或按百分比变化阈值上报；`BackupService` 已有的进度节流做法应推广到 FileMaster 的 5 个服务。
- **证据命令**：`grep -rn "progress?.Report" src/`

#### [P2-17] `FeatureDialogBase` 忙碌时禁用全部按钮 → 长操作只能靠关窗取消，且关窗不等待后台线程

- **文件**：`src/Tools/FileMaster/UI/FeatureDialogBase.cs`（`SetBusy` L706-L725、`SetChildrenEnabled` L255-L271）、各对话框 `OnFormClosing`
- **现象**：`SetBusy(true)` 递归禁用输入区与按钮（含"取消"按钮）；关闭窗口时直接 `Close()`，不等待后台任务结束。
- **为什么是问题**：长操作期间用户唯一的"取消"手段是关窗，而关窗既不取消 token 也不等待，后台线程继续操作**已释放**的控件（`FillScanResult`/`FillPreview` 等在窗口已释放时仍操作 ListView），异常被兜底吞掉。
- **影响**：用户无法优雅取消；关窗后出现难以复现的 `ObjectDisposedException`/跨线程异常；磁盘操作仍在后台继续。
- **修复建议**：忙碌时保留"取消"按钮可用并绑定 `CancellationTokenSource.Cancel()`；`OnFormClosing` 中 `Cancel()` + 等待后台任务（带超时），期间禁用关闭；表单关闭后所有 UI 回填前检查 `IsDisposed`。
- **证据命令**：`grep -n "SetBusy\|SetChildrenEnabled\|IsDisposed" src/Tools/FileMaster/UI/FeatureDialogBase.cs`

#### [P2-18] 根目录路径 `TrimEnd(DirectorySeparatorChar)` 把 `E:\` 退化为驱动器相对路径 `E:`（潜伏隐患）

- **文件**：`src/Tools/FileMaster/Services/FolderSyncService.cs:41-L42`、`L305`、`Services/EmptyFolderCleanerService.cs:87`
- **现象**：`Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar)` 会把驱动器根 `E:\` 变成 `E:`。在 .NET Framework 语义下 `Path.Combine("E:", "sub")` = `E:sub`（相对当前驱动器的工作目录），这是经典 bug；**但在本仓库的 `net8.0-windows`（.NET 8+）语义下**，实测 `Path.Combine("E:","sub")` = `E:\sub`、`Path.GetFullPath("E:")` = `E:\`，因此后果被大幅削弱。
- **影响**：在 .NET 8 上属于**潜伏隐患**（依赖"每驱动器当前目录"，例如继承来的 `=E:` 环境变量），不是可复现的写错路径；保留记录以便未来若改为 .NET Framework 或手工拼接字符串时不要踩坑。
- **修复建议**：改为 `Path.TrimEndingDirectorySeparator(x)`（.NET 6+ 的正确 API，保留根目录的分隔符），或先判断 `x` 是否已是根目录再处理。

#### [P2-19] `FolderSyncService`/`FolderDiffService` 重复 stat、无哈希缓存，且遗留死代码

- **文件**：`src/Tools/FileMaster/Services/FolderSyncService.cs`、`Services/FolderDiffService.cs`
- **现象**：同一文件在计划生成与执行阶段被多次 `new FileInfo`/`GetLastWriteTimeUtc`；`FolderDiffService` 对同一文件分别计算多次哈希而无缓存；`FolderSyncService` 内有未被调用的成员。
- **为什么是问题**：N+1 式 I/O 在网络盘/USB 上代价显著；哈希重复计算使"仅大小/时间"以外的比较模式慢数倍。
- **修复建议**：在计划阶段一次性物化 `FileInfo` 快照（`FileSystemInfo` 缓存），哈希结果按 `(FullPath, Length, LastWriteTimeUtc)` 缓存；删除死代码。
- **证据命令**：`grep -c "new FileInfo" src/Tools/FileMaster/Services/FolderSyncService.cs`

#### [P2-20] 配置损坏时静默回退默认值，随后一次「保存设置」会把用户原配置永久覆盖

- **文件**：`src/WinToolBox.Core/ConfigManager.cs`、`src/Tools/UsbBackup/MainForm.cs:454-L459`
- **现象**：`TryLoad` 在 JSON 解析失败时返回默认配置且不区分"文件不存在"与"文件损坏"；用户随后在界面点"保存设置"即用默认值覆盖原文件。
- **为什么是问题**：损坏的配置（手工编辑出错、磁盘写入中断）本可修复或备份，却被静默丢弃；用户看到的是"设置莫名其妙回到默认"。
- **影响**：UsbBackup 用户配置不可恢复。
- **修复建议**：`TryLoad` 区分 `Missing`/`Corrupted`（后者返回 `LastError` 与原始内容）；损坏时把原文件重命名为 `config.json.corrupt-<时间戳>` 再落盘，并在界面提示"配置已损坏，已备份为…"。
- **证据命令**：`grep -n "TryLoad\|catch" src/WinToolBox.Core/ConfigManager.cs`

#### [P2-21] 保存配置使用固定临时文件名，两个实例同时保存会互相踩

- **文件**：`src/WinToolBox.Core/ConfigManager.cs:114-L117`、`src/WinToolBox.Core/Services/TemplateManager.cs:369`
- **现象**：`var tempFile = ConfigFilePath + ".tmp";` —— 固定名，无进程/线程区分（`TemplateManager.cs:369` 同）。
- **修复建议**：同 P2-4（唯一临时名 + 重试）。

#### [P2-22] 设备目录名只用"卷标_序列号"，与唯一标识（含容量）不一致；序列号缺失时不同设备共用同一备份目录

- **文件**：`src/WinToolBox.Core/BackupRules.cs:32-L38`、`src/WinToolBox.Core/UsbDetector.cs:196`
- **现象**：`UniqueId` = `卷标_序列号_容量`，而备份目录名 = `卷标_序列号`（无容量）。`GetVolumeInformationW` 失败时序列号退化为占位 `00000000`。
- **为什么是问题**：两只同卷标、同容量、都读不到序列号的 U 盘会：① 在 `UsbWatcher` 的快照字典里键相同（`TryAdd` 只保留第一只）→ 第二次插入不产生 Arrived、第一只拔出也不产生 Removed；② 指向同一个备份目录，内容互相混合。
- **影响**：UsbBackup 漏报插拔事件；两份 U 盘备份混在同一目录并被"大小+时间"跳过规则误判。
- **修复建议**：优先使用稳定物理标识（`GetVolumeNameForVolumeMountPoint` 返回的 `\\?\Volume{GUID}\`），卷标/盘符仅用于显示；序列号缺失时至少把盘符并入键；目录名与 `UniqueId` 采用同一来源。
- **证据命令**：`grep -n "BuildUniqueId\|BuildDeviceFolderName\|00000000" src/WinToolBox.Core/BackupRules.cs`

#### [P2-23] 设备重扫在 UI 线程上调用阻塞式卷 API，坏盘/坏读卡器会冻结界面

- **文件**：`src/Tools/UsbBackup/UsbWatcher.cs:57`、L145-L186；`src/WinToolBox.Core/UsbDetector.cs:21-L60`、L111-L134
- **现象**：`System.Windows.Forms.Timer` 的 `Tick` 在 UI 线程执行，其中 `ScanDevices()` → `DriveInfo.GetDrives()` + 每个可移动盘的 `GetVolumeInformationW`/`GetDiskFreeSpaceExW` 全部同步跑在消息循环线程上。
- **为什么是问题**：对介质故障、正在掉线、读卡器空槽的设备，这些 API 会在内核层阻塞数秒至数十秒，期间 UI 不处理任何消息。
- **影响**：插上坏 U 盘时 UsbBackup 界面"未响应"、托盘菜单点不动；用户强杀进程（→ 命中 P1-6 截断）。
- **修复建议**：扫描移到后台线程并 `await` 回 UI 更新快照；加超时/防重入；先用轻量的 `GetLogicalDrives`/`GetDriveType` 过滤再读卷信息。
- **证据命令**：`grep -n "Timer\|ScanDevices\|GetRemovableDrives" src/Tools/UsbBackup/UsbWatcher.cs`

#### [P2-24] 去抖只"推迟"不"兜底"，连续设备消息会把扫描无限期饿死

- **文件**：`src/Tools/UsbBackup/UsbWatcher.cs:112-L143`（`ScheduleScan` 每次 `Stop()`+`Start()`）、L28（800 ms）
- **现象**：每收到一条 `WM_DEVICECHANGE` 就复位定时器；`DBT_DEVNODES_CHANGED` 被无差别接受。
- **为什么是问题**：设备消息间隔持续小于 800 ms 时（劣质 U 盘反复重枚举、USB 集线器抖动、虚拟驱动器软件周期性触发）定时器永不 Tick → 快照永不刷新 → 插入/拔出事件全部漏报。
- **影响**：用户插入 U 盘后状态栏与日志长时间不更新，误判工具失效。
- **修复建议**：改为"首条消息记时 + 累计等待超上限（如 2 s）强制执行一次扫描"；降低 `DBT_DEVNODES_CHANGED` 的触发权重。
- **证据命令**：`grep -n "ScheduleScan\|DebounceMilliseconds" src/Tools/UsbBackup/UsbWatcher.cs`

#### [P2-25] 备份过程中拔出 U 盘不会中止，也不处理 `QUERYREMOVE`/`REMOVEPENDING`

- **文件**：`src/Tools/UsbBackup/UsbWatcher.cs`、`src/Tools/UsbBackup/BackupService.cs`
- **现象**：watcher 只处理到达/移除通知，不处理"即将移除"；备份循环不订阅设备移除事件。
- **为什么是问题**：设备移除后备份会继续对已消失的路径逐个失败（与 P1-9 的"失败不早停"叠加），产生大量无意义 I/O 与错误明细；用户看到的是"备份仍在跑"。
- **修复建议**：处理 `DBT_DEVICEQUERYREMOVE`/`DBT_DEVICEREMOVEPENDING`，向正在进行的备份发出取消信号并立即停止（配合 P1-7 的 token 贯通）。
- **证据命令**：`grep -n "DBT_\|DeviceRemoved" src/Tools/UsbBackup/UsbWatcher.cs`

#### [P2-26] 长路径（>260）无任何处理：manifest 未声明 `longPathAware`

- **文件**：`src/Tools/UsbBackup/app.manifest`、`src/Tools/FileMaster/app.manifest`
- **现象**：两份清单都只有 `trustInfo` + `compatibility`，没有 `<windowsSettings><longPathAware>true</longPathAware></windowsSettings>`。
- **为什么是问题**：Windows 解除 MAX_PATH 需要"进程声明 longPathAware **且** 系统 `LongPathsEnabled=1`"同时满足。缺任一条，超长路径的文件在**打开/创建时**抛 `PathTooLongException`（注：`File.Exists` 不抛异常，而是返回 `false`——经实测复核，见 §七）；在 `FileCopier` 里这被当作**单文件失败**（记 `FailedFiles++` 后继续），表现为"备份部分失败"而用户拿不到指引。U 盘里出现长路径是常见情形（解压的源码包、`node_modules`、多层备份目录）。`UsbBackup/README.md:172` 甚至把"路径过长"列为"正常失败"之一 —— 等于把可修的能力缺口写成已知限制。
- **影响**：深层目录文件**永远无法备份**；FileMaster 全部文件操作同样受限。
- **修复建议**：两份 manifest 补 `<longPathAware>true</longPathAware>`（并确认 `Directory.Build.props` 未关闭 .NET 8 默认的长路径支持）；在 README 中把"路径过长"从"正常失败"移到"已知限制与开启方法"。
- **证据命令**：`grep -n "longPathAware" src/Tools/*/app.manifest`（无命中）

#### [P2-27] 配置/模板写入使用按 UTF-16 码元截断的名称清洗，可能劈开代理对

- **文件**：`src/WinToolBox.Core/BackupRules.cs`（`SanitizeName`）
- **现象**：按 64 个 UTF-16 码元截断，`Trim` 语义过宽。
- **为什么是问题**：emoji/罕见汉字等补充平面字符由代理对表示，截断可能产生孤立代理项，导致文件名非法或显示为乱码。
- **修复建议**：用 `StringInfo`/文本元素或 `Rune` 按码点截断，并校验结果中无孤立代理项。
- **证据命令**：`grep -n "SanitizeName" -A 25 src/WinToolBox.Core/BackupRules.cs`

#### [P2-28] `UsbBackup` 完全没有测试项目，`BackupService` 这类可测编排逻辑零覆盖

- **文件**：`tests/`（只有 `WinToolBox.Core.Tests`、`FileMaster.Tests`）
- **现象**：`WinToolBox.sln` 中 5 个项目，没有 `UsbBackup.Tests`。而 UsbBackup 是本仓库**唯一处理用户数据备份**的工具。
- **为什么是问题**：`BackupService` 的编排逻辑（设备遍历、结果汇总、失败聚合、`SemaphoreSlim` 互斥）完全可测（`_configManager`/`_notifier` 均为接口），却一行测试都没有；P0-1、P1-6、P1-8、P1-9 这些都发生在这条链路上。Core 的 `FileCopierTests.cs`（531 行）覆盖了复制引擎，但**不覆盖 UsbBackup 如何调用它**。
- **影响**：UsbBackup 的回归只能靠人工真机验收（`MD-files/本地手动测试清单.md`）；重构风险极高。
- **修复建议**：新建 `tests/UsbBackup.Tests`，用假的 `INotifier`/`ConfigManager`（接口化）与临时目录覆盖 `BackupService` 的编排路径；优先级最高的用例：同大小改写不被跳过（P0-1）、取消能中断（P1-7）、空间不足早停（P1-9）、设备移除中止（P2-25）。
- **证据命令**：`dotnet sln WinToolBox.sln list`；`ls tests/`

#### [P2-29] 两套 `TempWorkspace` 测试基建重复实现，行为已经漂移

- **文件**：`tests/WinToolBox.Core.Tests/TempWorkspace.cs`（176 行）、`tests/FileMaster.Tests/TempWorkspace.cs`（89 行）
- **现象**：两个同名类各自实现临时工作区，根目录名不同（`WinToolBoxTests` vs `FileMasterTests`）、API 完全不同（Core 版有 `CreateFile`/`CreateRandomBinaryFile`/`ComputeFileSha256`/只读属性清理，FileMaster 版有 `PathOf`/`Exists`/`EnumerateDirectories`），`Dispose` 的健壮性也不同（Core 版先清只读属性并双层兜底，FileMaster 版只有单层）。
- **为什么是问题**：同一职责两份实现，修一处不会修到另一处（例如"只读文件导致清理失败"只在 Core 版被处理）；新增第三个测试项目时会出现第三份。
- **影响**：测试基建的维护成本与不一致行为（清理失败在 FileMaster 版更常见，残留临时目录）。
- **修复建议**：合并为 `tests/WinToolBox.Testing` 共享项目（不产出测试），两个测试工程引用它；把两版的 API 取并集。
- **证据命令**：`glob "tests/**/TempWorkspace.cs"`

#### [P2-30] `FileMaster.Tests` 与 `WinToolBox.Core.Tests` 对同一批 Core 类型重复测试

- **文件**：`tests/WinToolBox.Core.Tests/FolderCheckReportTests.cs`、`RuleGeneratorTests.cs`、`tests/FileMaster.Tests/FolderCheckerReportTests.cs`、`RuleGeneratorContractTests.cs`、`FolderCheckerTests.cs`
- **现象**：Core 侧已测 `FolderCheckReport`/`RuleGenerator`，FileMaster 侧又测一遍同源逻辑（`RuleGeneratorContractTests.cs` 是"契约测试"，属有意设计）。
- **为什么是问题**：有意设计的部分（契约测试守护跨项目一致性）应保留，但 `FolderCheckReportTests`（181 行）与 `FolderCheckerReportTests`（95 行）覆盖高度重叠，属纯重复；两个测试工程都引用 `net8.0-windows + UseWindowsForms`，重复测试的代价被放大。
- **修复建议**：保留 `RuleGeneratorContractTests`（明确注释其守护目的），把报告渲染测试收敛到 Core 侧一处，FileMaster 侧只保留工具特有的报告入口测试。
- **证据命令**：对比 `tests/WinToolBox.Core.Tests/FolderCheckReportTests.cs` 与 `tests/FileMaster.Tests/FolderCheckerReportTests.cs`

#### [P2-31] 三个"被测试保护的生产死代码"虚高覆盖率

- **文件**：`src/Tools/FileMaster/TreePath.cs`（`KeysFor`）、`Services/FileUnlockerService.cs`（`DescribeAppType`）、`Services/AutoArchiverService.cs`（`BuildReport`）
- **现象**：三个成员在**生产代码中零调用**，但都有测试覆盖。
- **为什么是问题**：覆盖率数字被这些成员抬高，掩盖真实缺口（UI 层 5,635 行零覆盖、UsbBackup 零覆盖）。`TreePath.KeysFor` 连类本身在生产中都没有调用点。
- **修复建议**：删除死代码及其测试，或把 `DescribeAppType`/`BuildReport` 接入实际 UI/导出路径（后者本来就有价值）。
- **证据命令**：`grep -rn "KeysFor\|DescribeAppType\|BuildReport" src/ tests/`

#### [P2-32] 界面状态与实际执行行为不一致（执行期选项被冻结但控件仍可修改）

- **文件**：`src/Tools/FileMaster/UI/*.cs`
- **现象**：多个对话框在执行开始时把选项对象"冻结"传入 Service，但对应控件仍可编辑；用户在执行过程中修改选项，界面显示与实际行为不符。
- **修复建议**：执行期间禁用影响已提交计划的控件（`SetBusy` 已具备该能力，应包含选项区），或明确标注"修改将在下次执行生效"。

#### [P2-33] 时间戳服务全部使用本地时间 API，不标注 `DateTimeKind`、不处理夏令时歧义

- **文件**：`src/Tools/FileMaster/Services/TimestampService.cs`（L41、L200-L216、L270-L272）
- **现象**：`ResolveNewTimes` 与 `File.SetCreationTime`/`SetLastWriteTime`/`SetLastAccessTime` 全部走本地时间；`DateTimeKind` 未显式标注。
- **为什么是问题**：夏令时切换当天存在一个不存在或重复的本地时刻，直接 `Set*Time` 会产生偏移 1 小时或抛异常；`DateTimeKind.Unspecified` 与 UTC 混用时比较结果不确定。
- **修复建议**：明确使用 `DateTimeKind.Local` 并在夏令时无效时刻给出警告/顺延；提供 UTC 输入选项。
- **证据命令**：`grep -n "SetLastWriteTime\|SetCreationTime\|DateTimeKind" src/Tools/FileMaster/Services/TimestampService.cs`

#### [P2-34] 忽略名单解析与匹配被 3 个服务以不同方式复制，且依赖方向倒置

- **文件**：`Services/FolderDiffService.cs`（`ParseExcludes` L228-L257）、`Services/FolderSyncService.cs`、`Services/AutoArchiverService.cs`
- **现象**：三处各自解析/匹配忽略名单，规则语义（是否区分大小写、是否支持通配符、是否匹配目录名）不一致；`FolderDiffService` 的解析被另外两处复用但放在 diff 服务里（依赖方向倒置）。
- **修复建议**：把忽略名单解析与匹配提取为 `ExcludeMatcher` 独立类型（放 Core 或 FileMaster/Services 顶层），三个服务统一使用，并补一组针对匹配语义的单元测试。
- **证据命令**：`grep -rn "ParseExcludes\|IsExcluded" src/Tools/FileMaster/Services/`

#### [P2-35] 本地发布产物与 CI 交付形态不一致（多文件框架依赖 vs 单文件自包含）

- **文件**：`publish-local.ps1:42-L47`、`.github/workflows/release.yml:56-L70`
- **现象**：脚本发布 `--self-contained false -p:PublishSingleFile=false`（框架依赖多文件），CI 发布 `--self-contained true -p:PublishSingleFile=true`（自包含单文件）。
- **为什么是问题**：脚本的用途是"真机验收"，而两类产物的运行期差异恰好落在最需要验证的地方——单文件版首次运行要自解压到 `%TEMP%`、原生库抽取、压缩、启动变慢、`AppContext.BaseDirectory` 指向解压目录、自解压被拦截时的行为。这些在多文件版**根本不会发生**，所以"本地验收通过"推不出"发布包可用"。
- **修复建议**：脚本增加 `-Mode SingleFile|Framework`，默认与 CI 一致；或至少在任何一处明确写出两者的差异并说明本地验收的覆盖边界。

---

### P3 低优先级问题

#### [P3-1] `FileMaster/app.manifest` 仍停留在 FolderCreator 时代

- **文件**：`src/Tools/FileMaster/app.manifest:3`
- **现象**：`<assemblyIdentity version="0.1.0.0" name="WinToolBox.Tools.FolderCreator.app" />` —— v0.4.0 已把工具重命名为 FileMaster，程序集标识未同步。两份清单的 `version` 都还是 `0.1.0.0`，与 `Directory.Build.props:6` 的 `0.4.0` 及 tag `v0.4.0` 脱节。
- **影响**：兼容性诊断/崩溃归因会把 FileMaster 识别为 FolderCreator；"清单版本 = 发布版本"的假设不成立。
- **修复建议**：改为 `name="WinToolBox.Tools.FileMaster.app"`；更彻底的做法是用 MSBuild 生成清单，避免维护第二份版本号。
- **证据命令**：`grep -n "assemblyIdentity" src/Tools/*/app.manifest`

#### [P3-2] 根 `README.md` 的仓库结构与 CI 描述与实际不符

- **文件**：`README.md:81-L96`（结构树）、L83（"测试 → 编译"）、L121（"全部使用 .NET 原生 API"）
- **现象**：结构树漏列 `publish-local.ps1`、`MD-files/`、`LICENSE`、`.gitignore`；CI 描述顺序写反（实际为"还原 → 编译 Release → 运行测试"，`release.yml:33-L51`）；"全部使用 .NET 原生 API"与测试项目引用 `xunit`/`coverlet.collector`/`Microsoft.NET.Test.Sdk` 不符。
- **影响**：`publish-local.ps1` 成为"没人知道的脚本"（其默认参数必失败三个月无人发现，见 P3-3）；文档可信度下降。
- **修复建议**：补齐结构树、纠正 CI 顺序、把依赖表述改为"运行时不引入第三方 NuGet 包；测试使用 xUnit/coverlet"。

#### [P3-3] `publish-local.ps1` 默认 `-Target` 必然触发自己的护栏，裸跑 100% 失败且错误信息误导

- **文件**：`publish-local.ps1:26`（默认 `D:\github\WinToolBox-UsbBackup`）、L31（`$root = $PSScriptRoot` = `D:\github\WinToolBox`）、L34-L36
- **现象**：护栏用**纯字符串前缀比较且不补分隔符**：`'D:\github\WinToolBox-UsbBackup'.StartsWith('D:\github\WinToolBox')` → `True`，于是默认值（明明在工作区**之外**）被判为"工作区内"并抛错，错误信息与事实相反。
- **影响**：脚本的示例用法（L21）完全不可用；失败原因把人引向错误方向。
- **修复建议**：比较前补分隔符（`$rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'`），或改用 `Path.GetRelativePath` 判断是否以 `..` 开头；同时把默认值改为可移植路径（见 P3-4）。
- **证据命令**：`grep -n "Target = \|StartsWith" publish-local.ps1`

#### [P3-4] `publish-local.ps1` 默认目标路径硬编码 `D:\github\...`

- **文件**：`publish-local.ps1:15`（文档）、L26（默认值）
- **现象**：默认值是与作者机器绑定的绝对路径。
- **修复建议**：改为 `Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WinToolBox\tools'` 之类的可移植路径；并在 README 补一行用法示例。

#### [P3-5] `publish-local.ps1` 无条件创建桌面快捷方式，`-NoLaunch` 语义与副作用不匹配

- **文件**：`publish-local.ps1:53-L67`（无条件创建 `U盘备份.lnk`）、L17-L18（`-NoLaunch` 文档）、L74-L77
- **现象**：`-NoLaunch` 只控制最后是否 `Start-Process`，与"往桌面写快捷方式"无关；快捷方式名与 `-Target` 无关，且发布两个工具却只建 UsbBackup 的快捷方式。
- **修复建议**：改为 `-CreateShortcut` 开关（默认关闭）；快捷方式名带工具名。

#### [P3-6] `.gitignore` 未忽略审计/临时工作目录

- **文件**：`.gitignore`
- **现象**：本次审计目录 `_audit/` 与 `MD-files/DSH_*.md.md` 处于 untracked 状态，一次 `git add .` 即会入库。
- **修复建议**：按团队决定追加 `_audit/`（或反向纳入版本管理并说明）；顺带补齐 `*.bak`、`*.orig`、`*.rej`、`*.swp`、`.DS_Store` 等常见条目。

#### [P3-7] 文档重复与历史遗留

- **文件**：`MD-files/FolderCreator-本地手动测试清单.md` / `_EN.md`、根目录 `FileMaster-本地手动测试清单.md`、`MD-files/DSH_Task_FileMaster.md.md:4`
- **现象**：三份内容重叠的验收清单（其中英文侧没有 FileMaster 版本）；`MD-files/FolderCreator-本地手动测试清单.md:5` 仍写"适用程序 `FolderCreator.exe`"、L49/L73 仍指 `%AppData%\WinToolBox\FolderCreator\templates.json`（实际已迁移到 `FileMaster\`）；`DSH_Task_FileMaster.md.md:4` 仍有旧用户名拼写 `zhiyuebin`（会 404）。
- **修复建议**：合并三份清单并补齐英文版，把历史任务书标注为"已归档，其中路径与 URL 可能过期"。

#### [P3-8] `UsbBackup/README*.md` 的姊妹工具链接指向已重命名的目录

- **文件**：`src/Tools/UsbBackup/README.md:178`、`README_EN.md:178`
- **现象**：链接目标 `../FolderCreator/README.md` / `../FolderCreator/README_EN.md` 在磁盘上不存在（应为 `../FileMaster/`）。Lead 用脚本对全部 markdown 相对链接做存在性校验，确认这是 4 条失效链接中的 2 条。
- **修复建议**：改为 `../FileMaster/README.md` / `../FileMaster/README_EN.md`。

#### [P3-9] 日志时间戳与日志文件名各自取一次 `DateTime.Now`，跨零点会错位

- **文件**：`src/WinToolBox.Core/Logger.cs:113`（`timestamp`）、L88（`CurrentLogFilePath`）
- **现象**：同一次写入中两次独立取当前时间；在 23:59:59.999 附近可能出现"时间戳属于前一天，但写入的是新一天的文件"。
- **修复建议**：一次取值并传入 `BuildFileName(timestamp)`。

#### [P3-10] `Logger.Write` 每条日志 open/write/close 一次文件句柄

- **文件**：`src/WinToolBox.Core/Logger.cs:133`
- **现象**：无缓冲、无长生命周期句柄。
- **为什么是问题**：高频日志场景（大目录逐文件日志）会放大 I/O 次数；同时也把 P1-4 的竞争窗口最大化（每次写入都重新打开）。
- **修复建议**：改为持有 `FileStream`（`FileShare.ReadWrite`）并周期性 flush，或至少保留当前的"每次重开"但加上重试。

#### [P3-11] `FileCopier.CollectFiles` 注释称"广度优先"但实现是栈式深度优先

- **文件**：`src/WinToolBox.Core/FileCopier.cs:242`（XML 注释）、L246-L247（`Stack<string>`）
- **现象**：`Stack` + `Push` 是深度优先（后进先出），注释写"广度优先遍历"。
- **修复建议**：改注释，或改用 `Queue<string>` 实现真正的广度优先（后者对"先复制浅层文件"更友好）。

#### [P3-12] `FolderCreatorService.ExportCheckReport` 覆盖写目标文件无提示、无原子替换

- **文件**：`src/WinToolBox.Core/Services/FolderCreatorService.cs`
- **现象**：直接覆盖用户指定的报告文件；`EscapeInline` 不转义 `<`/`>`（Markdown 上下文中可能被当作 HTML）。
- **修复建议**：写入前确认覆盖（或走 SaveFileDialog 的 OverwritePrompt）、采用临时文件+替换、补齐转义。

#### [P3-13] 进度模型细节不一致

- **文件**：`src/WinToolBox.Core/CopyModels.cs`、`Core/Services/FolderCreateModels.cs`
- **现象**：`CopyProgress.Percent` 在 0 文件时返回 100（应为 0 或"未知"）；`MultiRootCreateProgress.Percent` 按根目录数平均，单个大根目录时进度条几乎不动。
- **修复建议**：统一"无工作 → 0%"语义；多根目录进度改为按文件数或字节数加权。

#### [P3-14] 命名与注释遗留："FolderCreator" 字样残留在 FileMaster 更名后的代码与日志中

- **文件**：`src/WinToolBox.Core/Services/TemplateManager.cs:225`、L250、L282（日志文案）、`Core/Services/RuleGenerator.cs:14-L15`、`Core/AppPaths.cs:20-L21`（迁移用常量，属有意保留）
- **现象**：日志里出现"FolderCreator 模板已保存/已删除/已写入内置模板"，用户在 FileMaster 的日志里看到另一个工具名。
- **修复建议**：日志文案改为 FileMaster；`AppPaths.LegacyFileMasterFolderName` 等迁移相关常量保留并补注释说明用途。

#### [P3-15] `MultiRootCreateResult.TotalFailed` 与 `FailedCount` 口径混用

- **文件**：`src/WinToolBox.Core/Services/FolderCreateModels.cs`
- **现象**：把"根目录级失败"折算成 +1 个失败项，与文件/目录级 `FailedCount` 混在同一数字里。
- **修复建议**：拆分为 `FailedRoots` 与 `FailedItems` 两个计数，摘要分别显示。

#### [P3-16] `HashEngine` 先取 `FileInfo.Length` 再打开文件（TOCTOU），进度百分比可超过 100

- **文件**：`src/WinToolBox.Core/HashEngine.cs`
- **现象**：大小在打开文件之前读取，文件在两步之间变化时进度 `Percent` 会超过 100。
- **修复建议**：打开句柄后从 `FileStream.Length` 取值；`Percent` 用 `Math.Min(100, ...)` 收敛。

#### [P3-17] `FileCopier` 的进度回调在 `try` 之外调用，回调抛异常会中断整个复制

- **文件**：`src/WinToolBox.Core/FileCopier.cs`
- **现象**：`progress?.Report(...)` 未包裹异常处理；`IProgress` 实现（UI 侧）抛异常会向上穿透复制循环。
- **修复建议**：回调调用点加 `try/catch` 并记日志（进度上报失败不应影响业务）。

#### [P3-18] `UsbDetector` 的 P/Invoke 声明了 `SetLastError = true` 却从不读取错误码

- **文件**：`src/WinToolBox.Core/UsbDetector.cs:222`、L234
- **现象**：失败时只记"无法读取卷序列号"，没有 Win32 错误码，无法诊断。
- **修复建议**：失败分支读取 `Marshal.GetLastWin32Error()` 并写入日志。

#### [P3-19] `ProcessIntegrity` 的 RID/用途限定与错误码未记录

- **文件**：`src/Tools/UsbBackup/ProcessIntegrity.cs`
- **现象**：见 P1-18；另有 RID 读取路径与用途缺乏注释说明。
- **修复建议**：补充注释与错误码日志。

#### [P3-20] `BackupConfig.Normalize` 用 `List.Contains` 去重（O(n²)）且会静默丢弃 `"."` 这类输入

- **文件**：`src/WinToolBox.Core/BackupConfig.cs`
- **现象**：去重为线性查找；`Length > 1` 的过滤条件会把合法的单字符项（如 `"."`）丢掉。
- **修复建议**：改用 `HashSet<string>(StringComparer.OrdinalIgnoreCase)`；过滤条件改为 `!string.IsNullOrWhiteSpace`。

#### [P3-21] `CopyResult.FormatSize` 被 `UsbDeviceInfo` 反向依赖，DTO 之间形成耦合

- **文件**：`src/WinToolBox.Core/CopyModels.cs`、`Core/UsbDeviceInfo.cs`
- **现象**：设备信息类型调用复制结果的格式化方法，字节格式化能力没有独立归属。
- **修复建议**：抽 `ByteSizeFormatter` 静态类，两处都调用它。

#### [P3-22] 静态可变状态：`public static readonly` 数组可被外部强转改写，默认规则存在双重出口

- **文件**：`src/WinToolBox.Core/ExcludeRules.cs`、`Core/BackupRules.cs`
- **现象**：`public static readonly` 数组/集合对外暴露，调用方可强转后修改；默认规则既可通过属性访问又可通过静态字段访问。
- **修复建议**：改为 `ImmutableArray<T>`/`IReadOnlyList<T>` 属性，或把可变数组设为 private 只暴露只读包装。

#### [P3-23] 任务书描述与磁盘不符之处（详见 §七）

- Core 手写 `.cs` 实际 17 个（任务书写 18，多算了 `obj/` 生成物）
- `FileMaster` 实际 12,767 行（任务书写约 5,700）
- `MainForm.cs` 实际 1,828 行（任务书写 1,522）、`FeatureDialogBase.cs` 805（写 669）
- `UsbBackup/MainForm.cs` 807（写 658）、`MainForm.Designer.cs` 296（写 244）
- `src/Tools/FolderCreator/`、`WinToolBox.App`、`FolderCreatorTab` 等任务书提到的路径/类**不存在**（历史设计，`DSH_Task_FolderCreator_Upgrade.md.md` 明确警告过）

#### [P3-24] `--selftest` 未覆盖新增的 8 项文件管理功能

- **文件**：`src/Tools/FileMaster/Program.cs:129-L266`
- **现象**：`RunSelfTest` 只有 **13 条** `Check(...)`（L166-L220），全部围绕 `RuleParser`/`FolderGenerator`/`FolderChecker`（文件夹结构部分），对 8 项新功能的 Service **零引用**。
- **修复建议**：把 8 项功能的核心 Service 调用加入自检（用临时目录），使 CI 能在没有 UI 的情况下验证它们。

#### [P3-25] 主界面 `MainForm` 是 God Object

- **文件**：`src/Tools/FileMaster/MainForm.cs`
- **现象**：1,828 行、63 个方法、21 个事件处理器、20 个实例字段，单一类承担 9 类职责（规则 UI/模板 CRUD/文件夹创建/一致性检查/目录树导出/报告导出/多根目录布局与 DPI 补偿/日志面板与跨线程 marshal/字体探测）；21 个事件处理器中 12 个超过 40 行；20 个字段中有 8 个是纯布局状态。
- **修复建议**：按职责拆分为 `FolderStructurePresenter`（创建/检查/导出）、`LogPanelController`、`LayoutScaler`、`TemplateController`；`MainForm` 只保留装配与菜单路由。参考：8 项新功能已走 `FeatureDialogBase` + Service，是正确方向，文件夹结构部分应照做。

#### [P3-26] 根 README 的 SignPath 代码签名声明与实际状态矛盾

- **文件**：`README.md:143`、`README_EN.md:128`、`.github/workflows/release.yml`
- **现象**：README 声明"uses free code signing provided by SignPath.io"，而 CI 中**没有任何签名步骤**，同一份 README 的 L54 又写"程序未做代码签名"。三处表述互相矛盾。
- **修复建议**：删除或改写 SignPath 声明（如为"计划申请"），或在 CI 中真正接入签名步骤；确保 L54 与之一致。

---

## 三、跨工具专项分析

### 3.1 重复实现清单

| # | 功能点 | FileMaster 位置 | UsbBackup 位置 | 判定 |
|---|---|---|---|---|
| 1 | 目录树递归遍历 | `Services/FolderSyncService.EnumerateFiles/EnumerateDirectories` L341-L382 | `Core/FileCopier.CollectFiles` L243-L304 | **应下沉 Core**（统一遍历器 + ReparsePoint/长路径/异常处理策略） |
| 2 | 文件"是否需要更新"判定 | `FolderSyncService.IsDifferent` L310-L323（`!=` 或 SHA256） | `Core/FileCopier.Decide` L190-L195（`>=`，仅大小+时间） | **应下沉 Core**（统一 `FileComparisonPolicy`，当前语义互相矛盾→P0-1） |
| 3 | 递归删除封装 | `Services/SafeDelete.cs`（唯一实现，FileMaster 内部） | 无（`FileCopier` 不删除） | 保留在 FileMaster，但需补"只删空目录"重载（P0-2） |
| 4 | 忽略/排除名单解析与匹配 | `FolderDiffService.ParseExcludes` L228-L257 + 另 2 处复制 | `Core/ExcludeRules.cs` | **应下沉 Core**（`ExcludeMatcher` 单一实现，见 P2-34） |
| 5 | 进度上报模型 | 各 Service 自带 `IProgress<T>` 模型（9 套） | `Core/CopyModels.CopyProgress` | 形状不统一（`Action<T>` vs `IProgress<T>`），**应统一** |
| 6 | 命令行开关解析 | `Program.cs:83-L99` | `Program.cs:194-L216` | **应下沉**（逐字重复，见 P2-11） |
| 7 | 版本号读取 | `Program.cs:71-L81` | `Program.cs:394-L402` | **应下沉** |
| 8 | `--selftest` 报告写出 | `Program.cs:249-L263` | `Program.cs:351-L361` | **应下沉**（报告框架可共享，用例各自实现） |
| 9 | 全局异常兜底 | `Program.cs:54-L55`、`HandleFatal` L102-L123 | `Program.cs:132-L156` | **应下沉**（策略还不一致，见 P2-9） |
| 10 | `AttachConsole` P/Invoke | `Program.cs:16`（`int -1`） | `Program.cs:24`（`uint 0xFFFFFFFF`） | **应下沉**（等价但易被误当 bug） |
| 11 | 字体探测 `ResolvePreferredFont` | `MainForm.cs:1802-L1826` | 同名逻辑 | 可下沉（行为一致性） |
| 12 | JSON 原子写 | `Core/Services/TemplateManager.cs:365-L375` | `Core/ConfigManager.cs:103-L120` | **Core 内部重复**→抽 `AtomicJsonFile`（P2-4） |
| 13 | 日志面板/日志显示 | `MainForm.cs:1007-L1073` | `MainForm.cs` 日志区 | 可下沉为共享控件 |
| 14 | `TempWorkspace` 测试基建 | `tests/FileMaster.Tests/TempWorkspace.cs`（89 行） | `tests/WinToolBox.Core.Tests/TempWorkspace.cs`（176 行） | **应合并**为共享测试项目（P2-29） |
| 15 | 报告渲染测试 | `tests/FileMaster.Tests/FolderCheckerReportTests.cs` | `tests/WinToolBox.Core.Tests/FolderCheckReportTests.cs` | 保留契约测试，其余合并（P2-30） |

### 3.2 不一致设计清单

| 维度 | FileMaster | UsbBackup | 影响 |
|---|---|---|---|
| 单实例 | **无互斥** | `Mutex`（非 `Global\`） | FileMaster 多开丢模板；UsbBackup 跨会话双实例（P2-3） |
| 依赖装配 | `MainForm` 内部 `new` 全部服务，`INotifier` 恒为 `null`（`MainForm.cs` 的 5 个调用点 L537/L617/L796/L847/L917 全部结构性死亡） | 组合根 `TrayApplicationContext` 注入 5 个依赖 | FileMaster 的 `MainForm` 不可测；`INotifier` 是死参数（P2-2/P2-3） |
| 全局异常策略 | 直接订阅 `ThreadException`，两事件都弹框 | 先 `SetUnhandledExceptionMode`，`AppDomain` 分支不弹框 | 同一类崩溃表现不同（P2-9） |
| 启动副作用 | `TemplateManager.MigrateLegacyTemplates`（Program.cs:52） | `_logger.CleanupOldLogs()`（TrayApplicationContext.cs:78） | FileMaster 日志永不清理（P2-12） |
| 配置持久化 | `TemplateManager` + `templates.json` | `ConfigManager` + `config.json` | "配置"概念不统一，无法共享 API |
| 通知能力 | `INotifier` 死参数 | 真实使用托盘气泡 | 接口存在误导（P2-2） |
| 文件比较语义 | `!=` 或 SHA256 | `>=`，仅大小+时间 | **同一台机器上两工具对"文件是否变了"的回答相反**（P0-1/P2-15） |
| 进度显示 | 文本 + 进度条（各对话框各自实现） | 文本 + 进度条 | 风格不统一 |
| 后台执行 | `Task.Run` + `FeatureDialogBase.RunBusyAsync` | `Task.Run` + 自定义 | 抽象未共享 |
| 删除默认行为 | 回收站优先（`SafeDelete`），但 AutoArchiver 硬编码永久删除 | 不删除 | FileMaster 内部自相矛盾（P0-2） |
| `--version` 输出 | `FileMaster <ver>` | `<ProductName> - U盘备份 <name> <ver>` | 脚本需分支处理（P2-11） |
| `--selftest` 路径契约 | 返回原样字符串 | 立即 `GetFullPath` | 同名参数语义不同（P2-11） |

### 3.3 Core 复用情况

**结论：`WinToolBox.Core` 不是共享核心，而是两套私有库的物理合并。**

| Core 公共类型 | FileMaster 引用 | UsbBackup 引用 | 归属 |
|---|---|---|---|
| `AppPaths` | ✅ | ✅ | **真共享** |
| `Logger` / `LogEntry` / `LogLevel` | ✅ | ✅ | **真共享** |
| `INotifier` | 仅签名占位（`MainForm.cs:53/89` 接收，`Program.cs:60` 恒传 `null`；5 个调用点 L537/L617/L796/L847/L917 全部结构性死亡） | ✅ | 名义共享 |
| `HashEngine` / `HashAlgorithmKind` | ✅ | ❌ | 仅 FileMaster |
| `FolderCreatorService` / `RuleGenerator` / `TemplateManager` / `FolderCheckReport` / `FolderCreateModels` | ✅ | ❌ | 仅 FileMaster |
| `ConfigManager` / `BackupConfig` / `BackupRules` / `ExcludeRules` | ❌ | ✅ | 仅 UsbBackup |
| `UsbDetector` / `UsbDeviceInfo` | ❌ | ✅ | 仅 UsbBackup |
| `FileCopier` / `CopyModels` | ❌ | ✅ | 仅 UsbBackup |
| `Notifier` | ❌ | ✅ | 仅 UsbBackup，且泄漏 WinForms 类型 |

实测计数：FileMaster 侧 26 处 Core 类型引用（无 `FileCopier`/`ConfigManager`/`UsbDetector`/`UsbDeviceInfo`/`BackupConfig`/`BackupRules`），UsbBackup 侧 54 处（无 `HashEngine`/`TemplateManager`/`FolderCreatorService`/`RuleGenerator`/`FolderCheckReport`）。**Core 共 30 个公共类型中 26 个只有单一消费者**（计数命令：`Select-String -Path src/WinToolBox.Core/**/*.cs -Pattern '^\s*public\s+(sealed\s+|static\s+|abstract\s+|partial\s+)*(class|interface|enum|record|struct)\s+\w+'`，排除 `bin`/`obj`）。

**连带成本**：`Notifier` 把 `NotifyIcon` 放进 Core 公共 API → `WinToolBox.Core.csproj:5` 必须 `UseWindowsForms=true` → `WinToolBox.Core.Tests.csproj:4-L5` 必须 `net8.0-windows` + `UseWindowsForms=true`。结果是 `HashEngine`/`BackupRules`/`ConfigManager` 这些**纯算法**测试被绑死在 Windows Desktop 运行时，无法在 Linux CI 上运行；`Directory.Build.props:18` 的 `EnableWindowsTargeting` 只解决"还原"，解决不了"运行测试"。

### 3.4 耦合与职责重叠

1. **`ConfigManager` 是"通用名 + UsbBackup 专属实现"**：`ConfigManager.cs:7` 注释、L24 `AppPaths.UsbBackupConfigFile`、L47 `DefaultConfigFilePath => AppPaths.UsbBackupConfigFile` 三处写死 UsbBackup 路径。下一个工具若按名字复用，会读写 UsbBackup 的配置。
2. **两个 `.csproj` 是"复制粘贴"而非"共享配置"**：25 行中只有 3 行不同（L7 `RootNamespace`、L8 `AssemblyName`、L13 注释），其余 11 条属性 + `ProjectReference` 逐字相同。新增第三个工具时任何漏改（漏 `ApplicationHighDpiMode`、漏 `SelfContained`）都**不会有编译期提示**，只会在发布产物里静默体现（见 P2-10）。
3. **`Notifier` 是"通用名 + UsbBackup 专属 + UI 泄漏"**：唯一真实实现只服务 UsbBackup，却迫使 Core 打开 WinForms、迫使所有消费者上 Windows Desktop。
3. **`Core/Services/*` 是 FileMaster 的私有业务层**（FolderCreatorService/RuleGenerator/TemplateManager/FolderCheckReport），放在 Core 让"共享库"承担了单一工具的业务逻辑。
4. **依赖方向本身是干净的**：`grep "WinToolBox\.Tools" src/WinToolBox.Core` → 0 命中，不存在工具被 Core 反向依赖。问题不在方向，而在**边界划分**。
5. **`FolderDiffService.ParseExcludes` 被 3 个服务复用**，但定义在其中一个服务里 → 隐式耦合，改动 diff 服务会波及另外两个功能。

---

## 四、测试与可维护性评估

### 4.1 测试覆盖盲区

| 区域 | 规模 | 测试状态 |
|---|---|---|
| `WinToolBox.Core` 纯逻辑（HashEngine/BackupRules/ConfigManager/FileCopier/Logger/TemplateManager/RuleGenerator/FolderCreatorService） | 10 个测试文件（Core 侧 17 个源文件） | ✅ 覆盖良好：201 `[Fact]` + 13 `[Theory]` |
| FileMaster 的 9 个 Service + 5 个根级类 | 15 个测试文件 | ✅ 覆盖良好：221 `[Fact]` + 45 `[Theory]` |
| **FileMaster 的 UI 层** | **10 个文件、4,707 行** | ❌ **零引用、零测试** |
| **`FileMaster/MainForm.cs` 的编排逻辑**（创建/检查/导出/模板 CRUD） | 1,828 行 | ❌ 无测试，且逻辑写在 UI 里不可测 |
| **`src/Tools/UsbBackup/**` 全部** | **11 个文件、3,098 行** | ❌ **没有测试项目，零覆盖** |
| 构建/发布链路（`publish-local.ps1`、`release.yml` 的分支） | — | ❌ 无测试；`publish-local.ps1` 的默认参数 bug 因此长期未被发现 |
| 关键数据安全路径 | — | ❌ 无测试：同大小改写不被跳过（P0-1）、空目录删除的判空复检（P0-2）、取消与部分结果（P2-6）、忽略名单与整目录删除（P1-14）、长路径（P2-26）与设备移除中止（P2-25） |

**已否定的一条推断**（避免误判）：T3 实测 `Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)` **不会**跳过隐藏/系统文件（`AttributesToSkip` 默认值仅在使用 `EnumerationOptions` 重载时生效），因此不存在"隐藏文件被遗漏备份"的问题。

**测试基建重复**：两个测试项目各有一份 `TempWorkspace`（176 行 / 89 行），API 与健壮性均不同（见 P2-29）。

**覆盖率虚高**：3 个生产零调用的成员有测试（`TreePath.KeysFor`、`FileUnlockerService.DescribeAppType`、`AutoArchiverService.BuildReport`），拉高覆盖率数字而掩盖真实缺口（见 P2-31）。

**CI 会跑测试**：`release.yml:47-L51` 执行 `dotnet test WinToolBox.sln -c Release --no-build` 并在失败时 throw，这一点是正确的；但**只覆盖上述有测试的部分**，且只在打 tag 时运行（P2-28 相关的"主分支无 CI"见 §五）。

### 4.2 巨型文件与坏味道

| 文件 | 行数 | 方法数 | 事件处理器 | 判定 |
|---|---|---|---|---|
| `src/Tools/FileMaster/MainForm.cs` | **1,828** | 63 | 21 | **God Object**：9 类职责混在一个类；12 个事件处理器 > 40 行；`OnCheckClick` 84 行 |
| `src/Tools/UsbBackup/MainForm.cs` | 807 | — | — | 偏大但职责相对集中（主界面 + 日志面板） |
| `src/Tools/FileMaster/UI/FeatureDialogBase.cs` | **805** | 31 | 0（用 lambda） | 手写 UI 框架，方法粒度健康（平均 15.1 行）但塞了 5 个子系统（布局 DSL/控件工厂/DPI/状态与日志/异步外壳） |
| `src/Tools/FileMaster/UI/DuplicateFinderDialog.cs` | 672 | — | — | 偏大 |
| `src/WinToolBox.Core/Services/FolderCreatorService.cs` | 553 | — | — | 偏大（Core 里的单工具业务） |

**其他坏味道**：
- 抽象漏口：`FeatureDialogBase.SafeHandler` 覆盖不了 `async` 续体 → 7 个子类各复制一份 12 行 `AsyncHandler`（P2-7）。
- 状态分散："同一时刻只允许一次备份"在 UsbBackup 的三处各有一份标志（`_backupRequestRunning`/`_backupRunning`/`SemaphoreSlim`），UI 之间互不可见（P2 类）。
- 计划失效语义三套（P2-8）。
- 死代码（P2-31）；`FolderSyncService` 内另有未被调用的成员（P2-18）。

### 4.3 重复代码与可提取点

优先级从高到低：

1. **文件比较策略**（P0-1 / P2-15）→ Core `FileComparisonPolicy`，消灭两工具语义矛盾。
2. **递归遍历器**（P1-1 / P1-20）→ Core `FileTreeWalker`，集中处理 ReparsePoint、长路径、异常跳过、惰性迭代。
3. **进程外壳**（P2-9 / P2-11）→ Core `CliSwitchParser` + `GlobalExceptionHandler`，消除 4 处逐字重复与 4 处行为漂移。
4. **JSON 原子写**（P2-4）→ Core `AtomicJsonFile`。
5. **忽略名单匹配**（P2-34）→ `ExcludeMatcher`。
6. **删除封装**（P0-2）→ `SafeDelete` 增加"只删空目录"语义并统一所有调用方。
7. **测试基建**（P2-29）→ `tests/WinToolBox.Testing`。
8. **工具工程属性**（P2-10）→ `Directory.Build.props` 条件化。
9. **字体探测/日志面板**（3.1 表 #11、#13）→ 共享控件。

---

## 五、构建与发布链路评估

### 5.1 `.sln` 与磁盘一致性

✅ **一致，无残留**：`WinToolBox.sln`（104 行）列出 5 个项目，与磁盘完全对应：

| `.sln` 项目 | 磁盘路径 | 存在 |
|---|---|---|
| `WinToolBox.Core` | `src/WinToolBox.Core/WinToolBox.Core.csproj` | ✅ |
| `UsbBackup` | `src/Tools/UsbBackup/UsbBackup.csproj` | ✅ |
| `FileMaster` | `src/Tools/FileMaster/FileMaster.csproj` | ✅ |
| `WinToolBox.Core.Tests` | `tests/WinToolBox.Core.Tests/…csproj` | ✅ |
| `FileMaster.Tests` | `tests/FileMaster.Tests/…csproj` | ✅ |

无 `FolderCreator.csproj` 残留、无已删除项目引用；解决方案文件夹嵌套（`src` → `Tools`）与配置映射（Debug/Release × AnyCPU/x64/x86）完整。**唯一瑕疵**：项目类型 GUID 使用旧版 C# 项目 GUID `{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}`（现代 SDK 风格为 `{9A19103F-16F7-4668-BE54-9A1E7A4F7556}`）——功能上无影响（P3）。

### 5.2 `Directory.Build.props` 与版本策略

- 单点定义 `<Version>0.4.0</Version>`（L6），5 个项目全部继承；`Nullable`/`ImplicitUsings`/`LangVersion`/`EnableWindowsTargeting`/`SatelliteResourceLanguages` 统一（L14-L19）；Release 关闭调试符号（L23-L26）。**这部分设计正确。**
- ⚠️ **与 tag 策略脱节**：tag 有 `v0.1.0 v0.2.0 v0.2.1 v0.2.2 v0.4.0`（5 个），而 `<Version>` 是手工维护的常量，CI 不做校验（P1-16）。tag 序列本身跳过了 `v0.3.0`，说明版本号与标签的对应关系依赖人工纪律。
- ⚠️ **发布参数未上提**：`PublishSingleFile`/`SelfContained`/`RuntimeIdentifiers`/`ApplicationHighDpiMode` 等在两个 csproj 中逐字复制（P2-10）。

### 5.3 CI（`.github/workflows/release.yml`，109 行）

**正确的部分**：触发条件明确（仅 `v*` tag，注释说明不使用 `workflow_dispatch` 以避免误发）；`permissions: contents: write` 最小化；9 个步骤顺序合理（checkout → setup-dotnet 8.0.x → restore → build → test → publish 两个工具 → 校验产物存在 → 打包两个 zip → 创建 Release）；每步都用 `$LASTEXITCODE` 判定并 `throw`；`-o publish/<Tool>` 分离输出；打包前清理旧 zip；产物存在性校验避免静默发布空包；文件名与 README 描述一致（`UsbBackup-win-x64.zip`/`FileMaster-win-x64.zip`）。

**缺陷**：

| 问题 | 位置 | 严重度 |
|---|---|---|
| 无 tag ↔ `<Version>` 一致性校验 | L5-L8、L47-L51 | P1-16 |
| **无任何代码签名步骤**，但 README 声明 SignPath 签名 | release.yml 全文 vs `README.md:143` | P3-26 |
| 只在 tag 上运行：**主分支/PR 没有 CI**，回归只能在准备发布时才发现 | L5-L8 | P2 |
| 只校验 exe **存在**，从不运行它（两个工具自带 `--selftest` 却未被使用） | L73-L83 | P2 |
| Action 版本未钉 commit SHA，工作流持有 `contents: write` 权限 | L26/L29、L11-L12 | P2 |
| 无校验和（SHA256）发布、无 concurrency/timeout 加固 | 全文 | P3 |

### 5.4 `publish-local.ps1`（80 行）

见 **P0-3**（无保护递归删除）、**P3-3**（默认参数必然触发自身护栏）、**P3-4**（硬编码 `D:\github\...`）、**P3-5**（无条件创建桌面快捷方式）、**P2-35**（与 CI 交付形态不一致）。

补充事实：该脚本**未被任何文档提及**（`grep -rn "publish-local" README.md README_EN.md` → 0 命中），这也是 `-Target` 默认值 bug 长期未被发现的原因之一。

### 5.5 `app.manifest` 对比

| 项 | UsbBackup | FileMaster | 判定 |
|---|---|---|---|
| `assemblyIdentity` name | `WinToolBox.Tools.UsbBackup.app` | `WinToolBox.Tools.FolderCreator.app` | ❌ FileMaster 未随重命名更新（P3-1） |
| `assemblyIdentity` version | `0.1.0.0` | `0.1.0.0` | ⚠️ 均与 `<Version>0.4.0</Version>` 脱节 |
| `requestedExecutionLevel` | `asInvoker` / `uiAccess=false` | 同 | ✅ 正确：不请求提权 |
| `supportedOS` | Win10/11 + Win8.1 | 同 | ⚠️ 声明支持 Win8.1，但代码使用 `PerMonitorV2`（需 Win10 1703+） |
| DPI 声明 | 无（交给 csproj `ApplicationHighDpiMode`） | 同 | ✅ 与 `ApplicationConfiguration.Initialize()` 自洽 |
| `longPathAware` | **无** | **无** | ❌ 长路径必然失败（P2-26） |

除 `name` 外两份文件逐字节相同。

### 5.6 未使用文件、依赖与配置

- ✅ 无构建产物入库（`git ls-files` 中 `bin/`/`obj/` 条目数 = 0）。
- ✅ `.gitignore` 覆盖 `bin/obj/log/publish/artifacts`、VS/Rider/VS Code 目录、`.dotnet/`/`.nuget/`、覆盖率与测试结果、`*.zip`、OS 垃圾文件。
- ✅ 无未引用的源码文件（唯一的"孤儿"是文档类与历史任务书）。
- ✅ 无第三方运行时依赖；两个测试项目统一 xunit 2.9.3（无版本分裂）。
- ⚠️ `.gitignore` 未忽略 `_audit/` 等临时工作目录（P3-6）。
- ⚠️ 文档冗余：`MD-files/FolderCreator-本地手动测试清单.md`、`_EN.md` 与根目录 `FileMaster-本地手动测试清单.md` 三份重叠，且英文侧缺 FileMaster 版本（P3-7）。
- ⚠️ 4 条 markdown 相对链接失效（Lead 实测）：`README_EN.md` → `src/Tools/FolderCreator/README_EN.md`（2 处）、`UsbBackup/README.md`/`README_EN.md` → `../FolderCreator/README*.md`。
- ⚠️ 历史任务书 URL 拼写错误：`MD-files/DSH_Task_FileMaster.md.md:4` 的 `zhiyuebin`（应为 `zhuyuebin`）；`MD-files/DSH_Audit_Task.md.md:15` 同样。

---

## 六、修复优先级建议（Top 10）

| 顺序 | 问题 | 严重度 | 预估工作量 | 建议动作 |
|---|---|---|---|---|
| 1 | 增量备份只看"大小+时间"，内容变化被永久静默跳过（P0-1） | **P0** | 1-2 人日 | 在 `FileCopier.Decide` 增加内容校验模式（默认 SHA256 或"大小+时间+抽样哈希"）；`>=` 改 `>`；`CopyResult` 增加 `VerifiedFiles`/`UnchangedAssumed`；补"同大小改写+时间戳回退"自检用例 |
| 2 | "删空目录"实为无差别递归永久删除，且不重新判空（P0-2） | **P0** | 0.5-1 人日 | `EmptyFolderCleanerService.Delete` 删除前复检 `IsEmptyDirectory`；`SafeDelete` 空目录场景改用 `recursive: false`；`AutoArchiverService:455` 的 `useRecycleBin:false` 改为默认 true 的可选项 |
| 3 | `publish-local.ps1` 对任意目录无保护递归删除（P0-3） | **P0** | 0.5 人日 | 拒绝驱动器根/系统目录；要求标记文件才允许清空；`ShouldProcess` + 默认 `-Confirm`；`-ErrorAction Stop` |
| 4 | 复制/同步引擎不识别目录联接与符号链接（P1-1） | **P1** | 1 人日 | 两个引擎的入栈/递归点增加 `FileAttributes.ReparsePoint` 判断；`IsSameOrChildPath` 改为分隔符补全比较；补 Junction 单测 |
| 5 | 托盘 UI 同步上下文恒为 null → 备份入口可能永久禁用（P1-2） | **P1** | 0.5 人日 | 把 `_uiContext`/`Notifier` 的创建推迟到首个 `Control` 之后，或 `PostToUi` 改用 `BeginInvoke`；`catch` 改为降级提示 |
| 6 | `FileCopier` 预扫描惰性枚举绕过 try → 一个无权限目录让备份 0 文件（P1-3） | **P1** | 0.5 人日 | `EnumerateFiles/Directories` 移入 `try` 或改为 `GetFiles/GetDirectories`；补"存在不可访问目录时其余文件仍复制"的单测 |
| 7 | 两工具共用日志文件且失败静默（P1-4，实测失败率 22.57%） | **P1** | 0.5 人日 | 日志文件名带工具名；`FileShare.ReadWrite` + 退避重试；`Logger` 暴露写失败信号 |
| 8 | 备份不可取消，退出时留下截断文件（P1-6 + P1-7） | **P1** | 1-2 人日 | `BackupService` 持有 `CancellationTokenSource` 并贯通到 I/O；退出时 Cancel + 等待 5 秒；UI 增加取消入口 |
| 9 | 模板保存失败仍提示"已保存"，用户规则文本丢失（P1-5） | **P1** | 0.5 人日 | `TemplateManager.Persist` 返回成功/失败（或改为抛异常），UI 显示真实结果 |
| 10 | 发布链路：无 tag↔版本校验 + 英文 README 整份过期（P1-16 + P1-17） | **P1** | 0.5-1 人日 | CI 增加版本一致性校验步骤；重写 `README_EN.md` 与中文版对齐；把双语文档同步纳入发布检查清单 |

**建议的第二批（P2 中优先）**：Core 拆分为真共享 + 工具私有程序集（P2-1/P2-2，工作量 3-5 人日，收益最大）；新建 `tests/UsbBackup.Tests`（P2-28）；补 `longPathAware`（P2-26）；统一文件比较策略（P2-15）；JSON 原子写与唯一临时名（P2-4/P2-21）；同步"先删后拷"改为"先拷后删"（P1-13）。

---

## 七、审计方法与限制

### 使用了哪些检查手段

1. **真实文件清单**：`git ls-files`（115 个文件）+ 磁盘遍历，与任务书描述逐条比对，差异见下。
2. **五路并行深审**（各自只读、各自独立产出）：
   - T1 跨工具一致性/重复实现/耦合（24 条）
   - T2 Core 抽象质量与复用（32 条）
   - T3 正确性/并发/性能/安全横切（34 条）
   - T4 FileMaster 深审（33 条）
   - T5 UsbBackup 深审 + 构建/发布/CI（45 条）
   原始条目合计 158 条，合并同源/重叠条目后为 **84 条**（P0×3 / P1×20 / P2×35 / P3×26）。
3. **对抗式验证**：4 组独立复核员回到源码逐行核对各报告的**行号与事实断言**，不采信原始结论；本报告行号以复核后的值为准。复核员发现了实质性错误并已全部修正，见下"### 对抗式验证发现并已修正的问题"。
4. **Lead 亲自复核的关键源码**：`FileCopier.cs`（判定+预扫描+StreamCopy）、`SafeDelete.cs`、`EmptyFolderCleanerService.cs`、`AutoArchiverService.cs`、`TrayApplicationContext.cs`、`Logger.cs`、`AppPaths.cs`、`Directory.Build.props`、全部 csproj、`WinToolBox.sln`、`release.yml`、`publish-local.ps1`、两份 `app.manifest`、`.gitignore`、根两份 README。
5. **静态模式搜索**：`catch` 空实现/仅日志、`.Result`/`.Wait()`/`Task.Run`、`GetFiles` vs `EnumerateFiles`、`File.AppendAllText`、硬编码路径、`Registry`、`Process.Start`/`UseShellExecute`、删除操作、`ReparsePoint`、`Path.Combine`、静态可变状态等。
6. **实测实验（Lead 执行）**：
   - **并发日志写入**：4 个独立进程各 5 秒持续 `File.AppendAllText`（与 `Logger` 相同的 `FileShare.Read` 语义）→ 成功 9,911 次、失败 2,889 次，**失败率 22.57%**，异常为 `IOException`（文件被另一进程占用）；成功次数与文件行数完全一致，**未发现静默损坏或丢行**（写入是原子的），问题在于失败被吞掉。
   - **markdown 链接存在性校验**：脚本遍历全部 `.md` 的相对链接并检查磁盘存在性 → 4 条失效（其中 2 条为 CJK 文件名编码误报已被 UTF-8 复核排除）。
   - **行数口径核对**：用 `ReadAllLines` / 换行符计数 / 非空行计数三种口径对照，确认差异来源（见下）。
7. **构建与测试验证（受限，见下）**：尝试 `dotnet test WinToolBox.sln -c Release`；确认磁盘上存在 2026-10-02 的 Debug/Release 编译产物与完整 `project.assets.json`/`obj` 还原状态。

### 对抗式验证发现并已修正的问题

为避免"审计员自己写错、Lead 直接采信"，4 组独立复核员对全部 158 条原始结论做了逐行核对。以下是**已在本报告中修正**的实质性错误（保留记录以示可追溯）：

| # | 原结论 | 实际情况 | 本报告的处理 |
|---|---|---|---|
| 1 | （Lead 自身度量错误）协议基线称 `MainForm.cs` 1522 行、总计 22,981 行 | 真实为 **1,828 行 / 28,317 行**。原因是用了 `Get-Content \| Measure-Object -Line`：它**丢弃空行**，且对 BOM-less UTF-8 会按 ANSI 误码后再次丢行 | 全文行数改用 `ReadAllLines`/换行符口径；§七单列"与任务书描述不符之处" |
| 2 | T5：调试器下 `CompleteBackupRequest` 收尾**整段不执行**，导致托盘"立即备份"**永久失效** | **错误**。`_backupRequestRunning = false` 在 `TrayApplicationContext.cs:236-L239`，位于 `try`（L241）**之前**，必然执行；菜单恢复在 L243 失败后还会在 L280 的 catch 中重试。因此不存在"永久失效" | P1-2 的"影响"改写为：正常状态下是 latent 并发缺陷（气泡竞态），附加调试器时**本轮气泡全部不显示**；并显式标注该修正 |
| 3 | T3/T5：`FileStream`/`File.Exists` 对超长路径都抛 `PathTooLongException` | `File.Exists` **不抛异常**，实测 310 字符路径返回 `false`；只有打开/创建路径（`FileStream`）才抛 | P2-26 补充括号说明 |
| 4 | T3：递归遍历联接会"栈无限增长、OOM" | **夸大**。本仓库未声明 `longPathAware`，超长路径会抛异常并被 `CollectFiles` 的按目录 catch 吞掉，递归按分支终止；实际后果是树外内容混入 + 深层重复 + 逐文件失败 | P1-1 改写后果描述 |
| 5 | T4：`TrimEnd(DirectorySeparatorChar)` 会把驱动器根退化成 `E:` 并导致写错路径 | 在 .NET 8 语义下 `Path.Combine("E:","sub")` = `E:\sub`，该失效形态是 **.NET Framework 专有**；在 `net8.0-windows` 上属"依赖每驱动器当前目录"的潜伏隐患 | 该条降级为 P2-18，并明确标注为潜伏隐患而非可复现故障 |
| 6 | T1：两个 `.csproj` "25 行里只有 2 处不同，且都只是注释" | 实为 **3 行不同**（L7 `RootNamespace`、L8 `AssemblyName`、L13 注释），且该说法与 T1 自己的属性表矛盾 | P2-10 与 §3.4 改写为"逐字节比对后只有 3 行不同"，并给出字节差校验 |
| 7 | T1：`INotifier` 在 FileMaster 有 2 个死调用点 | 实为 **5 个**（`MainForm.cs:537/617/796/847/917`） | P2-2、§3.3、§3.2 全部更新为 5 处 |
| 8 | T1/T2：Core 只有 20 个公共类型 | 实测 **30 个**公共类型（17 个手写 `.cs`） | P2-1、§3.3、执行摘要全部改为 30 / 26 |
| 9 | T1：`CleanupOldLogs` "在整个仓库中只有一处调用" | 生产代码确实只有 `TrayApplicationContext.cs:78` 一处，但 `LoggerTests.cs` 另有 7 处 | P2-12 的措辞限定为"生产代码中只有一处"（本报告采用该表述） |
| 10 | T4：`--selftest` 有 8 条断言、8 个对话框复制 `AsyncHandler`、UI 层 5,635 行 | 实为 **13 条** `Check(...)`、**7 个**对话框（`RenameDialog` 未复制）、UI 层 **4,707 行**；另 T4 的 `FeatureDialogBase` 最长方法（`RunBusyAsync` 51 行）实为构造函数 111 行 | P2-7、P3-24、§4.1、§4.2 更新为实测值 |
| 11 | Lead：测试"422 用例"、T4 称 `FileMaster.Tests` "16 文件/243 facts" | 逐项目实测：`WinToolBox.Core.Tests` 10 个文件 / 201 `[Fact]` + 13 `[Theory]`；`FileMaster.Tests` 15 个文件 / 221 `[Fact]` + 45 `[Theory]`；全仓库合计 422 `[Fact]` + 58 `[Theory]` | §一与 §4.1 改用逐项目实测值，不再混用两种统计口径 |
| 11 | T3：重复文件查找会因"自己与自己重复"而**默认勾选删除唯一副本** | 自重复组（路径字符串相同）下两行都判 `isKeep`、默认不勾选，「全选重复项」也跳过；真正的风险只在**路径字符串不同**（映射盘/UNC/联接）的场景 | P1-11 的"影响"补充该澄清 |
| 12 | T4：`AsyncHandler` 缺失导致 `RenameDialog` 无兜底（发现） | 复核员确认该机制成立（`FeatureDialogBase.cs:520/L529-L540` 无法覆盖 `async` 续体） | 作为 P2-7 的旁证保留 |
| 13 | T1：`HasSwitch` 为"逐字重复"、`CleanupOldLogs` "全仓库仅一处调用"、Core"20 个公共类型" | 措辞不精确：`HasSwitch` 的 lambda 参数名不同（非逐字）；`CleanupOldLogs` 在测试中有 7 处调用；Core 实测 30 个公共类型 | §3.1/§3.3/P2-12/P2-11 采用精确表述（"语义相同"而非"逐字"、"生产代码中"限定范围、"30 个"） |

**复核员未能证实的两条（本报告不采信为"已否定"，仅标注为未验证）**：`publish-local.ps1` 与 `release.yml` 中涉及外部信息的部分（Actions 版本过时与否、GitHub Actions 运行历史），因审计环境无外网。

**一处复核员判断有误、经 Lead 复核后予以驳回**：复核员认为 T4 的"自动分类可自我归档"不成立，理由是 `AutoArchiverService.cs:319` 已判断 `Path.GetFullPath(targetPath) == Path.GetFullPath(file)`。该判断比较的是**文件路径**与**目标路径**，只能命中"文件已在目标目录内"这一种情形；而扫描用的是 `Directory.EnumerateFiles(options.SourceDirectory, "*", searchOption)`（L280，含子目录），当**目标目录位于源目录之内**时，上一轮已归档的文件会在下一轮被再次扫描并按"目标子目录"再下沉一层（`targetPath` 与 `file` 不同，守卫不触发）。故 P1-19 维持成立，并已补充该守卫的精确语义以免读者误判。

### 未覆盖的范围

- **未编译、未运行被测程序**：见"无法确认的项"第 1 条。所有"运行期行为"结论均来自源码推理 + 静态证据，未做端到端执行验证。
- **未在真机上插拔 U 盘**：`UsbDetector`/`WM_DEVICECHANGE`/`DriveInfo` 的真实设备行为未验证（CI 同样不覆盖，`release.yml:45-L46` 的注释已自认此限制）。
- **未访问 GitHub/Gitee 远端**：Actions 运行历史、Release 实际产物、tag 与 Release 的对应关系未核对；`git log`/`git tag` 为本地只读结果。
- **未做性能基准测试**：所有性能结论为结构性分析（复杂度、I/O 次数、消息洪泛），无实测吞吐/内存数据。
- **未审计二进制产物**：`bin/`/`obj/` 下的 DLL/exe 未做反编译或签名核对。
- **未审计 `MD-files/**` 中历史任务书的业务正确性**：仅核对其与磁盘的一致性（如 URL 拼写、路径是否存在）。
- **未做安全渗透测试**：`ProcessIntegrity` 的令牌查询、Restart Manager 的 P/Invoke 只做了代码级审查。

### 无法确认的项

1. **能否编译与测试通过——无法确认（环境限制，非代码缺陷）**。
   本次审计环境有三重限制，导致无法完成一次真实的 `dotnet build`/`dotnet test`：
   - 沙箱禁止 `dotnet` 首次运行哨兵写入 `%USERPROFILE%\.dotnet\`（`UnauthorizedAccessException`）；改用可写 `DOTNET_CLI_HOME` 后，还原被重定向到一个空的包缓存；
   - 本机 NuGet 全局缓存 `C:\Users\<用户>\.nuget\packages` 中**只有** `microsoft.netframework.referenceassemblies*`，**没有 xunit / Microsoft.NET.Test.Sdk / coverlet.collector**，而到 `api.nuget.org` 的网络不通（SSL/连接失败），无法进行首次还原；
   - 沙箱禁止 testhost 观察父进程（`Process.EnableRaisingEvents` → Win32 拒绝访问），`dotnet vstest` 与直接运行 `testhost.exe` 均中止。
   **可确认的替代证据**：磁盘上存在 2026-10-02 的完整编译产物（两个测试项目的 Debug/Release `*Tests.dll` + testhost + xunit 全家桶 + `project.assets.json`），说明该次提交在作者环境**曾编译并运行过测试**；`release.yml:33-L51` 的 CI 步骤顺序正确（restore → build --no-restore → test --no-build）。
   **对结论的影响**：本报告的所有结论均不依赖"编译是否通过"。**建议由维护者在具备网络的机器上补跑 `dotnet build WinToolBox.sln -c Release` 与 `dotnet test WinToolBox.sln -c Release` 以确认基线**。
   **辅助说明（诚实披露）**：审计过程中一次探测性 `dotnet build`/`dotnet restore` 覆盖了 `src/WinToolBox.Core/obj/project.assets.json`（该文件被 `.gitignore` 忽略、不在版本控制内，`git status` 与 `git diff` 均无变化）。`WinToolBox.Core` 不含任何 `PackageReference`，该文件会在下次 `dotnet restore` 时自动重建；**仓库中无任何受版本控制的文件被修改**。
2. **`InvariantGlobalization` 未设置的影响**：三个项目都未设置该属性（默认 `false`），对 `DateTime` 解析、字符串比较的 ICU 依赖行为未实测。
3. **单文件发布下 `Microsoft.VisualBasic` 的回收站 API 可用性**：`SafeDelete` 依赖 `Microsoft.VisualBasic.FileIO.FileSystem` 的回收站删除；在 `PublishSingleFile + SelfContained + EnableCompressionInSingleFile` 下的可用性未实测（T1 已标注）。
4. **`EnableWindowsTargeting` 的非 Windows 行为**：`Directory.Build.props:18` 的注释称其允许在非 Windows 代理机上还原，但 Core 的 `UseWindowsForms` 使其无法在非 Windows 上**运行测试**；在真实 Linux runner 上的具体失败形态未实测。
5. **`EnumerationOptions.IgnoreInaccessible` 默认值的影响**：未实测；但 T3 已实测否定了"SearchOption 重载会跳过隐藏/系统文件"的推断，该结论可确认。
6. **精确的并发概率**：P1-4 的 22.57% 失败率来自 Lead 的压测（4 进程、每进程无间隔连续写、5 秒），真实场景（写入稀疏、进程间歇活动）的失败率显著更低，**不应把 22.57% 当作生产环境的丢日志比例**；可确认的是"冲突必然导致写入失败且失败被静默吞掉"这一机制。

### 与任务书描述不符之处（以磁盘为准）

| 项目 | 任务书声称 | 磁盘实测 | 说明 |
|---|---|---|---|
| `FileMaster` 规模 | 约 5,700 行 | **12,767 行** | 任务书用去空行口径；文件数与结构（UI 10 / Services 9 / Models 9）与任务书一致，无缺失 |
| `FileMaster/MainForm.cs` | 1,522 行 | **1,828 行** | `Get-Content \| Measure-Object -Line` 丢空行且口径不稳定；`grep -n` 与 `ReadAllLines` 一致报 1,828 |
| `UI/FeatureDialogBase.cs` | 669 行 | **805 行** | 同上 |
| `UsbBackup/MainForm.cs` | 658 行 | **807 行** | 同上 |
| `UsbBackup/MainForm.Designer.cs` | 244 行 | **296 行** | 同上 |
| Core 文件数 | 18 个 `.cs` | **17 个手写 `.cs`** | 任务书多算了 `obj/` 生成物 |
| `src/Tools/FolderCreator/` | 隐含存在（多份任务书提到） | **不存在**（已重命名为 `src/Tools/FileMaster/`） | `README_EN.md` 与部分 `MD-files` 未同步 |
| `WinToolBox.App` / `FolderCreatorTab` / `DirectoryPicker` / `ProgressPanel` / `LogPanel` / `Tabs/` | `DSH_Task_FolderCreator_Upgrade.md.md` 中提到 | **全部不存在** | 该任务书第 19 行已明确警告"不要假设存在"，属历史设计 |
| `tests/FolderCreator.Tests` | 被 `README_EN.md:75` 引用 | **不存在**（实际为 `tests/FileMaster.Tests`） | 文档过期 |
| 仓库 URL 用户名 | `MD-files/DSH_Audit_Task.md.md:15` 与 `DSH_Task_FileMaster.md.md:4` 写 `zhiyuebin` | 实际为 `zhuyuebin` | 历史笔误，会 404 |

### 审计完整性声明

- ✅ 未修改任何源码、配置、README、`.csproj`、`.sln`（`git status` 仅显示 `?? MD-files/DSH_Audit_Task.md.md` 与 `?? _audit/`；`git diff --stat` 为空）。
- ✅ 未执行任何 git 写操作（无 commit/push/branch/checkout/stash/add）。
- ✅ 未安装依赖、未修改任何 `.csproj` 或包引用。
- ✅ 本次审计的中间产物位于 `_audit/`（临时工作目录，含 5 份原始结论与作业协议），最终交付物为本文件。

---

*报告结束。*
