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
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(FormatLevel(level)).Append("] ")
            .Append(message ?? string.Empty);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        var text = line.ToString();

        lock (_sync)
        {
            try
            {
                AppPaths.EnsureDirectory(LogDirectory);
                File.AppendAllText(CurrentLogFilePath, text + Environment.NewLine, new UTF8Encoding(false));
                LastError = null;
            }
            catch (Exception ex)
            {
                // 日志失败不能再抛异常，否则会掩盖真实业务错误
                LastError = ex;
            }
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

    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO "
    };
}
