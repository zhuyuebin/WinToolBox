using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// 阶段四 P1-11「重复文件查找不校验扫描目录的嵌套/等价关系」的回归测试。
/// 覆盖：父目录 + 子目录同时列出时不再把同一文件扫两次（否则该文件会被当成「互为副本」的重复组）、
/// 输入顺序无关性、大小写 / 尾分隔符 / 相对写法的等价目录去重、候选文件的规范化路径去重、
/// 「D:\data 不能吞掉 D:\database」的前缀陷阱、以及「只扫顶层（IncludeSubDirectories=false）时父子目录必须各自保留」
/// 这一语义决定；同时固定 <see cref="DuplicateScanResult.MergedDirectories"/> 与摘要提示。
/// </summary>
/// <remarks>
/// 所有文件都建在各自的临时目录中，删除操作不涉及本文件（只扫描，不删除）。
/// </remarks>
public sealed class DuplicateFinderStage4Tests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>在临时工作区中创建文本文件（自动补齐父目录并固定修改时间），返回绝对路径。</summary>
    private static string CreateFile(
        TempWorkspace ws,
        string relativePath,
        string content,
        DateTime? lastWriteTimeUtc = null)
    {
        var fullPath = ws.PathOf(relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc ?? DateTime.UtcNow.AddMinutes(-10));
        return fullPath;
    }

    /// <summary>构造扫描选项。</summary>
    private static DuplicateFinderOptions CreateOptions(
        IReadOnlyList<string> directories,
        bool includeSubDirectories = true)
        => new()
        {
            Directories = directories,
            IncludeSubDirectories = includeSubDirectories,
            MinFileSize = 1
        };

    /// <summary>
    /// 构造「父目录 + 子目录」场景：scan\top.txt 与 scan\sub\inner.txt 内容不同但<b>大小相同</b>。
    /// 大小相同很关键：修复前同一个 inner.txt 会被枚举两次、大小相同而进入哈希阶段，
    /// 于是被当成一个「自己和自己重复」的重复组。
    /// </summary>
    private static (string Scan, string Sub, string Top, string Inner) CreateParentChildFixture(TempWorkspace ws)
    {
        var scan = ws.CreateDirectory("scan");
        var sub = ws.CreateDirectory(Path.Combine("scan", "sub"));
        var top = CreateFile(ws, Path.Combine("scan", "top.txt"), "AAA");
        var inner = CreateFile(ws, Path.Combine("scan", "sub", "inner.txt"), "BBB");

        return (scan, sub, top, inner);
    }

    // ------------------------------------------------------------------
    // 核心：父目录 + 子目录不再重复扫描
    // ------------------------------------------------------------------

    /// <summary>
    /// 核心回归点：同时列出父目录与它的子目录时，子目录必须被自动合并掉。
    /// 修复前 inner.txt 会被扫两次，哈希阶段看到两个「大小相同、内容相同」的条目，
    /// 于是产出只有一个路径的假重复组（用户「全选重复项」时会删掉唯一的一份原件）。
    /// </summary>
    [Fact]
    public void Find_WhenParentAndChildDirectoriesAreListed_DoesNotCreateSelfDuplicateGroup()
    {
        using var ws = new TempWorkspace();
        var (scan, sub, _, inner) = CreateParentChildFixture(ws);

        var result = new DuplicateFinderService().Find(CreateOptions(new[] { scan, sub }));

        Assert.Null(result.Error);
        Assert.Equal(2, result.ScannedFileCount);          // 修复前是 3（inner.txt 被数了两次）
        Assert.Empty(result.Groups);                        // 修复前有 1 组「inner.txt 与 inner.txt 重复」
        Assert.Equal(0, result.DuplicateFileCount);
        Assert.Equal(0L, result.WastedBytes);

        var merged = Assert.Single(result.MergedDirectories);
        Assert.Equal(sub, merged, ignoreCase: true);
        Assert.Contains("已自动合并", result.Summary);

        // 被合并的子目录里的文件仍然在扫描范围内（不是漏扫）
        Assert.True(File.Exists(inner));
    }

    /// <summary>
    /// 合并结果与输入顺序无关：子目录写在父目录前面时同样必须被合并
    /// （只做「向后看」的一次遍历会漏掉这种情况）。
    /// </summary>
    [Fact]
    public void Find_WhenChildDirectoryIsListedBeforeParent_StillMergesChild()
    {
        using var ws = new TempWorkspace();
        var (scan, sub, _, _) = CreateParentChildFixture(ws);

        var result = new DuplicateFinderService().Find(CreateOptions(new[] { sub, scan }));

        Assert.Null(result.Error);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Empty(result.Groups);
        Assert.Equal(sub, Assert.Single(result.MergedDirectories), ignoreCase: true);
    }

    /// <summary>
    /// 同一目录的不同写法（尾分隔符、大小写）必须被视为同一个目录：
    /// 修复前它们会各自被扫描一遍，同一批文件被统计多次并虚增「可回收空间」。
    /// </summary>
    /// <remarks>
    /// 注意 <see cref="Path.GetFullPath(string)"/> 在 Windows 上会把已存在路径的大小写规范化为磁盘上的真实写法，
    /// 因此「大小写变体」与「尾分隔符变体」最终都归一成同一个字符串；这里只断言
    /// 「同一批文件只被扫一次」以及「合并记录里的每一项都等价于该目录」，不依赖合并记录的条数。
    /// </remarks>
    [Fact]
    public void Find_WhenSameDirectoryIsWrittenDifferently_ScansFilesOnlyOnce()
    {
        using var ws = new TempWorkspace();
        var scan = ws.CreateDirectory("scan");
        CreateFile(ws, Path.Combine("scan", "copy-a.txt"), "identical-content", DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine("scan", "copy-b.txt"), "identical-content", DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(
            CreateOptions(new[] { scan, scan + Path.DirectorySeparatorChar, scan.ToUpperInvariant() }));

        Assert.Null(result.Error);
        Assert.Equal(2, result.ScannedFileCount);           // 修复前是 6（2 个文件 × 3 种写法）

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);                 // 修复前是 6，同一个文件重复出现
        Assert.Equal(
            2,
            group.Files.Select(static file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(1, result.DuplicateFileCount);

        Assert.NotEmpty(result.MergedDirectories);
        Assert.All(
            result.MergedDirectories,
            path => Assert.Equal(scan, path, ignoreCase: true));
    }

    /// <summary>
    /// 前缀陷阱：D:\data 不是 D:\database 的父目录。
    /// 用裸 <c>StartsWith</c> 做前缀比较会把「database」错误吞掉、直接漏扫整个目录。
    /// </summary>
    [Fact]
    public void Find_WhenSiblingDirectoriesShareNamePrefix_DoesNotMergeThem()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var database = ws.CreateDirectory("database");
        CreateFile(ws, Path.Combine("data", "a.txt"), "shared-content", DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine("database", "b.txt"), "shared-content", DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(new[] { data, database }));

        Assert.Null(result.Error);
        Assert.Empty(result.MergedDirectories);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Single(result.Groups);
    }

    /// <summary>
    /// 语义固定：只扫顶层（IncludeSubDirectories=false）时父子目录互不覆盖
    /// （父目录扫不到子目录里的文件），因此必须各自保留、分别扫描，不能合并。
    /// </summary>
    [Fact]
    public void Find_WhenRecursionDisabled_KeepsParentAndChildSeparate()
    {
        using var ws = new TempWorkspace();
        var scan = ws.CreateDirectory("scan");
        var sub = ws.CreateDirectory(Path.Combine("scan", "sub"));
        CreateFile(ws, Path.Combine("scan", "top.txt"), "same-content", DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine("scan", "sub", "inner.txt"), "same-content", DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(
            CreateOptions(new[] { scan, sub }, includeSubDirectories: false));

        Assert.Null(result.Error);
        Assert.Empty(result.MergedDirectories);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
    }

    /// <summary>没有嵌套 / 等价关系时 MergedDirectories 为空，摘要也不出现「已自动合并」。</summary>
    [Fact]
    public void Find_WhenDirectoriesAreIndependent_MergedDirectoriesIsEmpty()
    {
        using var ws = new TempWorkspace();
        var first = ws.CreateDirectory("first");
        var second = ws.CreateDirectory("second");
        CreateFile(ws, Path.Combine("first", "a.txt"), "shared-content", DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine("second", "b.txt"), "shared-content", DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(new[] { first, second }));

        Assert.Null(result.Error);
        Assert.Empty(result.MergedDirectories);
        Assert.DoesNotContain("已自动合并", result.Summary);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
    }

    // ------------------------------------------------------------------
    // NormalizeDirectories：可直接调用的归一化入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 递归扫描时的归一化：等价写法与被父目录包含的子目录都进入 merged，空白与不存在的目录直接忽略，
    /// 返回的实际扫描目录保持去重与原有顺序。
    /// </summary>
    [Fact]
    public void NormalizeDirectories_WhenRecursive_MergesEquivalentAndNestedDirectories()
    {
        using var ws = new TempWorkspace();
        var scan = ws.CreateDirectory("scan");
        var sub = ws.CreateDirectory(Path.Combine("scan", "sub"));
        var other = ws.CreateDirectory("other");

        var effective = DuplicateFinderService.NormalizeDirectories(
            new[] { scan, scan + Path.DirectorySeparatorChar, sub, "   ", ws.PathOf("missing"), other },
            includeSubDirectories: true,
            out var merged);

        Assert.Equal(2, effective.Count);
        Assert.Equal(scan, effective[0], ignoreCase: true);
        Assert.Equal(other, effective[1], ignoreCase: true);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, path => string.Equals(path, scan, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(merged, path => string.Equals(path, sub, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>只扫顶层时的归一化：只做等价写法去重，父子目录都保留（合并会漏扫子目录的顶层文件）。</summary>
    [Fact]
    public void NormalizeDirectories_WhenNotRecursive_KeepsNestedDirectories()
    {
        using var ws = new TempWorkspace();
        var scan = ws.CreateDirectory("scan");
        var sub = ws.CreateDirectory(Path.Combine("scan", "sub"));

        var effective = DuplicateFinderService.NormalizeDirectories(
            new[] { scan, sub, scan + Path.DirectorySeparatorChar },
            includeSubDirectories: false,
            out var merged);

        Assert.Equal(2, effective.Count);
        Assert.Equal(scan, effective[0], ignoreCase: true);
        Assert.Equal(sub, effective[1], ignoreCase: true);
        Assert.Single(merged); // 只有「尾分隔符」写法被当作等价目录合并
    }

    /// <summary>目录全部为空 / 不存在时归一化结果为空，调用方据此给出「没有可扫描的目录」错误。</summary>
    [Fact]
    public void NormalizeDirectories_WhenNothingUsable_ReturnsEmpty()
    {
        using var ws = new TempWorkspace();

        var effective = DuplicateFinderService.NormalizeDirectories(
            new[] { "   ", string.Empty, ws.PathOf("missing-1"), ws.PathOf(Path.Combine("missing-2", "deep")) },
            includeSubDirectories: true,
            out var merged);

        Assert.Empty(effective);
        Assert.Empty(merged);
    }

    /// <summary>IsSameOrSubPathOf：相同目录、子目录、前缀相近的兄弟目录三者的判定必须不同。</summary>
    [Fact]
    public void IsSameOrSubPathOf_HandlesEqualityNestingAndPrefixTrap()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var database = ws.CreateDirectory("database");
        var sub = ws.CreateDirectory(Path.Combine("data", "sub"));

        Assert.True(DuplicateFinderService.IsSameOrSubPathOf(data, data));
        Assert.True(DuplicateFinderService.IsSameOrSubPathOf(data + Path.DirectorySeparatorChar, data));
        Assert.True(DuplicateFinderService.IsSameOrSubPathOf(data.ToUpperInvariant(), data));
        Assert.True(DuplicateFinderService.IsSameOrSubPathOf(sub, data));
        Assert.False(DuplicateFinderService.IsSameOrSubPathOf(database, data));
        Assert.False(DuplicateFinderService.IsSameOrSubPathOf(data, database));
        Assert.False(DuplicateFinderService.IsSameOrSubPathOf("   ", data));
        Assert.False(DuplicateFinderService.IsSameOrSubPathOf(data, string.Empty));
    }

    // ------------------------------------------------------------------
    // 既有的错误语义不变
    // ------------------------------------------------------------------

    /// <summary>目录列表里混有不存在的目录时，只扫描存在的那些（既有语义：缺失项被忽略，不报错）。</summary>
    [Fact]
    public void Find_WhenSomeDirectoriesMissing_StillScansExistingOnes()
    {
        using var ws = new TempWorkspace();
        var scan = ws.CreateDirectory("scan");
        CreateFile(ws, Path.Combine("scan", "a.txt"), "shared-content", DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine("scan", "b.txt"), "shared-content", DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(
            CreateOptions(new[] { ws.PathOf("missing"), scan }));

        Assert.Null(result.Error);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Single(result.Groups);
    }

    /// <summary>目录全部不存在时保持既有错误语义：「没有可扫描的目录」。</summary>
    [Fact]
    public void Find_WhenAllDirectoriesMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var result = new DuplicateFinderService().Find(
            CreateOptions(new[] { ws.PathOf("missing-1"), ws.PathOf("missing-2") }));

        Assert.NotNull(result.Error);
        Assert.Contains("没有可扫描的目录", result.Error);
        Assert.Empty(result.Groups);
        Assert.Empty(result.MergedDirectories);
    }

    /// <summary>忽略名单（ExcludeNames）行为不受归一化影响：被忽略的文件仍不参与比较。</summary>
    [Fact]
    public void Find_WithExcludeNames_StillSkipsExcludedFilesAfterNormalization()
    {
        using var ws = new TempWorkspace();
        var scan = ws.CreateDirectory("scan");
        var sub = ws.CreateDirectory(Path.Combine("scan", "sub"));
        CreateFile(ws, Path.Combine("scan", "keep.txt"), "shared-content", DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine("scan", "skip.txt"), "shared-content", DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { scan, sub },
            IncludeSubDirectories = true,
            MinFileSize = 1,
            ExcludeNames = "skip.txt"
        });

        Assert.Null(result.Error);
        Assert.Equal(1, result.ScannedFileCount);
        Assert.Empty(result.Groups);
        Assert.Equal(sub, Assert.Single(result.MergedDirectories), ignoreCase: true);
    }
}
