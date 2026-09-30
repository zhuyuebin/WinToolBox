using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// Logger 单元测试：按天切割文件名、写入注入的临时目录、三个级别落盘、
/// 异常对象记录、最近日志查找与超期日志清理。
/// 所有日志都写在独立临时目录里，不接触系统 %LocalAppData%。
/// </summary>
public sealed class LoggerTests
{
    /// <summary>创建一个使用临时日志目录的记录器。</summary>
    private static Logger CreateLogger(TempWorkspace ws, int retentionDays = Logger.DefaultRetentionDays)
        => new(ws.Root, retentionDays);

    // ------------------------------------------------------------------
    // 文件名与路径
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 1, 2, "wintoolbox-20260102.log")]
    [InlineData(2026, 12, 31, "wintoolbox-20261231.log")]
    [InlineData(2000, 1, 1, "wintoolbox-20000101.log")]
    public void BuildFileName_FormatsAsWinToolboxDate(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, Logger.BuildFileName(new DateTime(year, month, day)));
    }

    [Fact]
    public void BuildFileName_UsesExpectedPrefixAndSuffix()
    {
        var name = Logger.BuildFileName(DateTime.Now);

        Assert.Equal("wintoolbox-", Logger.FileNamePrefix);
        Assert.StartsWith(Logger.FileNamePrefix, name);
        Assert.EndsWith(".log", name);
        Assert.Equal(Logger.FileNamePrefix.Length + 8 + 4, name.Length);
    }

    [Fact]
    public void CurrentLogFilePath_PointsToInjectedDirectoryWithTodaysFileName()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        Assert.Equal(ws.Root, logger.LogDirectory);
        Assert.Equal(Logger.DefaultRetentionDays, logger.RetentionDays);
        Assert.Equal(Path.Combine(ws.Root, Logger.BuildFileName(DateTime.Now)), logger.CurrentLogFilePath);
    }

    [Fact]
    public void Constructor_WhenRetentionDaysNegative_ClampsToZero()
    {
        using var ws = new TempWorkspace();

        Assert.Equal(0, new Logger(ws.Root, -5).RetentionDays);
        Assert.Equal(0, new Logger(ws.Root, 0).RetentionDays);
        Assert.Equal(7, new Logger(ws.Root, 7).RetentionDays);
    }

    // ------------------------------------------------------------------
    // 写入
    // ------------------------------------------------------------------

    [Fact]
    public void Info_WritesToInjectedDirectoryAndCreatesFile()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        Assert.False(File.Exists(logger.CurrentLogFilePath));

        logger.Info("单元测试信息");

        Assert.True(File.Exists(logger.CurrentLogFilePath));
        Assert.Null(logger.LastError);

        // 文件名符合 wintoolbox-yyyyMMdd.log
        var fileName = Path.GetFileName(logger.CurrentLogFilePath);
        Assert.Matches(@"^wintoolbox-\d{8}\.log$", fileName);

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);
        Assert.Contains("单元测试信息", content);
        Assert.Contains("[INFO ]", content);
        Assert.StartsWith(DateTime.Now.ToString("yyyy-MM-dd"), content);
    }

    [Fact]
    public void Warn_WritesWarningLevelMarkerAndDoesNotNull()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        logger.Warn("磁盘剩余空间不足");

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);
        Assert.Contains("磁盘剩余空间不足", content);
        Assert.Contains("[WARN ]", content);
        Assert.DoesNotContain("[ERROR", content);
        Assert.Null(logger.LastError);
    }

    [Fact]
    public void Error_WritesErrorLevelMarker()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        logger.Error("备份失败");

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);
        Assert.Contains("备份失败", content);
        Assert.Contains("[ERROR]", content);
        Assert.DoesNotContain("[WARN ]", content);
    }

    [Fact]
    public void AllLevels_AppendToSameDailyFile()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        logger.Info("第一条");
        logger.Warn("第二条");
        logger.Error("第三条");
        logger.Write(LogLevel.Info, "第四条");

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);

        // 三种级别都落盘，且顺序保持写入顺序
        Assert.Contains("[INFO ]", content);
        Assert.Contains("[WARN ]", content);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("第一条", content);
        Assert.Contains("第二条", content);
        Assert.Contains("第三条", content);
        Assert.Contains("第四条", content);
        Assert.True(content.IndexOf("第一条", StringComparison.Ordinal) < content.IndexOf("第三条", StringComparison.Ordinal));

        // 只有当天一个日志文件
        Assert.Single(Directory.GetFiles(ws.Root, "*.log"));
    }

    [Fact]
    public void Write_IncludesExceptionTypeAndMessage()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);
        var exception = new InvalidOperationException("模拟的失败原因");

        logger.Error("复制失败", exception);

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);
        Assert.Contains("复制失败", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("模拟的失败原因", content);
    }

    [Fact]
    public void Warn_WhenExceptionGiven_WritesExceptionDetails()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        logger.Warn("读取目录失败", new IOException("设备未就绪"));

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);
        Assert.Contains("[WARN ]", content);
        Assert.Contains("IOException", content);
        Assert.Contains("设备未就绪", content);
    }

    [Fact]
    public void Write_WithNoException_DoesNotAppendExceptionText()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        logger.Info("普通日志");

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);
        Assert.DoesNotContain("System.", content);
        Assert.DoesNotContain("Exception", content);
    }

    [Fact]
    public void Write_RepeatedWrites_AppendRatherThanOverwrite()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        for (var i = 0; i < 5; i++)
        {
            logger.Info($"第{i}条");
        }

        var content = TempWorkspace.ReadAllText(logger.CurrentLogFilePath);

        Assert.Contains("第0条", content);
        Assert.Contains("第4条", content);
        Assert.Equal(5, content.Split("第", StringSplitOptions.None).Length - 1);
    }

    // ------------------------------------------------------------------
    // 最近日志文件
    // ------------------------------------------------------------------

    [Fact]
    public void GetLatestLogFile_WhenDirectoryMissing_ReturnsNull()
    {
        using var ws = new TempWorkspace();
        var missingDirectory = Path.Combine(ws.Root, "no-such-log-dir");
        var logger = new Logger(missingDirectory);

        Assert.False(Directory.Exists(missingDirectory));
        Assert.Null(logger.GetLatestLogFile());
    }

    [Fact]
    public void GetLatestLogFile_WhenDirectoryHasNoLogFiles_ReturnsNull()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);
        Directory.CreateDirectory(ws.Root);
        File.WriteAllText(Path.Combine(ws.Root, "readme.txt"), "没有日志文件");

        Assert.Null(logger.GetLatestLogFile());
    }

    [Fact]
    public void GetLatestLogFile_ReturnsMostRecentByDateInName()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);
        Directory.CreateDirectory(ws.Root);

        // 只按文件名日期排序，不会真正访问文件系统时间
        File.WriteAllText(Path.Combine(ws.Root, "wintoolbox-20010101.log"), "oldest");
        File.WriteAllText(Path.Combine(ws.Root, "wintoolbox-20020101.log"), "middle");
        File.WriteAllText(Path.Combine(ws.Root, "wintoolbox-20030101.log"), "newest");
        File.WriteAllText(Path.Combine(ws.Root, "other-20040101.log"), "not-a-logger-file");

        var latest = logger.GetLatestLogFile();

        Assert.NotNull(latest);
        Assert.Equal("wintoolbox-20030101.log", Path.GetFileName(latest));
    }

    [Fact]
    public void GetLatestLogFile_AfterWrite_ReturnsCurrentLogFile()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws);

        logger.Info("hello");

        Assert.Equal(logger.CurrentLogFilePath, logger.GetLatestLogFile());
    }

    // ------------------------------------------------------------------
    // 清理旧日志
    // ------------------------------------------------------------------

    [Fact]
    public void CleanupOldLogs_DeletesExpiredAndKeepsFilesWithinRetention()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws, retentionDays: 30);
        Directory.CreateDirectory(ws.Root);

        var tooOld = Path.Combine(ws.Root, "wintoolbox-20000101.log");
        var tooOldFreeText = Path.Combine(ws.Root, "wintoolbox-20000102.log.txt");
        var keep = Path.Combine(ws.Root, "wintoolbox-" + DateTime.Now.Date.AddDays(-10).ToString("yyyyMMdd") + ".log");
        File.WriteAllText(tooOld, "非常旧的日志");
        File.WriteAllText(tooOldFreeText, "不是 .log 结尾");
        File.WriteAllText(keep, "保留期内的日志");

        logger.CleanupOldLogs();

        Assert.False(File.Exists(tooOld));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(tooOldFreeText));
        Assert.Null(logger.LastError);
    }

    [Fact]
    public void CleanupOldLogs_KeepsFileExactlyOnThreshold()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws, retentionDays: 30);
        Directory.CreateDirectory(ws.Root);

        // 阈值 = 今天 - 30 天，等于阈值的文件属于“保留期内”
        var boundary = DateTime.Now.Date.AddDays(-30);
        var boundaryFile = Path.Combine(ws.Root, Logger.BuildFileName(boundary));
        File.WriteAllText(boundaryFile, "边界日志");

        logger.CleanupOldLogs();

        Assert.True(File.Exists(boundaryFile));
    }

    [Fact]
    public void CleanupOldLogs_DeletesFileJustBeyondThreshold()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws, retentionDays: 30);
        Directory.CreateDirectory(ws.Root);

        var justExpired = Path.Combine(ws.Root, Logger.BuildFileName(DateTime.Now.Date.AddDays(-31)));
        var kept = Path.Combine(ws.Root, Logger.BuildFileName(DateTime.Now.Date.AddDays(-29)));
        File.WriteAllText(justExpired, "刚刚超过保留期");
        File.WriteAllText(kept, "仍在保留期");

        logger.CleanupOldLogs();

        Assert.False(File.Exists(justExpired));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void CleanupOldLogs_WhenRetentionDisabled_DeletesNothing()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws, retentionDays: 0);
        Directory.CreateDirectory(ws.Root);

        var oldFile = Path.Combine(ws.Root, "wintoolbox-20000101.log");
        File.WriteAllText(oldFile, "旧日志");

        logger.CleanupOldLogs();

        Assert.True(File.Exists(oldFile));
        Assert.Equal(0, logger.RetentionDays);
    }

    [Fact]
    public void CleanupOldLogs_IgnoresFilesWithoutParseableDate()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws, retentionDays: 30);
        Directory.CreateDirectory(ws.Root);

        var weirdName = Path.Combine(ws.Root, "wintoolbox-notadate.log");
        var shortName = Path.Combine(ws.Root, "wintoolbox-123.log");
        File.WriteAllText(weirdName, "名字里没有日期");
        File.WriteAllText(shortName, "名字太短");

        logger.CleanupOldLogs();

        Assert.True(File.Exists(weirdName));
        Assert.True(File.Exists(shortName));
        Assert.Null(logger.LastError);
    }

    [Fact]
    public void CleanupOldLogs_WhenDirectoryMissing_DoesNotThrow()
    {
        using var ws = new TempWorkspace();
        var missingDirectory = Path.Combine(ws.Root, "no-such-log-dir");
        var logger = new Logger(missingDirectory);

        logger.CleanupOldLogs();

        Assert.False(Directory.Exists(missingDirectory));
        Assert.Null(logger.LastError);
    }

    [Fact]
    public void CleanupOldLogs_KeepsTodaysLogAfterWrite()
    {
        using var ws = new TempWorkspace();
        var logger = CreateLogger(ws, retentionDays: 1);

        logger.Info("今天的日志");
        File.WriteAllText(Path.Combine(ws.Root, "wintoolbox-20000101.log"), "很久以前的日志");

        logger.CleanupOldLogs();

        Assert.True(File.Exists(logger.CurrentLogFilePath));
        Assert.False(File.Exists(Path.Combine(ws.Root, "wintoolbox-20000101.log")));
    }

    // ------------------------------------------------------------------
    // 容错
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WhenDirectoryCannotBeCreated_RecordsLastErrorInsteadOfThrowing()
    {
        using var ws = new TempWorkspace();

        // 用一个“同名文件”占住日志目录路径，让目录创建必然失败
        var blocked = Path.Combine(ws.Root, "blocked");
        File.WriteAllText(blocked, "我是一个文件，不是目录");

        var logger = new Logger(Path.Combine(blocked, "logs"));

        // 写入失败只记录 LastError，绝不向调用方抛异常
        logger.Error("这条写不进去");

        Assert.NotNull(logger.LastError);
    }

    [Fact]
    public void Constructor_WhenDirectoryIsWhitespace_FallsBackToDefaultDirectory()
    {
        var logger = new Logger("   ");

        Assert.False(string.IsNullOrWhiteSpace(logger.LogDirectory));
        Assert.True(Path.IsPathFullyQualified(logger.LogDirectory));
    }
}
