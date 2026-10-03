using System.Drawing;
using System.Windows.Forms;
using WinToolBox.Core;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// 托盘常驻程序：维护托盘图标与右键菜单，监听 U 盘插拔并调度备份。
/// 事件在 UI 线程触发，所有备份都放到后台线程执行，绝不阻塞消息循环。
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>并发保护：避免用户连点“立即备份”时重复提交后台任务。</summary>
    private readonly object _backupRequestSync = new();

    /// <summary>退出时等待正在运行的备份收尾的最长时间。</summary>
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>当前后台备份任务的句柄（没有任务时为 null）。退出时要等它收尾，不能让它带着半截文件跑完。</summary>
    private Task? _backupTask;

    /// <summary>创建本对象时的 UI 线程同步上下文（后台任务完成后用它切回 UI 线程）。</summary>
    private readonly SynchronizationContext? _uiContext;

    private readonly ConfigManager _configManager;
    private readonly Logger _logger;
    private readonly Notifier _notifier;
    private readonly UsbDetector _detector;
    private readonly BackupService _backupService;
    private readonly UsbWatcher _usbWatcher;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _backupMenuItem;

    /// <summary>托盘菜单里的「取消备份」项（只在有备份在跑时可用）。</summary>
    private readonly ToolStripMenuItem _cancelBackupMenuItem;

    private bool _backupRequestRunning;
    private bool _disposed;

    /// <summary>主界面窗口（普通用户的主要入口；关闭窗口时只隐藏，不退出程序）。</summary>
    private MainForm? _mainForm;

    /// <summary>创建托盘程序上下文（使用默认的 UsbBackup 专属日志记录器）。</summary>
    public TrayApplicationContext()
        : this(null)
    {
    }

    /// <summary>创建托盘程序上下文。</summary>
    /// <param name="logger">
    /// 日志记录器；为 null 时使用 UsbBackup 专属日志目录
    /// （<c>%LocalAppData%\WinToolBox\logs\UsbBackup\</c>）。
    /// </param>
    public TrayApplicationContext(Logger? logger)
    {
        // P1-4：默认走 UsbBackup 专属日志目录，不写共享日志文件（否则两个工具的日志会混在一起）
        _logger = logger ?? Logger.ForTool("UsbBackup");

        // 在 UI 线程上构造：先记下消息循环的同步上下文，供后台备份完成后回切使用
        _uiContext = SynchronizationContext.Current;

        _configManager = new ConfigManager(AppPaths.UsbBackupConfigFile, _logger);
        _detector = new UsbDetector(_logger);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "WinToolBox - U盘备份",
            Visible = true
        };

        // 复用托盘图标弹气泡：Notifier 会记住创建它时的 UI 线程，后台备份完成后自动切回 UI 线程
        _notifier = new Notifier(_notifyIcon, _logger);

        _backupService = new BackupService(_configManager, _logger, _notifier, _detector);

        _backupMenuItem = new ToolStripMenuItem("立即备份");
        _backupMenuItem.Click += OnBackupNowClick;

        // P1-7：备份中可用的「取消备份」入口
        _cancelBackupMenuItem = new ToolStripMenuItem("取消备份") { Enabled = false };
        _cancelBackupMenuItem.Click += OnCancelBackupClick;

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(CreateMenuItem("打开主界面", OnOpenMainClick));
        _contextMenu.Items.Add(_backupMenuItem);
        _contextMenu.Items.Add(_cancelBackupMenuItem);
        _contextMenu.Items.Add(CreateMenuItem("打开日志", OnOpenLogClick));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(CreateMenuItem("退出", OnExitClick));

        _notifyIcon.ContextMenuStrip = _contextMenu;
        _notifyIcon.DoubleClick += OnOpenMainClick;
        _notifyIcon.BalloonTipClicked += OnOpenMainClick;

        _usbWatcher = new UsbWatcher(_detector, _logger);
        _usbWatcher.DeviceArrived += OnDeviceArrived;
        _usbWatcher.DeviceRemoved += OnDeviceRemoved;
        _usbWatcher.Start();

        _logger.CleanupOldLogs();
        _logger.Info("UsbBackup 托盘图标已就绪，等待 U 盘插拔。");

        // 主界面：普通用户启动后直接看到窗口；托盘图标作为常驻入口与后台运行方式
        try
        {
            _mainForm = new MainForm(_configManager, _logger, _backupService, _detector, _notifier);
            _mainForm.ExitRequested += (_, _) => ExitApplication();
            ShowMainWindow();
            _logger.Info("主界面已打开。");
        }
        catch (Exception ex)
        {
            _logger.Error("创建主界面失败，将只以托盘方式运行。", ex);
            ShowErrorMessage("主界面打开失败，程序将继续在通知区域运行：" + ex.Message);
        }
    }

    /// <summary>显示并激活主界面（托盘双击/菜单、「打开主界面」、气泡点击都走这里）。</summary>
    private void ShowMainWindow()
    {
        try
        {
            _mainForm?.ShowAndActivate();
        }
        catch (Exception ex)
        {
            _logger.Error("打开主界面失败。", ex);
        }
    }

    /// <summary>U 盘插入：只写日志并提示用户可以手动备份（本工具不做自动备份）。</summary>
    private void OnDeviceArrived(object? sender, UsbDeviceInfo device)
    {
        try
        {
            _logger.Info($"U 盘已插入：{device}");
            _logger.Info("本工具不会自动备份，请在主界面点「立即备份」或右键托盘图标 →「立即备份」。");

            ShowBalloon("检测到 U 盘", $"{device.DriveLetter} 已插入。点击「立即备份」开始备份，或双击托盘图标打开主界面。");

            PostToUi(() => _mainForm?.SetStatus($"检测到 U 盘 {device.DriveLetter}，可点击「立即备份」。"));
        }
        catch (Exception ex)
        {
            _logger.Error("处理 U 盘插入事件失败。", ex);
        }
    }

    /// <summary>U 盘拔出：只写日志。</summary>
    private void OnDeviceRemoved(object? sender, UsbDeviceInfo device)
    {
        try
        {
            _logger.Info($"U 盘已拔出：{device}");
        }
        catch (Exception ex)
        {
            _logger.Error("处理 U 盘拔出事件失败。", ex);
        }
    }

    private void OnBackupNowClick(object? sender, EventArgs e)
        => StartBackupTask("正在后台备份所有已插入的 U 盘…");

    /// <summary>托盘菜单「打开主界面」：显示主窗口（设置、立即备份、运行日志都在里面）。</summary>
    private void OnOpenMainClick(object? sender, EventArgs e) => ShowMainWindow();

    /// <summary>用资源管理器打开日志目录；目录不存在先创建，失败时退化为提示框。</summary>
    private void OnOpenLogClick(object? sender, EventArgs e)
    {
        try
        {
            var directory = _logger.LogDirectory;
            AppPaths.EnsureDirectory(directory);

            if (!Directory.Exists(directory))
            {
                ShowErrorMessage($"日志目录不存在，且创建失败：{directory}");
                return;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            };

            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _logger.Warn("打开日志目录失败。", ex);

            var latest = _logger.GetLatestLogFile();
            ShowErrorMessage(latest is null
                ? $"打开日志目录失败：{ex.Message}{Environment.NewLine}日志目录：{_logger.LogDirectory}"
                : $"打开日志目录失败：{ex.Message}{Environment.NewLine}当前日志文件：{latest}");
        }
    }

    private void OnExitClick(object? sender, EventArgs e)
    {
        try
        {
            _logger.Info("用户选择退出 UsbBackup。");
        }
        catch (Exception)
        {
            // 退出流程中日志失败不影响退出
        }

        ExitApplication();
    }

    /// <summary>在后台线程执行一次“备份所有已插入设备”。</summary>
    private void StartBackupTask(string startMessage)
    {
        lock (_backupRequestSync)
        {
            if (_backupRequestRunning)
            {
                _logger.Warn("已有备份任务在执行，忽略本次备份请求。");
                ShowBalloon("备份正在进行", "上一次备份还没有结束，请稍候。");
                return;
            }

            _backupRequestRunning = true;
        }

        _backupMenuItem.Enabled = false;
        _cancelBackupMenuItem.Enabled = true;
        _logger.Info("提交备份任务：" + startMessage);

        // 保存 Task 句柄（不再用 `_ =` 丢弃）：退出流程需要等它收尾
        _backupTask = Task.Run(() =>
        {
            IReadOnlyList<BackupOutcome>? outcomes = null;
            Exception? failure = null;

            try
            {
                _logger.Info("后台备份任务开始。");
                outcomes = _backupService.BackupAllAttached();
                _logger.Info("后台备份任务结束。");
            }
            catch (Exception ex)
            {
                failure = ex;
                _logger.Error("后台备份任务发生未处理异常。", ex);
            }

            // 后台线程上 SynchronizationContext.Current 为 null，必须使用构造时捕获的 UI 上下文
            PostToUi(() => CompleteBackupRequest(outcomes, failure));
        });
    }

    /// <summary>请求取消正在进行的备份（托盘菜单入口）。</summary>
    private void OnCancelBackupClick(object? sender, EventArgs e)
    {
        if (!_backupService.CancelCurrentBackup())
        {
            ShowBalloon("没有正在进行的备份", "当前没有备份任务在运行。");
            return;
        }

        _logger.Warn("用户从托盘菜单请求取消备份。");
        ShowBalloon("正在取消备份", "已请求取消，备份会在数秒内停止；已复制完成的文件会被保留。");
    }

    /// <summary>
    /// 退出前收尾正在运行的备份：先请求取消，再最多等 <see cref="ExitWaitTimeout"/>。
    /// </summary>
    /// <returns>true 表示可以安全退出；false 表示备份仍在进行，需要用户决定是否强制退出。</returns>
    private bool TryStopRunningBackup()
    {
        Task? task;

        lock (_backupRequestSync)
        {
            task = _backupTask;
        }

        if (task is null || task.IsCompleted)
        {
            return true;
        }

        // 第一步：取消（令牌一路传到文件 I/O，大文件也能很快停下）
        _logger.Warn("退出前检测到备份仍在进行，先请求取消。");
        _backupService.CancelCurrentBackup();

        try
        {
            // 第二步：等待收尾，避免进程带着写了一半的目标文件结束
            if (task.Wait(ExitWaitTimeout))
            {
                _logger.Info("正在进行的备份已在退出等待期内安全结束。");
                return true;
            }
        }
        catch (Exception ex)
        {
            // 备份任务自身的异常不该阻塞退出
            _logger.Warn("等待备份任务收尾时发生异常。", ex);
            return true;
        }

        _logger.Warn($"备份在 {ExitWaitTimeout.TotalSeconds:0} 秒内仍未结束。");
        return false;
    }

    /// <summary>询问用户是否强制退出（备份仍在进行时）。</summary>
    private bool ConfirmForceExit()
    {
        try
        {
            var answer = MessageBox.Show(
                $"备份仍在进行，{ExitWaitTimeout.TotalSeconds:0} 秒内没有结束。" + Environment.NewLine + Environment.NewLine +
                "现在退出会中断备份：正在复制的那个文件不会留下半截内容（程序用临时文件 + 原子替换），" +
                "但本轮尚未复制的文件不会被备份。" + Environment.NewLine + Environment.NewLine +
                "是否强制退出？",
                "正在备份，退出将中断备份",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            return answer == DialogResult.Yes;
        }
        catch (Exception ex)
        {
            _logger.Warn("显示强制退出确认框失败。", ex);
            return false;
        }
    }

    /// <summary>备份任务收尾（始终回到 UI 线程执行）。</summary>
    private void CompleteBackupRequest(IReadOnlyList<BackupOutcome>? outcomes, Exception? failure)
    {
        lock (_backupRequestSync)
        {
            _backupRequestRunning = false;
        }

        try
        {
            _backupMenuItem.Enabled = true;

            // 备份已结束，取消入口重新禁用
            _cancelBackupMenuItem.Enabled = false;

            if (failure is not null)
            {
                ShowBalloon("备份失败", "备份过程中发生错误，详情已写入日志：" + failure.Message);
                return;
            }

            if (outcomes is null || outcomes.Count == 0)
            {
                return;
            }

            var completed = outcomes.Count(static o => o.Success);
            var skipped = outcomes.Count(static o => o.Skipped);
            var failed = outcomes.Count(static o => !o.Success && !o.Skipped);

            _logger.Info($"本轮备份汇总：成功 {completed} 个设备，跳过 {skipped} 个，失败 {failed} 个。");

            if (completed == 0 && skipped > 0 && failed == 0)
            {
                // BackupService 已经弹过具体原因，这里不再重复打扰用户
                return;
            }

            if (failed > 0)
            {
                ShowBalloon("备份未全部成功", $"成功 {completed} 个，失败 {failed} 个，请查看日志。");
            }
            else if (completed > 0)
            {
                ShowBalloon("U盘备份完成", $"已完成 {completed} 个设备的备份。日志中可查看详细结果。");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("处理备份结果时发生异常。", ex);
            _backupMenuItem.Enabled = true;
            _cancelBackupMenuItem.Enabled = false;
        }
        finally
        {
            // 任务已收尾，清掉句柄，避免退出流程去等一个早就结束的任务
            lock (_backupRequestSync)
            {
                _backupTask = null;
            }
        }
    }

    /// <summary>把回调切回 UI 线程（使用构造时捕获的同步上下文）。</summary>
    private void PostToUi(Action action)
    {
        try
        {
            var context = _uiContext;
            if (context is not null)
            {
                context.Post(_ => action(), null);
                return;
            }

            action();
        }
        catch (Exception ex)
        {
            _logger.Error("调度 UI 回调失败。", ex);
        }
    }

    private void ShowBalloon(string title, string message)
    {
        try
        {
            _notifier.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            _logger.Warn("显示托盘通知失败。", ex);
        }
    }

    private void ShowErrorMessage(string message)
    {
        try
        {
            MessageBox.Show(
                message,
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _logger.Warn("显示提示框失败。", ex);
        }
    }

    private static ToolStripMenuItem CreateMenuItem(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }

    /// <summary>退出程序：先关停正在进行的备份（取消 + 等待），再关闭主界面、释放托盘图标、停止监听、结束消息循环。</summary>
    private void ExitApplication()
    {
        // 第一步：绝不带着正在写盘的备份退出（否则目标文件可能是半截的）
        if (!TryStopRunningBackup() && !ConfirmForceExit())
        {
            _logger.Info("用户选择不强制退出，已取消退出流程。");
            return;
        }

        try
        {
            if (_mainForm is not null)
            {
                _mainForm.PrepareForExit();
                _mainForm.Close();
                _mainForm.Dispose();
                _mainForm = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("关闭主界面失败。", ex);
        }

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn("释放托盘图标失败。", ex);
        }

        try
        {
            _usbWatcher.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warn("停止 U 盘监听失败。", ex);
        }

        ExitThread();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            try
            {
                if (_mainForm is not null)
                {
                    _mainForm.PrepareForExit();
                    _mainForm.Dispose();
                    _mainForm = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("释放主界面失败。", ex);
            }

            try
            {
                _usbWatcher.DeviceArrived -= OnDeviceArrived;
                _usbWatcher.DeviceRemoved -= OnDeviceRemoved;
                _usbWatcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warn("释放 U 盘监听器失败。", ex);
            }

            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.ContextMenuStrip = null;
                _notifyIcon.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warn("释放托盘图标失败。", ex);
            }

            try
            {
                _contextMenu.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Warn("释放右键菜单失败。", ex);
            }
        }

        base.Dispose(disposing);
    }
}
