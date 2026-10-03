# DSH 任务书：WinToolBox P0/P1 问题修复（分五阶段执行）

## 0. 前置条件检查（必须先做）
1. 确认本机已安装 .NET 8 SDK，执行 `dotnet --version` 应输出 `8.0.x`。
2. 如果未安装，立即停止并提示用户安装 .NET 8 SDK，不要继续执行。
3. 在仓库根目录执行一次基线验证：
   ```
   dotnet build WinToolBox.sln -c Release
   dotnet test WinToolBox.sln -c Release
   ```
   记录测试通过数量。如果基线不通过，先报告给用户，不要开始修复。

## 1. 任务背景（自包含说明）
- 仓库：WinToolBox（https://github.com/zhuyuebin/WinToolBox），Windows 小工具集合 monorepo。
- 技术栈：C# (.NET 8) + WinForms + xUnit。
- 现有工具：
  - `src/Tools/FileMaster/`：文件/文件夹管理工具（12,767 行）
  - `src/Tools/UsbBackup/`：U盘自动备份托盘程序（3,098 行）
- 共享库：`src/WinToolBox.Core/`（30 个公共类型，但只有 4 个被两个工具共用）
- 测试：`tests/WinToolBox.Core.Tests`、`tests/FileMaster.Tests`（共 422 `[Fact]` + 58 `[Theory]`）
- **注意：你是在全新对话中执行，没有历史上下文。不要假设存在 `WinToolBox.App`、`Tabs/`、`FolderCreatorTab`、`DirectoryPicker` 等历史设计中提到的类，这些都不存在。**

## 2. 任务目标
修复一份专业审计报告中的 **3 个 P0 致命问题** 和 **20 个 P1 高优先级问题**。修复过程分 5 个阶段，每阶段结束必须编译+测试通过后才能继续。

## 3. UsbBackup 产品语义最终决策（重要，先读）

**决策：UsbBackup 的产品定位是"U盘的增量镜像 + 30天版本历史"。**

### 目录结构
```
{目标目录}/{卷标}_{序列号}/
├─ current/                    ← U盘的最新副本（每次增量更新）
│  ├─ 文档/
│  └─ 图片/
├─ history/                    ← 被删除/被覆盖的旧版本
│  ├─ 2026-10-01/
│  │  └─ 误删的文件.docx
│  └─ 2026-10-03/
│     └─ 被覆盖的旧版本.docx
└─ manifest.json               ← 记录最近一次备份信息
```

### 核心逻辑
1. **U 盘里的新文件** → 复制到 `current/`。
2. **U 盘里修改过的文件** → 新版本写入 `current/`，**旧版本移到 `history/{今天}/`**。
3. **U 盘里删除的文件** → `current/` 里对应文件**移到 `history/{今天}/`，不删除**。
4. **`history` 默认保留 30 天**，超过 30 天的目录自动清理。
5. **不再使用带日期的目标目录**（如 `{yyyy-MM-dd}`），整个 U 盘只对应一个备份根目录。
6. **不再实现"每日全量快照"**。

### 配置项
- `BackupConfig.HistoryRetentionDays`（默认 30，可配置为 7/30/90/永久）。
- 当用户配置为"永久"时，UI 明确提示磁盘消耗风险。

### 文案调整
- README 与界面文案把"增量备份"改为"增量备份 + 30天版本历史"。

**P1-8、P0-1、P1-9、P2-13 的实现都必须符合以上产品语义。**

## 4. 阶段划分与详细修复清单

---

### 🚩 阶段一：P0 数据安全修复（最高优先级）

#### [P0-1] 增量备份判定只看"大小 + 时间"，内容变化被永久静默跳过
**文件**：`src/WinToolBox.Core/FileCopier.cs`
**位置**：`Decide` L175-L198（核心判定 L190-L195）；`StreamCopy` L201-L240
**问题**：
```csharp
if (sourceInfo.Length == targetInfo.Length &&
    targetInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
{
    return CopyDecision.SkipUnchanged;   // 内容变了也会跳过
}
```
复制成功后 L232 把目标时间戳强制对齐为源时间，使下次判定继续成立。
**修复要求**：
1. 判定逻辑改为：先比大小，大小相同再调用 `HashEngine.AreEqual(src, dst, HashAlgorithmKind.SHA256, ct)` 做内容校验。
2. 对大文件（如 > 16MB）采用"大小 + mtime + 首尾各 64 KiB 抽样哈希"折中方案。
3. 在 `BackupConfig` 增加 `bool StrictContentVerification`（默认 `true`）。
4. 把 `>=` 改为 `>`，让同刻文件重拷一次。
5. `CopyResult` 增加 `VerifiedFiles` 和 `UnchangedAssumed` 两个计数，摘要明确写"（未做内容校验）"或"（已 SHA256 校验）"。
6. 补单元测试：**同大小改写 + 时间戳回退**，断言 `CopiedFiles == 1`。
7. **结合产品语义**：当判定为"内容变化"时，旧版本需要移入 `history/{今天}/`，而不是直接覆盖（详见 P1-8）。

**验收标准**：新测试通过；原有 `FileCopierTests.cs` 全部通过。

---

#### [P0-2] "删空目录"实为无差别递归永久删除，且扫描与删除之间不重新判空
**文件**：
- `src/Tools/FileMaster/Services/EmptyFolderCleanerService.cs`（`Delete` L107-L161，判定 L128-L140）
- `src/Tools/FileMaster/Services/SafeDelete.cs`（`DeleteDirectory` L34-L50，递归删除 L49）
- `src/Tools/FileMaster/Services/AutoArchiverService.cs`（`Apply` L443-L470，硬编码 L455）
- `src/Tools/FileMaster/UI/EmptyFolderDialog.cs`（L85 永久删除按钮，L172 默认全选）
**问题**：
```csharp
// EmptyFolderCleanerService.cs L128-L140：唯一检查是"目录还在"
if (!Directory.Exists(directory)) { ... }
else { SafeDelete.DeleteDirectory(directory, useRecycleBin); ... }

// SafeDelete.cs L48-L49：永久删除
Directory.Delete(path, recursive: true);

// AutoArchiverService.cs L455：硬编码永久删除
useRecycleBin: false
```
**修复要求**：
1. `EmptyFolderCleanerService.Delete` 循环内删除前重新调用 `IsEmptyDirectory(directory)`，返回 false 时记为 `Skipped` 而非删除。
2. `SafeDelete` 增加新方法 `DeleteEmptyDirectory(path, useRecycleBin)`，使用 `Directory.Delete(path, recursive: false)`（非空即抛异常，天然安全）。
3. `ArchiveOptions` 增加 `CleanEmptyFoldersUseRecycleBin`（默认 `true`），由对话框复选框控制，删除 `AutoArchiverService.cs:455` 的硬编码。
4. 确认框文案改为明确提示"永久删除、不进回收站"。
5. 补单元测试：
   - 扫描后往目录里塞文件，再执行删除，断言该目录被跳过。
   - `useRecycleBin: true` 时走回收站。
   - `useRecycleBin: false` 时二次确认文案含"永久"。

**验收标准**：新测试通过；UI 上执行删除时对话框文案正确。

---

#### [P0-3] `publish-local.ps1` 对任意目标目录执行无保护递归删除
**文件**：`publish-local.ps1`
**位置**：L30、L32、L34-L36、L39
**问题**：护栏只排除"工作区内"，`-Target D:\` 会清空整盘。
**修复要求**：
1. 加白名单/结构校验：
   - 拒绝驱动器根（`$targetFull -eq [IO.Path]::GetPathRoot($targetFull)`）。
   - 拒绝 `%SystemRoot%`、`%ProgramFiles%`、`%USERPROFILE%` 顶层。
   - 要求目标"不存在"或"带标记文件（如 `.wintoolbox-publish`）"才允许清空。
2. 用 `[CmdletBinding(SupportsShouldProcess)]` + `$PSCmdlet.ShouldProcess(...)`，默认 `-Confirm`。
3. L39 的 `-ErrorAction SilentlyContinue` 改为 `Stop`。
4. 修复 P3-3 默认参数 bug：护栏比较前补分隔符，或用 `Path.GetRelativePath` 判断。
5. 修复 P3-4 硬编码路径：默认值改为可移植路径（如 `Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WinToolBox\tools'`）。

**验收标准**：`publish-local.ps1 -Target D:\` 必须拒绝执行；默认参数裸跑能正常发布。

---

### 🚩 阶段二：UsbBackup 产品语义重构（按第 3 节决策实现）

#### [P1-8] UsbBackup 目标目录改为"固定目录 + 真增量 + 软删除历史"
**文件**：`src/Tools/UsbBackup/BackupService.cs`、`src/WinToolBox.Core/BackupRules.cs`、`src/WinToolBox.Core/BackupConfig.cs`
**位置**：`BackupService` L191（目标目录含 `{yyyy-MM-dd}`）、L204-L209
**修复要求**：
1. 目标目录改为 `{目标目录}/{卷标}_{序列号}/current/`，**不再带日期**。
2. 新增 `history` 目录管理逻辑：
   - U 盘里删除的文件 → 从 `current/` 移到 `history/{yyyy-MM-dd}/`，保留原相对路径。
   - U 盘里修改的文件 → `current/` 中的旧版本先移到 `history/{yyyy-MM-dd}/`，再写入新版本。
   - 新文件直接写入 `current/`。
3. `BackupConfig` 增加 `int HistoryRetentionDays`（默认 30，UI 可选 7/30/90/永久）。
4. 每次备份结束后清理超过保留期的 `history/{yyyy-MM-dd}/` 目录。
5. 每次备份生成/更新 `manifest.json`，记录：备份时间、文件总数、总大小、history 保留策略、上次备份对比结果。
6. README 与界面文案把"增量备份"改为"增量备份 + 30天版本历史"。
7. 补单元测试：
   - U 盘里删文件后，备份不删除 `current/` 里对应文件，而是移到 `history`。
   - U 盘里改文件后，旧版本进入 `history`。
   - 超过保留期的 `history` 目录被清理。
   - `manifest.json` 内容正确。

**验收标准**：新测试通过；README 与 UI 文案一致；手动测试：插入 U 盘 → 备份 → 删 U 盘文件 → 再次备份 → 确认文件在 `history/` 而非消失。

---

#### [P1-20] 备份预扫描把整盘文件清单物化进内存，复制开始前长时间无进度
**文件**：`src/WinToolBox.Core/FileCopier.cs`（`CollectFiles` L243-L304）
**问题**：`CollectFiles` 返回完整 `List<FileEntry>`，大 U 盘（数十万文件）下是"全量元数据入内存 + 复制前零进度"。
**修复要求**：
1. 改为惰性遍历（`EnumerateFiles` + `yield return`），或分批处理（每批 1000 个）。
2. 预扫描阶段上报"已发现 N 个文件"的进度。
3. 若要保留总量估算，先做一次轻量的 `EnumerateFiles` 计数（只计数不存路径）。

**验收标准**：用包含数万文件的目录测试，内存峰值明显下降。

---

### 🚩 阶段三：复制/备份引擎正确性修复

#### [P1-1] 复制/同步引擎完全不识别目录联接与符号链接
**文件**：`src/WinToolBox.Core/FileCopier.cs`（`CollectFiles` L243-L304）、`src/Tools/FileMaster/Services/FolderSyncService.cs`（`EnumerateFiles/EnumerateDirectories` L341-L382）
**问题**：两处都不检查 `FileAttributes.ReparsePoint`，而同仓库 `RuleGenerator.cs:246`、`FolderChecker.cs:280`、`EmptyFolderCleanerService.cs:39` 共 7 处**显式跳过**。
**修复要求**：
1. `FileCopier.CollectFiles` 入栈前加：
   ```csharp
   if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0) {
       logger?.Warn($"跳过目录联接/符号链接：{directory}");
       continue;
   }
   ```
2. `FolderSyncService` 同样处理。
3. `IsSameOrChildPath`（L307-L315）改为带分隔符补全的比较，或使用 `Path.GetRelativePath`。
4. 补单测："源目录含 Junction 时不复制其目标内容"。

**验收标准**：新测试通过。

---

#### [P1-3] `FileCopier` 预扫描把惰性枚举包在 try 内，一个无权限目录会让整个备份 0 文件复制
**文件**：`src/WinToolBox.Core/FileCopier.cs`（`CollectFiles` L243-L304，关键 L253-L267 与 L289）
**问题**：
```csharp
try {
    subDirectories = Directory.EnumerateDirectories(current);   // 只拿到迭代器
    files = Directory.EnumerateFiles(current);
} catch (Exception ex) {
    logger?.Warn(...);        // 实际上永远进不来
    continue;
}
foreach (var file in files) { ... }        // 真正的枚举发生在这里，已在 try 之外
```
**修复要求**：
1. 改为立即执行的 `Directory.GetDirectories` / `Directory.GetFiles`，包在 `try` 内并 `catch` 具体异常类型。
2. 或把两个 `foreach` 整体移入 `try` 内。
3. 补单测："存在不可访问目录时其余文件仍被复制"。

**验收标准**：新测试通过。

---

#### [P1-6] 退出流程既不取消也不等待正在运行的备份，进程带着截断的目标文件结束
**文件**：`src/Tools/UsbBackup/TrayApplicationContext.cs`、`src/WinToolBox.Core/FileCopier.cs`
**位置**：`ExitApplication` L340-L377（`ExitThread()` L376）、`StartBackupTask` L194-L231（`_ = Task.Run(...)` L211、L219 未传 token）；`FileCopier.StreamCopy` L212-L218（`FileMode.Create`）
**修复要求**：
1. `BackupService` 暴露 `CancellationTokenSource`（每次备份创建并保存，`BackupDevice` 传递该 token）。
2. `ExitApplication` 第一步 `cts.Cancel()` 并 `task.Wait(TimeSpan.FromSeconds(5))`，超时提示"备份仍在进行，是否强制退出"。
3. `StartBackupTask` 保存 `Task` 句柄，不要用 `_ =`。
4. 退出确认文案在 `_backupRunning` 时改为"正在备份，退出将中断备份"。
5. `FileCopier.StreamCopy` 对目标文件改为"先写 `.tmp` → 复制完成 → 原子替换 `File.Move(tmp, target, overwrite: true)`"，避免中途失败留下截断文件。

**验收标准**：手动测试大文件复制途中点退出，确认不会留下截断文件。

---

#### [P1-7] 取消令牌根本没有传到备份 I/O
**文件**：`src/Tools/UsbBackup/TrayApplicationContext.cs`、`BackupService.cs`、`MainForm.cs`
**问题**：`FileCopier.StreamCopy` L221-L225 **已经**正确实现了 `cancellationToken.ThrowIfCancellationRequested()`，但上层从未把 token 传下去。
**修复要求**：
1. `BackupService` 持有每次任务的 `CancellationTokenSource`。
2. `BackupAllAttached` / `BackupDevice` 显式接收 token 并透传到 `FileCopier`。
3. 主界面与托盘菜单增加"取消备份"入口（备份中启用）。
4. 补单测："取消能中断备份并返回已执行清单"。

**验收标准**：手动测试点击取消，备份在数秒内停止。

---

#### [P1-9] 目标空间不足无预检、失败不早停、失败明细无上限累积
**文件**：`src/Tools/UsbBackup/BackupService.cs`
**位置**：L191-L245（备份主循环）
**修复要求**：
1. 备份前用 `DriveInfo.AvailableFreeSpace` 与待复制总字节比对，不足时立即失败并给出"需要 X / 可用 Y"。
2. 连续失败超过阈值（如 20 个）时中止并提示可能原因。
3. 失败明细限制条数（保留前 N 条 + 总数）。
4. **结合产品语义**：预检时需要考虑 `history` 目录的额外空间占用（旧版本保留）。

**验收标准**：用容量不足的 U 盘测试，备份立即失败并给出清晰提示。

---

### 🚩 阶段四：FileMaster 文件操作安全修复

#### [P1-10] 文件占用查询静默截断到 256 个文件，摘要却断言"没有被任何进程占用"
**文件**：`src/Tools/FileMaster/Services/FileUnlockerService.cs`、`Models/UnlockModels.cs`、`UI/FileUnlockerDialog.cs`
**位置**：`MaxFilesPerQuery` L22；`ExpandDirectoryFiles` L240-L263；`FileLockQueryResult.Summary` L111-L113
**修复要求**：
1. `FileUnlockQueryResult` 增加 `Truncated` 和 `TotalFileCount` 字段。
2. `Summary` 在截断时改为"已检查前 256 个文件，未发现占用（该目录文件更多，结果不完整）"。
3. UI 在 `Truncated` 时用警告色显示"已检查 256 / 共 N 个文件"。
4. 治本方案：提高上限并在超限时**明确失败**而非静默截断。

**验收标准**：用含 300+ 文件的目录测试，UI 显示"结果不完整"。

---

#### [P1-11] 重复文件查找不校验扫描目录的嵌套/等价关系
**文件**：`src/Tools/FileMaster/Services/DuplicateFinderService.cs`、`UI/DuplicateFinderDialog.cs`
**位置**：`Find` L29-L33、L46-L96、L129-L148
**修复要求**：
1. 对**文件**做规范化去重（`Path.GetFullPath` + 去尾随分隔符 + `OrdinalIgnoreCase`）。
2. 对扫描目录做"若 A 是 B 的父路径则只保留 A"的归一化。
3. 更强可用 `File.OpenHandle` + `GetFileInformationByHandle` 的卷序列号/文件索引识别同一文件。
4. 对话框在添加目录时提示已自动合并。

**验收标准**：用嵌套目录测试，不产生"自重复组"。

---

#### [P1-12] 自动分类的目标子目录来自自由文本规则，路径未规范化校验 → 可写到目标目录之外
**文件**：`src/Tools/FileMaster/Services/AutoArchiverService.cs`
**问题**：规则第三列完全无校验，直接 `Path.Combine(targetRoot, ruleValue)`。`..\..\Windows\Temp` 或绝对路径 `D:\elsewhere` 会写到目标外。
**修复要求**：
1. 目标子目录先经 `Path.GetFullPath(Path.Combine(targetRoot, value))` 规范化。
2. 用"带分隔符补全的前缀比较"或 `Path.GetRelativePath` 断言其仍在 `targetRoot` 之内。
3. 不在范围内则记为规则错误并在预览中拒绝该条。
4. 非法字符复用 `RuleParser` 的校验逻辑。

**验收标准**：输入 `..\..\Windows` 作为规则时被拒绝。

---

#### [P1-13] 同步（镜像）在复制前先删，"删除成功而复制失败"会留下比同步前更少的目标
**文件**：`src/Tools/FileMaster/Services/FolderSyncService.cs`
**位置**：`Apply` L178-L240、`BuildPlan` L27-L117
**修复要求**：
1. 改为"先复制/更新，全部成功后再执行删除"。
2. 或在删除前把待删项移入本工具管理的暂存目录（`.wintoolbox-trash`），整轮成功后清理、失败时回滚。
3. 至少保证"删除某文件"之前其替代副本已就位。

**验收标准**：模拟复制失败场景，目标目录不会比同步前更少。

---

#### [P1-14] 同步的"删除多余目录"绕过忽略名单
**文件**：`src/Tools/FileMaster/Services/FolderSyncService.cs`
**位置**：`BuildPlan` L77-L117、`EnumerateFiles` L341-L360
**修复要求**：
1. 生成 `DeleteDirectory` 前先判定该目录子树内是否存在被忽略条目。
2. 存在则**放弃整目录删除**，退化为逐文件删除。
3. 在计划 `Reason` 中说明原因。
4. 让枚举额外返回 `ignoredRelativePaths`，或按目录删除前用 `Directory.EnumerateFileSystemEntries` 复核。

**验收标准**：目标侧 `.git` 目录内有文件时，镜像不删除该目录。

---

#### [P1-19] 自动分类不校验"目标目录位于源目录内"，重复运行会逐层自我归档
**文件**：`src/Tools/FileMaster/Services/AutoArchiverService.cs`
**位置**：扫描 L280、唯一守卫 L319-L323、目标路径拼装 L313
**修复要求**：
1. `BuildPlan` 中拒绝"目标根位于源根内"或"源根位于目标根内"的组合。
2. 用带分隔符补全的前缀比较，不要用裸 `StartsWith`。
3. 若确实需要允许嵌套，则在扫描时排除目标子树，并把 L319 的守卫扩展为"目标位于目标根之下且不等于源文件"的完整判定。

**验收标准**：源 `D:\Data`、目标 `D:\Data\Sorted` 时被拒绝。

---

### 🚩 阶段五：日志、UI、发布链路修复

#### [P1-2] 托盘与通知器捕获的 UI 同步上下文恒为 null
**文件**：`src/Tools/UsbBackup/TrayApplicationContext.cs`、`src/WinToolBox.Core/Notifier.cs`
**位置**：`TrayApplicationContext.cs:42`、L55、`PostToUi` L285-L302、`CompleteBackupRequest` L234-L243
**修复要求**（三选一，保持一致性）：
1. L42 之前显式 `SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext())`。
2. 把 `_uiContext`/`Notifier` 延迟到 `_mainForm` 构造之后创建（或改用 `Lazy<T>`）。
3. `PostToUi` 改为 `_mainForm` 的 `BeginInvoke`，并在 `InvokeRequired == false` 时直接执行。
4. 把 `PostToUi` 的 catch 从"仅记日志"改为降级提示。

**验收标准**：附加调试器运行，备份完成气泡正常显示。

---

#### [P1-4] 两个工具共用同一日志文件、用 `FileShare.Read` 并发写，失败被静默吞掉
**文件**：`src/WinToolBox.Core/AppPaths.cs:57-L58`、`Logger.cs:88-L99`、L111-L158
**修复要求**：
1. 日志文件名带工具名：`AppPaths` 增加 `LogDirectoryFor(string toolName)`，`Logger` 构造接收工具名（默认值保持兼容）。
2. 写入改为 `new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096)`。
3. 对 `IOException` 做 3 次 × 50ms 退避重试。
4. `Logger` 增加 `HasWriteFailure` 对外信号，或让 `Program.cs` 退出时提示"本次运行有 N 条日志未能写入"。

**验收标准**：模拟两个工具同时运行，日志不丢行。

---

#### [P1-5] `TemplateManager` 保存/删除模板失败被吞掉，UI 仍提示"模板已保存"
**文件**：`src/WinToolBox.Core/Services/TemplateManager.cs`（L212-L227、L356-L379）、`src/Tools/FileMaster/MainForm.cs`（L363-L366）
**修复要求**：
1. `Persist()` 返回 `bool`（或 `out string? error`）。
2. `SaveTemplate` / `DeleteTemplate` / `EnsureDefaults` 把写盘结果暴露给调用方。
3. 最简做法：让 `Persist` 直接抛 `IOException`，由 `MainForm` 现有 `catch` 显示失败。

**验收标准**：模拟磁盘满，UI 显示"保存失败"。

---

#### [P1-16] 无 tag 与 `<Version>` 的一致性校验
**文件**：`.github/workflows/release.yml`、`Directory.Build.props`
**修复要求**：
1. 在 `release.yml` 的 restore 之前加一步校验：
   ```powershell
   [xml]$props = Get-Content Directory.Build.props
   $v = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
   if ("v$v" -ne "${{ github.ref_name }}") { throw "标签 ${{ github.ref_name }} 与版本 $v 不一致" }
   ```
2. 或改用 `-p:Version=${{ github.ref_name }}`（去 `v` 前缀）让标签成为唯一版本源。

**验收标准**：模拟标签与版本不一致，CI 报错。

---

#### [P1-17] `README_EN.md` 整份停留在 `FolderCreator` 时代
**文件**：`README_EN.md`
**位置**：L20、L35、L53、L72、L75、L93、L96
**修复要求**：
1. 把 `README_EN.md` 与 `README.md` 对齐重写：
   - 工具名 `FolderCreator` → `FileMaster`
   - 路径 `src/Tools/FolderCreator/` → `src/Tools/FileMaster/`
   - 包名 `FolderCreator-win-x64.zip` → `FileMaster-win-x64.zip`
   - 测试项目 `FolderCreator.Tests` → `FileMaster.Tests`
   - 文档索引全部更新
2. 把"双语文档同步"加入发布检查清单。
3. 顺手修复 P3-8：`UsbBackup/README.md:178` 和 `README_EN.md:178` 的 `../FolderCreator/README*.md` 链接改为 `../FileMaster/README*.md`。
4. 同时更新 UsbBackup 的 README 与界面文案，把"增量备份"改为"增量备份 + 30天版本历史"（配合 P1-8）。

**验收标准**：英文用户按 README 能正常下载和使用；文案与实际行为一致。

---

#### [P1-18] 进程完整性检查 `fail-open`：读不到完整性级别时按"正常"处理
**文件**：`src/Tools/UsbBackup/ProcessIntegrity.cs`（L56-L102）、`Program.cs:103`
**修复要求**：
1. 引入三态：`enum IntegrityCheckResult { Normal, Restricted, Unknown }`。
2. `Unknown` 时输出"无法确定完整性级别（原因），若托盘图标不显示请…"的提示。
3. P/Invoke 声明了 `SetLastError = true` 却从不读 `Marshal.GetLastWin32Error()`，应把错误码写入日志。

**验收标准**：模拟 API 失败，输出 Unknown 提示。

---

## 5. 约束与注意事项
1. **每阶段结束必须**：`dotnet build WinToolBox.sln -c Release` + `dotnet test WinToolBox.sln -c Release`，全通过后才能进入下一阶段。
2. **不要一次改太多**：每个修复项独立提交（如可能），便于回滚。
3. **不要自动 git push**：等用户本地验证后再手动提交。
4. **补测试**：每个修复项必须有对应的新单元测试，覆盖"修复前会失败、修复后通过"的场景。
5. **不改与本次修复无关的代码**：避免扩大改动范围。
6. **不碰注册表**：本任务不涉及注册表操作。
7. **遇到编译错误自行修复**，直到 `dotnet build` 成功。
8. **P1-8 的决策已固化**：不要重新询问用户选哪个方案，直接按第 3 节的"方案 C：固定目录 + 真增量 + 软删除历史"实现。
9. **所有涉及 UsbBackup 备份语义的修复项**（P0-1、P1-8、P1-9、P1-20），都必须符合第 3 节的产品语义。

## 6. 交付物要求
1. 5 个阶段的修复代码，每阶段独立可编译、测试通过。
2. 每个修复项对应的新单元测试。
3. 一份 `FIX_REPORT.md` 放在仓库根目录，记录：
   - 每个修复项：文件、改动摘要、新增测试、测试结果。
   - 未修复的项及原因（如有）。
   - 阶段间的测试通过数量对比。
4. 每阶段完成后向我汇报：修改了哪些文件、`dotnet build` 和 `dotnet test` 结果、是否遇到需要决策的问题。

## 7. 执行顺序总结
```
阶段一（P0 数据安全）→ 编译+测试 → 汇报 → 用户确认
阶段二（UsbBackup 产品语义重构）→ 编译+测试 → 汇报 → 用户确认
阶段三（复制/备份引擎正确性）→ 编译+测试 → 汇报 → 用户确认
阶段四（FileMaster 文件操作安全）→ 编译+测试 → 汇报 → 用户确认
阶段五（日志/UI/发布链路）→ 编译+测试 → 汇报 → 用户确认
```

**任何阶段未通过测试，不得进入下一阶段。**