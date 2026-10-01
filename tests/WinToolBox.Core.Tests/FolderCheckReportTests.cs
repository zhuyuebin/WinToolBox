using System.Text;
using WinToolBox.Core.Services;

namespace WinToolBox.Core.Tests;

/// <summary>检查报告导出（<see cref="FolderCreatorService.ExportCheckReport"/>）的单元测试。</summary>
public class FolderCheckReportTests
{
    private static FolderCheckReport CreateReport(
        int matched = 0,
        int missing = 0,
        int extra = 0,
        string root = @"C:\项目",
        string mode = "严格模式")
    {
        var entries = new List<FolderCheckEntry>();

        for (var i = 0; i < matched; i++)
        {
            entries.Add(new FolderCheckEntry { RelativePath = $@"匹配{i:000}", Kind = FolderCheckEntryKind.Matched });
        }

        for (var i = 0; i < missing; i++)
        {
            entries.Add(new FolderCheckEntry { RelativePath = $@"缺失{i:000}", Kind = FolderCheckEntryKind.Missing });
        }

        for (var i = 0; i < extra; i++)
        {
            entries.Add(new FolderCheckEntry { RelativePath = $@"多余{i:000}", Kind = FolderCheckEntryKind.Extra });
        }

        return new FolderCheckReport
        {
            RootDirectory = root,
            ModeText = mode,
            CheckedAt = new DateTimeOffset(2026, 10, 2, 2, 30, 15, TimeSpan.FromHours(8)),
            Entries = entries
        };
    }

    [Fact]
    public void BuildCheckReport_ContainsTitleMetadataAndStatistics()
    {
        var markdown = FolderCreatorService.BuildCheckReport(CreateReport(matched: 3, missing: 2, extra: 1));

        Assert.StartsWith("# 目录一致性检查报告", markdown, StringComparison.Ordinal);
        Assert.Contains("| 检查时间 | 2026-10-02 02:30:15 |", markdown, StringComparison.Ordinal);
        Assert.Contains(@"| 目标目录 | C:\\项目 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 检查模式 | 严格模式 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 检查结论 | 存在差异 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 匹配 | 3 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 缺失 | 2 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 多余 | 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 合计 | 6 |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCheckReport_ListsEveryCategoryWithCounts()
    {
        var markdown = FolderCreatorService.BuildCheckReport(CreateReport(matched: 1, missing: 1, extra: 1));

        Assert.Contains("## 缺失的文件夹（1）", markdown, StringComparison.Ordinal);
        Assert.Contains("- 缺失000", markdown, StringComparison.Ordinal);
        Assert.Contains("## 多余的文件夹（1）", markdown, StringComparison.Ordinal);
        Assert.Contains("- 多余000", markdown, StringComparison.Ordinal);
        Assert.Contains("## 匹配的文件夹（1）", markdown, StringComparison.Ordinal);
        Assert.Contains("- 匹配000", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCheckReport_WhenConsistent_ReportsNoDifferences()
    {
        var markdown = FolderCreatorService.BuildCheckReport(CreateReport(matched: 2));

        Assert.Contains("| 检查结论 | 完全一致 |", markdown, StringComparison.Ordinal);
        Assert.Contains("## 缺失的文件夹（0）", markdown, StringComparison.Ordinal);
        Assert.Contains("## 多余的文件夹（0）", markdown, StringComparison.Ordinal);
        Assert.Contains("无。", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<details>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCheckReport_WhenSectionExceedsThreshold_FoldsWithDetails()
    {
        var markdown = FolderCreatorService.BuildCheckReport(CreateReport(missing: 101));

        Assert.Contains("<details>", markdown, StringComparison.Ordinal);
        Assert.Contains("<summary>展开查看 101 项</summary>", markdown, StringComparison.Ordinal);
        Assert.Contains("</details>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCheckReport_WhenSectionIsExactlyThreshold_DoesNotFold()
    {
        var markdown = FolderCreatorService.BuildCheckReport(CreateReport(missing: FolderCreatorService.ReportFoldThreshold));

        Assert.DoesNotContain("<details>", markdown, StringComparison.Ordinal);
        Assert.Contains($"## 缺失的文件夹（{FolderCreatorService.ReportFoldThreshold}）", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCheckReport_OnlyLargeSectionsAreFolded()
    {
        var markdown = FolderCreatorService.BuildCheckReport(CreateReport(missing: 101, extra: 1));

        // 多余只有 1 条 → 不折叠；缺失 101 条 → 折叠
        var detailsIndex = markdown.IndexOf("<details>", StringComparison.Ordinal);
        var extraIndex = markdown.IndexOf("## 多余的文件夹（1）", StringComparison.Ordinal);

        Assert.True(detailsIndex > 0);
        Assert.True(extraIndex > detailsIndex, "「多余」小节应排在「缺失」之后且不折叠");
        Assert.Equal(1, markdown.Split("<details>").Length - 1);
    }

    [Fact]
    public void BuildCheckReport_EscapesBackticksPipesAndBackslashes()
    {
        var report = new FolderCheckReport
        {
            RootDirectory = "C:\\a|b",
            ModeText = "严格模式",
            Entries = new[]
            {
                new FolderCheckEntry { RelativePath = "含`反引号", Kind = FolderCheckEntryKind.Missing },
                new FolderCheckEntry { RelativePath = "含|竖线", Kind = FolderCheckEntryKind.Missing }
            }
        };

        var markdown = FolderCreatorService.BuildCheckReport(report);

        Assert.Contains(@"C:\\a\|b", markdown, StringComparison.Ordinal);
        Assert.Contains(@"- 含\`反引号", markdown, StringComparison.Ordinal);
        Assert.Contains(@"- 含\|竖线", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("a`b", @"a\`b")]
    [InlineData("a|b", @"a\|b")]
    [InlineData("a\\b", @"a\\b")]
    public void EscapeInline_HandlesSpecialCharacters(string? input, string expected)
    {
        Assert.Equal(expected, FolderCreatorService.EscapeInline(input));
    }

    [Fact]
    public void EscapeInline_ReplacesLineBreaksWithSpaces()
    {
        Assert.Equal("a b", FolderCreatorService.EscapeInline("a\r\nb"));
    }

    [Fact]
    public void ExportCheckReport_WritesUtf8MarkdownAndCreatesDirectory()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "报告", "子目录", "report.md");

        var written = new FolderCreatorService().ExportCheckReport(path, CreateReport(matched: 1, missing: 1));

        Assert.Equal(Path.GetFullPath(path), written);
        Assert.True(File.Exists(written));

        var bytes = File.ReadAllBytes(written);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var text = File.ReadAllText(written, Encoding.UTF8);
        Assert.StartsWith("# 目录一致性检查报告", text, StringComparison.Ordinal);
        Assert.Contains("## 缺失的文件夹（1）", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportCheckReport_OverwritesExistingFile()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "report.md");
        File.WriteAllText(path, "旧内容");

        new FolderCreatorService().ExportCheckReport(path, CreateReport(matched: 2));

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("旧内容", text, StringComparison.Ordinal);
        Assert.Contains("# 目录一致性检查报告", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportCheckReport_WhenPathIsBlank_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new FolderCreatorService().ExportCheckReport("  ", CreateReport()));
    }

    [Fact]
    public void ExportCheckReport_WhenReportIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new FolderCreatorService().ExportCheckReport("report.md", null!));
    }

    [Fact]
    public void Report_CountsAreDerivedFromEntries()
    {
        var report = CreateReport(matched: 2, missing: 3, extra: 4);

        Assert.Equal(2, report.MatchedCount);
        Assert.Equal(3, report.MissingCount);
        Assert.Equal(4, report.ExtraCount);
        Assert.False(report.IsConsistent);
    }

    [Fact]
    public void Report_IsConsistentWhenOnlyMatched()
    {
        Assert.True(CreateReport(matched: 5).IsConsistent);
        Assert.True(new FolderCheckReport().IsConsistent);
    }
}
