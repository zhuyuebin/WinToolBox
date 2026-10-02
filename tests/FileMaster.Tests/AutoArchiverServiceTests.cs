using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// AutoArchiverService 单元测试。
/// 覆盖：ParseRules 的注释 / 空行忽略、四类规则解析、未知类型 / 缺列 / 参数为空 / 日期格式非法 / 多个兜底等错误、
/// 兜底规则排序与 SplitExtensions 的补点去重；ResolveTargetSubDirectory 的扩展名 / 日期 / 首字母 / 兜底分支；
/// MatchRule 的扩展名大小写不敏感、日期与首字母恒命中、兜底与无兜底场景；BuildPlan 的目录校验、目标路径生成、
/// 覆盖控制与子目录扫描开关；Apply 的 Move / Copy 真实执行、跳过不可执行条目、源文件缺失的失败项、
/// 移动后的空目录清理开关与 BuildReport 的执行标记。
/// 所有用例都在各自的临时目录中执行，不依赖真实 U 盘、网络路径或用户目录。
/// </summary>
public sealed class AutoArchiverServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>固定修改时间（2026-10-02 08:09:10），用于日期规则断言。</summary>
    private static readonly DateTime FixedLastWriteTime = new(2026, 10, 2, 8, 9, 10);

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

    /// <summary>解析规则文本并断言没有任何解析错误（规则文本本身有问题时应改用 ParseRules 直接断言）。</summary>
    private static IReadOnlyList<ArchiveRule> Parse(string text)
    {
        var parsed = AutoArchiverService.ParseRules(text);

        Assert.Empty(parsed.Errors);
        return parsed.Rules;
    }

    /// <summary>构造自动分类选项，规则文本经 Parse 校验，其余参数按需覆盖默认值。</summary>
    private static ArchiveOptions CreateOptions(
        string sourceDirectory,
        string targetDirectory,
        string rules,
        ArchiveAction action = ArchiveAction.Move,
        bool overwrite = false,
        bool includeSubDirectories = false,
        bool cleanEmptyFolders = false)
        => new()
        {
            SourceDirectory = sourceDirectory,
            TargetDirectory = targetDirectory,
            Rules = Parse(rules),
            Action = action,
            Overwrite = overwrite,
            IncludeSubDirectories = includeSubDirectories,
            CleanEmptyFolders = cleanEmptyFolders
        };

    // ------------------------------------------------------------------
    // ParseRules
    // ------------------------------------------------------------------

    /// <summary>规则文本为 null / 空 / 空白时直接报错，并且不返回任何规则。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseRules_WithEmptyText_ReturnsError(string? text)
    {
        var result = AutoArchiverService.ParseRules(text);

        Assert.False(result.Success);
        Assert.Empty(result.Rules);
        Assert.Contains("规则内容为空。", result.Errors);
    }

    /// <summary>以 # 开头的注释行与空行被忽略，CRLF 与 LF 换行都能正确切分。</summary>
    [Fact]
    public void ParseRules_IgnoresCommentsAndBlankLines()
    {
        var result = AutoArchiverService.ParseRules(
            "# 图片类\r\n扩展名|.jpg|图片\r\n\r\n# 兜底\r\n其它||其它\n");

        Assert.True(result.Success);
        Assert.Equal(2, result.Rules.Count);
        Assert.Equal(ArchiveRuleKind.Extension, result.Rules[0].Kind);
        Assert.Equal(ArchiveRuleKind.Fallback, result.Rules[1].Kind);
    }

    /// <summary>扩展名规则解析出 Kind / 扩展名列表 / 目标子目录与规则原文。</summary>
    [Fact]
    public void ParseRules_ParsesExtensionRule()
    {
        var rule = Assert.Single(Parse("扩展名|.jpg;.png|图片"));

        Assert.Equal(ArchiveRuleKind.Extension, rule.Kind);
        Assert.Equal(".jpg;.png", rule.Extensions);
        Assert.Equal("图片", rule.TargetSubDirectory);
        Assert.Equal("扩展名|.jpg;.png|图片", rule.SourceText);
    }

    /// <summary>日期规则解析出 Kind / 日期格式 / 目标子目录，扩展名列表保持为空。</summary>
    [Fact]
    public void ParseRules_ParsesDateRule()
    {
        var rule = Assert.Single(Parse("日期|yyyy-MM|按日期"));

        Assert.Equal(ArchiveRuleKind.Date, rule.Kind);
        Assert.Equal("yyyy-MM", rule.DateFormat);
        Assert.Equal("按日期", rule.TargetSubDirectory);
        Assert.Equal(string.Empty, rule.Extensions);
    }

    /// <summary>首字母规则只需要两列，参数列留空即可。</summary>
    [Fact]
    public void ParseRules_ParsesFirstLetterRule()
    {
        var rule = Assert.Single(Parse("首字母||按首字母"));

        Assert.Equal(ArchiveRuleKind.FirstLetter, rule.Kind);
        Assert.Equal("按首字母", rule.TargetSubDirectory);
    }

    /// <summary>「其它」及其别名「兜底」都解析为兜底规则。</summary>
    [Theory]
    [InlineData("其它||其它")]
    [InlineData("兜底||其它")]
    public void ParseRules_ParsesFallbackRule(string text)
    {
        var rule = Assert.Single(Parse(text));

        Assert.Equal(ArchiveRuleKind.Fallback, rule.Kind);
        Assert.Equal("其它", rule.TargetSubDirectory);
    }

    /// <summary>未知的规则类型报错，错误信息里指出行号与类型名，其余合法规则仍然保留。</summary>
    [Fact]
    public void ParseRules_WithUnknownKind_ReturnsError()
    {
        var result = AutoArchiverService.ParseRules("扩展名|.jpg|图片\r\n体积|10|大文件");

        Assert.False(result.Success);
        var rule = Assert.Single(result.Rules);
        Assert.Equal(ArchiveRuleKind.Extension, rule.Kind);

        var error = Assert.Single(result.Errors);
        Assert.Contains("第 2 行", error);
        Assert.Contains("体积", error);
    }

    /// <summary>缺少「|」分隔符（列数不足）时报错。</summary>
    [Theory]
    [InlineData("扩展名")]
    [InlineData("jpg")]
    public void ParseRules_WithMissingColumns_ReturnsError(string text)
    {
        var result = AutoArchiverService.ParseRules(text);

        Assert.False(result.Success);
        Assert.Empty(result.Rules);
        Assert.Contains("格式应为", Assert.Single(result.Errors));
    }

    /// <summary>扩展名规则的参数列为空时报错。</summary>
    [Fact]
    public void ParseRules_WithEmptyExtensionParameter_ReturnsError()
    {
        var result = AutoArchiverService.ParseRules("扩展名||图片");

        Assert.False(result.Success);
        Assert.Empty(result.Rules);
        Assert.Contains("必须填写扩展名", Assert.Single(result.Errors));
    }

    /// <summary>日期规则的参数列为空时报错。</summary>
    [Fact]
    public void ParseRules_WithEmptyDateFormat_ReturnsError()
    {
        var result = AutoArchiverService.ParseRules("日期||按日期");

        Assert.False(result.Success);
        Assert.Empty(result.Rules);
        Assert.Contains("必须填写日期格式", Assert.Single(result.Errors));
    }

    /// <summary>日期格式字符串非法（末尾是孤立的转义符）时报错。</summary>
    [Theory]
    [InlineData("yyyy\\")]
    [InlineData("yyyy-MM-dd HH:mm\\")]
    public void ParseRules_WithInvalidDateFormat_ReturnsError(string format)
    {
        var result = AutoArchiverService.ParseRules("日期|" + format + "|按日期");

        Assert.False(result.Success);
        Assert.Empty(result.Rules);
        Assert.Contains("不合法", Assert.Single(result.Errors));
    }

    /// <summary>兜底规则只能出现一次，第二条报错且不会进入规则表。</summary>
    [Fact]
    public void ParseRules_WithMultipleFallbackRules_ReturnsError()
    {
        var result = AutoArchiverService.ParseRules("其它||其它\r\n其它||杂物");

        Assert.False(result.Success);
        var rule = Assert.Single(result.Rules);
        Assert.Equal("其它", rule.TargetSubDirectory);
        Assert.Contains("只能出现一次", Assert.Single(result.Errors));
    }

    /// <summary>兜底规则无论写在第几行，都会被排到最后匹配。</summary>
    [Fact]
    public void ParseRules_OrdersFallbackRuleLast()
    {
        var rules = Parse("其它||其它\r\n日期|yyyy-MM|按日期\r\n扩展名|.jpg|图片");

        Assert.Equal(3, rules.Count);
        Assert.Equal(ArchiveRuleKind.Date, rules[0].Kind);
        Assert.Equal(ArchiveRuleKind.Extension, rules[1].Kind);
        Assert.Equal(ArchiveRuleKind.Fallback, rules[2].Kind);
    }

    /// <summary>只有注释（没有任何规则）时给出「没有解析到任何规则」的错误。</summary>
    [Fact]
    public void ParseRules_WithOnlyComments_ReturnsError()
    {
        var result = AutoArchiverService.ParseRules("# 只是注释\r\n\r\n# 还是注释");

        Assert.False(result.Success);
        Assert.Empty(result.Rules);
        Assert.Contains("没有解析到任何规则。", result.Errors);
    }

    // ------------------------------------------------------------------
    // SplitExtensions
    // ------------------------------------------------------------------

    /// <summary>扩展名列表支持分号 / 逗号 / 竖线 / 空格分隔，并自动补前导点、去掉空白。</summary>
    [Fact]
    public void SplitExtensions_SplitsOnEverySeparatorAndAddsLeadingDot()
    {
        Assert.Equal(new[] { ".jpg", ".png" }, AutoArchiverService.SplitExtensions(" jpg ; .png "));
        Assert.Equal(
            new[] { ".jpg", ".png", ".gif", ".bmp" },
            AutoArchiverService.SplitExtensions("jpg,png|gif bmp"));
    }

    /// <summary>扩展名去重不区分大小写，并保留第一次出现的写法。</summary>
    [Fact]
    public void SplitExtensions_RemovesDuplicatesIgnoringCase()
    {
        Assert.Equal(new[] { ".JPG" }, AutoArchiverService.SplitExtensions(".JPG;jpg;.Jpg"));
    }

    /// <summary>空文本或只有分隔符时返回空集合，不抛异常。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    public void SplitExtensions_WithEmptyText_ReturnsEmpty(string? extensions)
    {
        Assert.Empty(AutoArchiverService.SplitExtensions(extensions));
    }

    // ------------------------------------------------------------------
    // ResolveTargetSubDirectory
    // ------------------------------------------------------------------

    /// <summary>扩展名规则未指定目标目录时，用扩展名（不含点）作为目录名。</summary>
    [Fact]
    public void ResolveTargetSubDirectory_ExtensionRuleWithoutTarget_UsesExtensionWithoutDot()
    {
        var rule = new ArchiveRule { Kind = ArchiveRuleKind.Extension, Extensions = ".jpg" };

        Assert.Equal(
            "jpg",
            AutoArchiverService.ResolveTargetSubDirectory(rule, "photo.jpg", FixedLastWriteTime));
        Assert.Equal(
            "png",
            AutoArchiverService.ResolveTargetSubDirectory(rule, "icon.png", FixedLastWriteTime));
    }

    /// <summary>扩展名规则指定了目标目录时，以指定目录为准而不是扩展名。</summary>
    [Fact]
    public void ResolveTargetSubDirectory_ExtensionRuleWithTarget_UsesConfiguredDirectory()
    {
        var rule = new ArchiveRule
        {
            Kind = ArchiveRuleKind.Extension,
            Extensions = ".jpg",
            TargetSubDirectory = "图片"
        };

        Assert.Equal(
            "图片",
            AutoArchiverService.ResolveTargetSubDirectory(rule, "photo.jpg", FixedLastWriteTime));
    }

    /// <summary>文件没有扩展名且未指定目标目录时归入「无扩展名」。</summary>
    [Fact]
    public void ResolveTargetSubDirectory_ExtensionRuleWithoutExtension_UsesPlaceholderFolder()
    {
        var rule = new ArchiveRule { Kind = ArchiveRuleKind.Extension, Extensions = "readme" };

        Assert.Equal(
            "无扩展名",
            AutoArchiverService.ResolveTargetSubDirectory(rule, "README", FixedLastWriteTime));
    }

    /// <summary>日期规则按固定修改时间生成目录，并与配置的目标目录拼接。</summary>
    [Theory]
    [InlineData("yyyy-MM", "2026-10")]
    [InlineData("yyyy", "2026")]
    [InlineData("yyyy-MM-dd", "2026-10-02")]
    public void ResolveTargetSubDirectory_DateRule_CombinesTargetAndFormattedDate(string format, string expected)
    {
        var rule = new ArchiveRule
        {
            Kind = ArchiveRuleKind.Date,
            DateFormat = format,
            TargetSubDirectory = "按日期"
        };

        Assert.Equal(
            Path.Combine("按日期", expected),
            AutoArchiverService.ResolveTargetSubDirectory(rule, "any.txt", FixedLastWriteTime));
    }

    /// <summary>日期规则未指定目标目录时只用日期目录，日期格式为空时回退到 yyyy-MM。</summary>
    [Fact]
    public void ResolveTargetSubDirectory_DateRuleWithoutTarget_UsesDateFolderOnly()
    {
        var rule = new ArchiveRule { Kind = ArchiveRuleKind.Date, DateFormat = "yyyy-MM" };
        var withoutFormat = new ArchiveRule { Kind = ArchiveRuleKind.Date, DateFormat = string.Empty };

        Assert.Equal("2026-10", AutoArchiverService.ResolveTargetSubDirectory(rule, "any.txt", FixedLastWriteTime));
        Assert.Equal("2026-10", AutoArchiverService.ResolveTargetSubDirectory(withoutFormat, "any.txt", FixedLastWriteTime));
    }

    /// <summary>首字母规则取文件名首字符的大写；非字母（数字 / 下划线 / 符号）归入「#」。</summary>
    [Theory]
    [InlineData("apple.txt", "A")]
    [InlineData("Banana.txt", "B")]
    [InlineData("1note.txt", "#")]
    [InlineData("_note.txt", "#")]
    [InlineData("-x.txt", "#")]
    public void ResolveTargetSubDirectory_FirstLetterRule_UsesUpperLetterOrHash(string fileName, string expectedLetter)
    {
        var rule = new ArchiveRule { Kind = ArchiveRuleKind.FirstLetter, TargetSubDirectory = "按首字母" };

        Assert.Equal(
            Path.Combine("按首字母", expectedLetter),
            AutoArchiverService.ResolveTargetSubDirectory(rule, fileName, FixedLastWriteTime));
    }

    /// <summary>首字母规则未指定目标目录时只用字母目录名，常量 OtherLetterFolder 固定为「#」。</summary>
    [Fact]
    public void ResolveTargetSubDirectory_FirstLetterRuleWithoutTarget_UsesLetterFolderOnly()
    {
        var rule = new ArchiveRule { Kind = ArchiveRuleKind.FirstLetter };

        Assert.Equal("A", AutoArchiverService.ResolveTargetSubDirectory(rule, "apple.txt", FixedLastWriteTime));
        Assert.Equal("#", AutoArchiverService.OtherLetterFolder);
    }

    /// <summary>兜底规则未指定目标目录时用默认目录「其它」，指定时以指定目录为准。</summary>
    [Fact]
    public void ResolveTargetSubDirectory_FallbackRule_UsesDefaultFolder()
    {
        var rule = new ArchiveRule { Kind = ArchiveRuleKind.Fallback };
        var custom = new ArchiveRule { Kind = ArchiveRuleKind.Fallback, TargetSubDirectory = "杂物" };

        Assert.Equal(
            AutoArchiverService.DefaultFallbackFolder,
            AutoArchiverService.ResolveTargetSubDirectory(rule, "any.txt", FixedLastWriteTime));
        Assert.Equal("其它", AutoArchiverService.ResolveTargetSubDirectory(rule, "any.txt", FixedLastWriteTime));
        Assert.Equal("杂物", AutoArchiverService.ResolveTargetSubDirectory(custom, "any.txt", FixedLastWriteTime));
    }

    // ------------------------------------------------------------------
    // MatchRule
    // ------------------------------------------------------------------

    /// <summary>扩展名规则命中时返回该规则，扩展名比较不区分大小写。</summary>
    [Fact]
    public void MatchRule_ExtensionRule_MatchesIgnoringCase()
    {
        var rules = Parse("扩展名|jpg;.png|图片");

        Assert.Same(rules[0], AutoArchiverService.MatchRule(rules, "PHOTO.JPG"));
        Assert.Same(rules[0], AutoArchiverService.MatchRule(rules, "icon.png"));
    }

    /// <summary>日期 / 首字母规则对任何文件都命中。</summary>
    [Theory]
    [InlineData(ArchiveRuleKind.Date)]
    [InlineData(ArchiveRuleKind.FirstLetter)]
    public void MatchRule_DateOrFirstLetterRule_MatchesAnyFile(ArchiveRuleKind kind)
    {
        var rule = new ArchiveRule { Kind = kind, DateFormat = "yyyy-MM", TargetSubDirectory = "目标" };

        Assert.Same(rule, AutoArchiverService.MatchRule(new[] { rule }, "whatever.txt"));
    }

    /// <summary>扩展名都没命中时返回兜底规则，命中扩展名时仍优先返回扩展名规则。</summary>
    [Fact]
    public void MatchRule_WhenNoRuleMatches_ReturnsFallbackRule()
    {
        var rules = Parse("扩展名|.jpg|图片\r\n其它||其它");

        var fallback = AutoArchiverService.MatchRule(rules, "note.txt");

        Assert.NotNull(fallback);
        Assert.Equal(ArchiveRuleKind.Fallback, fallback.Kind);
        Assert.Equal(ArchiveRuleKind.Extension, AutoArchiverService.MatchRule(rules, "photo.jpg")!.Kind);
    }

    /// <summary>没有兜底规则且扩展名不命中时返回 null（由预览标记为「无匹配规则」）。</summary>
    [Fact]
    public void MatchRule_WithoutFallbackRule_ReturnsNull()
    {
        var rules = Parse("扩展名|.jpg|图片");

        Assert.Null(AutoArchiverService.MatchRule(rules, "note.txt"));
        Assert.NotNull(AutoArchiverService.MatchRule(rules, "photo.jpg"));
    }

    // ------------------------------------------------------------------
    // BuildPlan
    // ------------------------------------------------------------------

    /// <summary>源目录不存在时返回错误说明，不抛异常。</summary>
    [Fact]
    public void BuildPlan_WhenSourceDirectoryMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("不存在的源目录");

        var plan = new AutoArchiverService().BuildPlan(
            CreateOptions(missing, ws.CreateDirectory("target"), "其它||其它"));

        Assert.NotNull(plan.Error);
        Assert.Contains("源目录", plan.Error!);
        Assert.Empty(plan.Items);
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>目标目录为空时返回错误说明。</summary>
    [Fact]
    public void BuildPlan_WhenTargetDirectoryIsEmpty_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");

        var plan = new AutoArchiverService().BuildPlan(CreateOptions(source, string.Empty, "其它||其它"));

        Assert.NotNull(plan.Error);
        Assert.Contains("目标目录", plan.Error!);
        Assert.Empty(plan.Items);
    }

    /// <summary>没有配置任何规则时返回错误说明。</summary>
    [Fact]
    public void BuildPlan_WithoutRules_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        CreateFile(ws, Path.Combine("source", "photo.jpg"));

        var options = new ArchiveOptions
        {
            SourceDirectory = source,
            TargetDirectory = ws.CreateDirectory("target")
        };

        var plan = new AutoArchiverService().BuildPlan(options);

        Assert.NotNull(plan.Error);
        Assert.Contains("分类规则", plan.Error!);
        Assert.Empty(plan.Items);
    }

    /// <summary>按规则生成目标路径：目标根目录 + 子目录 + 原文件名，并标记为可执行。</summary>
    [Fact]
    public void BuildPlan_GeneratesTargetPathWithRuleSubDirectory()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "photo.jpg"), "img");

        var plan = new AutoArchiverService().BuildPlan(CreateOptions(source, target, "扩展名|.jpg|图片"));

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.True(item.CanApply);
        Assert.Equal(ArchiveRuleKind.Extension, item.RuleKind);
        Assert.Equal(Path.Combine(target, "图片", "photo.jpg"), item.TargetPath, ignoreCase: true);
        Assert.StartsWith(Path.Combine(target, "图片"), item.TargetPath);
        Assert.EndsWith("photo.jpg", item.TargetPath);
        Assert.Equal(1, plan.ReadyCount);
        Assert.Equal(0, plan.SkippedCount);
    }

    /// <summary>目标已存在同名文件且未启用覆盖时，条目标记为不可执行并给出原因。</summary>
    [Fact]
    public void BuildPlan_WhenTargetFileExistsWithoutOverwrite_MarksNotApplicable()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "photo.jpg"), "new");
        CreateFile(ws, Path.Combine("target", "图片", "photo.jpg"), "old");

        var plan = new AutoArchiverService().BuildPlan(CreateOptions(source, target, "扩展名|.jpg|图片"));

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.False(item.CanApply);
        Assert.Contains("覆盖", item.Message);
        Assert.Equal(0, plan.ReadyCount);
        Assert.Equal(1, plan.SkippedCount);
    }

    /// <summary>启用覆盖后，即使目标已存在同名文件也标记为可执行。</summary>
    [Fact]
    public void BuildPlan_WhenTargetFileExistsWithOverwrite_AllowsApply()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "photo.jpg"), "new");
        CreateFile(ws, Path.Combine("target", "图片", "photo.jpg"), "old");

        var plan = new AutoArchiverService().BuildPlan(
            CreateOptions(source, target, "扩展名|.jpg|图片", overwrite: true));

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.True(item.CanApply);
        Assert.Equal(string.Empty, item.Message);
        Assert.Equal(1, plan.ReadyCount);
        Assert.Equal(0, plan.SkippedCount);
    }

    /// <summary>IncludeSubDirectories 为 false 时不扫描子目录中的文件。</summary>
    [Fact]
    public void BuildPlan_WithoutSubDirectories_IgnoresNestedFiles()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "top.txt"));
        CreateFile(ws, Path.Combine("source", "sub", "inner.txt"));

        var withoutSubDirectories = new AutoArchiverService().BuildPlan(
            CreateOptions(source, target, "其它||其它", includeSubDirectories: false));
        var withSubDirectories = new AutoArchiverService().BuildPlan(
            CreateOptions(source, target, "其它||其它", includeSubDirectories: true));

        var item = Assert.Single(withoutSubDirectories.Items);
        Assert.Equal("top.txt", Path.GetFileName(item.SourcePath));
        Assert.Equal(2, withSubDirectories.Items.Count);
    }

    // ------------------------------------------------------------------
    // Apply
    // ------------------------------------------------------------------

    /// <summary>Move 模式真实移动文件：源文件消失，目标文件存在且内容一致。</summary>
    [Fact]
    public void Apply_MoveMovesFileToTargetDirectory()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        var sourceFile = CreateFile(ws, Path.Combine("source", "photo.jpg"), "image-bytes");

        var options = CreateOptions(source, target, "扩展名|.jpg|图片");
        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.ReadyCount);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);

        var targetFile = ws.PathOf(Path.Combine("target", "图片", "photo.jpg"));
        Assert.True(File.Exists(targetFile), "移动后目标文件应存在");
        Assert.False(File.Exists(sourceFile), "移动后源文件不应存在");
        Assert.Equal("image-bytes", File.ReadAllText(targetFile));
    }

    /// <summary>Copy 模式保留源文件，同时在目标目录生成副本。</summary>
    [Fact]
    public void Apply_CopyKeepsSourceFile()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        var sourceFile = CreateFile(ws, Path.Combine("source", "photo.jpg"), "image-bytes");

        var options = CreateOptions(source, target, "扩展名|.jpg|图片", action: ArchiveAction.Copy);
        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.ReadyCount);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(sourceFile), "复制后源文件应保留");
        var targetFile = ws.PathOf(Path.Combine("target", "图片", "photo.jpg"));
        Assert.True(File.Exists(targetFile), "复制后目标文件应存在");
        Assert.Equal("image-bytes", File.ReadAllText(targetFile));
    }

    /// <summary>预览中 CanApply 为 false 的条目会被跳过，不产生结果项也不改动文件。</summary>
    [Fact]
    public void Apply_SkipsItemsThatCannotApply()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        var occupiedSource = CreateFile(ws, Path.Combine("source", "a.jpg"), "new-a");
        var movableSource = CreateFile(ws, Path.Combine("source", "b.jpg"), "new-b");
        var occupiedTarget = CreateFile(ws, Path.Combine("target", "图片", "a.jpg"), "old-a");

        var options = CreateOptions(source, target, "扩展名|.jpg|图片");
        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(1, plan.SkippedCount);

        var result = service.Apply(plan, options);

        var applied = Assert.Single(result.Items);
        Assert.True(applied.Success);
        Assert.False(File.Exists(movableSource), "可执行条目应被移动");
        Assert.True(File.Exists(occupiedSource), "不可执行条目不应被改动");
        Assert.Equal("old-a", File.ReadAllText(occupiedTarget));
        Assert.True(File.Exists(ws.PathOf(Path.Combine("target", "图片", "b.jpg"))));
    }

    /// <summary>源文件在预览之后消失时返回失败项，不向外抛异常。</summary>
    [Fact]
    public void Apply_WhenSourceFileIsMissing_ReturnsFailedItemWithoutThrowing()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        var missing = ws.PathOf(Path.Combine("source", "gone.jpg"));
        var targetPath = ws.PathOf(Path.Combine("target", "图片", "gone.jpg"));

        var plan = new ArchivePlan
        {
            Items = new List<ArchivePlanItem>
            {
                new()
                {
                    SourcePath = missing,
                    TargetPath = targetPath,
                    CanApply = true
                }
            }
        };

        var result = new AutoArchiverService().Apply(plan, CreateOptions(source, target, "扩展名|.jpg|图片"));

        var item = Assert.Single(result.Items);
        Assert.False(item.Success);
        Assert.False(string.IsNullOrWhiteSpace(item.Error), "失败项应带有原因");
        Assert.Equal(1, result.FailedCount);
        Assert.False(result.Success);
        Assert.False(File.Exists(targetPath), "源文件缺失时不应生成目标文件");
    }

    /// <summary>CleanEmptyFolders 为 true 时，移动文件后源目录下的空子目录被清理。</summary>
    [Fact]
    public void Apply_WhenCleanEmptyFoldersEnabled_RemovesEmptySourceFolders()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "sub", "data.txt"), "data");
        ws.CreateDirectory(Path.Combine("source", "emptydir"));

        var options = CreateOptions(
            source,
            target,
            "其它||其它",
            includeSubDirectories: true,
            cleanEmptyFolders: true);

        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.ReadyCount);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.True(result.RemovedEmptyFolderCount >= 1, "应至少清理一个空目录");
        Assert.False(Directory.Exists(ws.PathOf(Path.Combine("source", "sub"))), "移动后空掉的子目录应被清理");
        Assert.False(Directory.Exists(ws.PathOf(Path.Combine("source", "emptydir"))), "原有的空目录应被清理");
        Assert.True(Directory.Exists(source), "源目录本身不会被删除");
        Assert.True(File.Exists(ws.PathOf(Path.Combine("target", "其它", "data.txt"))));
    }

    /// <summary>未启用 CleanEmptyFolders 时保留源目录下的空子目录。</summary>
    [Fact]
    public void Apply_WhenCleanEmptyFoldersDisabled_KeepsEmptySourceFolders()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "sub", "data.txt"), "data");

        var options = CreateOptions(source, target, "其它||其它", includeSubDirectories: true);
        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(0, result.RemovedEmptyFolderCount);
        Assert.True(Directory.Exists(ws.PathOf(Path.Combine("source", "sub"))), "未启用清理时子目录应保留");
    }

    // ------------------------------------------------------------------
    // BuildReport
    // ------------------------------------------------------------------

    /// <summary>BuildReport 为可执行条目输出 [执行]、为跳过条目输出 [跳过] 与原因。</summary>
    [Fact]
    public void BuildReport_ContainsExecutionFlagsAndSkipReason()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var target = ws.CreateDirectory("target");
        CreateFile(ws, Path.Combine("source", "a.jpg"), "new-a");
        CreateFile(ws, Path.Combine("target", "图片", "a.jpg"), "old-a");
        CreateFile(ws, Path.Combine("source", "b.jpg"), "new-b");

        var plan = new AutoArchiverService().BuildPlan(CreateOptions(source, target, "扩展名|.jpg|图片"));
        var report = AutoArchiverService.BuildReport(plan);

        Assert.Contains("[执行]", report);
        Assert.Contains("[跳过]", report);
        Assert.Contains("覆盖", report);
        Assert.Contains(Path.Combine("图片", "a.jpg"), report);
    }
}
