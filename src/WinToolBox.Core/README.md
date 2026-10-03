# WinToolBox.Core

[English](README_EN.md) | [简体中文](README.md)

[← 返回 WinToolBox 总览](../../README.md)

`WinToolBox.Core` 是 WinToolBox 各工具共用的基础类库（`net8.0-windows`），
把「配置、日志、通知、复制、U 盘识别、备份规则」这些通用能力收敛到一处，避免每个工具各写一套。

> 依赖 WinForms（`Notifier` 需要 `NotifyIcon`），所以目标框架是 `net8.0-windows`。
> 除参数校验外，所有磁盘 / 配置操作都不会把异常抛给调用方，错误会以返回值或日志形式给出。

## 类型一览

| 类型 | 作用 |
| --- | --- |
| `AppPaths` | 统一路径：`%AppData%\WinToolBox\...`（配置）、`%LocalAppData%\WinToolBox\logs`（共享日志）与 `LogDirectoryFor(工具名)`（按工具分目录），并提供 `EnsureDirectory` |
| `BackupConfig` | 备份配置模型：`BackupTargetDirectory`、`ExcludedExtensions`、`StrictContentVerification`（默认 `true`）、`HistoryRetentionDays`（默认 `30`，`0` = 永久保留）；含 `Clone()`（深拷贝）与 `Normalize()`（去空白、后缀补 `.`、转小写、去重） |
| `ConfigManager` | `config.json` 读写。读取永不抛异常（文件缺失 / 内容损坏都回落到默认配置，且不会覆盖损坏文件）；保存采用「先写 `.tmp` 再替换」，避免写一半损坏配置 |
| `Logger` / `LogLevel` / `LogEntry` | 按天切割的日志文件 `wintoolbox-yyyyMMdd.log`，经 `Logger.ForTool(...)` 写入 `logs\{工具名}\`（默认保留 30 天）；以 `FileShare.ReadWrite` 打开并对 `IOException` 退避重试，并发写不丢行；`Info` / `Warn` / `Error`；写失败记录 `LastError` 与 `FailedWriteCount`，绝不因日志失败拖垮业务；`EntryWritten` 事件供界面**实时**显示日志 |
| `INotifier` / `Notifier` | 托盘气泡通知封装。可复用宿主的 `NotifyIcon`；在后台线程调用时会自动切回创建它的 UI 线程（宿主必须在构造它之前装好 UI `SynchronizationContext`） |
| `ExcludeRules` | 复制排除规则（目录名 / 文件名 / 后缀，均忽略大小写）。默认排除 `System Volume Information`、`$RECYCLE.BIN`、`autorun.inf`、`desktop.ini`、`Thumbs.db`、`*.tmp` 等 |
| `FileCopier` / `CopyResult` / `CopyProgress` | 流式 + **内容校验**的增量复制引擎：固定缓冲区循环读写以支持大文件；大小相同则比内容（全量 SHA256，超过 16 MiB 用「大小 + mtime + 首尾 64 KiB 抽样」），`CopyResult` 给出 `VerifiedFiles` / `UnchangedAssumed`；惰性枚举源（不把整盘清单读进内存）、跳过目录联接 / 符号链接、连续失败早停、先写 `{目标}.tmp` 再原子替换；**不删除**目标中已存在的文件；支持进度回调与取消 |
| `HashEngine` | 常量内存的流式 MD5 / SHA1 / SHA256，支持进度与取消，并提供「先比大小再比哈希」的 `AreEqual` |
| `BackupMirrorService` / `MirrorResult` | U 盘镜像备份，实现「增量镜像 + 版本历史」产品语义：新文件写入 `current\`；修改过的文件**先**把旧版本**移入** `history\{日期}\` 再写新版本；U 盘上删除的文件**移入** `history\{日期}\`（软删除，绝不抹除）；过期 `history\{yyyy-MM-dd}\` 自动清理；同日同名冲突时每一版都以唯一名字保留 |
| `BackupManifest` | `manifest.json` 模型：最近备份时间、文件数、总大小、history 保留策略、与上次的对比结果、以及转入 history 的文件清单 |
| `UsbDetector` / `UsbDeviceInfo` | 可移动存储设备枚举：卷标、总容量、剩余空间、**卷序列号**、文件系统；唯一标识为「卷标 + 卷序列号 + 容量」（不使用会变化的盘符） |
| `BackupRules` | 备份业务规则：设备目录结构 `{备份根目录}\{卷标}_{序列号}\{ current | history | manifest.json }`（**不再有带日期的目标目录**）、默认排除规则、目录名清洗、history 目录名解析、源与目标同卷校验 |

## 典型用法

```csharp
using WinToolBox.Core;

var logger = Logger.ForTool("UsbBackup");
var config = new ConfigManager(AppPaths.UsbBackupConfigFile, logger).Load();

// 1) 找 U 盘（唯一标识 = 卷标 + 序列号 + 容量）
var device = new UsbDetector(logger).GetRemovableDrives().FirstOrDefault();
if (device is null) return;

// 2) 镜像备份：current\ 是最新副本，history\{日期}\ 保留旧版本
var mirror = new BackupMirrorService(logger);
var result = mirror.Mirror(
    sourceDirectory: device.RootPath,
    backupRootDirectory: config.BackupTargetDirectory,
    device: device,
    excludeRules: BackupRules.CreateDefaultExcludeRules(config.ExcludedExtensions),
    historyRetentionDays: config.HistoryRetentionDays);

logger.Info(result.Copy.Summary);
```

只需要「一次性复制」时，仍可直接使用 `FileCopier.CopyDirectory(...)`。

## 设计约定

- **不抛异常**：日志写失败 → `Logger.LastError` / `Logger.FailedWriteCount`；配置损坏 → 回落默认值；模板写盘失败 → `SaveTemplate` / `DeleteTemplate` 返回 false 并把原因放进 `LastPersistError`；单个文件复制失败 → `CopyResult.Errors` + `FailedFiles`。调用方只需检查返回值。
- **可测试**：`Logger`、`ConfigManager`、`FileCopier`、`TemplateManager` 都支持注入目录 / 文件路径，单元测试使用临时目录，不触碰真实用户数据。
- **不执行任何外部程序**：复制引擎只读源、只写目标，绝不执行源目录中的任何可执行文件。
- **线程安全**：`Logger` 按「日志文件路径」加锁（多实例写同一文件也不会丢行）；`Notifier` 自动切回 UI 线程；`FileCopier` 每实例独立，可并发使用（不同目标目录）。

## 单元测试

`tests/WinToolBox.Core.Tests` 覆盖：

- `FileCopier`：新文件 / 目录结构、二次运行全跳过、**同大小改写 + 时间戳回退也能识别**、大文件首尾抽样、惰性枚举（不物化清单）、跳过目录联接 / 符号链接、连续失败早停
- `ConfigManager`：默认值、往返（含中文路径）、损坏 JSON 容错、规范化、原子写入无残留、删除与重建
- `BackupMirrorService`：新增 / 修改 / 删除文件、软删除历史、同日多版本全部保留、history 保留期与清理、`manifest.json` 内容
- `Logger` / `TemplateManager`：按工具分日志目录、并发写不丢行、模板保存/删除失败如实上报而不是被吞
- `BackupRules` / `ExcludeRules` / `HashEngine`：规则、排除与哈希行为

```powershell
dotnet test WinToolBox.sln
```
