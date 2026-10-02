using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// TimestampService 单元测试。
/// 覆盖：ResolveNewTimes 的「修改时间 = 创建时间」与「统一改成指定时间（按字段）」；
/// Validate 的字段校验；BuildPreview 的目录扫描、Target 过滤与 WillChange 判定；
/// Apply 的真实写入（修改时间与创建时间对齐、只改选中字段、WillChange 为 false 的条目零写入、
/// 路径缺失时返回失败条目）。所有用例都在各自的临时目录中执行，不依赖用户真实目录。
/// </summary>
public sealed class TimestampServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>固定时间（2024-04-04 04:04:04），用于「统一改成指定时间」模式。</summary>
    private static readonly DateTime FixedTime = new(2024, 4, 4, 4, 4, 4);

    /// <summary>在临时工作区中创建文件（自动补齐父目录），返回文件的绝对路径。</summary>
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

    /// <summary>两个时间是否足够接近（容差 1 秒，避免文件系统精度导致误报）。</summary>
    private static bool IsClose(DateTime expected, DateTime actual)
        => Math.Abs((expected - actual).TotalSeconds) < 1;

    // ------------------------------------------------------------------
    // ResolveNewTimes
    // ------------------------------------------------------------------

    /// <summary>「修改时间 = 创建时间」模式：把传入的创建时间作为新的修改时间。</summary>
    [Fact]
    public void ResolveNewTimes_LastWriteEqualsCreation_ReturnsCreationTimeForLastWrite()
    {
        var creation = new DateTime(2024, 1, 2, 3, 4, 5);
        var options = new TimestampOptions { Mode = TimestampMode.LastWriteEqualsCreation };

        var (newCreation, newLastWrite, newLastAccess) = TimestampService.ResolveNewTimes(
            options,
            creation,
            new DateTime(2025, 6, 7, 8, 9, 10),
            new DateTime(2025, 6, 8, 9, 10, 11));

        Assert.Null(newCreation);
        Assert.Equal<DateTime?>(creation, newLastWrite);
        Assert.Null(newLastAccess);
    }

    /// <summary>「修改时间 = 创建时间」模式不受 FixedTime 与 Fields 影响。</summary>
    [Fact]
    public void ResolveNewTimes_LastWriteEqualsCreation_IgnoresFixedTimeAndFields()
    {
        var creation = new DateTime(2023, 3, 3, 3, 3, 3);
        var options = new TimestampOptions
        {
            Mode = TimestampMode.LastWriteEqualsCreation,
            Fields = TimestampField.CreationTime | TimestampField.LastAccessTime,
            FixedTime = FixedTime
        };

        var (newCreation, newLastWrite, newLastAccess) = TimestampService.ResolveNewTimes(
            options,
            creation,
            new DateTime(2025, 1, 1),
            new DateTime(2025, 1, 2));

        Assert.Null(newCreation);
        Assert.Equal<DateTime?>(creation, newLastWrite);
        Assert.Null(newLastAccess);
    }

    /// <summary>「统一改成指定时间」模式只返回 Fields 中选中的字段，其余为 null。</summary>
    [Theory]
    [InlineData(TimestampField.CreationTime)]
    [InlineData(TimestampField.LastWriteTime)]
    [InlineData(TimestampField.LastAccessTime)]
    [InlineData(TimestampField.CreationAndLastWrite)]
    [InlineData(TimestampField.CreationTime | TimestampField.LastWriteTime | TimestampField.LastAccessTime)]
    public void ResolveNewTimes_SetFixedTime_WritesOnlySelectedFields(TimestampField fields)
    {
        var options = new TimestampOptions
        {
            Mode = TimestampMode.SetFixedTime,
            Fields = fields,
            FixedTime = FixedTime
        };

        var (newCreation, newLastWrite, newLastAccess) = TimestampService.ResolveNewTimes(
            options,
            new DateTime(2020, 1, 1),
            new DateTime(2021, 1, 1),
            new DateTime(2022, 1, 1));

        Assert.Equal<DateTime?>(
            fields.HasFlag(TimestampField.CreationTime) ? FixedTime : null,
            newCreation);
        Assert.Equal<DateTime?>(
            fields.HasFlag(TimestampField.LastWriteTime) ? FixedTime : null,
            newLastWrite);
        Assert.Equal<DateTime?>(
            fields.HasFlag(TimestampField.LastAccessTime) ? FixedTime : null,
            newLastAccess);
    }

    /// <summary>「统一改成指定时间」但没有选中任何字段时，三个字段都不修改。</summary>
    [Fact]
    public void ResolveNewTimes_SetFixedTimeWithFieldsNone_ReturnsAllNull()
    {
        var options = new TimestampOptions
        {
            Mode = TimestampMode.SetFixedTime,
            Fields = TimestampField.None,
            FixedTime = FixedTime
        };

        var (newCreation, newLastWrite, newLastAccess) = TimestampService.ResolveNewTimes(
            options,
            new DateTime(2020, 1, 1),
            new DateTime(2021, 1, 1),
            new DateTime(2022, 1, 1));

        Assert.Null(newCreation);
        Assert.Null(newLastWrite);
        Assert.Null(newLastAccess);
    }

    // ------------------------------------------------------------------
    // Validate
    // ------------------------------------------------------------------

    /// <summary>「统一改成指定时间」但没有选中字段时规则不合法。</summary>
    [Fact]
    public void Validate_SetFixedTimeWithoutFields_ReturnsError()
    {
        var options = new TimestampOptions
        {
            Mode = TimestampMode.SetFixedTime,
            Fields = TimestampField.None
        };

        var error = TimestampService.Validate(options);

        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error), "应给出「至少选择一个字段」的说明");
    }

    /// <summary>「统一改成指定时间」选中了任意字段时规则合法。</summary>
    [Theory]
    [InlineData(TimestampField.CreationTime)]
    [InlineData(TimestampField.LastWriteTime)]
    [InlineData(TimestampField.LastAccessTime)]
    [InlineData(TimestampField.CreationAndLastWrite)]
    public void Validate_SetFixedTimeWithFields_ReturnsNull(TimestampField fields)
    {
        var options = new TimestampOptions
        {
            Mode = TimestampMode.SetFixedTime,
            Fields = fields
        };

        Assert.Null(TimestampService.Validate(options));
    }

    /// <summary>「修改时间 = 创建时间」模式不要求选择字段，规则合法。</summary>
    [Fact]
    public void Validate_LastWriteEqualsCreationWithoutFields_ReturnsNull()
    {
        var options = new TimestampOptions
        {
            Mode = TimestampMode.LastWriteEqualsCreation,
            Fields = TimestampField.None
        };

        Assert.Null(TimestampService.Validate(options));
    }

    // ------------------------------------------------------------------
    // BuildPreview
    // ------------------------------------------------------------------

    /// <summary>递归扫描目录时为每个文件生成一条预览。</summary>
    [Fact]
    public void BuildPreviewFromDirectory_CreatesItemForEachFile()
    {
        using var ws = new TempWorkspace();
        var first = CreateFile(ws, "a.txt");
        var second = CreateFile(ws, Path.Combine("sub", "b.txt"));

        var options = new TimestampOptions
        {
            Target = TimestampTarget.Files,
            IncludeSubDirectories = true,
            Mode = TimestampMode.SetFixedTime,
            Fields = TimestampField.LastWriteTime,
            FixedTime = FixedTime
        };

        var preview = new TimestampService().BuildPreviewFromDirectory(ws.Root, options);

        Assert.Null(preview.Error);
        Assert.Equal(2, preview.Items.Count);
        Assert.Contains(
            preview.Items,
            item => string.Equals(item.Path, first, StringComparison.OrdinalIgnoreCase) && !item.IsDirectory);
        Assert.Contains(
            preview.Items,
            item => string.Equals(item.Path, second, StringComparison.OrdinalIgnoreCase) && !item.IsDirectory);
        Assert.Equal(2, preview.ChangeCount);
    }

    /// <summary>目录不存在时返回错误说明，不抛异常。</summary>
    [Fact]
    public void BuildPreviewFromDirectory_WhenDirectoryMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("missing");

        var preview = new TimestampService().BuildPreviewFromDirectory(
            missing,
            new TimestampOptions { Mode = TimestampMode.LastWriteEqualsCreation });

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Items);
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>规则不合法时预览直接返回错误，不产生条目。</summary>
    [Fact]
    public void BuildPreview_WithInvalidRules_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "a.txt");

        var options = new TimestampOptions
        {
            Mode = TimestampMode.SetFixedTime,
            Fields = TimestampField.None
        };

        var preview = new TimestampService().BuildPreview(new[] { path }, options);

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.Items);
    }

    /// <summary>修改时间已经等于创建时间时 WillChange 为 false。</summary>
    [Fact]
    public void BuildPreview_WhenLastWriteAlreadyEqualsCreation_WillChangeIsFalse()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "synced.txt");
        var time = new DateTime(2024, 5, 6, 7, 8, 9);
        File.SetCreationTime(path, time);
        File.SetLastWriteTime(path, time);

        var options = new TimestampOptions
        {
            Mode = TimestampMode.LastWriteEqualsCreation,
            Target = TimestampTarget.Files
        };

        var preview = new TimestampService().BuildPreview(new[] { path }, options);

        Assert.Null(preview.Error);
        var item = Assert.Single(preview.Items);
        Assert.True(item.NewLastWriteTime.HasValue, "应计算出新的修改时间");
        Assert.True(IsClose(time, item.NewLastWriteTime.GetValueOrDefault()), "新的修改时间应等于创建时间");
        Assert.False(item.WillChange, "修改时间已与创建时间一致时不应再需要修改");
        Assert.Equal(0, preview.ChangeCount);
    }

    /// <summary>Target 为 Files 时不包含文件夹。</summary>
    [Fact]
    public void BuildPreview_TargetFiles_ExcludesDirectories()
    {
        using var ws = new TempWorkspace();
        var folder = ws.CreateDirectory("docs");
        var file = CreateFile(ws, Path.Combine("docs", "note.txt"));

        var options = new TimestampOptions
        {
            Target = TimestampTarget.Files,
            IncludeSubDirectories = true,
            Mode = TimestampMode.LastWriteEqualsCreation
        };

        var preview = new TimestampService().BuildPreviewFromDirectory(ws.Root, options);

        Assert.Null(preview.Error);
        var item = Assert.Single(preview.Items);
        Assert.Equal(file, item.Path, ignoreCase: true);
        Assert.False(item.IsDirectory);
        Assert.DoesNotContain(preview.Items, i => string.Equals(i.Path, folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Target 为 Folders 时不包含文件，且条目标记为目录。</summary>
    [Fact]
    public void BuildPreview_TargetFolders_ExcludesFiles()
    {
        using var ws = new TempWorkspace();
        var folder = ws.CreateDirectory("docs");
        CreateFile(ws, Path.Combine("docs", "note.txt"));

        var options = new TimestampOptions
        {
            Target = TimestampTarget.Folders,
            IncludeSubDirectories = false,
            Mode = TimestampMode.LastWriteEqualsCreation
        };

        var preview = new TimestampService().BuildPreviewFromDirectory(ws.Root, options);

        Assert.Null(preview.Error);
        var item = Assert.Single(preview.Items);
        Assert.Equal(folder, item.Path, ignoreCase: true);
        Assert.True(item.IsDirectory);
    }

    // ------------------------------------------------------------------
    // Apply
    // ------------------------------------------------------------------

    /// <summary>「修改时间 = 创建时间」执行后，磁盘上的修改时间与创建时间相差小于 1 秒。</summary>
    [Fact]
    public void Apply_LastWriteEqualsCreation_MakesLastWriteMatchCreation()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "sync.txt");
        File.SetCreationTime(path, new DateTime(2023, 2, 3, 4, 5, 6));
        File.SetLastWriteTime(path, new DateTime(2025, 9, 9, 9, 9, 9));

        var options = new TimestampOptions
        {
            Mode = TimestampMode.LastWriteEqualsCreation,
            Target = TimestampTarget.Files
        };

        var service = new TimestampService();
        var preview = service.BuildPreview(new[] { path }, options);

        Assert.Null(preview.Error);
        Assert.Equal(1, preview.ChangeCount);

        var result = service.Apply(preview.Items);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(
            IsClose(File.GetCreationTime(path), File.GetLastWriteTime(path)),
            "修改时间应与创建时间一致");
    }

    /// <summary>「统一改成指定时间」只写入选中的字段，未选中的字段保持原值。</summary>
    [Fact]
    public void Apply_SetFixedTime_ChangesOnlySelectedFields()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "fixed.txt");
        var creation = new DateTime(2022, 1, 1, 1, 1, 1);
        var lastWrite = new DateTime(2023, 2, 2, 2, 2, 2);
        var lastAccess = new DateTime(2023, 3, 3, 3, 3, 3);
        File.SetCreationTime(path, creation);
        File.SetLastWriteTime(path, lastWrite);
        File.SetLastAccessTime(path, lastAccess);

        var options = new TimestampOptions
        {
            Mode = TimestampMode.SetFixedTime,
            Target = TimestampTarget.Files,
            Fields = TimestampField.LastWriteTime,
            FixedTime = FixedTime
        };

        var service = new TimestampService();
        var preview = service.BuildPreview(new[] { path }, options);

        var item = Assert.Single(preview.Items);
        Assert.Null(item.NewCreationTime);
        Assert.Null(item.NewLastAccessTime);

        var result = service.Apply(preview.Items);

        Assert.True(result.Success, result.Summary);
        Assert.True(IsClose(FixedTime, File.GetLastWriteTime(path)), "选中的修改时间应被写成固定时间");
        Assert.True(IsClose(creation, File.GetCreationTime(path)), "未选中的创建时间不应被修改");
        Assert.True(IsClose(lastAccess, File.GetLastAccessTime(path)), "未选中的访问时间不应被修改");
    }

    /// <summary>同时选中创建时间与修改时间时，两个字段都被写成固定时间。</summary>
    [Fact]
    public void Apply_SetFixedTimeWithCreationAndLastWrite_UpdatesBothFields()
    {
        using var ws = new TempWorkspace();
        var path = CreateFile(ws, "both.txt");
        var origin = new DateTime(2021, 7, 7, 7, 7, 7);
        File.SetCreationTime(path, origin);
        File.SetLastWriteTime(path, origin);

        var options = new TimestampOptions
        {
            Mode = TimestampMode.SetFixedTime,
            Target = TimestampTarget.Files,
            Fields = TimestampField.CreationAndLastWrite,
            FixedTime = FixedTime
        };

        var service = new TimestampService();
        var preview = service.BuildPreview(new[] { path }, options);

        Assert.Equal(1, preview.ChangeCount);

        var result = service.Apply(preview.Items);

        Assert.True(result.Success, result.Summary);
        Assert.True(IsClose(FixedTime, File.GetCreationTime(path)), "创建时间应被写成固定时间");
        Assert.True(IsClose(FixedTime, File.GetLastWriteTime(path)), "修改时间应被写成固定时间");
    }

    /// <summary>WillChange 为 false 的条目不产生任何写操作：预览里放一个不存在的路径也不会失败。</summary>
    [Fact]
    public void Apply_WhenWillChangeIsFalse_DoesNotTouchDisk()
    {
        using var ws = new TempWorkspace();
        var ghost = ws.PathOf("ghost.txt");
        var time = new DateTime(2024, 1, 1, 0, 0, 0);

        var item = new TimestampPreviewItem
        {
            Path = ghost,
            IsDirectory = false,
            CurrentCreationTime = time,
            CurrentLastWriteTime = time,
            CurrentLastAccessTime = time,
            NewLastWriteTime = time
        };

        Assert.False(item.WillChange, "修改时间与当前值一致时不应视为需要修改");

        var result = new TimestampService().Apply(new[] { item });

        Assert.Empty(result.Items);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.Success);
        Assert.False(File.Exists(ghost), "不应因为这条目产生任何写入");
    }

    /// <summary>确实需要修改但路径已不存在时返回失败条目，不向外抛异常。</summary>
    [Fact]
    public void Apply_WhenWillChangeIsTrueButPathMissing_ReturnsFailedItem()
    {
        using var ws = new TempWorkspace();
        var ghost = ws.PathOf("ghost.txt");

        var item = new TimestampPreviewItem
        {
            Path = ghost,
            IsDirectory = false,
            CurrentLastWriteTime = new DateTime(2020, 1, 1),
            NewLastWriteTime = FixedTime
        };

        Assert.True(item.WillChange);

        var result = new TimestampService().Apply(new[] { item });

        var failed = Assert.Single(result.Items);
        Assert.False(failed.Success);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error), "失败项应带有原因");
        Assert.Equal(1, result.FailedCount);
        Assert.False(result.Success);
    }
}
