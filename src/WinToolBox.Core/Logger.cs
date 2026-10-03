using System.Text;
using System.Text.Json;

namespace WinToolBox.Core;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    /// <summary>普通信息。</summary>
    Info,

    /// <summary>警告，不影响主流程。</summary>
    Warn,

    /// <summary>错误，功能可能失败。</summary>
    Error
}

/// <summary>
/// 一条日志记录。界面可以订阅 <see cref="Logger.EntryWritten"/> 实时显示。
/// </summary>
public sealed class LogEntry
{
    /// <summary>发生时间。</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>级别。</summary>
    public LogLevel Level { get; init; }

    /// <summary>消息正文。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>关联异常（可空）。</summary>
    public Exception? Exception { get; init; }

    /// <summary>与写入日志文件完全一致的格式化文本（含异常堆栈，便于界面直接显示）。</summary>
    public string FormattedText
    {
        get
        {
            var head = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Logger.FormatLevel(Level)}] {Message}";
            return Exception is null ? head : head + Environment.NewLine + Exception;
        }
    }
}

/// <summary>
/// 日志记录器：写入 %LocalAppData%\WinToolBox\logs\{工具名}\，按天切割（每天一个文件）。
/// 线程安全；任何写入失败都不会抛给调用方（日志不能拖垮主程序）。
/// </summary>
public sealed class Logger
{
    /// <summary>默认保留天数。</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>日志文件名前缀。</summary>
    public const string FileNamePrefix = "wintoolbox-";

    /// <summary>写入失败时的重试次数。</summary>
    public const int WriteRetryCount = 3;

    /// <summary>每次重试之间的等待毫秒数。</summary>
    public const int WriteRetryDelayMilliseconds = 50;

    /// <summary>等待跨进程日志写锁的超时毫秒数。</summary>
    public const int CrossProcessLockTimeoutMilliseconds = 2000;

    private static readonly Lazy<Logger> LazyInstance = new(() => new Logger());

    /// <summary>
    /// 按日志文件路径共享的写锁。
    /// </summary>
    /// <remarks>
    /// 每个 <see cref="Logger"/> 实例都有自己的 <c>_sync</c>，但**同一个日志文件**可能被多个实例写
    /// （例如一个进程里同时存在 UsbBackup 与 FileMaster 的日志器，或测试里并发构造两个实例）。
    /// 只用实例级锁时，两个实例会同时以 <c>FileMode.Append</c> 打开同一文件、抢占同一段文件尾，
    /// 表现为「日志莫名少几行」。因此这里按「文件路径」取锁，保证同一文件的写入严格串行。
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();

    /// <summary>进程内共享单例（静态单例模式）。</summary>
    public static Logger Instance => LazyInstance.Value;

    /// <summary>使用默认日志目录。</summary>
    public Logger()
        : this(AppPaths.LogDirectory, DefaultRetentionDays, null)
    {
    }

    /// <summary>指定日志目录（测试可注入临时目录）。</summary>
    public Logger(string logDirectory, int retentionDays = DefaultRetentionDays)
        : this(logDirectory, retentionDays, null)
    {
    }

    /// <summary>
    /// 为某个工具创建日志记录器，写入该工具专属目录
    /// <c>%LocalAppData%\WinToolBox\logs\{工具名}\</c>。
    /// </summary>
    /// <param name="toolName">工具名（例如 <c>UsbBackup</c>）；为空时退回共享日志目录。</param>
    /// <param name="retentionDays">日志保留天数，0 表示不清理。</param>
    /// <remarks>
    /// 用工厂方法而不是构造函数重载：<c>(string, int)</c> 与「日志目录 + 保留天数」签名完全相同，
    /// 再多一个重载会让调用点难以分辨「传的是目录还是工具名」。
    /// </remarks>
    public static Logger ForTool(string? toolName, int retentionDays = DefaultRetentionDays)
        => new(AppPaths.LogDirectoryFor(toolName), retentionDays, toolName);

    /// <summary>使用指定日志目录与日志记录器。</summary>
    public Logger(string logDirectory, int retentionDays, string? toolName)
    {
        LogDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? AppPaths.LogDirectory
            : logDirectory!;
        RetentionDays = retentionDays < 0 ? 0 : retentionDays;
        ToolName = string.IsNullOrWhiteSpace(toolName) ? null : toolName!.Trim();
    }

    /// <summary>日志目录。</summary>
    public string LogDirectory { get; }

    /// <summary>日志保留天数，0 表示不清理。</summary>
    public int RetentionDays { get; }

    /// <summary>工具名（共享日志目录时为 null）。</summary>
    public string? ToolName { get; }

    /// <summary>当天日志文件的完整路径。</summary>
    public string CurrentLogFilePath => Path.Combine(LogDirectory, BuildFileName(DateTime.Now));

    /// <summary>最近一次写入失败的原因（正常时为 null）。</summary>
    public Exception? LastError { get; private set; }

    /// <summary>累计写入失败的日志条数（用于退出时提示「本次运行有 N 条日志未能写入」）。</summary>
    public int FailedWriteCount { get; private set; }

    /// <summary>本次运行是否出现过日志写入失败。</summary>
    public bool HasWriteFailure => FailedWriteCount > 0;

    /// <summary>
    /// 每写入一条日志都会触发（可能在后台线程触发，界面订阅时需自行切回 UI 线程）。
    /// </summary>
    public event EventHandler<LogEntry>? EntryWritten;

    /// <summary>按天切割的文件名。</summary>
    public static string BuildFileName(DateTime date) => $"{FileNamePrefix}{date:yyyyMMdd}.log";

    /// <summary>记录 Info 级别日志。</summary>
    public void Info(string message) => Write(LogLevel.Info, message);

    /// <summary>记录 Warn 级别日志。</summary>
    public void Warn(string message, Exception? exception = null) => Write(LogLevel.Warn, message, exception);

    /// <summary>记录 Error 级别日志。</summary>
    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    /// <summary>写入一条日志。</summary>
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        var timestamp = DateTime.Now;
        var text = message ?? string.Empty;

        var line = new StringBuilder()
            .Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(FormatLevel(level)).Append("] ")
            .Append(text);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        var fileText = line.ToString();

        lock (_sync)
        {
            try
            {
                AppPaths.EnsureDirectory(LogDirectory);

                var path = CurrentLogFilePath;

                // 同一文件的写入必须跨 **线程** 串行
                var fileLock = FileLocks.GetOrAdd(path, static _ => new object());

                lock (fileLock)
                {
                    // 同一文件的写入还必须跨 **进程** 串行：
                    // FileMode.Append 的句柄只在「打开时」定位到文件尾，两个进程各持一个句柄时，
                    // B 追加后 A 仍会从自己先前的位置写入，**整行覆盖 B 且不抛任何异常**。
                    // 因此这里用命名互斥体把「打开→写入」整段保护起来。
                    if (!TryRunUnderCrossProcessLock(path, () => AppendWithRetry(path, fileText + Environment.NewLine)))
                    {
                        throw new IOException($"无法在超时内获得日志文件的跨进程写锁（可能有其它实例长时间占用）：{path}");
                    }
                }

                LastError = null;
            }
            catch (Exception ex)
            {
                // 日志失败不能再抛异常，否则会掩盖真实业务错误
                LastError = ex;
                FailedWriteCount++;
            }
        }

        // 事件在锁外触发：订阅者（界面）里做任何事都不会卡住日志写入
        try
        {
            EntryWritten?.Invoke(this, new LogEntry
            {
                Timestamp = timestamp,
                Level = level,
                Message = text,
                Exception = exception
            });
        }
        catch
        {
            // 订阅者抛异常不能影响日志本身
        }
    }

    /// <summary>
    /// 在「按日志文件路径命名」的跨进程互斥体保护下执行写入。
    /// </summary>
    /// <param name="logFilePath">日志文件路径（用于生成互斥体名）。</param>
    /// <param name="write">实际写入动作。</param>
    /// <returns>成功取得锁并写入返回 true；超时未取得锁返回 false。</returns>
    /// <remarks>
    /// 为什么必须跨进程：<c>FileMode.Append</c> 只在打开句柄时定位到文件尾。两个进程各自打开句柄后，
    /// 进程 A 的位置可能已经落后于进程 B 刚追加的内容，于是 A 的写入会**整行覆盖** B 且不抛异常
    /// （<c>FileShare.ReadWrite</c> 恰好消除了本该出现的共享冲突异常）。实测 8 进程并发写同一文件会丢约 41% 的行。
    /// <para>互斥体名对「同一路径」稳定、对「不同路径」不同，因此互不干扰；
    /// 超时时间为 <see cref="CrossProcessLockTimeoutMilliseconds"/>，避免某个进程卡死拖住其它实例。</para>
    /// </remarks>
    private static bool TryRunUnderCrossProcessLock(string logFilePath, Action write)
    {
        var name = BuildMutexName(logFilePath);
        Mutex? mutex = null;

        try
        {
            // 用本地命名空间（不加 Global\ 前缀）：Global\ 在部分受限上下文里会因权限不足抛异常，
            // 而日志必须永远可用。本地互斥体已足够覆盖「同一用户下的多个实例」这一实际场景。
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (Exception)
        {
            // 连互斥体都建不出来（极少见）：退化为「不加跨进程锁」，
            // 仍然保留 AppendWithRetry 的重试保护，绝不因为日志基础设施不可用而让写入整体失败。
            write();
            return true;
        }

        try
        {
            var acquired = false;

            try
            {
                acquired = mutex.WaitOne(CrossProcessLockTimeoutMilliseconds);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者异常退出：互斥体已归本线程所有，继续写入即可
                acquired = true;
            }

            if (!acquired)
            {
                return false;
            }

            try
            {
                write();
                return true;
            }
            finally
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 未持有（理论上不会发生）：忽略
                }
            }
        }
        finally
        {
            mutex.Dispose();
        }
    }

    /// <summary>由日志文件路径生成跨进程互斥体名（不含路径分隔符与冒号，避免命名非法）。</summary>
    private static string BuildMutexName(string logFilePath)
    {
        var safe = new StringBuilder(logFilePath.Length);

        foreach (var ch in logFilePath.ToLowerInvariant())
        {
            safe.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return "WinToolBox.Log." + safe;
    }

    /// <summary>
    /// 追加一行日志：<c>FileShare.ReadWrite</c> 打开，并在 <see cref="IOException"/> 时退避重试。
    /// </summary>
    /// <remarks>
    /// 旧实现用 <c>File.AppendAllText</c>（内部是 <c>FileShare.Read</c>）：两个工具同时写同一个文件时，
    /// 后写入的一方会拿到共享冲突异常，而异常又被吞掉 —— 表现为日志「莫名少了几行」，排障时极具误导性。
    /// <para>这里同时做两件事：① 用 <c>FileShare.ReadWrite</c> 允许并发读写；
    /// ② 对 <see cref="IOException"/>（含共享冲突、文件被占用）退避重试
    /// <see cref="WriteRetryCount"/> 次，每次间隔 <see cref="WriteRetryDelayMilliseconds"/> 毫秒。</para>
    /// </remarks>
    private static void AppendWithRetry(string path, string text)
    {
        var payload = new UTF8Encoding(false).GetBytes(text);
        Exception? lastException = null;

        for (var attempt = 0; attempt <= WriteRetryCount; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096);

                stream.Write(payload, 0, payload.Length);
                stream.Flush();
                return;
            }
            catch (IOException ex)
            {
                // 共享冲突 / 文件暂时被占用：等一下再试
                lastException = ex;

                if (attempt < WriteRetryCount)
                {
                    Thread.Sleep(WriteRetryDelayMilliseconds);
                }
            }
        }

        throw new IOException(
            $"写入日志失败（已重试 {WriteRetryCount} 次）：{path}",
            lastException);
    }

    /// <summary>返回最近的日志文件路径（用于“打开日志”菜单），没有日志时返回 null。</summary>
    public string? GetLatestLogFile()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                return null;
            }

            return Directory.EnumerateFiles(LogDirectory, FileNamePrefix + "*.log")
                .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            LastError = ex;
            return null;
        }
    }

    /// <summary>删除超过保留天数的历史日志（按天滚动后的清理）。</summary>
    public void CleanupOldLogs()
    {
        if (RetentionDays <= 0)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    return;
                }

                var threshold = DateTime.Now.Date.AddDays(-RetentionDays);
                foreach (var file in Directory.EnumerateFiles(LogDirectory, FileNamePrefix + "*.log"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var stamp = name.Length >= 8 ? name[^8..] : string.Empty;
                    if (!DateTime.TryParseExact(stamp, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
                    {
                        continue;
                    }

                    if (date < threshold)
                    {
                        File.Delete(file);
                    }
                }

                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex;
            }
        }
    }

    /// <summary>日志级别对应的短标签（与文件格式一致）。</summary>
    internal static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO "
    };
}
