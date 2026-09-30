# WinToolBox.Core

[← 返回 WinToolBox 总览](../../README.md)

`WinToolBox.Core` 是 WinToolBox 各工具共用的基础类库（`net8.0-windows`），
把「配置、日志、通知、复制、U 盘识别、备份规则」这些通用能力收敛到一处，避免每个工具各写一套。

> 依赖 WinForms（`Notifier` 需要 `NotifyIcon`），所以目标框架是 `net8.0-windows`。
> 除参数校验外，所有磁盘 / 配置操作都不会把异常抛给调用方，错误会以返回值或日志形式给出。

## 类型一览

| 类型 | 作用 |
| --- | --- |
| `AppPaths` | 统一路径：`%AppData%\WinToolBox\...`（配置）、`%LocalAppData%\WinToolBox\logs`（日志），并提供 `EnsureDirectory` |
| `BackupConfig` | 备份配置模型：`BackupTargetDirectory`、`ExcludedExtensions`；含 `Clone()`（深拷贝）与 `Normalize()`（去空白、后缀补 `.`、转小写、去重） |
| `ConfigManager` | `config.json` 读写。读取永不抛异常（文件缺失 / 内容损坏都回落到默认配置，且不会覆盖损坏文件）；保存采用「先写 `.tmp` 再替换」，避免写一半损坏配置 |
| `Logger` / `LogLevel` / `LogEntry` | 按天切割的日志文件 `wintoolbox-yyyyMMdd.log`（默认保留 30 天）；`Info` / `Warn` / `Error`；写失败只记录 `LastError`，绝不因日志失败拖垮业务；`EntryWritten` 事件供界面**实时**显示日志 |
| `INotifier` / `Notifier` | 托盘气泡通知封装。可复用宿主的 `NotifyIcon`；在后台线程调用时会自动切回创建它的 UI 线程 |
| `ExcludeRules` | 复制排除规则（目录名 / 文件名 / 后缀，均忽略大小写）。默认排除 `System Volume Information`、`$RECYCLE.BIN`、`autorun.inf`、`desktop.ini`、`Thumbs.db`、`*.tmp` 等 |
| `FileCopier` / `CopyResult` / `CopyProgress` | 流式 + 增量复制引擎：固定缓冲区循环读写以支持大文件；按「大小 + 最后修改时间」跳过未修改文件；**不删除**目标中已存在的文件；支持进度回调与取消；单个文件失败会记入 `Errors` 并继续 |
| `UsbDetector` / `UsbDeviceInfo` | 可移动存储设备枚举：卷标、总容量、剩余空间、**卷序列号**、文件系统；唯一标识为「卷标 + 卷序列号 + 容量」（不使用会变化的盘符） |
| `BackupRules` | 备份业务规则：目标路径模板 `{备份根目录}\{卷标}_{序列号}\{yyyy-MM-dd}`、默认排除规则、目录名清洗、源与目标同卷校验 |

## 典型用法

```csharp
using WinToolBox.Core;

var logger = Logger.Instance;
var config = new ConfigManager(AppPaths.UsbBackupConfigFile, logger).Load();

// 1) 找 U 盘（唯一标识 = 卷标 + 序列号 + 容量）
var device = new UsbDetector(logger).GetRemovableDrives().FirstOrDefault();
if (device is null) return;

// 2) 按规则算出本次备份目录
var target = BackupRules.BuildTargetDirectory(config.BackupTargetDirectory, device, DateTime.Now);

// 3) 增量复制（跳过未修改文件、不删除目标文件、进度回调）
var copier = new FileCopier(logger);
var result = copier.CopyDirectory(
    device.RootPath,
    target,
    progressCallback: p => Console.WriteLine($"{p.Percent}% {p.CurrentFile}"),
    excludeRules: BackupRules.CreateDefaultExcludeRules(config.ExcludedExtensions));

logger.Info(result.Summary);
```

## 设计约定

- **不抛异常**：日志写失败 → `Logger.LastError`；配置损坏 → 回落默认值；单个文件复制失败 → `CopyResult.Errors` + `FailedFiles`。调用方只需检查返回值。
- **可测试**：`Logger`、`ConfigManager`、`FileCopier` 都支持注入目录 / 文件路径，单元测试使用临时目录，不触碰真实用户数据。
- **不执行任何外部程序**：复制引擎只读源、只写目标，绝不执行源目录中的任何可执行文件。
- **线程安全**：`Logger` 写入加锁；`Notifier` 自动切回 UI 线程；`FileCopier` 每实例独立，可并发使用（不同目标目录）。

## 单元测试

`tests/WinToolBox.Core.Tests` 覆盖：

- `FileCopier`：新文件 / 目录结构、二次运行全跳过、源更新只重拷改动文件、目标多余文件不被删除、默认与自定义排除规则、大文件二进制一致性、取消、异常场景
- `ConfigManager`：默认值、往返（含中文路径）、损坏 JSON 容错、规范化、原子写入无残留、删除与重建
- `BackupRules` / `Logger` / `ExcludeRules`：规则与日志行为

```powershell
dotnet test WinToolBox.sln
```
