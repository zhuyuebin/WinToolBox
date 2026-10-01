using WinToolBox.Core.Services;

namespace WinToolBox.Core.Tests;

/// <summary>目录反向工具（<see cref="RuleGenerator"/>）的单元测试：短横线规则生成与 tree 文本导出。</summary>
public class RuleGeneratorTests
{
    private static RuleGenerator CreateGenerator() => new(null);

    /// <summary>把规则文本按行拆开（忽略空行）。</summary>
    private static string[] Lines(string text)
        => text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void GenerateRuleFromDirectory_WhenRootHasNoSubDirectory_ReturnsEmptyString()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.Root);

        Assert.Equal(string.Empty, CreateGenerator().GenerateRuleFromDirectory(ws.Root));
    }

    [Fact]
    public void GenerateRuleFromDirectory_SingleLevel_UsesSingleDash()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("alpha", ws.Root);
        ws.CreateDirectory("beta", ws.Root);

        var text = CreateGenerator().GenerateRuleFromDirectory(ws.Root);

        Assert.Equal(new[] { "-alpha", "-beta" }, Lines(text));
    }

    [Fact]
    public void GenerateRuleFromDirectory_Nested_UsesIncreasingDashesAndParentFirst()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"a\b\c", ws.Root);

        var text = CreateGenerator().GenerateRuleFromDirectory(ws.Root);

        Assert.Equal(new[] { "-a", "--b", "---c" }, Lines(text));
    }

    [Fact]
    public void GenerateRuleFromDirectory_SortsSiblingsByNameAtEveryLevel()
    {
        using var ws = new TempWorkspace();
        // 故意乱序创建
        ws.CreateDirectory(@"zeta\z2", ws.Root);
        ws.CreateDirectory(@"alpha\a2", ws.Root);
        ws.CreateDirectory(@"Middle", ws.Root);

        var text = CreateGenerator().GenerateRuleFromDirectory(ws.Root);

        Assert.Equal(
            new[] { "-alpha", "--a2", "-Middle", "-zeta", "--z2" },
            Lines(text));
    }

    [Fact]
    public void GenerateRuleFromDirectory_ExcludesDefaultFolders()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("keep", ws.Root);
        foreach (var excluded in RuleGenerator.DefaultExcludes)
        {
            ws.CreateDirectory(excluded, ws.Root);
            ws.CreateDirectory(Path.Combine(excluded, "inner"), ws.Root);
        }

        var text = CreateGenerator().GenerateRuleFromDirectory(ws.Root);

        Assert.Equal(new[] { "-keep" }, Lines(text));
    }

    [Fact]
    public void GenerateRuleFromDirectory_UsesCustomExcludesWhenProvided()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("keep", ws.Root);
        ws.CreateDirectory("node_modules", ws.Root);
        ws.CreateDirectory("temp", ws.Root);

        var text = CreateGenerator().GenerateRuleFromDirectory(ws.Root, new[] { "temp" });

        // 传入自定义排除项后不再使用默认排除项
        Assert.Equal(new[] { "-keep", "-node_modules" }, Lines(text));
    }

    [Fact]
    public void GenerateRuleFromDirectory_IsStableAcrossRuns()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"b\b1\b11", ws.Root);
        ws.CreateDirectory(@"a\a1", ws.Root);
        ws.CreateDirectory("c", ws.Root);

        var generator = CreateGenerator();
        var first = generator.GenerateRuleFromDirectory(ws.Root);
        var second = generator.GenerateRuleFromDirectory(ws.Root);

        Assert.Equal(first, second);
    }

    [Fact]
    public void GenerateRuleFromDirectory_WhenNoExcludesPassed_UsesDefaultExcludes()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(".git", ws.Root);

        Assert.Equal(string.Empty, CreateGenerator().GenerateRuleFromDirectory(ws.Root, null));
    }

    [Fact]
    public void GenerateRuleFromDirectory_WhenRootIsBlank_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CreateGenerator().GenerateRuleFromDirectory("   "));
    }

    [Fact]
    public void GenerateRuleFromDirectory_WhenRootMissing_ThrowsDirectoryNotFoundException()
    {
        using var ws = new TempWorkspace();

        Assert.Throws<DirectoryNotFoundException>(
            () => CreateGenerator().GenerateRuleFromDirectory(Path.Combine(ws.Root, "不存在")));
    }

    [Fact]
    public void GenerateRuleFromDirectory_RespectsCancellation()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"a\b", ws.Root);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateGenerator().GenerateRuleFromDirectory(ws.Root, null, cts.Token));
    }

    [Fact]
    public void GenerateTreeText_FirstLineIsRootPathAndRowsUseBoxCharacters()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("a", ws.Root);
        ws.CreateDirectory("b", ws.Root);

        var text = CreateGenerator().GenerateTreeText(ws.Root);

        Assert.Equal(
            new[]
            {
                Path.GetFullPath(ws.Root),
                "├─a",
                "└─b"
            },
            Lines(text).Take(3));
    }

    [Fact]
    public void GenerateTreeText_LastEntryUsesCornerAndOthersUseTee()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("first", ws.Root);
        ws.CreateDirectory("second", ws.Root);
        ws.CreateDirectory("third", ws.Root);

        var lines = Lines(CreateGenerator().GenerateTreeText(ws.Root));

        Assert.Equal("├─first", lines[1]);
        Assert.Equal("├─second", lines[2]);
        Assert.Equal("└─third", lines[3]);
    }

    [Fact]
    public void GenerateTreeText_NestedLevelsUseVerticalAndIndentPrefixes()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"a\a1", ws.Root);
        ws.CreateDirectory(@"a\a2", ws.Root);
        ws.CreateDirectory("b", ws.Root);

        var lines = Lines(CreateGenerator().GenerateTreeText(ws.Root));

        // 首行是根路径、末行是统计，中间 4 行才是树
        Assert.Equal(
            new[] { "├─a", "│  ├─a1", "│  └─a2", "└─b" },
            lines.Skip(1).Take(4));
    }

    [Fact]
    public void GenerateTreeText_ExcludesFilesByDefault()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("dir", ws.Root);
        ws.CreateFile("readme.txt", "x", null, ws.Root);

        var text = CreateGenerator().GenerateTreeText(ws.Root);

        Assert.DoesNotContain("readme.txt", text);
        Assert.Equal("1 个目录", Lines(text)[^1]);
    }

    [Fact]
    public void GenerateTreeText_WhenIncludeFiles_ListsFilesAndCountsBoth()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"dir\sub", ws.Root);
        ws.CreateFile("a.txt", "x", null, ws.Root);
        ws.CreateFile(@"dir\b.txt", "x", null, ws.Root);

        var text = CreateGenerator().GenerateTreeText(ws.Root, null, includeFiles: true);

        // 根层排序后：a.txt 在前（非末尾用 ├─），dir 在后（末尾用 └─）
        Assert.Contains("├─a.txt", text, StringComparison.Ordinal);
        Assert.Contains("└─dir", text, StringComparison.Ordinal);
        // dir 内部按名称排序：b.txt 在前（├─）、sub 在后（└─）
        Assert.Contains("├─b.txt", text, StringComparison.Ordinal);
        Assert.Contains("└─sub", text, StringComparison.Ordinal);
        Assert.Equal("2 个目录，2 个文件", Lines(text)[^1]);
    }

    [Fact]
    public void GenerateTreeText_ExcludesDefaultFolders()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("keep", ws.Root);
        ws.CreateDirectory(@"bin\Debug", ws.Root);

        var lines = Lines(CreateGenerator().GenerateTreeText(ws.Root));

        // 只比较树行：根路径本身可能包含 "bin" 之类的子串，不能直接对全文断言
        Assert.Equal(
            new[] { Path.GetFullPath(ws.Root), "└─keep", "1 个目录" },
            lines);
    }

    [Fact]
    public void GenerateTreeText_EndsWithBlankLineThenSummary()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("a", ws.Root);

        var text = CreateGenerator().GenerateTreeText(ws.Root);

        Assert.EndsWith("\r\n\r\n1 个目录", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateTreeText_RespectsCancellation()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("a", ws.Root);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateGenerator().GenerateTreeText(ws.Root, null, false, cts.Token));
    }

    [Fact]
    public void NormalizeExcludes_WhenNull_ReturnsDefaults()
    {
        var excludes = RuleGenerator.NormalizeExcludes(null);

        Assert.Equal(RuleGenerator.DefaultExcludes.Count, excludes.Count);
        Assert.Contains("node_modules", excludes);
    }

    [Fact]
    public void NormalizeExcludes_TrimsDeduplicatesAndIgnoresCase()
    {
        var excludes = RuleGenerator.NormalizeExcludes(new[] { " bin ", "BIN", string.Empty, "   ", "obj" });

        Assert.Equal(2, excludes.Count);
        Assert.Contains("bin", excludes);
        Assert.Contains("obj", excludes);
    }

    [Fact]
    public void MaxDepth_IsThirtyTwo()
    {
        // 与 FolderCreator 的 RuleParser.MaxDepth 保持一致（跨项目契约测试见 FolderCreator.Tests）
        Assert.Equal(32, RuleGenerator.MaxDepth);
    }
}
