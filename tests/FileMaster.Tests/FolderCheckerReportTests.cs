using WinToolBox.Core.Services;
using WinToolBox.Tools.FileMaster;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// 检查结果到报告数据的映射（<see cref="FolderChecker.ToReport"/>）与「检查 → 导出报告」的联通测试。
/// </summary>
public class FolderCheckerReportTests
{
    [Fact]
    public void ToReport_MapsStatusesAndMetadata()
    {
        var checkedAt = new DateTimeOffset(2026, 10, 2, 3, 0, 0, TimeSpan.FromHours(8));
        var result = new FolderCheckResult
        {
            RootDirectory = @"C:\项目",
            Mode = FolderCheckMode.Strict,
            Items = new[]
            {
                new FolderCheckItem { RelativePath = "匹配项", Depth = 1, Status = FolderCheckStatus.Matched },
                new FolderCheckItem { RelativePath = "缺失项", Depth = 1, Status = FolderCheckStatus.Missing },
                new FolderCheckItem { RelativePath = "多余项", Depth = 1, Status = FolderCheckStatus.Extra }
            }
        };

        var report = FolderChecker.ToReport(result, checkedAt);

        Assert.Equal(@"C:\项目", report.RootDirectory);
        Assert.Equal("严格模式", report.ModeText);
        Assert.Equal(checkedAt, report.CheckedAt);
        Assert.Equal(3, report.Entries.Count);
        Assert.Equal(FolderCheckEntryKind.Matched, report.Entries[0].Kind);
        Assert.Equal(FolderCheckEntryKind.Missing, report.Entries[1].Kind);
        Assert.Equal(FolderCheckEntryKind.Extra, report.Entries[2].Kind);
        Assert.Equal(1, report.MatchedCount);
        Assert.Equal(1, report.MissingCount);
        Assert.Equal(1, report.ExtraCount);
        Assert.False(report.IsConsistent);
    }

    [Fact]
    public void ToReport_WhenLooseMode_UsesLooseModeText()
    {
        var result = new FolderCheckResult
        {
            RootDirectory = @"C:\项目",
            Mode = FolderCheckMode.Loose,
            Items = Array.Empty<FolderCheckItem>()
        };

        Assert.Equal("宽松模式", FolderChecker.ToReport(result).ModeText);
    }

    [Fact]
    public void ToReport_WhenResultIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FolderChecker.ToReport(null!));
    }

    [Fact]
    public void ToReport_WhenTimeIsOmitted_UsesCurrentTime()
    {
        var before = DateTimeOffset.Now.AddSeconds(-2);
        var result = new FolderCheckResult
        {
            RootDirectory = @"C:\项目",
            Mode = FolderCheckMode.Strict,
            Items = Array.Empty<FolderCheckItem>()
        };

        var report = FolderChecker.ToReport(result);

        Assert.True(report.CheckedAt >= before);
    }

    [Fact]
    public void CheckThenExport_ProducesReportWithRealDifferences()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("存在");
        ws.CreateDirectory("额外目录");

        var rules = new RuleParser().Parse("-存在\r\n-缺失").Rules;
        var result = new FolderChecker().Check(ws.Root, rules, FolderCheckMode.Strict);

        Assert.Equal(1, result.MissingCount);
        Assert.Equal(1, result.ExtraCount);

        var report = FolderChecker.ToReport(result, DateTimeOffset.Now);
        var path = ws.PathOf("报告/report.md");
        var written = new FolderCreatorService().ExportCheckReport(path, report);

        var text = File.ReadAllText(written);
        Assert.Contains("## 缺失的文件夹（1）", text, StringComparison.Ordinal);
        Assert.Contains("- 缺失", text, StringComparison.Ordinal);
        Assert.Contains("## 多余的文件夹（1）", text, StringComparison.Ordinal);
        Assert.Contains("- 额外目录", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RespectsCancellation()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("目录");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var rules = new RuleParser().Parse("-目录").Rules;

        Assert.Throws<OperationCanceledException>(
            () => new FolderChecker().Check(ws.Root, rules, FolderCheckMode.Strict, cts.Token));
    }
}
