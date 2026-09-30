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
/// 日志记录器：写入 %LocalAppData%\WinToolBox\logs\，按天切割（每天一个文件）。
/// 线程安全；任何写入失败都不会抛给调用方（日志不能拖垮主程序）。
/// </summary>
public sealed class Logger
{
    /// <summary>默认保留天数。</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>日志文件名前缀。</summary>
    public const string FileNamePrefix = "wintoolbox-";

    private static readonly Lazy<Logger> LazyInstance = new(() => new Logger());

    private readonly object _sync = new();

    /// <summary>进程内共享单例（静态单例模式）。</summary>
    public static Logger Instance => LazyInstance.Value;

    /// <summary>使用默认日志目录。</summary>
    public Logger()
        : this(AppPaths.LogDirectory, DefaultRetentionDays)
    {
    }

    /// <summary>指定日志目录（测试可注入临时目录）。</summary>
    public Logger(string logDirectory, int retentionDays = DefaultRetentionDays)
    {
        LogDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? AppPaths.LogDirectory
            : logDirectory!;
        RetentionDays = retentionDays < 0 ? 0 : retentionDays;
    }

    /// <summary>日志目录。</summary>
    public string LogDirectory { get; }

    /// <summary>日志保留天数，0 表示不清理。</summary>
    public int RetentionDays { get; }

    /// <summary>当天日志文件的完整路径。</summary>
    public string CurrentLogFilePath => Path.Combine(LogDirectory, BuildFileName(DateTime.Now));

    /// <summary>最近一次写入失败的原因（正常时为 null）。</summary>
    public Exception? LastError { get; private set; }

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
                File.AppendAllText(CurrentLogFilePath, fileText + Environment.NewLine, new UTF8Encoding(false));
                LastError = null;
            }
            catch (Exception ex)
            {
                // 日志失败不能再抛异常，否则会掩盖真实业务错误
                LastError = ex;
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
