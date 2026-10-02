using WinToolBox.Tools.FileMaster;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// FolderGenerator 单元测试。
/// 覆盖：批量创建层级目录、已存在目录跳过、根目录自动创建、逐项结果字段、
/// 进度回调、异步生成、参数校验、空规则列表、取消以及“路径被同名文件占用”的失败项。
/// 所有用例都在各自的临时目录中执行，不依赖真实 U 盘、网络路径或用户目录。
/// </summary>
public sealed class FolderGeneratorTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>按“名称 + 深度”构造规则列表：RelativePath 由父级名称自动拼装，父级在前，与解析器输出一致。</summary>
    private static List<FolderRule> BuildRules(params (string Name, int Depth)[] items)
    {
        var rules = new List<FolderRule>();
        var path = new List<string>();

        foreach (var (name, depth) in items)
        {
            while (path.Count >= depth)
            {
                path.RemoveAt(path.Count - 1);
            }

            while (path.Count < depth - 1)
            {
                path.Add(FolderRule.AutoCreatedName);
            }

            path.Add(name);

            rules.Add(new FolderRule
            {
                Name = name,
                Depth = depth,
                LineNumber = rules.Count + 1,
                RawLine = new string('-', depth) + name,
                RelativePath = string.Join(Path.DirectorySeparatorChar, path),
                IsAutoCreated = false
            });
        }

        return rules;
    }

    /// <summary>示例规则：5 个目录、3 个层级、两棵子树，与任务书示例文本等价。</summary>
    private static List<FolderRule> SampleRules() => BuildRules(
        ("一级目录", 1),
        ("二级目录", 2),
        ("三级目录", 3),
        ("一级目录2", 1),
        ("二级目录2", 2));

    /// <summary>去掉末尾分隔符并转成绝对路径，避免因实现差异（是否带尾部斜杠）导致误报。</summary>
    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// 同步进度接收器：直接在当前线程回调并记录快照。
    /// 不用 Progress&lt;T&gt;，避免其依赖同步上下文而导致断言时序不确定。
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly List<T> _values = new();

        /// <summary>按回调顺序记录的进度快照。</summary>
        public IReadOnlyList<T> Values => _values;

        public void Report(T value) => _values.Add(value);
    }

    // ------------------------------------------------------------------
    // 创建行为
    // ------------------------------------------------------------------

    /// <summary>在临时根目录下生成全部规则目录。</summary>
    [Fact]
    public void Generate_在临时目录下创建全部目录()
    {
        using var ws = new TempWorkspace();
        var rules = SampleRules();
        const string rootFolder = "目标根目录";
        var root = ws.PathOf(rootFolder);

        var result = new FolderGenerator().Generate(root, rules);

        Assert.True(result.Success, $"创建应当成功，摘要：{result.Summary}");
        Assert.Equal(5, result.CreatedCount);
        Assert.Equal(0, result.ExistedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(rules.Count, result.Items.Count);
        Assert.Equal(NormalizePath(root), NormalizePath(result.RootDirectory));

        foreach (var rule in rules)
        {
            var fullPath = Path.Combine(root, rule.RelativePath);
            Assert.True(Directory.Exists(fullPath), $"目录应被创建：{rule.RelativePath}");
        }

        // 临时目录中应恰好出现“根目录 + 5 条规则目录”，既不多也不少
        var expected = new List<string> { rootFolder };
        expected.AddRange(rules.Select(rule => Path.Combine(rootFolder, rule.RelativePath)));

        Assert.Equal(
            expected.OrderBy(path => path, StringComparer.Ordinal),
            ws.EnumerateDirectories());
    }

    /// <summary>端到端：直接用 RuleParser 解析示例文本，再按解析结果创建目录。</summary>
    [Fact]
    public void Generate_使用解析器输出的五条规则创建目录()
    {
        using var ws = new TempWorkspace();
        const string sampleText = """
            # 注释
            根目录名称
            -一级目录
            --二级目录
            ---三级目录
            -一级目录2
            --二级目录2
            """;

        var parseResult = new RuleParser().Parse(sampleText);
        Assert.True(parseResult.Success, "示例文本应解析成功");
        Assert.Equal(5, parseResult.Rules.Count);

        var root = ws.PathOf("端到端根目录");
        var result = new FolderGenerator().Generate(root, parseResult.Rules);

        Assert.True(result.Success, $"摘要：{result.Summary}");
        Assert.Equal(5, result.CreatedCount);
        Assert.Equal(0, result.FailedCount);

        foreach (var rule in parseResult.Rules)
        {
            Assert.True(Directory.Exists(Path.Combine(root, rule.RelativePath)), $"目录应被创建：{rule.RelativePath}");
        }

        Assert.Equal(5, Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Count());
    }

    /// <summary>第二次对同一目标目录执行时全部命中“已存在”，不报错。</summary>
    [Fact]
    public void Generate_目标目录已存在时跳过且不报错()
    {
        using var ws = new TempWorkspace();
        var rules = SampleRules();
        var root = ws.PathOf("重复根目录");
        var generator = new FolderGenerator();

        var first = generator.Generate(root, rules);
        var second = generator.Generate(root, rules);

        Assert.True(first.Success);
        Assert.Equal(5, first.CreatedCount);

        Assert.True(second.Success, $"已存在的目录不应被当作错误，摘要：{second.Summary}");
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(5, second.ExistedCount);
        Assert.Equal(0, second.FailedCount);
        Assert.DoesNotContain(second.Items, item => item.Status == FolderItemStatus.Failed);

        foreach (var rule in rules)
        {
            Assert.True(Directory.Exists(Path.Combine(root, rule.RelativePath)), $"目录应仍然存在：{rule.RelativePath}");
        }
    }

    /// <summary>根目录本身不存在时应被自动创建（含多级父目录）。</summary>
    [Fact]
    public void Generate_根目录不存在时自动创建()
    {
        using var ws = new TempWorkspace();
        var root = ws.PathOf(Path.Combine("层级一", "层级二", "根目录"));
        var rules = BuildRules(("子目录", 1));

        Assert.False(Directory.Exists(root), "前置条件：根目录此时不应存在");

        var result = new FolderGenerator().Generate(root, rules);

        Assert.True(result.Success, $"摘要：{result.Summary}");
        Assert.True(Directory.Exists(root), "根目录应被自动创建");
        Assert.True(Directory.Exists(Path.Combine(root, "子目录")), "根目录下的规则目录应被创建");
        Assert.Equal(1, result.CreatedCount);
    }

    /// <summary>逐项检查 Items 的相对路径、完整路径与状态。</summary>
    [Fact]
    public void Generate_逐项结果包含相对路径与完整路径()
    {
        using var ws = new TempWorkspace();
        var rules = SampleRules();
        var root = ws.PathOf("明细根目录");

        var result = new FolderGenerator().Generate(root, rules);

        Assert.Equal(rules.Count, result.Items.Count);

        for (var i = 0; i < rules.Count; i++)
        {
            var item = result.Items[i];

            Assert.Equal(rules[i].RelativePath, item.RelativePath);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, rules[i].RelativePath)),
                Path.GetFullPath(item.FullPath));
            Assert.StartsWith(root, item.FullPath);
            Assert.Equal(FolderItemStatus.Created, item.Status);
        }
    }

    /// <summary>空规则列表时不创建任何目录，也不算失败。</summary>
    [Fact]
    public void Generate_空规则列表时不创建任何目录()
    {
        using var ws = new TempWorkspace();
        var root = ws.PathOf("空规则根目录");

        var result = new FolderGenerator().Generate(root, Array.Empty<FolderRule>());

        Assert.True(result.Success, "没有规则不算失败");
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.ExistedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.Items);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary), "Summary 不应为空");

        // 根目录本身允许被预先创建，但其下不应出现任何规则目录
        var subDirectories = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).ToArray()
            : Array.Empty<string>();
        Assert.Empty(subDirectories);
    }

    /// <summary>目标路径被同名文件占用时，该项标记为失败，其余目录仍正常创建。</summary>
    [Fact]
    public void Generate_路径被同名文件占用时该项标记为失败()
    {
        using var ws = new TempWorkspace();
        var root = ws.PathOf("冲突根目录");
        Directory.CreateDirectory(root);

        // 用同名文件占位，模拟“目标路径已被文件占用”的真实场景
        File.WriteAllText(Path.Combine(root, "冲突目录"), "占位文件");

        var rules = BuildRules(("冲突目录", 1), ("正常目录", 1));
        var result = new FolderGenerator().Generate(root, rules);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.False(result.Success, "存在失败项时不应报告成功");

        Assert.Contains(
            result.Items,
            item => item.RelativePath == "冲突目录" && item.Status == FolderItemStatus.Failed);

        var failed = result.Items.First(item => item.RelativePath == "冲突目录");
        Assert.False(string.IsNullOrWhiteSpace(failed.Message), "失败项应给出原因说明");

        Assert.True(Directory.Exists(Path.Combine(root, "正常目录")), "单个失败不应影响其它目录的创建");
    }

    // ------------------------------------------------------------------
    // 进度与异步
    // ------------------------------------------------------------------

    /// <summary>进度回调次数等于规则数，Processed 单调递增，最后一次为 100%。</summary>
    [Fact]
    public void Generate_进度回调按规则数逐步上报()
    {
        using var ws = new TempWorkspace();
        var rules = SampleRules();
        var progress = new SyncProgress<FolderProgress>();

        var result = new FolderGenerator().Generate(ws.PathOf("进度根目录"), rules, progress);

        Assert.True(result.Success, $"摘要：{result.Summary}");
        Assert.Equal(rules.Count, progress.Values.Count);

        foreach (var value in progress.Values)
        {
            Assert.Equal(rules.Count, value.Total);
        }

        for (var i = 1; i < progress.Values.Count; i++)
        {
            Assert.True(
                progress.Values[i].Processed >= progress.Values[i - 1].Processed,
                $"Processed 应单调递增，实际为 {string.Join(",", progress.Values.Select(value => value.Processed))}");
        }

        var last = progress.Values[^1];
        Assert.Equal(rules.Count, last.Processed);
        Assert.Equal(100d, last.Percent, 3);
        Assert.False(string.IsNullOrWhiteSpace(last.CurrentPath), "当前处理路径不应为空");
    }

    /// <summary>GenerateAsync 能正常完成，并返回与同步版本一致的创建结果。</summary>
    [Fact]
    public async Task GenerateAsync_能正常完成并返回结果()
    {
        using var ws = new TempWorkspace();
        var rules = SampleRules();
        var root = ws.PathOf("异步根目录");

        var result = await new FolderGenerator().GenerateAsync(root, rules);

        Assert.True(result.Success, $"摘要：{result.Summary}");
        Assert.Equal(5, result.CreatedCount);
        Assert.Equal(0, result.ExistedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(rules.Count, result.Items.Count);
        Assert.Equal(
            rules.Select(rule => rule.RelativePath),
            result.Items.Select(item => item.RelativePath));

        foreach (var rule in rules)
        {
            Assert.True(Directory.Exists(Path.Combine(root, rule.RelativePath)), $"目录应被创建：{rule.RelativePath}");
        }
    }

    // ------------------------------------------------------------------
    // 参数校验与取消
    // ------------------------------------------------------------------

    /// <summary>根目录为 null/空白时抛参数异常。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_根目录为空时抛异常(string? rootDirectory)
    {
        var rules = SampleRules();

        Assert.ThrowsAny<ArgumentException>(() => new FolderGenerator().Generate(rootDirectory!, rules));
    }

    /// <summary>规则列表为 null 时抛参数异常。</summary>
    [Fact]
    public void Generate_规则列表为null时抛异常()
    {
        using var ws = new TempWorkspace();

        Assert.ThrowsAny<ArgumentException>(() => new FolderGenerator().Generate(ws.Root, null!));
    }

    /// <summary>传入已取消的令牌时抛 OperationCanceledException。</summary>
    [Fact]
    public void Generate_已取消的令牌抛出OperationCanceledException()
    {
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var rules = SampleRules();

        Assert.ThrowsAny<OperationCanceledException>(
            () => new FolderGenerator().Generate(ws.PathOf("取消根目录"), rules, null, cts.Token));
    }

    /// <summary>异步接口在令牌已取消时同样以 OperationCanceledException 结束。</summary>
    [Fact]
    public async Task GenerateAsync_已取消的令牌抛出OperationCanceledException()
    {
        using var ws = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var rules = SampleRules();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FolderGenerator().GenerateAsync(ws.PathOf("取消根目录"), rules, null, cts.Token));
    }
}
