using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// FolderDiffService 单元测试。
/// 覆盖：ParseExcludes 的空值 / 多种分隔符 / 去重 / 去空白；Compare 的左右目录缺失错误、
/// 完全相同 / 仅左侧 / 仅右侧 / 大小不同 / 修改时间不同、SizeOnly 与 TimeToleranceSeconds 的判定、
/// Hash 模式下「大小相同但内容不同」的识别；子目录开关、ExcludeNames 过滤、相对路径排序；
/// StatusText 的中英文映射与 BuildReport 的表头与摘要。
/// 所有用例都在各自的临时目录中执行，不依赖真实 U 盘、网络路径或用户目录。
/// </summary>
public sealed class FolderDiffServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>左侧目录名（位于临时工作区内）。</summary>
    private const string LeftName = "left";

    /// <summary>右侧目录名（位于临时工作区内）。</summary>
    private const string RightName = "right";

    /// <summary>比对基准时间（2024-05-01 10:00:00），用于修改时间相关的断言。</summary>
    private static readonly DateTime BaseTime = new(2024, 5, 1, 10, 0, 0);

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

    /// <summary>在左、右两个目录中创建同名同内容的文件，返回两个文件的绝对路径。</summary>
    private static (string Left, string Right) CreatePair(TempWorkspace ws, string relativePath, string content)
    {
        var left = CreateFile(ws, Path.Combine(LeftName, relativePath), content);
        var right = CreateFile(ws, Path.Combine(RightName, relativePath), content);

        return (left, right);
    }

    /// <summary>把「左 / 右」两个文件的大小与修改时间显式固定下来，避免依赖文件系统的实时时钟。</summary>
    private static void FixTimes(string leftPath, string rightPath, DateTime leftTime, DateTime rightTime)
    {
        File.SetLastWriteTime(leftPath, leftTime);
        File.SetLastWriteTime(rightPath, rightTime);
    }

    /// <summary>构造比对选项：左右目录固定为临时工作区中的 left / right，其余参数按需覆盖。</summary>
    private static FolderDiffOptions CreateOptions(
        TempWorkspace ws,
        DiffCompareMode compareMode = DiffCompareMode.SizeAndTime,
        bool includeSubDirectories = true,
        int timeToleranceSeconds = 2,
        string? excludeNames = null)
        => new()
        {
            LeftDirectory = ws.PathOf(LeftName),
            RightDirectory = ws.PathOf(RightName),
            CompareMode = compareMode,
            IncludeSubDirectories = includeSubDirectories,
            TimeToleranceSeconds = timeToleranceSeconds,
            ExcludeNames = excludeNames
        };

    // ------------------------------------------------------------------
    // ParseExcludes
    // ------------------------------------------------------------------

    /// <summary>忽略名单为空（null / 空串 / 只有分隔符）时返回空列表。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;,,\r\n")]
    public void ParseExcludes_WithEmptyText_ReturnsEmpty(string? text)
    {
        Assert.Empty(FolderDiffService.ParseExcludes(text));
    }

    /// <summary>忽略名单支持分号 / 逗号 / 换行分隔，并去掉每一项两端的空白。</summary>
    [Fact]
    public void ParseExcludes_SplitsOnSemicolonCommaAndNewLine()
    {
        var result = FolderDiffService.ParseExcludes(" a.txt ;b.txt,c.txt\r\nd.txt ");

        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "d.txt" }, result);
    }

    /// <summary>重复项忽略大小写去重，并保留第一次出现的写法。</summary>
    [Fact]
    public void ParseExcludes_RemovesDuplicatesIgnoringCase()
    {
        Assert.Equal(new[] { "Temp" }, FolderDiffService.ParseExcludes("Temp;temp;TEMP"));
    }

    // ------------------------------------------------------------------
    // Compare：目录校验
    // ------------------------------------------------------------------

    /// <summary>左侧目录不存在时返回错误说明，不抛异常。</summary>
    [Fact]
    public void Compare_WhenLeftDirectoryMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(RightName);

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.NotNull(result.Error);
        Assert.Contains("左侧目录", result.Error!);
        Assert.Empty(result.Items);
        Assert.False(result.IsIdentical, "有错误时不应视为完全一致");
    }

    /// <summary>右侧目录不存在时返回错误说明，不抛异常。</summary>
    [Fact]
    public void Compare_WhenRightDirectoryMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(LeftName);

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.NotNull(result.Error);
        Assert.Contains("右侧目录", result.Error!);
        Assert.Empty(result.Items);
    }

    // ------------------------------------------------------------------
    // Compare：状态判定
    // ------------------------------------------------------------------

    /// <summary>两侧文件大小与修改时间都一致时判为 Same，整体 IsIdentical。</summary>
    [Fact]
    public void Compare_WhenBothSidesIdentical_ReturnsSameAndIdentical()
    {
        using var ws = new TempWorkspace();
        var (left, right) = CreatePair(ws, "a.txt", "identical-content");
        FixTimes(left, right, BaseTime, BaseTime);

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal("a.txt", item.RelativePath);
        Assert.Equal(DiffStatus.Same, item.Status);
        Assert.Equal(1, result.SameCount);
        Assert.Equal(0, result.DifferentCount);
        Assert.True(result.IsIdentical);
        Assert.Equal(left, item.LeftPath!, ignoreCase: true);
        Assert.Equal(right, item.RightPath!, ignoreCase: true);
    }

    /// <summary>只有左侧存在时判为 LeftOnly，右侧路径与右侧大小为空。</summary>
    [Fact]
    public void Compare_WhenFileExistsOnlyOnLeft_ReturnsLeftOnly()
    {
        using var ws = new TempWorkspace();
        CreateFile(ws, Path.Combine(LeftName, "only-left.txt"), "left");
        ws.CreateDirectory(RightName);

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(DiffStatus.LeftOnly, item.Status);
        Assert.Equal("仅左侧存在", item.Message);
        Assert.Null(item.RightPath);
        Assert.True(File.Exists(item.LeftPath!), "LeftPath 应指向真实文件");
        Assert.Equal(4L, item.LeftSize!.Value);
        Assert.Equal(1, result.LeftOnlyCount);
        Assert.False(result.IsIdentical);
    }

    /// <summary>只有右侧存在时判为 RightOnly，左侧路径与左侧大小为空。</summary>
    [Fact]
    public void Compare_WhenFileExistsOnlyOnRight_ReturnsRightOnly()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(LeftName);
        CreateFile(ws, Path.Combine(RightName, "only-right.txt"), "right!");

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(DiffStatus.RightOnly, item.Status);
        Assert.Equal("仅右侧存在", item.Message);
        Assert.Null(item.LeftPath);
        Assert.True(File.Exists(item.RightPath!), "RightPath 应指向真实文件");
        Assert.Equal(6L, item.RightSize!.Value);
        Assert.Equal(1, result.RightOnlyCount);
        Assert.False(result.IsIdentical);
    }

    /// <summary>两侧文件大小不同时判为 Different，说明中包含「大小」。</summary>
    [Fact]
    public void Compare_WhenSizesDiffer_ReturnsDifferentWithSizeMessage()
    {
        using var ws = new TempWorkspace();
        CreateFile(ws, Path.Combine(LeftName, "a.txt"), "short");
        CreateFile(ws, Path.Combine(RightName, "a.txt"), "a-much-longer-content");

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(DiffStatus.Different, item.Status);
        Assert.Contains("大小", item.Message);
        Assert.Equal(1, result.DifferentCount);
        Assert.False(result.IsIdentical);
    }

    /// <summary>大小相同但修改时间相差较大（SizeAndTime 模式）时判为 Different。</summary>
    [Fact]
    public void Compare_WhenModifiedTimeDiffersGreatly_ReturnsDifferent()
    {
        using var ws = new TempWorkspace();
        var (left, right) = CreatePair(ws, "a.txt", "same-size");
        FixTimes(left, right, BaseTime, BaseTime.AddHours(8));

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(DiffStatus.Different, item.Status);
        Assert.Contains("修改时间", item.Message);
    }

    /// <summary>SizeOnly 模式只比大小，忽略修改时间差异。</summary>
    [Fact]
    public void Compare_SizeOnlyMode_IgnoresModifiedTimeDifference()
    {
        using var ws = new TempWorkspace();
        var (left, right) = CreatePair(ws, "a.txt", "same-size");
        FixTimes(left, right, BaseTime, BaseTime.AddHours(8));

        var result = new FolderDiffService().Compare(
            CreateOptions(ws, compareMode: DiffCompareMode.SizeOnly));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(DiffStatus.Same, item.Status);
        Assert.True(result.IsIdentical);
    }

    /// <summary>时间差在 TimeToleranceSeconds 之内视为相同，容差为 0 时视为不同。</summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public void Compare_TimeToleranceSeconds_ControlsSameStatus(int toleranceSeconds, bool expectedSame)
    {
        using var ws = new TempWorkspace();
        var (left, right) = CreatePair(ws, "a.txt", "same-size");
        FixTimes(left, right, BaseTime, BaseTime.AddSeconds(1));

        var result = new FolderDiffService().Compare(
            CreateOptions(ws, timeToleranceSeconds: toleranceSeconds));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(expectedSame ? DiffStatus.Same : DiffStatus.Different, item.Status);
    }

    // ------------------------------------------------------------------
    // Compare：Hash 模式
    // ------------------------------------------------------------------

    /// <summary>Hash 模式下内容相同判为 Same，且不受修改时间差异影响。</summary>
    [Fact]
    public void Compare_HashMode_WhenContentIsSame_ReturnsSame()
    {
        using var ws = new TempWorkspace();
        var (left, right) = CreatePair(ws, "a.txt", "abcdefgh");
        FixTimes(left, right, BaseTime, BaseTime.AddHours(8));

        var result = new FolderDiffService().Compare(
            CreateOptions(ws, compareMode: DiffCompareMode.Hash));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(DiffStatus.Same, item.Status);
        Assert.Contains("SHA256", item.Message);
        Assert.True(result.IsIdentical);
    }

    /// <summary>Hash 模式下大小相同但内容不同（aaaa / bbbb）时判为 Different。</summary>
    [Fact]
    public void Compare_HashMode_WhenContentDiffersWithSameSize_ReturnsDifferent()
    {
        using var ws = new TempWorkspace();
        CreateFile(ws, Path.Combine(LeftName, "a.txt"), "aaaa");
        CreateFile(ws, Path.Combine(RightName, "a.txt"), "bbbb");

        var result = new FolderDiffService().Compare(
            CreateOptions(ws, compareMode: DiffCompareMode.Hash));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal(DiffStatus.Different, item.Status);
        Assert.Contains("SHA256", item.Message);
        Assert.Equal(4L, item.LeftSize!.Value);
        Assert.Equal(4L, item.RightSize!.Value);
        Assert.Equal(1, result.DifferentCount);
    }

    // ------------------------------------------------------------------
    // Compare：子目录、忽略名单与排序
    // ------------------------------------------------------------------

    /// <summary>IncludeSubDirectories 为 false 时不比对子目录中的文件。</summary>
    [Fact]
    public void Compare_WithoutSubDirectories_IgnoresNestedFiles()
    {
        using var ws = new TempWorkspace();
        CreatePair(ws, "top.txt", "top");
        CreateFile(ws, Path.Combine(LeftName, "sub", "nested.txt"), "nested");

        var result = new FolderDiffService().Compare(
            CreateOptions(ws, includeSubDirectories: false));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal("top.txt", item.RelativePath);

        var recursive = new FolderDiffService().Compare(
            CreateOptions(ws, includeSubDirectories: true));

        Assert.Null(recursive.Error);
        Assert.Equal(2, recursive.Items.Count);
        Assert.Equal(1, recursive.LeftOnlyCount);
    }

    /// <summary>ExcludeNames 命中的文件不出现在结果中。</summary>
    [Fact]
    public void Compare_ExcludeNames_SkipsMatchedFiles()
    {
        using var ws = new TempWorkspace();
        CreatePair(ws, "keep.txt", "keep");
        CreatePair(ws, "skip.tmp", "skip");

        var result = new FolderDiffService().Compare(CreateOptions(ws, excludeNames: "skip.tmp"));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal("keep.txt", item.RelativePath);
    }

    /// <summary>ExcludeNames 命中目录名时，该目录下的所有文件都被跳过。</summary>
    [Fact]
    public void Compare_ExcludeNames_SkipsFilesInsideExcludedDirectory()
    {
        using var ws = new TempWorkspace();
        CreateFile(ws, Path.Combine(LeftName, "cache", "data.txt"), "cache");
        CreateFile(ws, Path.Combine(LeftName, "data.txt"), "data");
        ws.CreateDirectory(RightName);

        var result = new FolderDiffService().Compare(CreateOptions(ws, excludeNames: "cache"));

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal("data.txt", item.RelativePath);
        Assert.Equal(DiffStatus.LeftOnly, item.Status);
    }

    /// <summary>结果按相对路径排序（SortedSet，忽略大小写）。</summary>
    [Fact]
    public void Compare_ReturnsItemsSortedByRelativePath()
    {
        using var ws = new TempWorkspace();
        CreatePair(ws, "b.txt", "b");
        CreatePair(ws, "a.txt", "a");
        CreatePair(ws, "c.txt", "c");

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.Null(result.Error);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(
            new[] { "a.txt", "b.txt", "c.txt" },
            result.Items.Select(static item => item.RelativePath));
    }

    /// <summary>两侧目录都为空时没有任何差异条目，视为完全一致。</summary>
    [Fact]
    public void Compare_WhenBothDirectoriesAreEmpty_IsIdentical()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(LeftName);
        ws.CreateDirectory(RightName);

        var result = new FolderDiffService().Compare(CreateOptions(ws));

        Assert.Null(result.Error);
        Assert.Empty(result.Items);
        Assert.True(result.IsIdentical);
    }

    // ------------------------------------------------------------------
    // StatusText 与 BuildReport
    // ------------------------------------------------------------------

    /// <summary>StatusText 把四种状态映射为中文说明。</summary>
    [Theory]
    [InlineData(DiffStatus.Same, "相同")]
    [InlineData(DiffStatus.LeftOnly, "仅左侧")]
    [InlineData(DiffStatus.RightOnly, "仅右侧")]
    [InlineData(DiffStatus.Different, "内容不同")]
    public void StatusText_MapsStatusToChineseText(DiffStatus status, string expected)
    {
        Assert.Equal(expected, FolderDiffService.StatusText(status));
    }

    /// <summary>BuildReport 包含表头、每条差异明细与最后的结果摘要。</summary>
    [Fact]
    public void BuildReport_ContainsHeaderItemsAndSummary()
    {
        using var ws = new TempWorkspace();
        CreatePair(ws, "same.txt", "same");
        CreateFile(ws, Path.Combine(LeftName, "only-left.txt"), "left");

        var result = new FolderDiffService().Compare(CreateOptions(ws));
        var report = FolderDiffService.BuildReport(result);

        Assert.Contains("状态", report);
        Assert.Contains("相对路径", report);
        Assert.Contains("same.txt", report);
        Assert.Contains("only-left.txt", report);
        Assert.Contains("仅左侧", report);
        Assert.Contains(result.Summary, report);
    }
}
