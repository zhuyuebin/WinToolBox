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

    private bool _backupRequestRunning;
    private bool _disposed;

    /// <summary>主界面窗口（普通用户的主要入口；关闭窗口时只隐藏，不退出程序）。</summary>
    private MainForm? _mainForm;

    /// <summary>创建托盘程序上下文。</summary>
    public TrayApplicationContext()
    {
        _logger = Logger.Instance;

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

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(CreateMenuItem("打开主界面", OnOpenMainClick));
        _contextMenu.Items.Add(_backupMenuItem);
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
        _logger.Info("提交备份任务：" + startMessage);

        _ = Task.Run(() =>
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

    /// <summary>退出程序：先关闭主界面，再隐藏并释放托盘图标、停止监听、结束消息循环。</summary>
    private void ExitApplication()
    {
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
