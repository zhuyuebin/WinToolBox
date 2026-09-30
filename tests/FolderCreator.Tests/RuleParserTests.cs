using WinToolBox.Tools.FolderCreator;
using WinToolBox.Tools.FolderCreator.Models;

namespace WinToolBox.FolderCreator.Tests;

/// <summary>
/// RuleParser 单元测试。
/// 覆盖：短横线层级解析、注释/空行/普通行忽略、短横线后前导空格、缺失父级自动补齐、
/// 非法字符、空名称、Windows 保留设备名、名称以点或空格结尾、深度上限、重复规则去重、空输入与名称校验。
/// 全部为纯文本解析，不访问文件系统，也不依赖真实 U 盘/网络/用户目录。
/// </summary>
public sealed class RuleParserTests
{
    /// <summary>任务书 3.1 的示例文本：第 1 行的注释与第 2 行的“根目录名称”都应被忽略。</summary>
    private const string SampleText = """
        # 这是一个注释行，以 # 开头将被忽略
        根目录名称

        -一级目录
        --二级目录
        ---三级目录
        -一级目录2
        --二级目录2
        """;

    /// <summary>每个测试使用独立的解析器实例，避免潜在的实例状态相互影响。</summary>
    private static RuleParser NewParser() => new();

    // ------------------------------------------------------------------
    // 基础解析
    // ------------------------------------------------------------------

    /// <summary>示例文本应解析出 5 条规则（父级在前、文档顺序），且全部不是自动补齐。</summary>
    [Fact]
    public void Parse_示例文本_解析出五条规则()
    {
        var result = NewParser().Parse(SampleText);

        Assert.True(result.Success, "示例文本应当解析成功");
        Assert.Empty(result.Errors);
        Assert.Equal(5, result.Rules.Count);

        Assert.Equal(
            new[]
            {
                "一级目录",
                Path.Combine("一级目录", "二级目录"),
                Path.Combine("一级目录", "二级目录", "三级目录"),
                "一级目录2",
                Path.Combine("一级目录2", "二级目录2")
            },
            result.Rules.Select(rule => rule.RelativePath));

        Assert.Equal(new[] { 1, 2, 3, 1, 2 }, result.Rules.Select(rule => rule.Depth));
        Assert.Equal(
            new[] { "一级目录", "二级目录", "三级目录", "一级目录2", "二级目录2" },
            result.Rules.Select(rule => rule.Name));
        Assert.DoesNotContain(result.Rules, rule => rule.IsAutoCreated);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary), "Summary 不应为空");

        // 行号应为 1 起的源文本行号，并按文档顺序严格递增
        var lineNumbers = result.Rules.Select(rule => rule.LineNumber).ToArray();
        Assert.True(lineNumbers.All(number => number >= 1), $"行号应从 1 开始，实际为 {string.Join(",", lineNumbers)}");
        for (var i = 1; i < lineNumbers.Length; i++)
        {
            Assert.True(
                lineNumbers[i] > lineNumbers[i - 1],
                $"行号应按源文本顺序递增，实际为 {string.Join(",", lineNumbers)}");
        }
    }

    /// <summary>以 # 开头的行、空行、无短横线的普通行都不参与创建。</summary>
    [Fact]
    public void Parse_忽略注释空行与无短横线的普通行()
    {
        var text = """
            # 注释一
            根目录名称

              # 缩进的井号行同样不是规则行
            -一级目录
            # 注释二
            --二级目录

            随便写的一行说明文字
            """;

        var result = NewParser().Parse(text);

        Assert.True(result.Success, "只包含注释与两行规则时应解析成功");
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rules.Count);
        Assert.Equal(new[] { "一级目录", "二级目录" }, result.Rules.Select(rule => rule.Name));
        Assert.Equal(new[] { 1, 2 }, result.Rules.Select(rule => rule.Depth));
    }

    /// <summary>短横线后的前导空格应被去除。</summary>
    [Theory]
    [InlineData("-无空格", "无空格")]
    [InlineData("-   带空格的名字", "带空格的名字")]
    [InlineData("--    二级带空格", "二级带空格")]
    public void Parse_去除短横线后的前导空格(string line, string expectedName)
    {
        var result = NewParser().Parse(line);

        Assert.True(result.Success, $"「{line}」应解析成功");
        Assert.Equal(expectedName, result.Rules[^1].Name);
    }

    /// <summary>行首连续的短横线数量即为层级深度（父级缺失时会被自动补齐，故用最后一条规则断言）。</summary>
    [Theory]
    [InlineData("-a", 1, "a")]
    [InlineData("--b", 2, "b")]
    [InlineData("------f", 6, "f")]
    public void Parse_连续短横线数量决定层级(string line, int expectedDepth, string expectedName)
    {
        var result = NewParser().Parse(line);

        Assert.True(result.Success, $"「{line}」应解析成功");
        Assert.Equal(expectedDepth, result.Rules.Count);

        var last = result.Rules[^1];
        Assert.Equal(expectedDepth, last.Depth);
        Assert.Equal(expectedName, last.Name);
        Assert.False(last.IsAutoCreated, "显式写出的规则不应被标记为自动补齐");
    }

    // ------------------------------------------------------------------
    // 自动补齐父级
    // ------------------------------------------------------------------

    /// <summary>只写三级目录时，缺失的一、二级父目录应被自动补齐，并给出警告。</summary>
    [Fact]
    public void Parse_缺失父级时自动补齐()
    {
        var result = NewParser().Parse("---三级目录");

        Assert.True(result.Success, "自动补齐父级属于正常情况，不应视为失败");
        Assert.Equal(3, result.Rules.Count);

        var autoName = FolderRule.AutoCreatedName;
        Assert.Equal(
            new[]
            {
                autoName,
                Path.Combine(autoName, autoName),
                Path.Combine(autoName, autoName, "三级目录")
            },
            result.Rules.Select(rule => rule.RelativePath));

        Assert.Equal(new[] { 1, 2, 3 }, result.Rules.Select(rule => rule.Depth));
        Assert.Equal(new[] { autoName, autoName, "三级目录" }, result.Rules.Select(rule => rule.Name));
        Assert.True(result.Rules[0].IsAutoCreated, "深度 1 的父级应为自动补齐");
        Assert.True(result.Rules[1].IsAutoCreated, "深度 2 的父级应为自动补齐");
        Assert.False(result.Rules[2].IsAutoCreated, "显式写出的三级目录不应标记为自动补齐");

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, warning => warning.Contains("自动补齐"));
    }

    // ------------------------------------------------------------------
    // 名称合法性
    // ------------------------------------------------------------------

    /// <summary>名称包含 Windows 非法字符时应解析失败，并给出带行号的错误。</summary>
    [Theory]
    [InlineData('\\')]
    [InlineData('/')]
    [InlineData(':')]
    [InlineData('*')]
    [InlineData('?')]
    [InlineData('"')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('|')]
    public void Parse_名称含非法字符时报错(char illegal)
    {
        var result = NewParser().Parse("-非法" + illegal + "名称");

        Assert.False(result.Success, $"名称含「{illegal}」时应解析失败");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, error => error.Contains("第 1 行"));
        Assert.Contains(result.Errors, error => error.Contains("非法字符") || error.Contains(illegal));
        Assert.Empty(result.Rules);
    }

    /// <summary>只有短横线、没有名称时应报错。</summary>
    [Theory]
    [InlineData("-")]
    [InlineData("- ")]
    [InlineData("--   ")]
    [InlineData("---")]
    public void Parse_名称为空时报错(string line)
    {
        var result = NewParser().Parse(line);

        Assert.False(result.Success, $"「{line}」缺少目录名，应解析失败");
        Assert.NotEmpty(result.Errors);

        // 该行不得作为规则进入结果（自动补齐的父级不受影响）
        Assert.DoesNotContain(result.Rules, rule => string.IsNullOrWhiteSpace(rule.Name));
    }

    /// <summary>Windows 保留设备名不能作为目录名（大小写不敏感）。</summary>
    [Theory]
    [InlineData("-CON")]
    [InlineData("-PRN")]
    [InlineData("-AUX")]
    [InlineData("-NUL")]
    [InlineData("-COM1")]
    [InlineData("-LPT1")]
    [InlineData("-con")]
    [InlineData("-Com9")]
    public void Parse_保留设备名时报错(string line)
    {
        var result = NewParser().Parse(line);

        Assert.False(result.Success, $"「{line}」是 Windows 保留设备名，应解析失败");
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Rules);
    }

    /// <summary>名称以点号或空格结尾时 Windows 无法创建，应报错。</summary>
    [Theory]
    [InlineData("-abc.")]
    [InlineData("-abc ")]
    public void Parse_名称以点或空格结尾时报错(string line)
    {
        var result = NewParser().Parse(line);

        Assert.False(result.Success, $"「{line}」的结尾字符不被 Windows 允许，应解析失败");
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Rules);
    }

    /// <summary>层级超过 MaxDepth 时应报错，而不是生成超深目录。</summary>
    [Fact]
    public void Parse_超过最大深度时报错()
    {
        Assert.Equal(32, RuleParser.MaxDepth);

        var tooDeep = new string('-', RuleParser.MaxDepth + 8) + "太深了";
        var result = NewParser().Parse(tooDeep);

        Assert.False(result.Success, "超过最大深度应解析失败");
        Assert.NotEmpty(result.Errors);
    }

    // ------------------------------------------------------------------
    // 去重与空输入
    // ------------------------------------------------------------------

    /// <summary>同一个相对路径重复出现时只保留一条，并给出警告。</summary>
    [Fact]
    public void Parse_重复规则只保留一条并给出警告()
    {
        var result = NewParser().Parse("""
            -重复目录
            -重复目录
            """);

        Assert.True(result.Success, "重复规则应当以警告形式提示，而不是解析失败");
        Assert.Single(result.Rules);
        Assert.Equal("重复目录", result.Rules[0].Name);
        Assert.NotEmpty(result.Warnings);
    }

    /// <summary>空文本、null、纯空白与纯注释都应返回“没有规则”的成功结果。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# 只有注释")]
    [InlineData("\r\n\r\n")]
    [InlineData("根目录名称")]
    public void Parse_空文本或纯注释时返回空结果(string? text)
    {
        var result = NewParser().Parse(text);

        Assert.True(result.Success, "没有规则不属于错误");
        Assert.Empty(result.Rules);
        Assert.Empty(result.Errors);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary), "Summary 应说明没有解析到规则");
    }

    /// <summary>Parse(null) 不允许抛异常。</summary>
    [Fact]
    public void Parse_null不抛异常()
    {
        var result = NewParser().Parse(null);

        Assert.True(result.Success);
        Assert.Empty(result.Rules);
    }

    // ------------------------------------------------------------------
    // IsValidFolderName
    // ------------------------------------------------------------------

    /// <summary>合法目录名返回 true，且不产生错误说明。</summary>
    [Theory]
    [InlineData("我的文件夹")]
    [InlineData("a b-c_1")]
    [InlineData("数字123")]
    public void IsValidFolderName_合法名称返回true(string name)
    {
        var valid = RuleParser.IsValidFolderName(name, out var error);

        Assert.True(valid, $"「{name}」应当是合法目录名");
        Assert.Null(error);
    }

    /// <summary>null、空白、非法字符、保留设备名、结尾点号都返回 false，且给出非空错误说明。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("非法:名称")]
    [InlineData("斜杠/名称")]
    [InlineData("反斜杠\\名称")]
    [InlineData("星号*名称")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("abc.")]
    public void IsValidFolderName_非法名称返回false(string? name)
    {
        var valid = RuleParser.IsValidFolderName(name, out var error);

        Assert.False(valid, $"「{name}」应当是非法目录名");
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error), "非法名称必须给出错误说明");
    }
}
