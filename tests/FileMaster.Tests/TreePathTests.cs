namespace WinToolBox.FileMaster.Tests;

using WinToolBox.Tools.FileMaster;

/// <summary>
/// 结果树节点键的单元测试。
/// 覆盖过一次真实缺陷：拼接多级路径时漏掉当前层级名，导致同一父级下的兄弟节点共用同一个键，
/// 后一个节点覆盖前一个 —— 表现为「严格模式下缺失项被多余项覆盖、界面上看不到缺失」。
/// </summary>
public class TreePathTests
{
    [Fact]
    public void Combine_UsesBackslashSeparator()
    {
        Assert.Equal("一级目录", TreePath.Combine(null, "一级目录"));
        Assert.Equal("一级目录", TreePath.Combine(string.Empty, "一级目录"));
        Assert.Equal(@"一级目录\二级目录", TreePath.Combine("一级目录", "二级目录"));
        Assert.Equal(@"一级目录\二级目录\三级目录", TreePath.Combine(@"一级目录\二级目录", "三级目录"));
    }

    [Fact]
    public void KeysFor_SingleLevel_ReturnsItself()
    {
        var keys = TreePath.KeysFor("一级目录");

        Assert.Single(keys);
        Assert.Equal("一级目录", keys[0]);
    }

    [Fact]
    public void KeysFor_MultiLevel_ContainsEverySegmentInTheKey()
    {
        var keys = TreePath.KeysFor(@"一级目录2\二级目录2");

        Assert.Equal(2, keys.Count);
        Assert.Equal("一级目录2", keys[0]);
        Assert.Equal(@"一级目录2\二级目录2", keys[1]);
    }

    [Fact]
    public void KeysFor_ThreeLevels_BuildsCumulativeKeys()
    {
        var keys = TreePath.KeysFor(@"一级目录\二级目录\三级目录");

        Assert.Equal(
            new[] { "一级目录", @"一级目录\二级目录", @"一级目录\二级目录\三级目录" },
            keys);
    }

    /// <summary>回归用例：同一父级下的兄弟节点必须得到不同的键（修复前两者都是「一级目录2\」）。</summary>
    [Fact]
    public void KeysFor_SiblingsUnderSameParent_HaveDistinctKeys()
    {
        var missing = TreePath.KeysFor(@"一级目录2\二级目录2");
        var extra = TreePath.KeysFor(@"一级目录2\二级目录22");

        Assert.Equal(missing[0], extra[0]);
        Assert.NotEqual(missing[^1], extra[^1]);
        Assert.Equal(@"一级目录2\二级目录2", missing[^1]);
        Assert.Equal(@"一级目录2\二级目录22", extra[^1]);
    }

    [Fact]
    public void KeysFor_AcceptsForwardSlashAndTrimsWhitespace()
    {
        var keys = TreePath.KeysFor("一级目录/ 二级目录 ");

        Assert.Equal(new[] { "一级目录", @"一级目录\二级目录" }, keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\")]
    public void KeysFor_EmptyInput_ReturnsEmptyList(string? input)
    {
        Assert.Empty(TreePath.KeysFor(input));
    }

    [Fact]
    public void KeysFor_IgnoresDuplicateSeparators()
    {
        var keys = TreePath.KeysFor(@"一级目录\\\二级目录");

        Assert.Equal(new[] { "一级目录", @"一级目录\二级目录" }, keys);
    }
}
