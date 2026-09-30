using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinToolBox.Core;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// U盘备份主界面：把「立即备份按钮 / 备份路径设置 / 运行日志」全部收进一个窗口，托盘图标继续保留。
/// 线程约定：控件的所有读写都必须在 UI 线程上完成。日志事件可能来自后台备份线程，托盘上下文也可能
/// 从其它线程调用 <see cref="SetStatus"/> 与 <see cref="ShowAndActivate"/>，因此统一用
/// <see cref="RunOnUiThread(Action)"/> 切回 UI 线程，控件句柄未创建或已释放时直接忽略。
/// </summary>
public partial class MainForm : Form
{
    /// <summary>窗口标题，消息框统一复用。</summary>
    private const string AppTitle = "WinToolBox - U盘备份";

    /// <summary>日志框最多保留的行数，超过后从最前面开始删。</summary>
    private const int MaxLogLines = 800;

    /// <summary>触发裁剪时一次删除的最前面行数（避免频繁重建整段文本）。</summary>
    private const int TrimLogLines = 200;

    /// <summary>启动时从当天日志文件回显的最大行数。</summary>
    private const int MaxLogTailLines = 100;

    private readonly ConfigManager _configManager;
    private readonly Logger _logger;
    private readonly BackupService _backupService;
    private readonly UsbDetector _detector;
    private readonly INotifier _notifier;

    /// <summary>创建本窗口的线程（UI 线程）：句柄尚未创建时用它判断能否直接操作控件。</summary>
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    /// <summary>是否已经提示过“关闭窗口只是最小化到托盘”（只提示第一次，避免每次都打扰）。</summary>
    private bool _hideNoticeShown;

    /// <summary>是否允许真正关闭窗口；托盘退出前会调用 <see cref="PrepareForExit"/> 置为 true。</summary>
    private bool _allowClose;

    /// <summary>是否已执行过释放逻辑（保证日志事件只退订一次）。</summary>
    private bool _disposed;

    /// <summary>“立即备份”是否正在执行，防止重复提交后台任务。</summary>
    private bool _backupRunning;

    /// <summary>日志框当前行数（裁剪判断用）。</summary>
    private int _logLineCount;

    /// <summary>最近一次状态文字：句柄创建前来自后台线程的更新会先缓存在这里。</summary>
    private string _pendingStatus = string.Empty;

    /// <summary>用户点击「退出程序」时触发；由托盘上下文负责真正退出。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>创建主界面（构造函数注入 Core 组件，窗口本身不持有任何静态状态）。</summary>
    /// <param name="configManager">配置读写。</param>
    /// <param name="logger">日志记录器（同时作为日志框的数据源）。</param>
    /// <param name="backupService">备份服务。</param>
    /// <param name="detector">U 盘枚举。</param>
    /// <param name="notifier">托盘通知。</param>
    public MainForm(
        ConfigManager configManager,
        Logger logger,
        BackupService backupService,
        UsbDetector detector,
        INotifier notifier)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));

        InitializeComponent();

        // 字体缺失不能让窗口构造失败（设计器默认用 Microsoft YaHei UI）
        ApplyPreferredFont();

        LoadConfiguration();

        // 先订阅日志事件，再回显当天日志尾部，最后写启动信息：这样启动信息只会出现一次
        _logger.EntryWritten += OnLoggerEntryWritten;

        LoadTodayLogTail();
        _logger.Info("U盘备份主界面已打开。");

        RefreshDeviceList();
        SetStatus("就绪");
    }

    /// <summary>从托盘打开：显示、还原、激活到前台。</summary>
    public void ShowAndActivate()
    {
        RunOnUiThread(() =>
        {
            Show();

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            Activate();
            BringToFront();
        });
    }

    /// <summary>由托盘上下文更新状态栏文字（线程安全，内部自行 BeginInvoke）。</summary>
    /// <param name="text">要显示的状态文字。</param>
    public void SetStatus(string text)
    {
        var value = text ?? string.Empty;
        _pendingStatus = value;

        if (!IsHandleCreated && Environment.CurrentManagedThreadId != _uiThreadId)
        {
            // 句柄还没创建且来自后台线程：先记下来，等窗口句柄创建后由 OnHandleCreated 补上
            return;
        }

        RunOnUiThread(() => ApplyStatus(value));
    }

    /// <summary>允许真正关闭（托盘退出前调用），之后再 <see cref="Form.Close"/> 不会只隐藏。</summary>
    public void PrepareForExit()
    {
        _allowClose = true;
    }

    /// <summary>
    /// 探测设计器指定的「Microsoft YaHei UI」是否存在，缺失时退回系统消息字体。
    /// 任何异常都只记日志，绝不影响窗口构造。
    /// </summary>
    private void ApplyPreferredFont()
    {
        const string preferredFontName = "Microsoft YaHei UI";

        try
        {
            var probe = new Font(preferredFontName, 9F);
            if (string.Equals(probe.Name, preferredFontName, StringComparison.OrdinalIgnoreCase))
            {
                Font = probe;
                return;
            }

            // 字体不存在时 Font 会静默回退到别的字体，探测实例需要自行释放
            probe.Dispose();

            var fallback = SystemFonts.MessageBoxFont;
            if (fallback is not null)
            {
                Font = new Font(fallback.FontFamily, fallback.Size);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("设置界面字体失败，沿用系统默认字体。", ex);
        }
    }

    /// <summary>把磁盘上的配置读进控件（<see cref="ConfigManager.Load"/> 永不抛异常，这里仍做兜底）。</summary>
    private void LoadConfiguration()
    {
        BackupConfig config;

        try
        {
            config = _configManager.Load();
        }
        catch (Exception ex)
        {
            _logger.Warn("读取配置失败，界面将显示默认值。", ex);
            config = BackupConfig.CreateDefault();
        }

        txtTarget.Text = config.BackupTargetDirectory ?? string.Empty;

        // 配置文件里 excludedExtensions 为 null 时 string.Join 会抛异常，这里补一个空列表
        txtExclude.Text = string.Join(", ", config.ExcludedExtensions ?? new List<string>());
    }

    /// <summary>
    /// 把当天日志文件末尾最多 <see cref="MaxLogTailLines"/> 行读进日志框，
    /// 让用户一打开窗口就能看到之前发生了什么（文件不存在或读取失败都只提示，不影响启动）。
    /// </summary>
    private void LoadTodayLogTail()
    {
        var path = _logger.CurrentLogFilePath;

        try
        {
            if (!File.Exists(path))
            {
                AppendLogText($"[提示] 当天日志文件尚未生成：{path}");
                return;
            }

            // TakeLast 只缓存最后 N 行，日志文件很大时也不会把整个文件读进内存
            var tail = File.ReadLines(path).TakeLast(MaxLogTailLines).ToList();

            AppendLogText($"---------- 以下为当天日志末尾 {tail.Count} 行：{path} ----------");

            foreach (var line in tail)
            {
                AppendLogText(line);
            }
        }
        catch (Exception ex)
        {
            // 读取历史日志失败不能挡住主界面启动，这里直接写进日志框（不写日志文件，避免递归）
            AppendLogText($"[提示] 读取当天日志文件失败：{ex.Message}");
        }
    }

    /// <summary>日志写入事件：可能来自后台线程，切回 UI 线程后追加到日志框。</summary>
    private void OnLoggerEntryWritten(object? sender, LogEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        RunOnUiThread(() => AppendLogText(entry.FormattedText));
    }

    /// <summary>把一段文本追加到日志框（必须在 UI 线程调用），超过上限时删掉最前面的若干行。</summary>
    private void AppendLogText(string? text)
    {
        try
        {
            if (txtLog.IsDisposed)
            {
                return;
            }

            var value = text ?? string.Empty;

            txtLog.AppendText(value + Environment.NewLine);

            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.ScrollToCaret();

            _logLineCount += CountLines(value);

            if (_logLineCount > MaxLogLines)
            {
                TrimLogHead();
            }
        }
        catch (Exception)
        {
            // 日志框刷新失败不能影响备份等主流程；这里也不能写日志，否则会再次触发本方法造成递归
        }
    }

    /// <summary>日志框超过上限时，删掉最前面的 <see cref="TrimLogLines"/> 行。</summary>
    private void TrimLogHead()
    {
        var lines = txtLog.Lines;
        if (lines.Length <= TrimLogLines)
        {
            return;
        }

        var kept = new string[lines.Length - TrimLogLines];
        Array.Copy(lines, TrimLogLines, kept, 0, kept.Length);

        txtLog.Lines = kept;
        _logLineCount = kept.Length;

        txtLog.SelectionStart = txtLog.TextLength;
        txtLog.SelectionLength = 0;
        txtLog.ScrollToCaret();
    }

    /// <summary>统计一段文本占用的显示行数（含内部换行）。</summary>
    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 1;
        }

        var count = 1;

        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 在 UI 线程上执行动作：句柄未创建（构造阶段）时只有创建线程可以操作控件，
    /// 已释放或正在释放时直接忽略。这里的异常一律吞掉，避免窗口销毁阶段再次写日志造成递归。
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        try
        {
            if (_disposed || IsDisposed || Disposing)
            {
                return;
            }

            if (!IsHandleCreated)
            {
                if (Environment.CurrentManagedThreadId == _uiThreadId)
                {
                    action();
                }

                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(action);
                return;
            }

            action();
        }
        catch (Exception)
        {
            // 窗口关闭后 BeginInvoke / 访问控件会抛异常，忽略即可（不能在此写日志，避免递归）
        }
    }

    /// <summary>真正把状态文字写到状态栏（必须在 UI 线程调用；状态栏已释放时静默忽略）。</summary>
    private void ApplyStatus(string text)
    {
        try
        {
            lblStatus.Text = text;
        }
        catch (Exception)
        {
            // 窗口/状态栏已释放：不能在这里写日志，否则会再次触发 EntryWritten 造成递归
        }
    }

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 句柄创建前来自后台线程的状态更新被缓存在 _pendingStatus，这里补上最后一次
        if (_pendingStatus.Length > 0)
        {
            ApplyStatus(_pendingStatus);
        }
    }

    /// <summary>
    /// 关闭窗口 = 最小化到托盘：除非托盘上下文已经调用 <see cref="PrepareForExit"/>，
    /// 或者本次关闭来自系统关机 / Application.Exit（这时必须放行，否则会挡住整个进程退出）。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            var forceClose =
                e.CloseReason is CloseReason.ApplicationExitCall
                    or CloseReason.WindowsShutDown
                    or CloseReason.TaskManagerClosing;

            if (!_allowClose && !forceClose)
            {
                e.Cancel = true;
                Hide();

                // 只提示第一次，避免用户每次关窗都被气泡打扰
                if (!_hideNoticeShown)
                {
                    _hideNoticeShown = true;
                    ShowNotification("UsbBackup", "已最小化到托盘，双击托盘图标可重新打开。");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("处理窗口关闭事件失败。", ex);
        }

        base.OnFormClosing(e);
    }

    /// <summary>「浏览…」：选择备份目标目录。</summary>
    private void OnBrowseClick(object? sender, EventArgs e)
    {
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "请选择 U 盘备份的保存位置",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            var current = (txtTarget.Text ?? string.Empty).Trim();
            if (current.Length > 0 && Directory.Exists(current))
            {
                dialog.SelectedPath = current;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedPath.Length > 0)
            {
                txtTarget.Text = dialog.SelectedPath;
                SetStatus("已选择备份目标目录，记得点「保存设置」");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("选择备份目标目录失败。", ex);
            ShowErrorMessage("选择备份目标目录失败：" + ex.Message);
        }
    }

    /// <summary>「保存设置」：写回配置并提示用户。</summary>
    private void OnSaveClick(object? sender, EventArgs e)
    {
        var target = (txtTarget.Text ?? string.Empty).Trim();

        // 没有目标目录就无从备份，保存了也没意义，直接拦下
        if (target.Length == 0)
        {
            MessageBox.Show(
                this,
                "请先选择备份目标目录",
                AppTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            txtTarget.Focus();
            return;
        }

        try
        {
            var config = _configManager.Load();
            config.BackupTargetDirectory = target;
            config.ExcludedExtensions = ParseExtensions(txtExclude.Text);
            config.Normalize();

            _configManager.Save(config);

            // 把规范化后的结果回填界面，让用户看到真正生效的值
            txtTarget.Text = config.BackupTargetDirectory;
            txtExclude.Text = string.Join(", ", config.ExcludedExtensions);

            _logger.Info(
                $"设置已保存：目标目录={config.BackupTargetDirectory}，" +
                $"排除后缀={string.Join(",", config.ExcludedExtensions)}");

            SetStatus("设置已保存");

            try
            {
                _notifier.ShowNotification("UsbBackup", "设置已保存");
            }
            catch (Exception notifyEx)
            {
                _logger.Warn("显示设置已保存通知失败。", notifyEx);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("保存设置失败。", ex);
            ShowErrorMessage("保存设置失败：" + ex.Message);
        }
    }

    /// <summary>「立即备份」：在后台线程备份所有已插入的 U 盘，结果写日志 + 状态栏。</summary>
    private async void OnBackupClick(object? sender, EventArgs e)
    {
        if (_backupRunning)
        {
            // 按钮已经禁用，这里只做双保险
            return;
        }

        _backupRunning = true;
        btnBackup.Enabled = false;
        UseWaitCursor = true;
        SetStatus("正在备份…");

        try
        {
            _logger.Info("用户在主界面点击「立即备份」，开始备份所有已插入的 U 盘。");

            // BackupAllAttached 是同步阻塞实现，必须放到后台线程，否则界面会假死
            var outcomes = await Task.Run(() => _backupService.BackupAllAttached(CancellationToken.None));

            if (_disposed || IsDisposed)
            {
                return;
            }

            foreach (var outcome in outcomes)
            {
                if (outcome.Success)
                {
                    _logger.Info(outcome.Message);
                }
                else
                {
                    // 跳过与失败都按警告记录，方便用户在日志里一眼看到没备份成功的原因
                    _logger.Warn(outcome.Message);
                }

                if (outcome.Copy is not null)
                {
                    _logger.Info($"复制统计：{outcome.Copy.Summary}目标目录：{outcome.TargetDirectory}");
                }
            }

            var success = outcomes.Count(static o => o.Success);
            var skipped = outcomes.Count(static o => o.Skipped);
            var failed = outcomes.Count(static o => !o.Success && !o.Skipped);

            var summary = $"备份完成：成功 {success} 个，跳过 {skipped} 个，失败 {failed} 个。";
            var copySummary = string.Join(
                " ",
                outcomes.Where(static o => o.Copy is not null).Select(static o => o.Copy!.Summary));

            if (copySummary.Length > 0)
            {
                summary += " " + copySummary;
            }

            SetStatus(summary);
            _logger.Info("本轮备份汇总：" + summary);
        }
        catch (Exception ex)
        {
            _logger.Error("立即备份失败。", ex);
            SetStatus("备份失败");
            ShowErrorMessage("备份失败：" + ex.Message);
        }
        finally
        {
            _backupRunning = false;

            if (!_disposed && !IsDisposed)
            {
                btnBackup.Enabled = true;
                UseWaitCursor = false;
            }
        }
    }

    /// <summary>「刷新设备」：重新枚举 U 盘并更新状态。</summary>
    private void OnRefreshClick(object? sender, EventArgs e)
    {
        var devices = RefreshDeviceList();

        if (devices is null)
        {
            SetStatus("刷新设备失败");
            ShowErrorMessage("刷新设备失败，详情见日志。");
            return;
        }

        SetStatus(devices.Count == 0 ? "未检测到 U 盘" : $"已检测到 {devices.Count} 个 U 盘");
    }

    /// <summary>枚举可移动磁盘：更新「设备：N 个」标签并把每个设备写进日志。</summary>
    /// <returns>设备列表；枚举失败时返回 null。</returns>
    private IReadOnlyList<UsbDeviceInfo>? RefreshDeviceList()
    {
        IReadOnlyList<UsbDeviceInfo> devices;

        try
        {
            devices = _detector.GetRemovableDrives();
        }
        catch (Exception ex)
        {
            _logger.Error("刷新设备列表失败。", ex);

            try
            {
                lblDevices.Text = "设备：未知";
            }
            catch (Exception)
            {
                // 控件已释放时忽略
            }

            return null;
        }

        lblDevices.Text = $"设备：{devices.Count} 个";

        if (devices.Count == 0)
        {
            _logger.Info("未检测到 U 盘。");
            return devices;
        }

        foreach (var device in devices)
        {
            _logger.Info($"检测到 U 盘：{device.DisplayName}");
        }

        return devices;
    }

    /// <summary>「打开日志目录」：只允许用资源管理器打开日志目录，绝不执行任何其它程序。</summary>
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

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });

            _logger.Info($"已用资源管理器打开日志目录：{directory}");
            SetStatus("已打开日志目录");
        }
        catch (Exception ex)
        {
            _logger.Warn("打开日志目录失败。", ex);
            ShowErrorMessage($"打开日志目录失败：{ex.Message}{Environment.NewLine}日志目录：{_logger.LogDirectory}");
        }
    }

    /// <summary>「隐藏到托盘」：只隐藏窗口，程序继续在托盘常驻。</summary>
    private void OnHideClick(object? sender, EventArgs e)
    {
        try
        {
            _logger.Info("用户在主界面选择「隐藏到托盘」。");
            Hide();
            ShowNotification("UsbBackup", "已最小化到托盘，双击托盘图标可重新打开。");
        }
        catch (Exception ex)
        {
            _logger.Warn("隐藏到托盘失败。", ex);
        }
    }

    /// <summary>「退出程序」：确认后交给托盘上下文真正退出。</summary>
    private void OnExitClick(object? sender, EventArgs e)
    {
        try
        {
            var answer = MessageBox.Show(
                this,
                "确定要退出 U盘备份吗？",
                AppTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                return;
            }

            _logger.Info("用户在主界面选择「退出程序」。");

            if (ExitRequested is null)
            {
                // 没有订阅者时点了不会有反应，写进日志便于排查（例如设计期单独打开本窗口）
                _logger.Warn("退出请求没有订阅者，程序未退出。");
                return;
            }

            ExitRequested.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error("退出程序失败。", ex);
            ShowErrorMessage("退出程序失败：" + ex.Message);
        }
    }

    /// <summary>「清空」：清空日志框，程序日志文件不受影响。</summary>
    private void OnClearLogClick(object? sender, EventArgs e)
    {
        try
        {
            txtLog.Clear();
            _logLineCount = 0;

            SetStatus("日志框已清空");
            AppendLogText("[提示] 日志框已清空，新的日志会继续显示在这里。");
        }
        catch (Exception ex)
        {
            _logger.Warn("清空日志框失败。", ex);
        }
    }

    /// <summary>弹托盘通知；通知失败只记日志（例如通知器已释放）。</summary>
    private void ShowNotification(string title, string message)
    {
        try
        {
            _notifier.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            _logger.Warn($"显示通知失败：{title}", ex);
        }
    }

    /// <summary>弹错误提示框；提示框本身失败只记日志。</summary>
    private void ShowErrorMessage(string message)
    {
        try
        {
            MessageBox.Show(this, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _logger.Warn("显示错误提示框失败。", ex);
        }
    }

    /// <summary>把界面上的逗号（中英文）分隔文本解析为后缀列表，规范化交给 <see cref="BackupConfig.Normalize"/>。</summary>
    private static List<string> ParseExtensions(string? text)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var part in text!.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Trim();

            if (value.Length == 0)
            {
                continue;
            }

            var exists = result.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>
    /// 释放窗口：先退订日志事件（否则窗口释放后后台线程仍会回调），再释放设计器组件。
    /// 本方法是 MainForm 唯一的 <c>Dispose(bool)</c> 覆写，保证 <c>base.Dispose(disposing)</c> 只调用一次。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            try
            {
                _logger.EntryWritten -= OnLoggerEntryWritten;
            }
            catch (Exception)
            {
                // 退订失败不影响窗体释放
            }

            try
            {
                components?.Dispose();
            }
            catch (Exception)
            {
                // 设计器组件释放失败不影响窗体释放
            }
        }

        base.Dispose(disposing);
    }
}
