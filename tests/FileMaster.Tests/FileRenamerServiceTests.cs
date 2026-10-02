using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// FileRenamerService 单元测试。
/// 覆盖：BuildNewName 的文本 / 正则替换（含大小写敏感与 $1 分组）、删除字符、前缀后缀、
/// 序号（起始值、补零位数、分隔符、插入位置）、日期（格式与取值来源）、扩展名保护与统一扩展名、
/// 非法字符清理与空名称；Validate 的规则校验；BuildPlan 的目标冲突 / 名称未变化 / 预览内重名 /
/// Target 过滤；Apply 的真实重命名、跳过不可执行条目与源路径缺失时的失败项。
/// 所有用例都在各自的临时目录中执行，不依赖真实 U 盘、网络路径或用户目录。
/// </summary>
public sealed class FileRenamerServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>固定创建时间（2024-01-02 03:04:05），用于日期规则断言。</summary>
    private static readonly DateTime FixedCreationTime = new(2024, 1, 2, 3, 4, 5);

    /// <summary>固定修改时间（2024-03-15 16:17:18），用于日期规则断言。</summary>
    private static readonly DateTime FixedLastWriteTime = new(2024, 3, 15, 16, 17, 18);

    /// <summary>调用纯函数 BuildNewName，统一使用上面两个固定时间。</summary>
    private static string BuildName(RenameOptions options, string originalName, int sequenceIndex = 0)
        => FileRenamerService.BuildNewName(options, originalName, sequenceIndex, FixedCreationTime, FixedLastWriteTime);

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

    // ------------------------------------------------------------------
    // BuildNewName：查找替换
    // ------------------------------------------------------------------

    /// <summary>文本替换默认区分大小写：只有大小写完全一致的内容才会被替换。</summary>
    [Fact]
    public void BuildNewName_TextReplace_IsCaseSensitiveByDefault()
    {
        var options = new RenameOptions { FindText = "abc", ReplaceText = "X" };

        Assert.Equal("X.txt", BuildName(options, "abc.txt"));
        Assert.Equal("ABC.txt", BuildName(options, "ABC.txt"));
    }

    /// <summary>把 CaseSensitive 设为 false 后，文本替换不区分大小写。</summary>
    [Fact]
    public void BuildNewName_TextReplace_WhenCaseInsensitive_ReplacesAnyCase()
    {
        var options = new RenameOptions
        {
            FindText = "abc",
            ReplaceText = "X",
            CaseSensitive = false
        };

        Assert.Equal("X.txt", BuildName(options, "ABC.txt"));
    }

    /// <summary>文本替换会替换名称中所有出现的位置，而不是只替换第一处。</summary>
    [Fact]
    public void BuildNewName_TextReplace_ReplacesAllOccurrences()
    {
        var options = new RenameOptions { FindText = "a", ReplaceText = "o" };

        Assert.Equal("bobol.txt", BuildName(options, "babal.txt"));
    }

    /// <summary>正则替换支持 $1 / $2 分组引用。</summary>
    [Fact]
    public void BuildNewName_RegexReplace_SupportsGroupReference()
    {
        var options = new RenameOptions
        {
            UseRegex = true,
            FindText = @"(\d+)-(\d+)",
            ReplaceText = "$2-$1"
        };

        Assert.Equal("34-12.txt", BuildName(options, "12-34.txt"));
    }

    /// <summary>正则替换同样遵循 CaseSensitive：默认区分大小写，设为 false 后忽略大小写。</summary>
    [Theory]
    [InlineData(true, "IMG001.txt")]
    [InlineData(false, "pic001.txt")]
    public void BuildNewName_RegexReplace_RespectsCaseSensitivity(bool caseSensitive, string expected)
    {
        var options = new RenameOptions
        {
            UseRegex = true,
            FindText = "^img",
            ReplaceText = "pic",
            CaseSensitive = caseSensitive
        };

        Assert.Equal(expected, BuildName(options, "IMG001.txt"));
    }

    /// <summary>RemoveChars 按字符逐个删除（空格也属于要删除的字符）。</summary>
    [Fact]
    public void BuildNewName_RemoveChars_DeletesEveryListedCharacter()
    {
        var options = new RenameOptions { RemoveChars = "_- " };

        Assert.Equal("abcd.txt", BuildName(options, "a-b_c d.txt"));
    }

    // ------------------------------------------------------------------
    // BuildNewName：前缀 / 后缀
    // ------------------------------------------------------------------

    /// <summary>前缀加在名称主体之前、后缀加在扩展名之前，两者可以同时生效。</summary>
    [Fact]
    public void BuildNewName_PrefixAndSuffix_WrapBodyAndKeepExtension()
    {
        var options = new RenameOptions { Prefix = "pre-", Suffix = "-v2" };

        Assert.Equal("pre-file-v2.txt", BuildName(options, "file.txt"));
    }

    // ------------------------------------------------------------------
    // BuildNewName：序号
    // ------------------------------------------------------------------

    /// <summary>序号 = 起始值 + 下标，并按位数补零（超出位数不截断）。</summary>
    [Theory]
    [InlineData(1, 3, 0, "photo_001.jpg")]
    [InlineData(5, 2, 0, "photo_05.jpg")]
    [InlineData(10, 3, 2, "photo_012.jpg")]
    [InlineData(1, 1, 9, "photo_10.jpg")]
    public void BuildNewName_Sequence_UsesStartValueDigitsAndIndex(int start, int digits, int index, string expected)
    {
        var options = new RenameOptions { SequenceStart = start, SequenceDigits = digits };

        Assert.Equal(expected, BuildName(options, "photo.jpg", index));
    }

    /// <summary>序号的分隔符与插入位置（前缀 / 后缀）都生效，分隔符为空时不额外插入字符。</summary>
    [Theory]
    [InlineData(RenameAffixPosition.Suffix, "-", "photo-01.jpg")]
    [InlineData(RenameAffixPosition.Prefix, "-", "01-photo.jpg")]
    [InlineData(RenameAffixPosition.Suffix, "", "photo01.jpg")]
    [InlineData(RenameAffixPosition.Prefix, "", "01photo.jpg")]
    public void BuildNewName_Sequence_HonorsSeparatorAndPosition(
        RenameAffixPosition position,
        string separator,
        string expected)
    {
        var options = new RenameOptions
        {
            SequenceStart = 1,
            SequenceDigits = 2,
            SequenceSeparator = separator,
            SequencePosition = position
        };

        Assert.Equal(expected, BuildName(options, "photo.jpg"));
    }

    // ------------------------------------------------------------------
    // BuildNewName：日期
    // ------------------------------------------------------------------

    /// <summary>日期规则的默认来源是修改时间。</summary>
    [Fact]
    public void BuildNewName_Date_UsesLastWriteTimeByDefault()
    {
        var options = new RenameOptions { DateFormat = "yyyyMMdd" };

        Assert.Equal("photo_20240315.jpg", BuildName(options, "photo.jpg"));
    }

    /// <summary>把 DateSource 设为 CreationTime 后，日期取创建时间。</summary>
    [Fact]
    public void BuildNewName_Date_WhenSourceIsCreationTime_UsesCreationTime()
    {
        var options = new RenameOptions
        {
            DateFormat = "yyyyMMdd",
            DateSource = RenameDateSource.CreationTime
        };

        Assert.Equal("photo_20240102.jpg", BuildName(options, "photo.jpg"));
    }

    /// <summary>日期按传入的格式字符串输出。</summary>
    [Theory]
    [InlineData("yyyyMMdd", "20240315")]
    [InlineData("yyyy-MM-dd", "2024-03-15")]
    [InlineData("HHmmss", "161718")]
    public void BuildNewName_Date_HonorsFormat(string format, string expected)
    {
        var options = new RenameOptions { DateFormat = format };

        Assert.Equal("photo_" + expected + ".jpg", BuildName(options, "photo.jpg"));
    }

    // ------------------------------------------------------------------
    // BuildNewName：扩展名
    // ------------------------------------------------------------------

    /// <summary>默认保护扩展名：规则只作用于名称主体，主体里的 jpg 不会被换成 png。</summary>
    [Fact]
    public void BuildNewName_KeepExtensionDefault_ProtectsExtensionFromRules()
    {
        var options = new RenameOptions { FindText = "jpg", ReplaceText = "png" };

        Assert.Equal("photo.jpg", BuildName(options, "photo.jpg"));
    }

    /// <summary>KeepExtension 为 false 时不再剥离扩展名，规则作用于完整名称。</summary>
    [Fact]
    public void BuildNewName_KeepExtensionFalse_AppliesRulesToWholeName()
    {
        var options = new RenameOptions
        {
            KeepExtension = false,
            FindText = ".jpg",
            ReplaceText = ""
        };

        Assert.Equal("photo", BuildName(options, "photo.jpg"));
    }

    /// <summary>NewExtension 统一扩展名：自动补前导点并去掉首尾空白。</summary>
    [Theory]
    [InlineData("txt", "photo.txt")]
    [InlineData(".md", "photo.md")]
    [InlineData("  log  ", "photo.log")]
    public void BuildNewName_NewExtension_NormalizesLeadingDotAndTrims(string newExtension, string expected)
    {
        var options = new RenameOptions { NewExtension = newExtension };

        Assert.Equal(expected, BuildName(options, "photo.jpg"));
    }

    /// <summary>KeepExtension 为 false 又不剥离原扩展名时，新扩展名直接追加在完整名称之后。</summary>
    [Fact]
    public void BuildNewName_NewExtensionWithKeepExtensionFalse_AppendsAfterFullName()
    {
        var options = new RenameOptions { KeepExtension = false, NewExtension = "png" };

        Assert.Equal("photo.jpg.png", BuildName(options, "photo.jpg"));
    }

    // ------------------------------------------------------------------
    // BuildNewName：非法字符与空名称
    // ------------------------------------------------------------------

    /// <summary>非法文件名字符（如 &lt; 与 :）会被替换成下划线。</summary>
    [Fact]
    public void BuildNewName_InvalidCharacters_AreReplacedWithUnderscore()
    {
        var options = new RenameOptions { Prefix = "a<b:c" };

        Assert.Equal("a_b_cfile.txt", BuildName(options, "file.txt"));
    }

    /// <summary>目录分隔符也会被替换成下划线，避免生成的新名称造成路径穿越。</summary>
    [Fact]
    public void BuildNewName_PathSeparators_AreReplacedWithUnderscore()
    {
        var options = new RenameOptions { Prefix = "sub/dir" };

        Assert.Equal("sub_dirfile.txt", BuildName(options, "file.txt"));
    }

    /// <summary>名称末尾的点会被去掉，避免生成 Windows 无法创建的结尾点名称。</summary>
    [Fact]
    public void BuildNewName_TrailingDotsAreTrimmed()
    {
        var options = new RenameOptions { Suffix = "..." };

        Assert.Equal("file.txt", BuildName(options, "file.txt"));
    }

    /// <summary>规则把名称主体清空时返回空字符串，交由预览层判定为非法。</summary>
    [Fact]
    public void BuildNewName_WhenRulesRemoveEverything_ReturnsEmptyString()
    {
        var options = new RenameOptions { FindText = "abc", ReplaceText = "" };

        Assert.Equal(string.Empty, BuildName(options, "abc.txt"));
    }

    // ------------------------------------------------------------------
    // Validate
    // ------------------------------------------------------------------

    /// <summary>没有配置任何规则时给出错误说明。</summary>
    [Fact]
    public void Validate_WithoutAnyRule_ReturnsError()
    {
        Assert.NotNull(FileRenamerService.Validate(new RenameOptions()));
    }

    /// <summary>只填了替换内容而没有查找内容，不构成一条规则。</summary>
    [Fact]
    public void Validate_WithReplaceTextOnly_ReturnsError()
    {
        Assert.NotNull(FileRenamerService.Validate(new RenameOptions { ReplaceText = "x" }));
    }

    /// <summary>正则模式下表达式不合法时给出错误说明。</summary>
    [Theory]
    [InlineData("[")]
    [InlineData("(unclosed")]
    [InlineData("\\")]
    public void Validate_WithInvalidRegex_ReturnsError(string pattern)
    {
        var options = new RenameOptions { UseRegex = true, FindText = pattern };

        Assert.NotNull(FileRenamerService.Validate(options));
    }

    /// <summary>配置了序号且位数超出 1-10 时给出错误说明。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validate_WithSequenceDigitsOutOfRange_ReturnsError(int digits)
    {
        var options = new RenameOptions { SequenceStart = 1, SequenceDigits = digits };

        Assert.NotNull(FileRenamerService.Validate(options));
    }

    /// <summary>位数在 1-10 之间时规则合法。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void Validate_WithSequenceDigitsInRange_ReturnsNull(int digits)
    {
        var options = new RenameOptions { SequenceStart = 1, SequenceDigits = digits };

        Assert.Null(FileRenamerService.Validate(options));
    }

    /// <summary>没有启用序号（SequenceStart 为 null）时，位数越界不影响校验结果。</summary>
    [Fact]
    public void Validate_SequenceDigitsOutOfRangeWithoutSequenceRule_ReturnsNull()
    {
        var options = new RenameOptions { Prefix = "x", SequenceDigits = 0 };

        Assert.Null(FileRenamerService.Validate(options));
    }

    /// <summary>各类正常规则都通过校验。</summary>
    [Fact]
    public void Validate_WithValidRules_ReturnsNull()
    {
        Assert.Null(FileRenamerService.Validate(new RenameOptions { Prefix = "x" }));
        Assert.Null(FileRenamerService.Validate(new RenameOptions { Suffix = "-v2" }));
        Assert.Null(FileRenamerService.Validate(new RenameOptions { RemoveChars = "_-" }));
        Assert.Null(FileRenamerService.Validate(new RenameOptions { FindText = "a", ReplaceText = "b" }));
        Assert.Null(FileRenamerService.Validate(new RenameOptions
        {
            UseRegex = true,
            FindText = @"^(?<n>\d+)$",
            ReplaceText = "${n}"
        }));
        Assert.Null(FileRenamerService.Validate(new RenameOptions { SequenceStart = 1, SequenceDigits = 3 }));
        Assert.Null(FileRenamerService.Validate(new RenameOptions { DateFormat = "yyyyMMdd" }));
        Assert.Null(FileRenamerService.Validate(new RenameOptions { NewExtension = ".txt" }));
    }

    /// <summary>日期格式不合法（末尾是孤立的转义符）时给出错误说明。</summary>
    [Fact]
    public void Validate_WithInvalidDateFormat_ReturnsError()
    {
        // "yyyy\"会让 DateTime.ToString 抛 FormatException
        var options = new RenameOptions { DateFormat = "yyyy\\" };

        Assert.NotNull(FileRenamerService.Validate(options));
    }

    // ------------------------------------------------------------------
    // BuildPlan
    // ------------------------------------------------------------------

    /// <summary>目标名称已被磁盘上的文件占用时标记为 Conflict。</summary>
    [Fact]
    public void BuildPlan_WhenTargetAlreadyExists_MarksConflict()
    {
        using var ws = new TempWorkspace();
        var source = CreateFile(ws, "b.txt");
        CreateFile(ws, "a.txt");

        var options = new RenameOptions { FindText = "b", ReplaceText = "a" };
        var plan = new FileRenamerService().BuildPlan(new[] { source }, options);

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal(RenameItemStatus.Conflict, item.Status);
        Assert.False(item.CanApply);
        Assert.False(string.IsNullOrWhiteSpace(item.Message), "冲突项应有原因说明");
        Assert.Equal(1, plan.ConflictCount);
    }

    /// <summary>规则没有改变名称时标记为 Unchanged，不参与执行。</summary>
    [Fact]
    public void BuildPlan_WhenNameDoesNotChange_MarksUnchanged()
    {
        using var ws = new TempWorkspace();
        var source = CreateFile(ws, "keep.txt");

        var options = new RenameOptions { FindText = "zzz", ReplaceText = "yyy" };
        var plan = new FileRenamerService().BuildPlan(new[] { source }, options);

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal(RenameItemStatus.Unchanged, item.Status);
        Assert.False(item.CanApply);
        Assert.Equal(0, plan.ReadyCount);
        Assert.Equal(1, plan.UnchangedCount);
    }

    /// <summary>预览内两条记录算出同一个目标名称时，后一条标记为 Conflict。</summary>
    [Fact]
    public void BuildPlan_WhenTwoItemsProduceSameTarget_MarksSecondAsConflict()
    {
        using var ws = new TempWorkspace();
        var first = CreateFile(ws, "a1.txt");
        var second = CreateFile(ws, "a2.txt");

        var options = new RenameOptions { RemoveChars = "12" };
        var plan = new FileRenamerService().BuildPlan(new[] { first, second }, options);

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(RenameItemStatus.Ready, plan.Items[0].Status);
        Assert.Equal(RenameItemStatus.Conflict, plan.Items[1].Status);
        Assert.Equal(1, plan.ReadyCount);
    }

    /// <summary>Target 为 Files 时不返回文件夹，即使开启了递归扫描。</summary>
    [Fact]
    public void BuildPlanFromDirectory_TargetFiles_ExcludesDirectories()
    {
        using var ws = new TempWorkspace();
        CreateFile(ws, Path.Combine("sub", "inner.txt"));
        ws.CreateDirectory("empty");

        var options = new RenameOptions
        {
            Target = RenameTarget.Files,
            IncludeSubDirectories = true,
            Prefix = "p_"
        };

        var plan = new FileRenamerService().BuildPlanFromDirectory(ws.Root, options);

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal(RenameItemKind.File, item.Kind);
        Assert.Equal("p_inner.txt", item.NewName);
    }

    /// <summary>Target 为 Folders 时不返回文件。</summary>
    [Fact]
    public void BuildPlan_TargetFolders_ExcludesFiles()
    {
        using var ws = new TempWorkspace();
        var folder = ws.CreateDirectory("docs");
        var file = CreateFile(ws, "note.txt");

        var options = new RenameOptions { Target = RenameTarget.Folders, Prefix = "p_" };
        var plan = new FileRenamerService().BuildPlan(new[] { file, folder }, options);

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal(RenameItemKind.Folder, item.Kind);
        Assert.Equal("p_docs", item.NewName);
    }

    /// <summary>规则生成空名称时标记为 Invalid，并给出空的新名称与目标路径。</summary>
    [Fact]
    public void BuildPlan_WhenRulesProduceEmptyName_MarksItemInvalid()
    {
        using var ws = new TempWorkspace();
        var source = CreateFile(ws, "abc.txt");

        var options = new RenameOptions { FindText = "abc", ReplaceText = "" };
        var plan = new FileRenamerService().BuildPlan(new[] { source }, options);

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal(RenameItemStatus.Invalid, item.Status);
        Assert.Equal(string.Empty, item.NewName);
        Assert.Equal(string.Empty, item.TargetPath);
        Assert.Equal(1, plan.InvalidCount);
    }

    /// <summary>规则不合法时预览直接返回错误，不产生任何条目。</summary>
    [Fact]
    public void BuildPlan_WithInvalidRules_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var source = CreateFile(ws, "a.txt");

        var plan = new FileRenamerService().BuildPlan(new[] { source }, new RenameOptions());

        Assert.NotNull(plan.Error);
        Assert.Empty(plan.Items);
    }

    /// <summary>目录不存在时返回错误说明，而不是抛异常。</summary>
    [Fact]
    public void BuildPlanFromDirectory_WhenDirectoryMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("不存在的目录");

        var plan = new FileRenamerService().BuildPlanFromDirectory(
            missing,
            new RenameOptions { Prefix = "p_" });

        Assert.NotNull(plan.Error);
        Assert.Empty(plan.Items);
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>嵌套文件夹按「深度从深到浅」排列，保证先重命名子目录。</summary>
    [Fact]
    public void BuildPlan_OrdersNestedFoldersDeepestFirst()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("parent");
        var child = ws.CreateDirectory(Path.Combine("parent", "child"));

        var options = new RenameOptions { Target = RenameTarget.Folders, Prefix = "p_" };
        var plan = new FileRenamerService().BuildPlan(new[] { parent, child }, options);

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(child, plan.Items[0].SourcePath, ignoreCase: true);
        Assert.Equal(parent, plan.Items[1].SourcePath, ignoreCase: true);
    }

    // ------------------------------------------------------------------
    // Apply
    // ------------------------------------------------------------------

    /// <summary>执行预览后磁盘上出现新路径、旧路径消失，文件内容保持不变。</summary>
    [Fact]
    public void Apply_RenamesFilesOnDisk()
    {
        using var ws = new TempWorkspace();
        var first = CreateFile(ws, "a.txt", "A");
        var second = CreateFile(ws, "b.txt", "B");

        var service = new FileRenamerService();
        var plan = service.BuildPlan(new[] { first, second }, new RenameOptions { Prefix = "new_" });

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.ReadyCount);

        var result = service.Apply(plan);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);

        var newFirst = ws.PathOf("new_a.txt");
        var newSecond = ws.PathOf("new_b.txt");
        Assert.True(File.Exists(newFirst), "应生成新文件 new_a.txt");
        Assert.True(File.Exists(newSecond), "应生成新文件 new_b.txt");
        Assert.False(File.Exists(first), "原文件 a.txt 不应存在");
        Assert.False(File.Exists(second), "原文件 b.txt 不应存在");
        Assert.Equal("A", File.ReadAllText(newFirst));
    }

    /// <summary>文件夹同样可以被真实重命名，内部文件随之迁移。</summary>
    [Fact]
    public void Apply_RenamesFolderOnDisk()
    {
        using var ws = new TempWorkspace();
        var folder = ws.CreateDirectory("docs");
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "content");

        var options = new RenameOptions { Target = RenameTarget.Folders, Suffix = "_2024" };
        var service = new FileRenamerService();
        var plan = service.BuildPlan(new[] { folder }, options);

        Assert.Null(plan.Error);
        Assert.Equal(1, plan.ReadyCount);

        var result = service.Apply(plan);

        Assert.True(result.Success, result.Summary);
        var renamed = ws.PathOf("docs_2024");
        Assert.True(Directory.Exists(renamed), "应生成新目录 docs_2024");
        Assert.False(Directory.Exists(folder), "原目录 docs 不应存在");
        Assert.True(File.Exists(Path.Combine(renamed, "readme.txt")), "内部文件应随目录一起迁移");
    }

    /// <summary>Unchanged 与 Conflict 的条目会被跳过，只有 Ready 条目真正执行。</summary>
    [Fact]
    public void Apply_SkipsUnchangedAndConflictItems()
    {
        using var ws = new TempWorkspace();
        var readySource = CreateFile(ws, "ready.txt");
        var unchangedSource = CreateFile(ws, "keep.txt");
        var conflictSource = CreateFile(ws, "taken.txt");
        var occupied = CreateFile(ws, "occupied.txt");

        var plan = new RenamePlan
        {
            Items = new List<RenamePlanItem>
            {
                new()
                {
                    SourcePath = readySource,
                    OriginalName = "ready.txt",
                    NewName = "renamed.txt",
                    TargetPath = ws.PathOf("renamed.txt"),
                    Kind = RenameItemKind.File,
                    Status = RenameItemStatus.Ready
                },
                new()
                {
                    SourcePath = unchangedSource,
                    OriginalName = "keep.txt",
                    NewName = "keep.txt",
                    TargetPath = unchangedSource,
                    Kind = RenameItemKind.File,
                    Status = RenameItemStatus.Unchanged
                },
                new()
                {
                    SourcePath = conflictSource,
                    OriginalName = "taken.txt",
                    NewName = "occupied.txt",
                    TargetPath = occupied,
                    Kind = RenameItemKind.File,
                    Status = RenameItemStatus.Conflict
                }
            }
        };

        var result = new FileRenamerService().Apply(plan);

        var applied = Assert.Single(result.Items);
        Assert.True(applied.Success);
        Assert.Equal(readySource, applied.SourcePath, ignoreCase: true);
        Assert.True(File.Exists(ws.PathOf("renamed.txt")), "Ready 条目应被重命名");
        Assert.False(File.Exists(readySource), "Ready 条目的原路径不应存在");
        Assert.True(File.Exists(unchangedSource), "Unchanged 条目不应被改动");
        Assert.True(File.Exists(conflictSource), "Conflict 条目不应被改动");
    }

    /// <summary>源文件已不存在时返回失败条目，不向外抛异常。</summary>
    [Fact]
    public void Apply_WhenSourceFileIsMissing_ReturnsFailedItemWithoutThrowing()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("missing.txt");

        var plan = new RenamePlan
        {
            Items = new List<RenamePlanItem>
            {
                new()
                {
                    SourcePath = missing,
                    OriginalName = "missing.txt",
                    NewName = "renamed.txt",
                    TargetPath = ws.PathOf("renamed.txt"),
                    Kind = RenameItemKind.File,
                    Status = RenameItemStatus.Ready
                }
            }
        };

        var result = new FileRenamerService().Apply(plan);

        var item = Assert.Single(result.Items);
        Assert.False(item.Success);
        Assert.False(string.IsNullOrWhiteSpace(item.Error), "失败项应带有原因");
        Assert.Equal(1, result.FailedCount);
        Assert.False(result.Success);
        Assert.False(File.Exists(ws.PathOf("renamed.txt")), "源文件缺失时不应生成目标文件");
    }

    /// <summary>源文件夹已不存在时同样返回失败条目，不向外抛异常。</summary>
    [Fact]
    public void Apply_WhenSourceFolderIsMissing_ReturnsFailedItemWithoutThrowing()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("missing-folder");

        var plan = new RenamePlan
        {
            Items = new List<RenamePlanItem>
            {
                new()
                {
                    SourcePath = missing,
                    OriginalName = "missing-folder",
                    NewName = "renamed-folder",
                    TargetPath = ws.PathOf("renamed-folder"),
                    Kind = RenameItemKind.Folder,
                    Status = RenameItemStatus.Ready
                }
            }
        };

        var result = new FileRenamerService().Apply(plan);

        var item = Assert.Single(result.Items);
        Assert.False(item.Success);
        Assert.False(string.IsNullOrWhiteSpace(item.Error), "失败项应带有原因");
        Assert.False(Directory.Exists(ws.PathOf("renamed-folder")));
    }
}
