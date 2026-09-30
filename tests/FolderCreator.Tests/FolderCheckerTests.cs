using WinToolBox.Tools.FolderCreator;
using WinToolBox.Tools.FolderCreator.Models;

namespace WinToolBox.FolderCreator.Tests;

/// <summary>
/// FolderChecker 单元测试。
/// 覆盖：严格模式同时报出缺失与多余、宽松模式忽略多余、完全一致时的通过、目标根目录不存在、
/// Items 深度排序、多级多余目录的递归扫描、目标目录为空以及参数校验。
/// 所有用例都在各自的临时目录中执行，不依赖真实 U 盘、网络路径或用户目录。
/// </summary>
public sealed class FolderCheckerTests
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

    /// <summary>示例规则：5 个目录、3 个层级、两棵子树。</summary>
    private static List<FolderRule> SampleRules() => BuildRules(
        ("一级目录", 1),
        ("二级目录", 2),
        ("三级目录", 3),
        ("一级目录2", 1),
        ("二级目录2", 2));

    /// <summary>统一分隔符后再比较，避免实现拼接方式不同导致的误报。</summary>
    private static string NormalizeRelative(string relativePath)
        => relativePath.Replace('\\', '/').Trim('/');

    /// <summary>
    /// 构造“既缺目录又多目录”的目标目录：存在 一级目录 / 一级目录2，多出 多余目录，
    /// 缺少 二级目录、三级目录、二级目录2。
    /// </summary>
    private static (string Root, List<FolderRule> Rules) CreateMixedScenario(TempWorkspace ws)
    {
        const string rootFolder = "检查根目录";

        ws.CreateDirectory(Path.Combine(rootFolder, "一级目录"));
        ws.CreateDirectory(Path.Combine(rootFolder, "一级目录2"));
        ws.CreateDirectory(Path.Combine(rootFolder, "多余目录"));

        return (ws.PathOf(rootFolder), SampleRules());
    }

    /// <summary>先用 FolderGenerator 生成与规则完全一致的目标目录。</summary>
    private static (string Root, List<FolderRule> Rules) CreateConsistentScenario(TempWorkspace ws)
    {
        var rules = SampleRules();
        var root = ws.PathOf("一致根目录");

        new FolderGenerator().Generate(root, rules);

        return (root, rules);
    }

    // ------------------------------------------------------------------
    // 严格模式 / 宽松模式
    // ------------------------------------------------------------------

    /// <summary>严格模式：缺失与多余都要报出来。</summary>
    [Fact]
    public void Check_严格模式报出缺失与多余()
    {
        using var ws = new TempWorkspace();
        var (root, rules) = CreateMixedScenario(ws);

        var result = new FolderChecker().Check(root, rules);

        Assert.Equal(FolderCheckMode.Strict, result.Mode);
        Assert.Null(result.Error);
        Assert.False(result.IsConsistent, "存在缺失与多余时不应判定为一致");
        Assert.True(result.MissingCount >= 1, $"应报出缺失，实际 MissingCount={result.MissingCount}");
        Assert.True(result.ExtraCount >= 1, $"应报出多余，实际 ExtraCount={result.ExtraCount}");
        Assert.Contains(result.Items, item => item.Status == FolderCheckStatus.Missing);
        Assert.Contains(result.Items, item => item.Status == FolderCheckStatus.Extra);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary), "Summary 不应为空");

        var missing = result.Items.First(item => item.Status == FolderCheckStatus.Missing);
        Assert.False(string.IsNullOrWhiteSpace(missing.DisplayText), "缺失项应有可展示的文本");
    }

    /// <summary>宽松模式：只关心规则要求的目录是否缺失，多出的目录不计入结果。</summary>
    [Fact]
    public void Check_宽松模式忽略多余只报缺失()
    {
        using var ws = new TempWorkspace();
        var (root, rules) = CreateMixedScenario(ws);

        var result = new FolderChecker().Check(root, rules, FolderCheckMode.Loose);

        Assert.Equal(FolderCheckMode.Loose, result.Mode);
        Assert.Equal(0, result.ExtraCount);
        Assert.True(result.MissingCount >= 1, $"应报出缺失，实际 MissingCount={result.MissingCount}");
        Assert.False(result.IsConsistent, "仍有缺失时不应判定为一致");
        Assert.DoesNotContain(result.Items, item => item.Status == FolderCheckStatus.Extra);
    }

    /// <summary>目标目录与规则完全一致时，严格模式判定一致。</summary>
    [Fact]
    public void Check_完全一致时严格模式通过()
    {
        using var ws = new TempWorkspace();
        var (root, rules) = CreateConsistentScenario(ws);

        var result = new FolderChecker().Check(root, rules);

        Assert.True(result.IsConsistent, $"目录应与规则完全一致，摘要：{result.Summary}");
        Assert.Equal(0, result.MissingCount);
        Assert.Equal(0, result.ExtraCount);
        Assert.Equal(rules.Count, result.MatchedCount);
        Assert.Equal(rules.Count, result.Items.Count);
        Assert.DoesNotContain(result.Items, item => item.Status != FolderCheckStatus.Matched);
    }

    /// <summary>目标目录与规则完全一致时，宽松模式同样判定一致。</summary>
    [Fact]
    public void Check_完全一致时宽松模式通过()
    {
        using var ws = new TempWorkspace();
        var (root, rules) = CreateConsistentScenario(ws);

        var result = new FolderChecker().Check(root, rules, FolderCheckMode.Loose);

        Assert.True(result.IsConsistent, $"目录应与规则完全一致，摘要：{result.Summary}");
        Assert.Equal(0, result.MissingCount);
        Assert.Equal(0, result.ExtraCount);
        Assert.Equal(rules.Count, result.MatchedCount);
    }

    // ------------------------------------------------------------------
    // 边界场景
    // ------------------------------------------------------------------

    /// <summary>目标根目录不存在时返回错误信息，而不是抛异常。</summary>
    [Fact]
    public void Check_目标根目录不存在时返回错误()
    {
        using var ws = new TempWorkspace();
        var root = ws.PathOf("不存在的根目录");

        Assert.False(Directory.Exists(root), "前置条件：根目录不应存在");

        var result = new FolderChecker().Check(root, SampleRules());

        Assert.False(result.IsConsistent, "根目录不存在时不应判定为一致");
        Assert.NotNull(result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Error), "应给出根目录不存在的说明");
        Assert.NotNull(result.Items);
    }

    /// <summary>目标目录为空时，规则要求的所有目录都判为缺失。</summary>
    [Fact]
    public void Check_目标目录为空时全部缺失()
    {
        using var ws = new TempWorkspace();
        var rules = SampleRules();
        var root = ws.CreateDirectory("空根目录");

        var result = new FolderChecker().Check(root, rules);

        Assert.False(result.IsConsistent);
        Assert.Equal(rules.Count, result.MissingCount);
        Assert.Equal(0, result.ExtraCount);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(rules.Count, result.Items.Count);
        Assert.DoesNotContain(result.Items, item => item.Status != FolderCheckStatus.Missing);
    }

    /// <summary>Items 按深度升序排列。</summary>
    [Fact]
    public void Check_结果按深度升序排列()
    {
        using var ws = new TempWorkspace();
        var (root, rules) = CreateMixedScenario(ws);

        var result = new FolderChecker().Check(root, rules);
        var depths = result.Items.Select(item => item.Depth).ToArray();

        Assert.NotEmpty(depths);
        Assert.True(depths[0] >= 1, "深度应从 1 开始");
        for (var i = 1; i < depths.Length; i++)
        {
            Assert.True(
                depths[i] >= depths[i - 1],
                $"Items 应按深度升序排列，实际深度序列：{string.Join(",", depths)}");
        }
    }

    /// <summary>目标目录里多出的多级目录也要被递归发现，至少要报出最上层的多余目录。</summary>
    [Fact]
    public void Check_多级多余目录至少报出顶层多余项()
    {
        using var ws = new TempWorkspace();
        const string rootFolder = "多级根目录";

        ws.CreateDirectory(Path.Combine(rootFolder, "X", "Y"));

        var rules = BuildRules(("一级目录", 1));
        var result = new FolderChecker().Check(ws.PathOf(rootFolder), rules);

        Assert.False(result.IsConsistent);
        Assert.True(result.ExtraCount >= 1, $"应递归发现多余目录，实际 ExtraCount={result.ExtraCount}");
        Assert.Contains(
            result.Items,
            item => item.Status == FolderCheckStatus.Extra
                && item.Depth == 1
                && NormalizeRelative(item.RelativePath) == "X");
    }

    // ------------------------------------------------------------------
    // 参数校验
    // ------------------------------------------------------------------

    /// <summary>根目录为 null/空白时抛参数异常。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Check_根目录为空时抛异常(string? rootDirectory)
    {
        var rules = SampleRules();

        Assert.ThrowsAny<ArgumentException>(() => new FolderChecker().Check(rootDirectory!, rules));
    }

    /// <summary>规则列表为 null 时抛参数异常。</summary>
    [Fact]
    public void Check_规则列表为null时抛异常()
    {
        using var ws = new TempWorkspace();

        Assert.ThrowsAny<ArgumentException>(() => new FolderChecker().Check(ws.Root, null!));
    }
}
