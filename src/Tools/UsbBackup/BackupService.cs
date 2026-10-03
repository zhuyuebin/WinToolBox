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

    /// <summary>是否因为用户取消而中止。</summary>
    public bool Cancelled { get; set; }

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

    /// <summary>创建一个“取消”结果。</summary>
    public static BackupOutcome Cancel(string message) => new()
    {
        Success = false,
        Skipped = false,
        Cancelled = true,
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

    /// <summary>预扫描日志间隔（每发现多少个文件记录一次“已发现 N 个文件”）。</summary>
    private const int ScanDiscoveryLogInterval = 2000;

    /// <summary>并发保护：同一时刻只允许一个备份任务运行。</summary>
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    /// <summary>当前正在运行的备份的取消源（没有备份在跑时为 null）。</summary>
    private volatile CancellationTokenSource? _currentBackupCts;

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

    /// <summary>当前是否有备份正在运行。</summary>
    public bool IsBackupRunning => _currentBackupCts is not null;

    /// <summary>
    /// 请求取消当前正在运行的备份（不阻塞）。
    /// <para>取消令牌会一路传到文件 I/O，因此大文件复制也能在数秒内停下来。</para>
    /// </summary>
    /// <returns>是否真的发出了取消（没有备份在跑时返回 false）。</returns>
    public bool CancelCurrentBackup()
    {
        var cts = _currentBackupCts;

        if (cts is null)
        {
            return false;
        }

        try
        {
            _logger.Warn("用户请求取消正在进行的备份。");
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // 恰好在这一刻备份结束了：不算错误
            return false;
        }
    }

    /// <summary>
    /// 把外部传入的取消令牌与内部取消源合并：任一取消都能中止本次备份。
    /// </summary>
    private CancellationTokenSource BeginBackupScope(CancellationToken externalToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _currentBackupCts = cts;
        return cts;
    }

    /// <summary>结束备份作用域：清空当前取消源并释放资源。</summary>
    private void EndBackupScope(CancellationTokenSource cts)
    {
        // 只有仍指向自己时才清空，避免把后来者的句柄清掉
        if (ReferenceEquals(_currentBackupCts, cts))
        {
            _currentBackupCts = null;
        }

        cts.Dispose();
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

        // 内部取消源 + 外部令牌：用户点「取消备份」或调用方取消都能中止 I/O
        var scope = BeginBackupScope(cancellationToken);

        try
        {
            if (scope.IsCancellationRequested)
            {
                return BackupOutcome.Cancel("备份已被取消。");
            }

            return ExecuteBackup(device, scope.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"备份已取消：{device.RootPath}");
            return BackupOutcome.Cancel("备份已被取消。");
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
            EndBackupScope(scope);
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
                outcomes.Add(BackupOutcome.Cancel("备份已被取消。"));
                break;
            }

            var outcome = BackupDevice(device, cancellationToken);
            outcomes.Add(outcome);

            if (outcome.Cancelled)
            {
                _logger.Warn("备份已被取消，剩余设备不再处理。");
                break;
            }
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
            const string notConfigured = "尚未配置备份目标目录：请在主界面选择目录后点「保存设置」再备份。";
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

        var targetDirectory = BackupRules.BuildCurrentDirectory(targetRoot, device);
        var rules = BackupRules.CreateDefaultExcludeRules(config.ExcludedExtensions);

        _logger.Info($"开始备份：{device} -> {targetDirectory}");

        // ---- P1-9.1 空间预检：先算清需要多少、可用多少，不足立即失败 ----
        var spaceError = TryCheckFreeSpace(device.RootPath, targetRoot, device, rules, out var spacePlan);
        if (spaceError is not null)
        {
            _logger.Error($"备份中止：{spaceError}");
            Notify("备份未执行", spaceError);
            return BackupOutcome.Fail(spaceError);
        }

        _logger.Info(
            $"空间预检通过：需要 {CopyResult.FormatSize(spacePlan.RequiredBytes)}" +
            $"（含 history 预留 {CopyResult.FormatSize(spacePlan.HistoryReserveBytes)}），" +
            $"可用 {CopyResult.FormatSize(spacePlan.AvailableBytes)}。");

        var mirror = new BackupMirrorService(_logger);
        var progress = new ProgressReporter(_logger, device.VolumeLabel);

        MirrorResult mirrorResult;
        try
        {
            mirrorResult = mirror.Mirror(
                sourceDirectory: device.RootPath,
                backupRootDirectory: targetRoot,
                device: device,
                excludeRules: rules,
                historyRetentionDays: config.HistoryRetentionDays,
                strictContentVerification: config.StrictContentVerification,
                progressCallback: progress.Report,
                scanProgress: progress.ReportScan,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"备份已取消：{device.RootPath}");
            return BackupOutcome.Cancel("备份已被取消。");
        }

        var result = mirrorResult.Copy;

        if (mirrorResult.Cancelled || result.Cancelled)
        {
            var cancelled = $"备份已取消：{device.RootPath} -> {targetDirectory}";
            _logger.Warn(cancelled);
            Notify("备份已取消", cancelled);
            return new BackupOutcome
            {
                Success = false,
                Skipped = false,
                Cancelled = true,
                Message = cancelled,
                TargetDirectory = targetDirectory,
                Copy = result
            };
        }

        // P1-9.2 连续失败早停：Mirror 内部达到阈值即中止本轮，这里给出可执行的提示
        if (mirrorResult.AbortedByFailureThreshold)
        {
            var tooMany =
                $"连续失败已达 {BackupMirrorService.ConsecutiveFailureAbortThreshold} 个，已提前中止本轮备份。" +
                Environment.NewLine + "常见原因：U 盘接触不良或已被拔出、目标磁盘写满、文件被其它程序独占、权限不足。" +
                Environment.NewLine + $"已复制 {result.CopiedFiles} 个、跳过 {result.SkippedFiles} 个、失败 {result.FailedFiles} 个。" +
                Environment.NewLine + "目标目录：" + targetDirectory;

            _logger.Error(tooMany);
            Notify("备份已中止", tooMany);
            return new BackupOutcome
            {
                Success = false,
                Skipped = false,
                Message = tooMany,
                TargetDirectory = targetDirectory,
                Copy = result
            };
        }

        if (mirrorResult.FailedFiles > 0)
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
        var historyText = config.KeepsHistoryForever
            ? "history 永久保留"
            : $"history 保留 {config.HistoryRetentionDays} 天";

        var message =
            $"“{title}”备份完成（增量备份 + 版本历史）：{result.Summary}" +
            (mirrorResult.ArchivedFiles > 0
                ? $"保留旧版本 {mirrorResult.ArchivedFiles} 个到 history/{DateTime.Now:yyyy-MM-dd}。"
                : string.Empty) +
            Environment.NewLine + $"保存位置：{targetDirectory}（{historyText}）";

        _logger.Info($"备份完成：{title} -> {targetDirectory}；{result.Summary}");
        Notify("U盘备份完成", message);

        return BackupOutcome.Ok(message, targetDirectory, result);
    }

    /// <summary>
    /// P1-9.1 空间预检：估算本次需要写入的字节数，与目标卷可用空间比对。
    /// <para>结合产品语义，预检时额外为 history 预留空间（旧版本会被保留而不是覆盖）。</para>
    /// </summary>
    /// <returns>不足时返回中文错误说明；充足时返回 null 并给出 <paramref name="plan"/>。</returns>
    private static string? TryCheckFreeSpace(
        string source,
        string targetRoot,
        UsbDeviceInfo device,
        ExcludeRules rules,
        out SpacePlan plan)
    {
        plan = default;

        try
        {
            var targetFull = Path.GetFullPath(targetRoot);
            var volumeRoot = Path.GetPathRoot(targetFull);
            if (string.IsNullOrEmpty(volumeRoot))
            {
                return null;   // 拿不到卷信息就不阻断备份
            }

            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady)
            {
                return $"目标磁盘不可用（{volumeRoot}），无法备份。";
            }

            // 待复制总量：惰性统计，避免把清单物化进内存
            var required = 0L;
            var fileCount = 0;

            foreach (var entry in EnumerateSourceFiles(source, rules))
            {
                required += entry;
                fileCount++;
            }

            // history 预留：结合产品语义，旧版本会被保留而不是覆盖，
            // 因此按「现有 current 体积的一半」与「本次待复制量的四分之一」取较小值预留，
            // 避免写完 current 之后没有空间放 history。
            var currentDirectory = BackupRules.BuildCurrentDirectory(targetRoot, device);
            var currentBytes = Directory.Exists(currentDirectory)
                ? BackupMirrorService.MeasureDirectory(currentDirectory).TotalBytes
                : 0L;

            var historyReserve = Math.Min(currentBytes / 2, required / 4);
            var margin = 64L * 1024 * 1024;   // 64 MiB 余量，避免刚好写满
            var needed = required + historyReserve + margin;

            plan = new SpacePlan
            {
                RequiredBytes = required,
                HistoryReserveBytes = historyReserve,
                AvailableBytes = drive.AvailableFreeSpace,
                FileCount = fileCount
            };

            return drive.AvailableFreeSpace >= needed
                ? null
                : $"目标磁盘空间不足：需要 {CopyResult.FormatSize(needed)}" +
                  $"（待复制 {CopyResult.FormatSize(required)} + history 预留 {CopyResult.FormatSize(historyReserve)} + 余量 {CopyResult.FormatSize(margin)}），" +
                  $"可用 {CopyResult.FormatSize(drive.AvailableFreeSpace)}。";
        }
        catch (Exception ex)
        {
            // 预检本身失败不应该阻断备份（沿用“读不到就不阻断”的保守策略）
            plan = default;
            return ex is IOException or UnauthorizedAccessException
                ? $"无法读取目标磁盘空间信息：{ex.Message}"
                : null;
        }
    }

    /// <summary>惰性统计源目录中参与复制的文件大小（不保存路径）。</summary>
    private static IEnumerable<long> EnumerateSourceFiles(string source, ExcludeRules rules)
    {
        var pending = new Stack<string>();
        if (Directory.Exists(source))
        {
            pending.Push(source);
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] files;
            string[] subDirectories;
            try
            {
                files = Directory.GetFiles(current);
                subDirectories = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (rules.IsExcludedFile(Path.GetFileName(file)))
                {
                    continue;
                }

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch
                {
                    continue;
                }

                yield return length;
            }

            foreach (var directory in subDirectories)
            {
                if (!rules.IsExcludedDirectory(Path.GetFileName(directory)))
                {
                    pending.Push(directory);
                }
            }
        }
    }

    /// <summary>空间预检结果。</summary>
    private readonly record struct SpacePlan
    {
        /// <summary>待复制字节数。</summary>
        public long RequiredBytes { get; init; }

        /// <summary>为 history 预留的字节数。</summary>
        public long HistoryReserveBytes { get; init; }

        /// <summary>目标卷可用字节数。</summary>
        public long AvailableBytes { get; init; }

        /// <summary>待复制文件数。</summary>
        public int FileCount { get; init; }
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
        private int _lastScanLogged;

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

        /// <summary>
        /// 预扫描进度回调：解决“复制开始前长时间零进度”的问题。
        /// <para>每累计 <see cref="ScanDiscoveryLogInterval"/> 个文件记一行，避免大 U 盘刷屏。</para>
        /// </summary>
        public void ReportScan(int discoveredFiles)
        {
            if (discoveredFiles - _lastScanLogged < ScanDiscoveryLogInterval)
            {
                return;
            }

            _lastScanLogged = discoveredFiles;
            _logger.Info($"正在扫描（{_deviceName}）：已发现 {discoveredFiles} 个文件…");
        }
    }
}
