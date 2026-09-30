using WinToolBox.Core;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// 单个设备的备份结果。
/// </summary>
public sealed class BackupOutcome
{
    /// <summary>是否成功完成备份。</summary>
    public bool Success { get; set; }

    /// <summary>是否因为条件不满足或已有备份在跑而跳过（不是错误）。</summary>
    public bool Skipped { get; set; }

    /// <summary>中文结果说明，用于日志与托盘通知。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>本次备份的目标目录（未执行时为空字符串）。</summary>
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>文件复制结果；被跳过或失败时为 null。</summary>
    public CopyResult? Copy { get; set; }

    /// <summary>创建一个“成功”结果。</summary>
    public static BackupOutcome Ok(string message, string targetDirectory, CopyResult copy) => new()
    {
        Success = true,
        Skipped = false,
        Message = message,
        TargetDirectory = targetDirectory,
        Copy = copy
    };

    /// <summary>创建一个“跳过”结果。</summary>
    public static BackupOutcome Skip(string message) => new()
    {
        Success = false,
        Skipped = true,
        Message = message
    };

    /// <summary>创建一个“失败”结果。</summary>
    public static BackupOutcome Fail(string message) => new()
    {
        Success = false,
        Skipped = false,
        Message = message
    };
}

/// <summary>
/// 备份服务：读取配置、校验目标目录、执行增量复制、记录日志并弹出托盘通知。
/// 安全红线：只读 U 盘（源）、只写备份目录（目标），绝不执行 U 盘中的任何程序，绝不删除目标中的任何文件。
/// </summary>
public sealed class BackupService
{
    /// <summary>进度日志的间隔（每处理多少个文件记录一次）。</summary>
    private const int ProgressLogInterval = 200;

    /// <summary>并发保护：同一时刻只允许一个备份任务运行。</summary>
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    private readonly ConfigManager _configManager;
    private readonly Logger _logger;
    private readonly INotifier _notifier;
    private readonly UsbDetector _detector;

    /// <summary>创建备份服务（构造函数注入 Core 组件）。</summary>
    public BackupService(ConfigManager configManager, Logger logger, INotifier notifier, UsbDetector detector)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    /// <summary>备份指定设备（可取消；被取消时返回失败结果并说明原因）。</summary>
    public BackupOutcome BackupDevice(UsbDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (device is null)
        {
            return BackupOutcome.Fail("设备信息为空，无法备份。");
        }

        if (!_backupLock.Wait(0))
        {
            // 正在备份：不排队、不阻塞，直接跳过
            const string busy = "已有一个备份任务正在进行，本次请求已跳过。";
            _logger.Warn(busy);
            return BackupOutcome.Skip(busy);
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return BackupOutcome.Fail("备份已被取消。");
            }

            return ExecuteBackup(device, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"备份已取消：{device.RootPath}");
            return BackupOutcome.Fail("备份已被取消。");
        }
        catch (Exception ex)
        {
            var message = $"备份 {device.RootPath} 失败：{ex.Message}";
            _logger.Error(message, ex);
            Notify("备份失败", message);
            return BackupOutcome.Fail(message);
        }
        finally
        {
            _backupLock.Release();
        }
    }

    /// <summary>备份当前所有已插入的 U 盘（逐个执行，单个失败不影响其它设备）。</summary>
    public IReadOnlyList<BackupOutcome> BackupAllAttached(CancellationToken cancellationToken = default)
    {
        var outcomes = new List<BackupOutcome>();

        IReadOnlyList<UsbDeviceInfo> devices;
        try
        {
            devices = _detector.GetRemovableDrives();
        }
        catch (Exception ex)
        {
            _logger.Error("枚举可移动磁盘失败，无法执行备份。", ex);
            outcomes.Add(BackupOutcome.Fail("枚举可移动磁盘失败：" + ex.Message));
            return outcomes;
        }

        if (devices.Count == 0)
        {
            const string none = "没有检测到可移动磁盘（U 盘），本次备份已跳过。";
            _logger.Warn(none);
            Notify("未检测到 U 盘", none);
            outcomes.Add(BackupOutcome.Skip(none));
            return outcomes;
        }

        foreach (var device in devices)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(BackupOutcome.Fail("备份已被取消。"));
                break;
            }

            outcomes.Add(BackupDevice(device, cancellationToken));
        }

        return outcomes;
    }

    /// <summary>执行一次完整备份（调用方已持有并发锁）。</summary>
    private BackupOutcome ExecuteBackup(UsbDeviceInfo device, CancellationToken cancellationToken)
    {
        var config = _configManager.Load();

        var targetRoot = (config.BackupTargetDirectory ?? string.Empty).Trim();
        if (targetRoot.Length == 0)
        {
            const string notConfigured = "尚未配置备份目标目录，请右键托盘图标选择“设置”。";
            _logger.Warn(notConfigured);
            Notify("备份未执行", notConfigured);
            return BackupOutcome.Fail(notConfigured);
        }

        // 不能把 U 盘备份回它自己
        if (BackupRules.IsTargetOnSameVolume(device.RootPath, targetRoot))
        {
            var sameVolume = $"目标目录与 U 盘位于同一个卷（{BackupRules.SanitizeName(targetRoot)}），已跳过，避免备份回 U 盘自身。";
            _logger.Warn(sameVolume);
            Notify("备份已跳过", sameVolume);
            return BackupOutcome.Skip(sameVolume);
        }

        if (!Directory.Exists(device.RootPath))
        {
            var missing = $"U 盘已不可用，跳过备份：{device.RootPath}";
            _logger.Warn(missing);
            return BackupOutcome.Fail(missing);
        }

        var targetDirectory = BackupRules.BuildTargetDirectory(targetRoot, device, DateTime.Now);
        var rules = BackupRules.CreateDefaultExcludeRules(config.ExcludedExtensions);

        _logger.Info($"开始备份：{device} -> {targetDirectory}");

        var copier = new FileCopier(_logger)
        {
            OverwriteExistingFiles = true,
            PreserveTimestamps = true
        };

        var progress = new ProgressReporter(_logger, device.VolumeLabel);

        var result = copier.CopyDirectory(
            device.RootPath,
            targetDirectory,
            progress.Report,
            rules,
            cancellationToken);

        if (result.Cancelled)
        {
            var cancelled = $"备份已取消：{device.RootPath} -> {targetDirectory}";
            _logger.Warn(cancelled);
            Notify("备份已取消", cancelled);
            return new BackupOutcome
            {
                Success = false,
                Skipped = false,
                Message = cancelled,
                TargetDirectory = targetDirectory,
                Copy = result
            };
        }

        if (result.FailedFiles > 0)
        {
            var failed = $"备份部分失败：{result.Summary}{Environment.NewLine}目标目录：{targetDirectory}";
            _logger.Error($"备份部分失败：{device.RootPath} -> {targetDirectory}；{result.Summary}");

            foreach (var error in result.Errors.Take(5))
            {
                _logger.Warn($"失败明细：{error}");
            }

            Notify("备份部分失败", failed);
            return new BackupOutcome
            {
                Success = false,
                Skipped = false,
                Message = failed,
                TargetDirectory = targetDirectory,
                Copy = result
            };
        }

        var title = string.IsNullOrWhiteSpace(device.VolumeLabel) ? device.DriveLetter : device.VolumeLabel;
        var message = $"“{title}”备份完成：{result.Summary}{Environment.NewLine}保存位置：{targetDirectory}";
        _logger.Info($"备份完成：{title} -> {targetDirectory}；{result.Summary}");
        Notify("U盘备份完成", message);

        return BackupOutcome.Ok(message, targetDirectory, result);
    }

    /// <summary>弹出通知；通知本身失败不能影响备份结论。</summary>
    private void Notify(string title, string message)
    {
        try
        {
            _notifier.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            _logger.Warn($"弹出通知失败：{title}", ex);
        }
    }

    /// <summary>
    /// 进度节流器：备份目录文件很多时，避免为每个文件都写一行日志。
    /// 注意：进度回调运行在调用 <see cref="FileCopier.CopyDirectory"/> 的线程上。
    /// </summary>
    private sealed class ProgressReporter
    {
        private readonly Logger _logger;
        private readonly string _deviceName;
        private int _lastLoggedCount;

        /// <summary>创建进度记录器。</summary>
        public ProgressReporter(Logger logger, string deviceName)
        {
            _logger = logger;
            _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "未命名U盘" : deviceName;
        }

        /// <summary>进度回调。</summary>
        public void Report(CopyProgress progress)
        {
            if (progress.TotalFiles == 0)
            {
                return;
            }

            var isFinished = progress.ProcessedFiles >= progress.TotalFiles;

            if (!isFinished && progress.ProcessedFiles - _lastLoggedCount < ProgressLogInterval)
            {
                return;
            }

            _lastLoggedCount = progress.ProcessedFiles;

            _logger.Info(
                $"备份进度（{_deviceName}）：{progress.ProcessedFiles}/{progress.TotalFiles}（{progress.Percent:0.0}%），" +
                $"新增/更新 {progress.CopiedFiles}，跳过 {progress.SkippedFiles}，失败 {progress.FailedFiles}，" +
                $"已复制 {CopyResult.FormatSize(progress.CopiedBytes)}");
        }
    }
}
