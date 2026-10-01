using WinToolBox.Core.Services;

namespace WinToolBox.Core.Tests;

/// <summary>
/// FolderCreator 核心业务逻辑（<see cref="FolderCreatorService"/>）的单元测试：
/// 批量创建、占位文件、多根目录去重与逐项容错。
/// </summary>
public class FolderCreatorServiceTests
{
    private static FolderCreatorService CreateService() => new(null);

    // ---------------------------------------------------------------- 基础创建

    [Fact]
    public void CreateFolders_CreatesAllDirectories()
    {
        using var ws = new TempWorkspace();
        var service = CreateService();

        var result = service.CreateFolders(
            ws.Root,
            new[] { "一级目录", @"一级目录\二级目录", "另一个" });

        Assert.True(result.Success, result.Summary);
        Assert.Equal(3, result.CreatedCount);
        Assert.Equal(0, result.ExistedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(Directory.Exists(Path.Combine(ws.Root, "一级目录")));
        Assert.True(Directory.Exists(Path.Combine(ws.Root, @"一级目录\二级目录")));
        Assert.True(Directory.Exists(Path.Combine(ws.Root, "另一个")));
        Assert.Equal(Path.GetFullPath(ws.Root), result.RootDirectory);
    }

    [Fact]
    public void CreateFolders_WhenDirectoryExists_MarksExistedAndDoesNotFail()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("已存在", ws.Root);

        var result = CreateService().CreateFolders(ws.Root, new[] { "已存在", "新目录" });

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.ExistedCount);
        Assert.Equal(0, result.FailedCount);

        var existed = result.Items.Single(item => item.RelativePath == "已存在");
        Assert.Equal(FolderCreateStatus.Existed, existed.Status);
        Assert.Equal("已存在，跳过", existed.Message);
    }

    [Fact]
    public void CreateFolders_WhenRootMissing_CreatesRootAutomatically()
    {
        using var ws = new TempWorkspace();
        var root = Path.Combine(ws.Root, "还不存在的根目录");

        var result = CreateService().CreateFolders(root, new[] { "子目录" });

        Assert.True(result.Success, result.Summary);
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(Path.Combine(root, "子目录")));
    }

    [Fact]
    public void CreateFolders_WhenPathOccupiedByFile_MarksFailedAndContinues()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("占位文件", "x", null, ws.Root);

        var result = CreateService().CreateFolders(ws.Root, new[] { "占位文件", "正常目录" });

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.False(string.IsNullOrWhiteSpace(result.Items[0].Message));
        Assert.True(Directory.Exists(Path.Combine(ws.Root, "正常目录")));
    }

    [Fact]
    public void CreateFolders_IgnoresBlankRelativePaths()
    {
        using var ws = new TempWorkspace();

        var result = CreateService().CreateFolders(ws.Root, new[] { "a", "   ", string.Empty, "b" });

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.CreatedCount);
    }

    [Fact]
    public void CreateFolders_WhenRootIsBlank_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CreateService().CreateFolders("   ", new[] { "a" }));
    }

    [Fact]
    public void CreateFolders_ReportsProgressForEveryItem()
    {
        using var ws = new TempWorkspace();
        var progress = new List<FolderCreateProgress>();

        var result = CreateService().CreateFolders(
            ws.Root,
            new[] { "a", "b", "c" },
            progress: new SyncProgress<FolderCreateProgress>(progress.Add));

        Assert.Equal(3, progress.Count);
        Assert.Equal(new[] { 1, 2, 3 }, progress.Select(value => value.Processed));
        Assert.All(progress, value => Assert.Equal(3, value.Total));
        Assert.Equal(100d, progress[^1].Percent, 3);
        Assert.Equal(3, result.CreatedCount);
    }

    [Fact]
    public void CreateFolders_RespectsCancellation()
    {
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateService().CreateFolders(ws.Root, new[] { "a" }, cancellationToken: cts.Token));
    }

    // ---------------------------------------------------------------- 占位文件

    [Fact]
    public void CreateFolders_WithPlaceholder_CreatesGitkeepInEmptyDirectory()
    {
        using var ws = new TempWorkspace();

        var result = CreateService().CreateFolders(
            ws.Root,
            new[] { "空目录" },
            createPlaceholder: true);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.PlaceholderCount);
        Assert.True(File.Exists(Path.Combine(ws.Root, "空目录", ".gitkeep")));

        var item = result.Items[0];
        Assert.True(item.PlaceholderCreated);
        Assert.Equal(Path.Combine(ws.Root, "空目录", ".gitkeep"), item.PlaceholderPath);
        Assert.Contains("占位文件", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFolders_WithPlaceholder_DoesNotTouchDirectoryContainingFile()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("有文件", ws.Root);
        ws.CreateFile(@"有文件\readme.txt", "x", null, ws.Root);

        var result = CreateService().CreateFolders(ws.Root, new[] { "有文件" }, createPlaceholder: true);

        Assert.Equal(0, result.PlaceholderCount);
        Assert.False(File.Exists(Path.Combine(ws.Root, "有文件", ".gitkeep")));
    }

    [Fact]
    public void CreateFolders_WithPlaceholder_DoesNotTouchDirectoryContainingSubDirectory()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"父目录\子目录", ws.Root);

        var result = CreateService().CreateFolders(ws.Root, new[] { "父目录" }, createPlaceholder: true);

        Assert.Equal(0, result.PlaceholderCount);
        Assert.False(File.Exists(Path.Combine(ws.Root, "父目录", ".gitkeep")));
    }

    [Fact]
    public void CreateFolders_WithPlaceholder_OnlyMarksTrulyEmptyLeafDirectories()
    {
        using var ws = new TempWorkspace();

        // 规则顺序恒为父目录在前（解析器保证），父目录在创建时是空的，但随后会得到子目录
        var result = CreateService().CreateFolders(
            ws.Root,
            new[] { "文档", @"文档\图片", "源码" },
            createPlaceholder: true);

        // 文档 因为有了子目录，已不再是空目录 → 不加占位文件
        Assert.False(File.Exists(Path.Combine(ws.Root, "文档", ".gitkeep")));

        // 图片 与 源码 是创建后仍为空 → 各加一个占位文件
        Assert.True(File.Exists(Path.Combine(ws.Root, "文档", "图片", ".gitkeep")));
        Assert.True(File.Exists(Path.Combine(ws.Root, "源码", ".gitkeep")));
        Assert.Equal(2, result.PlaceholderCount);
    }

    [Fact]
    public void CreateFolders_WithPlaceholder_WhenPlaceholderExists_KeepsContentAndCountsZero()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("空目录", ws.Root);
        ws.CreateFile(@"空目录\.gitkeep", "已有内容", null, ws.Root);

        var result = CreateService().CreateFolders(ws.Root, new[] { "空目录" }, createPlaceholder: true);

        Assert.True(result.Success);
        Assert.Equal(0, result.PlaceholderCount);
        Assert.Equal("已有内容", TempWorkspace.ReadAllText(Path.Combine(ws.Root, "空目录", ".gitkeep")));
    }

    [Fact]
    public void CreateFolders_WithoutPlaceholder_CreatesNoFiles()
    {
        using var ws = new TempWorkspace();

        var result = CreateService().CreateFolders(ws.Root, new[] { "空目录" });

        Assert.Equal(0, result.PlaceholderCount);
        Assert.Empty(Directory.GetFiles(Path.Combine(ws.Root, "空目录")));
    }

    [Fact]
    public void CreateFolders_WithCustomPlaceholderName_UsesIt()
    {
        using var ws = new TempWorkspace();

        var result = CreateService().CreateFolders(
            ws.Root,
            new[] { "空目录" },
            createPlaceholder: true,
            placeholderName: "keep.me");

        Assert.Equal(1, result.PlaceholderCount);
        Assert.True(File.Exists(Path.Combine(ws.Root, "空目录", "keep.me")));
    }

    [Fact]
    public void CreateFolders_WithBlankPlaceholderName_FallsBackToDefault()
    {
        using var ws = new TempWorkspace();

        var result = CreateService().CreateFolders(
            ws.Root,
            new[] { "空目录" },
            createPlaceholder: true,
            placeholderName: "   ");

        Assert.True(File.Exists(Path.Combine(ws.Root, "空目录", FolderCreatorService.DefaultPlaceholderName)));
        Assert.Equal(1, result.PlaceholderCount);
    }

    [Fact]
    public void CreateFolders_PlaceholderFileIsEmpty()
    {
        using var ws = new TempWorkspace();

        CreateService().CreateFolders(ws.Root, new[] { "空目录" }, createPlaceholder: true);

        Assert.Empty(TempWorkspace.ReadAllBytes(Path.Combine(ws.Root, "空目录", ".gitkeep")));
    }

    // ---------------------------------------------------------------- 多根目录

    [Fact]
    public void CreateFoldersInMultipleRoots_CreatesStructureInEveryRoot()
    {
        using var ws = new TempWorkspace();
        var rootA = Path.Combine(ws.Root, "RootA");
        var rootB = Path.Combine(ws.Root, "RootB");

        var result = CreateService().CreateFoldersInMultipleRoots(
            new[] { rootA, rootB },
            new[] { "一级", @"一级\二级" });

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.RootCount);
        Assert.Equal(4, result.TotalCreated);
        Assert.Equal(0, result.TotalFailed);
        Assert.True(Directory.Exists(Path.Combine(rootA, "一级", "二级")));
        Assert.True(Directory.Exists(Path.Combine(rootB, "一级", "二级")));
    }

    [Fact]
    public void CreateFoldersInMultipleRoots_DeduplicatesIgnoringCase()
    {
        using var ws = new TempWorkspace();
        var root = Path.Combine(ws.Root, "Root");
        var upper = Path.Combine(ws.Root, "ROOT");

        var result = CreateService().CreateFoldersInMultipleRoots(new[] { root, upper }, new[] { "a" });

        Assert.Equal(1, result.RootCount);
        Assert.Single(result.SkippedRoots);
        Assert.Contains("重复", result.SkippedRoots[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFoldersInMultipleRoots_SkipsBlankAndInvalidEntries()
    {
        using var ws = new TempWorkspace();
        var valid = Path.Combine(ws.Root, "Valid");

        var result = CreateService().CreateFoldersInMultipleRoots(
            new[] { string.Empty, "   ", "bad|path", valid },
            new[] { "a" });

        Assert.Equal(1, result.RootCount);
        Assert.Equal(3, result.SkippedRoots.Count);
        Assert.True(result.Success, result.Summary);
        Assert.True(result.Summary.Contains("已忽略", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateFoldersInMultipleRoots_ContinuesWhenOneRootFails()
    {
        using var ws = new TempWorkspace();
        // 该路径被文件占用，无法作为根目录
        ws.CreateFile("被占用的根", "x", null, ws.Root);
        var broken = Path.Combine(ws.Root, "被占用的根");
        var good = Path.Combine(ws.Root, "正常根");

        var result = CreateService().CreateFoldersInMultipleRoots(new[] { broken, good }, new[] { "a" });

        Assert.Equal(2, result.RootCount);
        Assert.False(result.Success);
        Assert.True(result.TotalFailed >= 1);
        Assert.True(Directory.Exists(Path.Combine(good, "a")), "单个根目录失败不应影响其它根目录");
    }

    [Fact]
    public void CreateFoldersInMultipleRoots_ReportsRootIndexProgress()
    {
        using var ws = new TempWorkspace();
        var progress = new List<MultiRootCreateProgress>();

        CreateService().CreateFoldersInMultipleRoots(
            new[] { Path.Combine(ws.Root, "A"), Path.Combine(ws.Root, "B") },
            new[] { "x" },
            progress: new SyncProgress<MultiRootCreateProgress>(progress.Add));

        Assert.Equal(2, progress.Count);
        Assert.Equal("当前处理第 1 / 共 2 个", progress[0].RootText);
        Assert.Equal("当前处理第 2 / 共 2 个", progress[1].RootText);
        Assert.Equal(50d, progress[0].Percent, 3);
        Assert.Equal(100d, progress[1].Percent, 3);
    }

    [Fact]
    public void CreateFoldersInMultipleRoots_WithPlaceholder_CreatesInEachRoot()
    {
        using var ws = new TempWorkspace();

        var result = CreateService().CreateFoldersInMultipleRoots(
            new[] { Path.Combine(ws.Root, "A"), Path.Combine(ws.Root, "B") },
            new[] { "空目录" },
            createPlaceholder: true);

        Assert.Equal(2, result.TotalPlaceholders);
        Assert.True(File.Exists(Path.Combine(ws.Root, "A", "空目录", ".gitkeep")));
        Assert.True(File.Exists(Path.Combine(ws.Root, "B", "空目录", ".gitkeep")));
        Assert.Contains("占位文件", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFoldersInMultipleRoots_WhenListEmpty_ReturnsNoResults()
    {
        using var ws = new TempWorkspace();

        var result = CreateService().CreateFoldersInMultipleRoots(Array.Empty<string>(), new[] { "a" });

        Assert.Equal(0, result.RootCount);
        Assert.False(result.Success);
        Assert.Empty(result.Results);
    }

    // ---------------------------------------------------------------- 静态工具

    [Fact]
    public void NormalizeRoots_TrimsDeduplicatesAndDropsInvalid()
    {
        using var ws = new TempWorkspace();
        var root = Path.Combine(ws.Root, "Root");

        var roots = FolderCreatorService.NormalizeRoots(
            new[] { " " + root + " ", "bad|path", root.ToUpperInvariant(), null! });

        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(root), roots[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeRoot_WhenBlank_ReturnsFalse(string? input)
    {
        Assert.False(FolderCreatorService.TryNormalizeRoot(input, out _, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryNormalizeRoot_WhenInvalidCharacters_ReturnsFalse()
    {
        Assert.False(FolderCreatorService.TryNormalizeRoot("C:\\bad|name", out _, out var reason));
        Assert.Contains("非法字符", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryNormalizeRoot_WhenValid_ReturnsFullPath()
    {
        using var ws = new TempWorkspace();

        Assert.True(FolderCreatorService.TryNormalizeRoot(ws.Root, out var normalized, out _));
        Assert.Equal(Path.GetFullPath(ws.Root), normalized);
    }

    [Fact]
    public void IsDirectoryEmpty_ReflectsFileSystemState()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("空的", ws.Root);
        ws.CreateDirectory(@"有子目录\inner", ws.Root);
        ws.CreateDirectory("有文件", ws.Root);
        ws.CreateFile(@"有文件\a.txt", "x", null, ws.Root);

        Assert.True(FolderCreatorService.IsDirectoryEmpty(Path.Combine(ws.Root, "空的")));
        Assert.False(FolderCreatorService.IsDirectoryEmpty(Path.Combine(ws.Root, "有子目录")));
        Assert.False(FolderCreatorService.IsDirectoryEmpty(Path.Combine(ws.Root, "有文件")));
    }

    [Fact]
    public void DescribeError_MapsKnownExceptionsToChineseMessages()
    {
        Assert.Contains("权限不足", FolderCreatorService.DescribeError(new UnauthorizedAccessException()));
        Assert.Contains("路径过长", FolderCreatorService.DescribeError(new PathTooLongException()));
        Assert.Contains("非法字符", FolderCreatorService.DescribeError(new ArgumentException()));
        Assert.Contains("IO 错误", FolderCreatorService.DescribeError(new IOException("disk")));
    }

    /// <summary>把进度回调收集到列表里的同步实现（后台线程回调，避免 Progress&lt;T&gt; 的异步投递）。</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onReport;

        public SyncProgress(Action<T> onReport) => _onReport = onReport;

        public void Report(T value) => _onReport(value);
    }
}
