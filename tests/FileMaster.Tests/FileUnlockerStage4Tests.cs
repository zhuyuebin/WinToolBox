using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// 阶段四 P1-10「占用查询静默截断到 N 个文件，摘要却断言没有被占用」的回归测试。
/// 覆盖：<see cref="FileLockQueryResult.Truncated"/> 与 <see cref="FileLockQueryResult.TotalFileCount"/> 的计算、
/// 截断与未截断时的摘要文案（未截断必须保持原有文案不变）、复制报告中的「结果不完整」说明，
/// 以及单文件 / 空目录 / 路径不存在 / 小目录 / 超过单次上限这几类边界。
/// </summary>
/// <remarks>
/// <para>本文件刻意不依赖「某个进程真的占用某个文件」——Restart Manager 的结果随环境波动，
/// 这类断言由 <see cref="FileUnlockerServiceTests"/> 负责；这里只验证与占用进程无关的计数、截断标记与文案。</para>
/// <para>受限环境（沙箱会阻断 Restart Manager 依赖的命名管道 / RPC，<c>RmStartSession</c> 直接失败）下，
/// 摘要会是「查询失败：…」；因此所有涉及摘要文案的断言都只在未返回该错误时执行，计数与截断标记则始终断言。</para>
/// <para>已知未覆盖：目录枚举中途失败（权限不足、子目录被删除等）无法在测试里稳定构造，
/// 该分支只做保守标记 <c>Truncated=true</c>，其文案由模型层「截断」用例间接覆盖。</para>
/// </remarks>
public sealed class FileUnlockerStage4Tests
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

    /// <summary>构造一个「已截断且没有任何占用进程」的查询结果。</summary>
    private static FileLockQueryResult TruncatedWithoutLock(int scanned, int total) => new()
    {
        Path = @"D:\data",
        PathExists = true,
        IsDirectory = true,
        ScannedFileCount = scanned,
        TotalFileCount = total,
        Truncated = true
    };

    // ------------------------------------------------------------------
    // 治本：上限本身必须显著提高
    // ------------------------------------------------------------------

    /// <summary>
    /// 旧上限 256 对「照片 / 文档 / 源码树」这类常见目录来说太小，会把结果截断成常态。
    /// 修复后上限应显著提高（至少 4096），并且达到上限时必须暴露 Truncated 而不是静默截断。
    /// </summary>
    [Fact]
    public void MaxFilesPerQuery_IsRaisedWellAboveLegacyLimit()
    {
        Assert.True(
            FileUnlockerService.MaxFilesPerQuery >= 4096,
            $"单次查询上限应显著提高（至少 4096），当前为 {FileUnlockerService.MaxFilesPerQuery}。");
    }

    // ------------------------------------------------------------------
    // 摘要文案：未截断时保持原样
    // ------------------------------------------------------------------

    /// <summary>未被截断且没有占用时，摘要必须保持原有文案（不能因为新增字段而改变既有输出）。</summary>
    [Fact]
    public void Summary_WhenNotTruncatedAndNoLock_KeepsOriginalText()
    {
        var result = new FileLockQueryResult
        {
            Path = @"D:\data\report.txt",
            PathExists = true,
            ScannedFileCount = 1,
            TotalFileCount = 1
        };

        Assert.False(result.Truncated);
        Assert.Equal("没有被任何进程占用。", result.Summary);
    }

    /// <summary>未被截断但发现占用时，摘要同样保持原有文案（不含「结果不完整」）。</summary>
    [Fact]
    public void Summary_WhenNotTruncatedAndLocked_KeepsOriginalText()
    {
        var result = new FileLockQueryResult
        {
            Path = @"D:\data\report.txt",
            PathExists = true,
            ScannedFileCount = 1,
            TotalFileCount = 1,
            Processes = new List<FileLockProcess>
            {
                new() { ProcessId = 1234, ProcessName = "notepad", AppType = LockingAppType.MainWindow }
            }
        };

        Assert.Equal("被 1 个进程占用：notepad.exe(1234)", result.Summary);
        Assert.DoesNotContain("结果不完整", result.Summary);
    }

    // ------------------------------------------------------------------
    // 摘要文案：截断时必须如实说明「结果不完整」
    // ------------------------------------------------------------------

    /// <summary>
    /// 核心回归点：目录被截断且没发现占用时，绝不能再输出「没有被任何进程占用。」，
    /// 而必须写明已检查多少、目录共多少、结果不完整。
    /// </summary>
    [Fact]
    public void Summary_WhenTruncatedWithoutLock_ReportsIncompleteResult()
    {
        var result = TruncatedWithoutLock(scanned: 100, total: 300);

        Assert.Equal(
            "已检查前 100 个文件，未发现占用（该目录共 300 个文件，结果不完整）。",
            result.Summary);
        Assert.DoesNotContain("没有被任何进程占用", result.Summary);
    }

    /// <summary>截断但发现了占用时，占用明细照常给出，同时必须注明「结果不完整」。</summary>
    [Fact]
    public void Summary_WhenTruncatedWithLock_StillMarksResultIncomplete()
    {
        var result = new FileLockQueryResult
        {
            Path = @"D:\data",
            PathExists = true,
            IsDirectory = true,
            ScannedFileCount = 4096,
            TotalFileCount = 5000,
            Truncated = true,
            Processes = new List<FileLockProcess>
            {
                new() { ProcessId = 4321, ProcessName = "excel", AppType = LockingAppType.MainWindow }
            }
        };

        Assert.Contains("excel.exe(4321)", result.Summary);
        Assert.Contains("共 5000 个文件", result.Summary);
        Assert.Contains("结果不完整", result.Summary);
    }

    /// <summary>查询失败（例如 Restart Manager 不可用）时错误优先，摘要仍是「查询失败」，不受截断标记影响。</summary>
    [Fact]
    public void Summary_WhenTruncatedWithError_ReportsErrorFirst()
    {
        var result = new FileLockQueryResult
        {
            Path = @"D:\data",
            PathExists = true,
            IsDirectory = true,
            ScannedFileCount = 10,
            TotalFileCount = 300,
            Truncated = true,
            Error = "RmStartSession 失败（错误码 5）。"
        };

        Assert.Equal("查询失败：RmStartSession 失败（错误码 5）。", result.Summary);
    }

    /// <summary>路径不存在时仍按原语义输出「路径不存在」，截断信息不参与。</summary>
    [Fact]
    public void Summary_WhenPathMissing_KeepsNotExistsText()
    {
        var result = new FileLockQueryResult { Path = @"D:\missing", PathExists = false, Truncated = true };

        Assert.Equal(@"路径不存在：D:\missing", result.Summary);
    }

    // ------------------------------------------------------------------
    // 复制报告
    // ------------------------------------------------------------------

    /// <summary>截断时复制的报告必须写出目录文件总数与「结果不完整」，避免复制出去的文本误导他人。</summary>
    [Fact]
    public void BuildReport_WhenTruncated_IncludesTotalCountAndIncompleteNote()
    {
        var report = FileUnlockerService.BuildReport(TruncatedWithoutLock(scanned: 100, total: 300));

        Assert.Contains("检查文件数：100", report);
        Assert.Contains("目录文件总数：300（结果不完整）", report);
        Assert.Contains("结果不完整", report);
    }

    /// <summary>未截断时报告不出现「目录文件总数」这一行，保持既有输出不变。</summary>
    [Fact]
    public void BuildReport_WhenNotTruncated_OmitsTotalCountLine()
    {
        var result = new FileLockQueryResult
        {
            Path = @"D:\data\a.txt",
            PathExists = true,
            ScannedFileCount = 1,
            TotalFileCount = 1
        };

        var report = FileUnlockerService.BuildReport(result);

        Assert.Contains("检查文件数：1", report);
        Assert.DoesNotContain("目录文件总数", report);
        Assert.Contains("没有被任何进程占用。", report);
    }

    // ------------------------------------------------------------------
    // 真实服务的边界行为（与占用进程无关的部分）
    // ------------------------------------------------------------------

    /// <summary>查询单个文件：总数固定为 1，永远不截断。</summary>
    [Fact]
    public void FindLockingProcesses_ForSingleFile_IsNeverTruncated()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "single.txt");

        var result = new FileUnlockerService().FindLockingProcesses(path);

        Assert.True(result.PathExists);
        Assert.False(result.IsDirectory);
        Assert.Equal(1, result.ScannedFileCount);
        Assert.Equal(1, result.TotalFileCount);
        Assert.False(result.Truncated);
        Assert.DoesNotContain("结果不完整", result.Summary);
    }

    /// <summary>空目录：文件总数为 0，且不算截断（结果确实是完整的：目录里没有文件）。</summary>
    [Fact]
    public void FindLockingProcesses_ForEmptyDirectory_ReportsZeroTotalAndNotTruncated()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("empty");

        var result = new FileUnlockerService().FindLockingProcesses(directory);

        Assert.True(result.PathExists);
        Assert.True(result.IsDirectory);
        Assert.Equal(0, result.ScannedFileCount);
        Assert.Equal(0, result.TotalFileCount);
        Assert.False(result.Truncated);
    }

    /// <summary>路径不存在时没有文件总数、也不算截断（错误语义由摘要「路径不存在」表达）。</summary>
    [Fact]
    public void FindLockingProcesses_ForMissingPath_ReportsNoCountsAndNotTruncated()
    {
        using var ws = new TempWorkspace();

        var result = new FileUnlockerService().FindLockingProcesses(ws.PathOf("missing"));

        Assert.False(result.PathExists);
        Assert.Equal(0, result.ScannedFileCount);
        Assert.Equal(0, result.TotalFileCount);
        Assert.False(result.Truncated);
    }

    /// <summary>小目录（含子目录）会被完整展开：总数等于实际文件数，ScannedFileCount 与之一致且不截断。</summary>
    [Fact]
    public void FindLockingProcesses_ForSmallDirectory_CountsEveryFile()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("small");
        CreateFile(ws, Path.Combine("small", "a.txt"));
        CreateFile(ws, Path.Combine("small", "b.txt"));
        CreateFile(ws, Path.Combine("small", "nested", "c.txt"));

        var result = new FileUnlockerService().FindLockingProcesses(directory);

        Assert.True(result.PathExists);
        Assert.True(result.IsDirectory);
        Assert.Equal(3, result.ScannedFileCount);
        Assert.Equal(3, result.TotalFileCount);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// 核心回归点：目录内文件数超过单次查询上限时，
    /// TotalFileCount 必须是<b>不截断</b>的真实总数、ScannedFileCount 等于上限、Truncated 为 true，
    /// 并且摘要里必须说明结果不完整（修复前这里是静默截断到 256 且口径完整的「没有被占用」）。
    /// </summary>
    [Fact]
    public void FindLockingProcesses_WhenDirectoryExceedsLimit_ReportsTruncationWithFullTotal()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("many");

        var total = FileUnlockerService.MaxFilesPerQuery + 1;

        for (var index = 0; index < total; index++)
        {
            File.WriteAllText(Path.Combine(directory, $"f{index:D5}.tmp"), string.Empty);
        }

        var result = new FileUnlockerService().FindLockingProcesses(directory);

        Assert.True(result.PathExists);
        Assert.True(result.IsDirectory);
        Assert.Equal(FileUnlockerService.MaxFilesPerQuery, result.ScannedFileCount);
        Assert.Equal(total, result.TotalFileCount);
        Assert.True(result.Truncated, "超过单次查询上限时必须标记结果不完整");

        if (result.Error is null)
        {
            Assert.Contains($"已检查前 {FileUnlockerService.MaxFilesPerQuery} 个文件", result.Summary);
            Assert.Contains($"共 {total} 个文件", result.Summary);
            Assert.Contains("结果不完整", result.Summary);
        }
        else
        {
            // 受限环境（Restart Manager 不可用）：错误优先，但截断信息必须已经如实记录
            Assert.Contains("查询失败", result.Summary);
        }

        // 报告（会被复制出去）同样要体现结果不完整
        var report = FileUnlockerService.BuildReport(result);
        Assert.Contains($"目录文件总数：{total}（结果不完整）", report);
    }
}
