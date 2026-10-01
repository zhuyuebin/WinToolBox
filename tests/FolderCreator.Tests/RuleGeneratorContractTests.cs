using WinToolBox.Core.Services;
using WinToolBox.Tools.FolderCreator;

namespace WinToolBox.FolderCreator.Tests;

/// <summary>
/// 跨项目契约测试：<see cref="RuleGenerator"/>（WinToolBox.Core）生成的规则文本
/// 必须能被本工具的 <see cref="RuleParser"/> 原样解析 —— 两个类的层级上限也必须一致。
/// </summary>
public class RuleGeneratorContractTests
{
    [Fact]
    public void MaxDepth_MatchesRuleParser()
    {
        Assert.Equal(RuleParser.MaxDepth, RuleGenerator.MaxDepth);
    }

    [Fact]
    public void GeneratedRules_ParseBackToTheSameHierarchy()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"alpha\a1");
        ws.CreateDirectory(@"alpha\a2\deep");
        ws.CreateDirectory("beta");
        ws.CreateDirectory(".git");

        var text = new RuleGenerator().GenerateRuleFromDirectory(ws.Root);
        var parsed = new RuleParser().Parse(text);

        Assert.True(parsed.Success, string.Join(" / ", parsed.Errors));
        Assert.Equal(
            new[] { "alpha", @"alpha\a1", @"alpha\a2", @"alpha\a2\deep", "beta" },
            parsed.Rules.Select(rule => rule.RelativePath).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void GeneratedRules_WhenCheckingTheSameDirectory_ReportsFullyConsistent()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"alpha\a1");
        ws.CreateDirectory("beta");

        var text = new RuleGenerator().GenerateRuleFromDirectory(ws.Root);
        var rules = new RuleParser().Parse(text).Rules;

        var result = new FolderChecker().Check(ws.Root, rules, FolderCheckMode.Strict);

        Assert.Equal(0, result.MissingCount);
        Assert.Equal(0, result.ExtraCount);
        Assert.True(result.IsConsistent);
    }

    [Fact]
    public void GeneratedRules_WithChineseNames_ParseBackCorrectly()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(@"一级目录\二级目录\三级目录");

        var text = new RuleGenerator().GenerateRuleFromDirectory(ws.Root);
        var parsed = new RuleParser().Parse(text);

        Assert.True(parsed.Success);
        Assert.Equal(
            new[] { "一级目录", @"一级目录\二级目录", @"一级目录\二级目录\三级目录" },
            parsed.Rules.Select(rule => rule.RelativePath));
    }
}
