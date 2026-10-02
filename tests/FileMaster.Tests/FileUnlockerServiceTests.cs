using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// FileUnlockerService 单元测试。
/// 覆盖：FindLockingProcesses 的「被独占文件 / 已释放 / 文件不存在 / 空路径 / 目录 / 空目录 / 未被占用」七类查询，
/// FindLockingProcessesForFiles 的路径过滤与去重，IsCriticalProcessName 的大小写不敏感保护名单，
/// TerminateProcess 对系统进程（PID 0-4）的拒绝，DescribeAppType 的中文映射，
/// FileLockProcess.CanTerminate 的判定规则，BuildReport / ShortenAppName 的文本输出，
/// 以及 UnlockResult 的计数与摘要。
/// 除「拒绝结束关键进程」这一类只走保护分支的用例外，本文件不会结束任何真实进程；
/// 也不访问注册表、用户配置目录或真实设备。所有文件操作都在各自的临时目录中完成。
/// </summary>
public sealed class FileUnlockerServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>在临时工作区中创建文件（自动补齐缺失的父目录），返回文件的绝对路径。</summary>
    private static string CreateFile(TempWorkspace ws, string relativePath, string content = "sample")
    {
        var fullPath = ws.PathOf(relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    // ------------------------------------------------------------------
    // FindLockingProcesses：核心用例
    // ------------------------------------------------------------------

    /// <summary>
    /// 受限环境（例如沙箱会阻断 Restart Manager 依赖的命名管道/RPC）下 <c>RmStartSession</c> 会失败。
    /// 此时跳过「真实占用查询」的断言，只保留错误信息可读这一前提，避免把环境限制当成代码缺陷。
    /// 在普通桌面环境（无沙箱）中这些断言会真实执行。
    /// </summary>
    private static bool RestartManagerUnavailable(FileLockQueryResult result)
        => result.Error is not null && result.Error.Contains("RmStartSession", StringComparison.Ordinal);

    /// <summary>
    /// 核心用例：当前测试进程以 FileShare.None 独占临时文件时，
    /// Restart Manager 会报告调用进程本身，因此结果中必须出现当前进程 ID。
    /// </summary>
    [Fact]
    public void FindLockingProcesses_WhenFileExclusivelyLocked_ReportsCurrentProcess()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "locked.txt");
        var service = new FileUnlockerService();

        FileLockQueryResult result;

        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(stream.CanWrite, "测试前置条件：文件应以可写方式独占打开");

            result = service.FindLockingProcesses(path);
        }

        if (RestartManagerUnavailable(result))
        {
            return;
        }

        Assert.Null(result.Error);
        Assert.True(result.PathExists, "文件存在时应返回 PathExists=true");
        Assert.False(result.IsDirectory, "文件查询不应被标记为目录");
        Assert.Equal(1, result.ScannedFileCount);

        Assert.Contains(result.Processes, process => process.ProcessId == Environment.ProcessId);
        Assert.True(result.LockCount >= 1, "独占锁至少应产生一个占用进程");
        Assert.False(string.IsNullOrWhiteSpace(result.Summary), "摘要不应为空");
        Assert.Contains(Environment.ProcessId.ToString(), result.Summary);
    }

    /// <summary>
    /// 文件流释放后再次查询，占用应随之消失（每次查询都使用独立的 Restart Manager 会话）。
    /// 句柄关闭在实际系统中可能稍有延迟，因此这里做短暂轮询，避免偶发失败。
    /// </summary>
    [Fact]
    public void FindLockingProcesses_AfterLockReleased_ReportsNoLock()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "released.txt");
        var service = new FileUnlockerService();

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // 只负责持锁，检测能力由核心用例断言
        }

        var deadline = DateTime.UtcNow.AddSeconds(2);
        FileLockQueryResult? latest = null;

        while (DateTime.UtcNow < deadline)
        {
            latest = service.FindLockingProcesses(path);

            if (latest.LockCount == 0)
            {
                break;
            }

            Thread.Sleep(200);
        }

        Assert.NotNull(latest);

        if (RestartManagerUnavailable(latest!))
        {
            return;
        }

        Assert.Null(latest!.Error);
        Assert.True(latest.PathExists);
        Assert.Equal(1, latest.ScannedFileCount);
        Assert.Equal(0, latest.LockCount);
        Assert.Contains("没有", latest.Summary);
    }

    // ------------------------------------------------------------------
    // FindLockingProcesses：路径形态
    // ------------------------------------------------------------------

    /// <summary>文件不存在时报告 PathExists=false、没有任何占用进程，且不算查询失败。</summary>
    [Fact]
    public void FindLockingProcesses_WhenFileMissing_ReportsPathNotExists()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("missing.txt");

        var result = new FileUnlockerService().FindLockingProcesses(missing);

        Assert.Null(result.Error);
        Assert.False(result.PathExists);
        Assert.Empty(result.Processes);
        Assert.Equal(0, result.LockCount);
        Assert.Contains("不存在", result.Summary);
        Assert.Equal(missing, result.Path, ignoreCase: true);
    }

    /// <summary>空路径、纯空白路径与 null 都返回错误说明，且不向外抛异常。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void FindLockingProcesses_WithBlankPath_ReturnsErrorWithoutThrowing(string? path)
    {
        // 生产代码声明为非空参数，但内部按「空或空白」处理，这里显式传入以验证该分支
        var result = new FileUnlockerService().FindLockingProcesses(path!);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Processes);
        Assert.Equal(0, result.LockCount);
    }

    /// <summary>目录查询会递归展开目录内的文件，并标记 IsDirectory。</summary>
    [Fact]
    public void FindLockingProcesses_ForDirectory_ExpandsContainedFiles()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("docs");
        CreateFile(ws, Path.Combine("docs", "readme.txt"));
        CreateFile(ws, Path.Combine("docs", "sub", "inner.txt"));

        var result = new FileUnlockerService().FindLockingProcesses(directory);

        if (RestartManagerUnavailable(result))
        {
            return;
        }

        Assert.Null(result.Error);
        Assert.True(result.PathExists);
        Assert.True(result.IsDirectory, "目录查询应标记 IsDirectory=true");
        Assert.True(result.ScannedFileCount >= 1, "目录查询应至少展开一个文件");
        Assert.Equal(2, result.ScannedFileCount);
    }

    /// <summary>空目录不会向 Restart Manager 注册任何文件：文件数与占用数都是 0。</summary>
    [Fact]
    public void FindLockingProcesses_ForEmptyDirectory_ScansNothing()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("empty");

        var result = new FileUnlockerService().FindLockingProcesses(directory);

        Assert.Null(result.Error);
        Assert.True(result.PathExists);
        Assert.True(result.IsDirectory);
        Assert.Equal(0, result.ScannedFileCount);
        Assert.Equal(0, result.LockCount);
    }

    /// <summary>未被任何进程打开的文件：占用数为 0，摘要提示「没有被任何进程占用」。</summary>
    [Fact]
    public void FindLockingProcesses_ForUnlockedFile_ReportsNoLock()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "free.txt");

        var result = new FileUnlockerService().FindLockingProcesses(path);

        if (RestartManagerUnavailable(result))
        {
            return;
        }

        Assert.Null(result.Error);
        Assert.True(result.PathExists);
        Assert.Equal(1, result.ScannedFileCount);
        Assert.Equal(0, result.LockCount);
        Assert.Contains("没有", result.Summary);
    }

    // ------------------------------------------------------------------
    // FindLockingProcessesForFiles
    // ------------------------------------------------------------------

    /// <summary>列表中的路径都不存在（或为空）时返回空集合，且不抛异常。</summary>
    [Fact]
    public void FindLockingProcessesForFiles_WhenNoFileExists_ReturnsEmpty()
    {
        using var ws = new TempWorkspace();
        var service = new FileUnlockerService();

        var processes = service.FindLockingProcessesForFiles(new[]
        {
            ws.PathOf("missing-1.txt"),
            ws.PathOf(Path.Combine("missing-dir", "missing-2.txt")),
            "   ",
            string.Empty
        });

        Assert.NotNull(processes);
        Assert.Empty(processes);
        Assert.Empty(service.FindLockingProcessesForFiles(Array.Empty<string>()));
    }

    /// <summary>被当前进程独占的文件在批量查询中同样报告当前进程，且结果按进程 ID 去重。</summary>
    [Fact]
    public void FindLockingProcessesForFiles_WhenFileIsLocked_ReportsCurrentProcess()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "bulk-locked.txt");
        var service = new FileUnlockerService();

        IReadOnlyList<FileLockProcess> processes;

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // 同一个路径传两次，用于验证内部按路径去重
            processes = service.FindLockingProcessesForFiles(new[] { path, path });
        }

        if (processes.Count == 0 && RestartManagerUnavailable(service.FindLockingProcesses(path)))
        {
            return;
        }

        Assert.NotEmpty(processes);
        Assert.Contains(processes, process => process.ProcessId == Environment.ProcessId);
        Assert.Equal(processes.Select(static process => process.ProcessId).Distinct().Count(), processes.Count);
    }

    // ------------------------------------------------------------------
    // IsCriticalProcessName
    // ------------------------------------------------------------------

    /// <summary>受保护的系统关键进程名，比较时不区分大小写，也会忽略首尾空白。</summary>
    [Theory]
    [InlineData("System")]
    [InlineData("system")]
    [InlineData("lsass")]
    [InlineData("LSASS")]
    [InlineData("csrss")]
    [InlineData("SVCHOST")]
    [InlineData("  lsass  ")]
    [InlineData("Memory Compression")]
    public void IsCriticalProcessName_ForProtectedNames_ReturnsTrue(string name)
    {
        Assert.True(FileUnlockerService.IsCriticalProcessName(name));
    }

    /// <summary>普通进程名、空串、空白与 null 都不属于受保护名单。</summary>
    [Theory]
    [InlineData("notepad")]
    [InlineData("explorer")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsCriticalProcessName_ForOrdinaryOrBlankName_ReturnsFalse(string? name)
    {
        Assert.False(FileUnlockerService.IsCriticalProcessName(name));
    }

    // ------------------------------------------------------------------
    // TerminateProcess：只验证保护分支
    // ------------------------------------------------------------------

    /// <summary>
    /// PID 0-4 属于系统 / 空闲进程，结束请求必然被保护逻辑拒绝：返回失败、Rejected=true、带错误说明，且不抛异常。
    /// 本用例不会真的去结束任何进程（保护判断发生在调用 Process.Kill 之前）。
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public void TerminateProcess_ForSystemProcessId_IsRejected(int processId)
    {
        var result = new FileUnlockerService().TerminateProcess(processId);

        Assert.Equal(processId, result.ProcessId);
        Assert.False(result.Success);
        Assert.True(result.Rejected, "系统进程应被主动拒绝");
        Assert.NotNull(result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ------------------------------------------------------------------
    // DescribeAppType
    // ------------------------------------------------------------------

    /// <summary>应用类型到中文说明的映射。</summary>
    [Theory]
    [InlineData(LockingAppType.MainWindow, "桌面应用")]
    [InlineData(LockingAppType.OtherWindow, "后台窗口")]
    [InlineData(LockingAppType.Service, "Windows 服务")]
    [InlineData(LockingAppType.Explorer, "资源管理器")]
    [InlineData(LockingAppType.Console, "控制台程序")]
    [InlineData(LockingAppType.Critical, "关键系统进程")]
    [InlineData(LockingAppType.Unknown, "未知类型")]
    public void DescribeAppType_ReturnsChineseText(LockingAppType type, string expected)
    {
        Assert.Equal(expected, FileUnlockerService.DescribeAppType(type));
    }

    /// <summary>未定义的枚举值落到默认分支，返回「未知类型」。</summary>
    [Fact]
    public void DescribeAppType_ForUndefinedValue_ReturnsUnknownText()
    {
        Assert.Equal("未知类型", FileUnlockerService.DescribeAppType((LockingAppType)999));
    }

    // ------------------------------------------------------------------
    // FileLockProcess.CanTerminate
    // ------------------------------------------------------------------

    /// <summary>
    /// 关键类型、PID &lt;= 4 或关键进程名三者之一命中时都不允许结束，只有普通应用才允许。
    /// </summary>
    [Theory]
    [InlineData(1234, "notepad", LockingAppType.MainWindow, true)]
    [InlineData(1234, "notepad", LockingAppType.Service, true)]
    [InlineData(1234, "notepad", LockingAppType.Critical, false)]
    [InlineData(4, "notepad", LockingAppType.MainWindow, false)]
    [InlineData(1234, "lsass", LockingAppType.MainWindow, false)]
    [InlineData(1234, "", LockingAppType.MainWindow, true)]
    public void CanTerminate_ReflectsCriticalRules(
        int processId,
        string processName,
        LockingAppType appType,
        bool expected)
    {
        var process = new FileLockProcess
        {
            ProcessId = processId,
            ProcessName = processName,
            AppType = appType
        };

        Assert.Equal(expected, process.CanTerminate);
    }

    // ------------------------------------------------------------------
    // BuildReport
    // ------------------------------------------------------------------

    /// <summary>报告文本包含路径、检查文件数、一句话摘要，以及每个占用进程的显示名与 PID。</summary>
    [Fact]
    public void BuildReport_ContainsPathSummaryAndProcessLines()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathOf("report.txt");

        var result = new FileLockQueryResult
        {
            Path = path,
            PathExists = true,
            IsDirectory = false,
            ScannedFileCount = 3,
            Processes = new List<FileLockProcess>
            {
                new()
                {
                    ProcessId = 1234,
                    ProcessName = "notepad",
                    AppType = LockingAppType.MainWindow,
                    Restartable = true
                }
            }
        };

        var report = FileUnlockerService.BuildReport(result);

        Assert.Contains(path, report);
        Assert.Contains("检查文件数：3", report);
        Assert.Contains("是否目录：否", report);
        Assert.Contains(result.Summary, report);
        Assert.Contains("notepad.exe", report);
        Assert.Contains("PID 1234", report);
        Assert.Contains("可重启", report);
    }

    /// <summary>没有被占用时报告仍然给出文件数与摘要，但不含任何进程明细行。</summary>
    [Fact]
    public void BuildReport_WhenNoLock_StillContainsSummary()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "report-free.txt");

        var result = new FileUnlockerService().FindLockingProcesses(path);
        var report = FileUnlockerService.BuildReport(result);

        // 受限环境（沙箱内 Restart Manager 不可用）下摘要为「查询失败」，此时只验证报告结构
        if (RestartManagerUnavailable(result))
        {
            Assert.Contains(path, report);
            Assert.Contains("查询失败", report);
            return;
        }

        Assert.Contains("检查文件数：1", report);
        Assert.Contains("没有", report);
        Assert.DoesNotContain("PID ", report);
    }

    /// <summary>传入 null 结果时抛出 ArgumentNullException。</summary>
    [Fact]
    public void BuildReport_WithNullResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FileUnlockerService.BuildReport(null!));
    }

    // ------------------------------------------------------------------
    // ShortenAppName
    // ------------------------------------------------------------------

    /// <summary>超长应用名被截断为「…」加尾部片段，总长度等于默认上限 60。</summary>
    [Fact]
    public void ShortenAppName_WhenTooLong_TruncatesWithEllipsis()
    {
        var longName = new string('a', 200);

        var shortened = FileUnlockerService.ShortenAppName(longName);

        Assert.StartsWith("…", shortened);
        Assert.Equal(60, shortened.Length);
        Assert.Equal("…" + longName[^59..], shortened);
    }

    /// <summary>显式传入 maxLength 时按该上限截断，省略号计入长度。</summary>
    [Fact]
    public void ShortenAppName_WithCustomMaxLength_TruncatesToThatLength()
    {
        var shortened = FileUnlockerService.ShortenAppName("0123456789", 5);

        Assert.StartsWith("…", shortened);
        Assert.Equal(5, shortened.Length);
        Assert.Equal("…6789", shortened);
    }

    /// <summary>短名称原样返回（去掉首尾空白）；null、空串与纯空白返回空串。</summary>
    [Theory]
    [InlineData("notepad", "notepad")]
    [InlineData("  notepad  ", "notepad")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ShortenAppName_ForShortOrBlankName_ReturnsTrimmedText(string? appName, string expected)
    {
        Assert.Equal(expected, FileUnlockerService.ShortenAppName(appName));
    }

    // ------------------------------------------------------------------
    // UnlockResult
    // ------------------------------------------------------------------

    /// <summary>一个成功、一个失败：计数分别为 1，整体判定为失败，摘要含成功与失败数量。</summary>
    [Fact]
    public void UnlockResult_CountsSucceededAndFailedItems()
    {
        var result = new UnlockResult
        {
            Items = new List<ProcessTerminateResult>
            {
                new() { ProcessId = 1234, ProcessName = "notepad", Success = true },
                new() { ProcessId = 5678, ProcessName = "chrome", Success = false, Error = "拒绝访问" }
            }
        };

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.False(result.Success);
        Assert.Contains("成功结束 1 个进程", result.Summary);
        Assert.Contains("失败 1 个", result.Summary);
    }

    /// <summary>没有任何明细时失败数为 0，整体判定为成功。</summary>
    [Fact]
    public void UnlockResult_WithoutItems_ReportsSuccess()
    {
        var result = new UnlockResult();

        Assert.Empty(result.Items);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.Success);
        Assert.Contains("失败 0 个", result.Summary);
    }
}
