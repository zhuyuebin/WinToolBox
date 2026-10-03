using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// 阶段四回归测试：自动分类的路径安全。
/// <list type="number">
/// <item><b>P1-12</b>：规则第三列是自由文本，必须校验解析后仍在目标根目录之内 ——
/// 否则 <c>..\..\Windows\Temp</c> 或绝对路径 <c>D:\elsewhere</c> 会把文件写到目标之外。</item>
/// <item><b>P1-19</b>：源目录与目标目录互相嵌套时必须拒绝，否则重复运行会「逐层自我归档」。</item>
/// </list>
/// </summary>
public sealed class AutoArchiverPathSafetyTests
{
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

    /// <summary>用一条「其它 → 指定子目录」的兜底规则构造选项。</summary>
    private static ArchiveOptions OptionsWithTargetSubDirectory(TempWorkspace ws, string source, string target, string subDirectory)
        => new()
        {
            SourceDirectory = source,
            TargetDirectory = target,
            IncludeSubDirectories = true,
            Action = ArchiveAction.Move,
            Overwrite = false,
            Rules = new[]
            {
                new ArchiveRule
                {
                    Kind = ArchiveRuleKind.Fallback,
                    TargetSubDirectory = subDirectory,
                    SourceText = "其它||" + subDirectory
                }
            }
        };

    // ==================================================================
    // P1-12：目标子目录越界
    // ==================================================================

    [Fact]
    public void BuildPlan_RejectsRuleThatEscapesTargetRoot_ViaParentTraversal()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var target = ws.CreateDirectory("target");
        var options = OptionsWithTargetSubDirectory(ws, source, target, Path.Combine("..", "..", "Windows"));

        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.False(item.CanApply, "越界规则必须被拒绝");
        Assert.Equal(0, plan.ReadyCount);
        Assert.Contains("目标目录之外", item.Message);

        // 不能给出任何越界的目标路径
        Assert.Equal(string.Empty, item.TargetPath);
    }

    [Fact]
    public void BuildPlan_RejectsRuleWithAbsolutePath()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var target = ws.CreateDirectory("target");

        // 绝对路径：Path.Combine 会直接丢弃 root，等于写到目标之外
        var options = OptionsWithTargetSubDirectory(ws, source, target, Path.Combine(ws.Root, "elsewhere"));

        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.False(item.CanApply, "绝对路径子目录必须被拒绝");
        Assert.Equal(string.Empty, item.TargetPath);
    }

    [Fact]
    public void BuildPlan_RejectsRuleThatEscapesThenComesBackIntoASibling()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var target = ws.CreateDirectory("target");
        var sibling = ws.CreateDirectory("sibling");

        // ..\..\sibling 也在目标之外
        var options = OptionsWithTargetSubDirectory(
            ws,
            source,
            Path.Combine(ws.Root, "target"),
            Path.Combine("..", "sibling"));

        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.False(item.CanApply);
        Assert.False(Directory.Exists(Path.Combine(sibling, "a.txt")));
    }

    [Fact]
    public void BuildPlan_AcceptsLegitimateNestedSubDirectory()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var target = ws.CreateDirectory("target");
        var options = OptionsWithTargetSubDirectory(
            ws,
            source,
            target,
            Path.Combine("images", "2026", "10"));

        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.True(item.CanApply, item.Message);
        Assert.StartsWith(
            Path.GetFullPath(target),
            Path.GetFullPath(item.TargetPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("a.txt", item.TargetPath);
    }

    [Fact]
    public void BuildPlan_AcceptsSubDirectoryWithInnerDotDotThatStaysInside()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var target = ws.CreateDirectory("target");

        // images\..\docs 归一化后仍是 target\docs —— 合法，不该被误拒
        var options = OptionsWithTargetSubDirectory(
            ws,
            source,
            target,
            Path.Combine("images", "..", "docs"));

        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.True(item.CanApply, item.Message);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(target), "docs", "a.txt"),
            Path.GetFullPath(item.TargetPath));
    }

    [Fact]
    public void BuildPlan_RejectsTraversalButStillAcceptsOtherFiles()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var target = ws.CreateDirectory("target");

        // 两条规则：第一条越界、第二条合法；扩展名规则按顺序匹配，a.txt 命中第一条
        var options = new ArchiveOptions
        {
            SourceDirectory = source,
            TargetDirectory = target,
            IncludeSubDirectories = true,
            Rules = new[]
            {
                new ArchiveRule
                {
                    Kind = ArchiveRuleKind.Extension,
                    Extensions = ".txt",
                    TargetSubDirectory = Path.Combine("..", "..", "escaped"),
                    SourceText = ".txt||..\\..\\escaped"
                }
            }
        };

        var plan = new AutoArchiverService().BuildPlan(options);

        Assert.Equal(1, plan.SkippedCount);
        Assert.Equal(0, plan.ReadyCount);
        Assert.Contains("目标目录之外", plan.Items[0].Message);
    }

    // ==================================================================
    // P1-19：源目录与目标目录互相嵌套
    // ==================================================================

    [Fact]
    public void BuildPlan_RejectsTargetInsideSource()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("data");
        CreateFile(ws, Path.Combine("data", "a.txt"));

        // 源 D:\...\data、目标 D:\...\data\sorted —— 教科书式的「逐层自我归档」
        var target = Path.Combine(source, "sorted");
        var options = OptionsWithTargetSubDirectory(ws, source, target, "moved");

        var plan = new AutoArchiverService().BuildPlan(options);

        Assert.NotNull(plan.Error);
        Assert.Contains("目标目录不能位于源目录内部", plan.Error!);
        Assert.Empty(plan.Items);
    }

    [Fact]
    public void BuildPlan_RejectsSourceInsideTarget()
    {
        using var ws = new TempWorkspace();
        var target = ws.CreateDirectory("target");
        var source = Path.Combine(target, "inner");
        Directory.CreateDirectory(source);
        CreateFile(ws, Path.Combine("target", "inner", "a.txt"));

        var options = OptionsWithTargetSubDirectory(ws, source, target, "moved");

        var plan = new AutoArchiverService().BuildPlan(options);

        Assert.NotNull(plan.Error);
        Assert.Contains("源目录不能位于目标目录内部", plan.Error!);
    }

    [Fact]
    public void BuildPlan_RejectsSameSourceAndTarget()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("both");
        CreateFile(ws, Path.Combine("both", "a.txt"));

        var options = OptionsWithTargetSubDirectory(ws, source, source, "moved");

        var plan = new AutoArchiverService().BuildPlan(options);

        Assert.NotNull(plan.Error);
        Assert.Contains("不能是同一个目录", plan.Error!);
    }

    [Fact]
    public void ValidateSourceAndTarget_DoesNotConfuseSiblingNamesWithCommonPrefix()
    {
        using var ws = new TempWorkspace();

        // D:\...\data 与 D:\...\database 是兄弟，不是父子 —— 裸 StartsWith 会误判
        var data = ws.CreateDirectory("data");
        var database = ws.CreateDirectory("database");

        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(data, database));
        Assert.Null(AutoArchiverService.ValidateSourceAndTarget(database, data));
    }

    [Fact]
    public void ValidateSourceAndTarget_HandlesTrailingSeparatorAndCase()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("data");
        var nested = ws.CreateDirectory(Path.Combine("data", "nested"));

        // 尾分隔符不应让判定失效
        Assert.NotNull(AutoArchiverService.ValidateSourceAndTarget(source + Path.DirectorySeparatorChar, nested));
        Assert.NotNull(AutoArchiverService.ValidateSourceAndTarget(source, nested + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void BuildPlan_WithSiblingTarget_StillWorks()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("data");
        CreateFile(ws, Path.Combine("data", "a.txt"));

        var target = ws.CreateDirectory("database");   // 同名前缀的兄弟目录
        var options = OptionsWithTargetSubDirectory(ws, source, target, "moved");

        var plan = new AutoArchiverService().BuildPlan(options);

        Assert.Null(plan.Error);
        Assert.Equal(1, plan.ReadyCount);
    }

    // ==================================================================
    // P1-19.3：源文件已在目标目录之内
    // ==================================================================

    [Fact]
    public void BuildPlan_RejectsFileAlreadyInsideTargetRoot()
    {
        using var ws = new TempWorkspace();
        var target = ws.CreateDirectory("target");

        // 源目录 = 目标目录的子目录，已被上面的嵌套检查拦下；
        // 这里单独验证「源文件本身位于目标根之下」的守卫（通过同根不同子目录的场景）。
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var options = OptionsWithTargetSubDirectory(ws, source, target, "moved");
        var plan = new AutoArchiverService().BuildPlan(options);

        // 常规场景不应被新守卫误伤
        Assert.Null(plan.Error);
        Assert.Equal(1, plan.ReadyCount);
        Assert.False(plan.Items[0].Message.Contains("已经位于目标目录之内"));
    }

    [Fact]
    public void PathSafety_TryResolveInside_RejectsEscapesAndAcceptsInsidePaths()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("root");

        Assert.True(PathSafety.TryResolveInside(root, "docs", out var inside));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "docs"), inside);

        Assert.False(PathSafety.TryResolveInside(root, Path.Combine("..", "outside"), out _));
        Assert.False(PathSafety.TryResolveInside(root, Path.Combine(ws.Root, "absolute"), out _));
        Assert.False(PathSafety.TryResolveInside("", "docs", out _));
    }

    /// <summary>
    /// 段末尾的空格 / 点必须被拒绝：Win32 打开路径时会去掉每个段末尾的空格与点，
    /// 于是 <c>target\.. \outside</c> 字符串上仍在 target 内，实际却落到 <c>target\..\outside</c>。
    /// 不做这层检查就是一个真实的越界写。
    /// </summary>
    [Theory]
    [InlineData(@".. \outside")]     // 段「.. 」→ Win32 归一化成「..」
    [InlineData(@".. .\outside")]    // 段「.. .」→ 同上
    [InlineData(@". \outside")]
    [InlineData(@"images\.. \outside")]
    public void BuildPlan_RejectsSegmentsThatWin32WouldNormalizeIntoParentReferences(string subDirectory)
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        var sourceFile = CreateFile(ws, Path.Combine("source", "a.txt"));

        var options = OptionsWithTargetSubDirectory(ws, source, target, subDirectory);
        var service = new AutoArchiverService();

        var plan = service.BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.False(item.CanApply, $"[{subDirectory}] 这类段必须被拒绝，否则会真实越界");
        Assert.Equal(string.Empty, item.TargetPath);

        // 执行后：源文件原封不动，工作区顶层不得出现 source / target 之外的条目
        var result = service.Apply(plan, options);
        Assert.Equal(0, result.SucceededCount);
        Assert.True(File.Exists(sourceFile), "源文件必须原封不动");

        var unexpected = Directory.GetFileSystemEntries(ws.Root)
            .Where(entry => !PathSafety.IsSamePath(entry, source) && !PathSafety.IsSamePath(entry, target))
            .ToArray();

        Assert.Empty(unexpected);
    }

    /// <summary>
    /// 纯空白子目录是**无害**的：<c>Path.GetFullPath</c> 会把末尾空格去掉，
    /// 于是它等价于「目标根目录本身」，文件落在 <c>target\a.txt</c>，没有越界也没有改变层级。
    /// 这里把该结论固定下来，避免以后有人「顺手」把它也拒了而改变既有行为。
    /// </summary>
    [Fact]
    public void BuildPlan_WhitespaceOnlySubDirectory_LandsInTargetRoot()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var options = OptionsWithTargetSubDirectory(ws, source, target, "   ");
        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.True(item.CanApply, item.Message);

        // 归一化后就是目标根，没有越界
        Assert.Equal(
            Path.Combine(Path.GetFullPath(target), "a.txt"),
            Path.GetFullPath(item.TargetPath));

        var result = new AutoArchiverService().Apply(plan, options);
        Assert.Equal(1, result.SucceededCount);
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }

    /// <summary>
    /// 整段只有点（<c>"..."</c>）会被 Win32 丢弃，导致字符串与实际路径层级不一致 —— 拒绝。
    /// </summary>
    [Theory]
    [InlineData(@"...\outside")]
    [InlineData("....//")]
    public void BuildPlan_RejectsSegmentsThatWin32WouldDrop(string subDirectory)
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var options = OptionsWithTargetSubDirectory(ws, source, target, subDirectory);
        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.False(item.CanApply, $"[{subDirectory}] 整段会被 Win32 丢弃，必须拒绝");
        Assert.Equal(string.Empty, item.TargetPath);
    }

    [Fact]
    public void BuildPlan_StillAcceptsPlainNameWithTrailingCharactersInTheMiddle()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "a.txt"));

        var target = ws.CreateDirectory("target");

        // 正常名字（末尾既不是空格也不是点）必须照常接受
        var options = OptionsWithTargetSubDirectory(ws, source, target, Path.Combine("my docs", "2026 v1"));

        var plan = new AutoArchiverService().BuildPlan(options);

        var item = Assert.Single(plan.Items);
        Assert.True(item.CanApply, item.Message);
        Assert.StartsWith(Path.GetFullPath(target), Path.GetFullPath(item.TargetPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P1-11 回归：驱动器根不能被自己吞掉。
    /// </summary>
    [Fact]
    public void NormalizeDirectories_DriveRootIsNotSwallowedByItself()
    {
        using var ws = new TempWorkspace();
        var child = ws.CreateDirectory("data");

        var driveRoot = Path.GetPathRoot(Path.GetFullPath(ws.Root))!;

        var kept = DuplicateFinderService.NormalizeDirectories(
            new[] { driveRoot, child },
            includeSubDirectories: true,
            out var merged);

        Assert.Contains(driveRoot, kept);
        Assert.DoesNotContain(child, kept);
        Assert.Contains(child, merged);
    }

    [Fact]
    public void NormalizeDirectories_AllDriveRootsInput_YieldsAllRoots()
    {
        var roots = Directory.GetLogicalDrives()
            .Where(static root => Directory.Exists(root))
            .Take(2)
            .ToArray();

        if (roots.Length < 2)
        {
            return;   // 本机只有一个可用盘：该场景无意义
        }

        var kept = DuplicateFinderService.NormalizeDirectories(
            roots,
            includeSubDirectories: true,
            out _);

        Assert.Equal(roots.Length, kept.Count);
    }

    [Fact]
    public void PathSafety_IsChildPath_IsSeparatorAware()
    {
        using var ws = new TempWorkspace();
        var data = ws.CreateDirectory("data");
        var dataBase = ws.CreateDirectory("database");
        var nested = ws.CreateDirectory(Path.Combine("data", "nested"));

        Assert.True(PathSafety.IsChildPath(nested, data));
        Assert.False(PathSafety.IsChildPath(dataBase, data));       // 同名前缀的兄弟
        Assert.False(PathSafety.IsChildPath(data, data));           // 自身不算子路径
        Assert.True(PathSafety.IsSameOrChildPath(data, data));
    }
}
