using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// FolderSyncService 单元测试。
/// 覆盖：ValidateDirectories 的同目录 / 互相包含 / 无关目录判定；BuildPlan 的源目录缺失、
/// 目标目录为空、目标目录不存在（新建目录 + 复制文件）；大小与修改时间一致时不产生动作；
/// 内容或时间变化时产生 UpdateFile；多余文件与多余目录在 CopyOnly / OneWaySync / Mirror 下的差异；
/// CompareByHash 模式的时间与内容判定；Apply 的真实复制、真实删除与失败项；
/// DescribeAction 的中文映射以及 SyncPlan.Summary 的关键字。
/// 所有用例都在各自的临时目录中执行，删除一律 <c>UseRecycleBin = false</c>，不产生回收站残留。
/// </summary>
public sealed class FolderSyncServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>源 / 目标文件中使用的公共内容。</summary>
    private const string SharedContent = "sync-shared-content";

    /// <summary>在指定目录下创建文本文件（自动补齐父目录并固定修改时间），返回绝对路径。</summary>
    private static string CreateFile(
        string baseDirectory,
        string relativePath,
        string content,
        DateTime? lastWriteTimeUtc = null)
    {
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc ?? DateTime.UtcNow.AddMinutes(-10));
        return fullPath;
    }

    /// <summary>在源目录下创建文件。</summary>
    private static string CreateSourceFile(
        TempWorkspace ws,
        string relativePath,
        string content,
        DateTime? lastWriteTimeUtc = null)
        => CreateFile(ws.SourceDir, relativePath, content, lastWriteTimeUtc);

    /// <summary>在目标目录下创建文件。</summary>
    private static string CreateTargetFile(
        TempWorkspace ws,
        string relativePath,
        string content,
        DateTime? lastWriteTimeUtc = null)
        => CreateFile(ws.TargetDir, relativePath, content, lastWriteTimeUtc);

    /// <summary>把目标文件的修改时间对齐到源文件，模拟「已经同步过」的状态。</summary>
    private static void SyncTimestamps(string sourcePath, string targetPath)
        => File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath));

    /// <summary>构造同步选项：源 / 目标固定为临时工作区中的 source / target，其余参数按需覆盖。</summary>
    private static FolderSyncOptions CreateOptions(
        TempWorkspace ws,
        SyncMode mode = SyncMode.OneWaySync,
        bool includeSubDirectories = true,
        bool compareByHash = false,
        bool useRecycleBin = true,
        string? excludeNames = null)
        => new()
        {
            SourceDirectory = ws.SourceDir,
            TargetDirectory = ws.TargetDir,
            Mode = mode,
            IncludeSubDirectories = includeSubDirectories,
            CompareByHash = compareByHash,
            UseRecycleBin = useRecycleBin,
            ExcludeNames = excludeNames
        };

    // ------------------------------------------------------------------
    // ValidateDirectories
    // ------------------------------------------------------------------

    /// <summary>源目录与目标目录相同时返回错误说明。</summary>
    [Fact]
    public void ValidateDirectories_WhenSourceEqualsTarget_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var error = FolderSyncService.ValidateDirectories(ws.SourceDir, ws.SourceDir);

        Assert.NotNull(error);
    }

    /// <summary>目标目录位于源目录内部时返回错误说明。</summary>
    [Fact]
    public void ValidateDirectories_WhenTargetIsInsideSource_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var error = FolderSyncService.ValidateDirectories(
            ws.SourceDir,
            Path.Combine(ws.SourceDir, "inner"));

        Assert.NotNull(error);
    }

    /// <summary>源目录位于目标目录内部时返回错误说明。</summary>
    [Fact]
    public void ValidateDirectories_WhenSourceIsInsideTarget_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var error = FolderSyncService.ValidateDirectories(
            Path.Combine(ws.TargetDir, "inner"),
            ws.TargetDir);

        Assert.NotNull(error);
    }

    /// <summary>两个互不包含的目录通过校验，返回 null。</summary>
    [Fact]
    public void ValidateDirectories_WhenDirectoriesAreUnrelated_ReturnsNull()
    {
        using var ws = new TempWorkspace();

        Assert.Null(FolderSyncService.ValidateDirectories(ws.SourceDir, ws.TargetDir));
    }

    // ------------------------------------------------------------------
    // BuildPlan：参数校验
    // ------------------------------------------------------------------

    /// <summary>源目录不存在时返回错误说明，不抛异常。</summary>
    [Fact]
    public void BuildPlan_WhenSourceDirectoryMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var missing = Path.Combine(ws.Root, "no-such-source");

        var plan = new FolderSyncService().BuildPlan(new FolderSyncOptions
        {
            SourceDirectory = missing,
            TargetDirectory = ws.TargetDir
        });

        Assert.NotNull(plan.Error);
        Assert.Empty(plan.Items);
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>目标目录为空（空串或纯空白）时返回错误说明。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildPlan_WhenTargetDirectoryIsBlank_ReturnsError(string targetDirectory)
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "a.txt", "hello");

        var plan = new FolderSyncService().BuildPlan(new FolderSyncOptions
        {
            SourceDirectory = ws.SourceDir,
            TargetDirectory = targetDirectory
        });

        Assert.NotNull(plan.Error);
        Assert.Empty(plan.Items);
    }

    // ------------------------------------------------------------------
    // BuildPlan：目标目录不存在 / 已一致
    // ------------------------------------------------------------------

    /// <summary>目标目录不存在时计划里包含「创建目录」与「复制文件」，且不报错、不修改磁盘。</summary>
    [Fact]
    public void BuildPlan_WhenTargetDirectoryMissing_PlansCreateDirectoryAndCopyFile()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, Path.Combine("sub", "inner.txt"), "inner-content");

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.CopyOnly));

        Assert.Null(plan.Error);
        Assert.False(Directory.Exists(ws.TargetDir), "生成计划不应创建目标目录");
        Assert.Equal(1, plan.CreateDirectoryCount);
        Assert.Equal(1, plan.CopyCount);
        Assert.Equal("sub", plan.Items.Single(item => item.Kind == SyncActionKind.CreateDirectory).RelativePath);

        var copyItem = plan.Items.Single(item => item.Kind == SyncActionKind.CopyFile);
        Assert.Equal(Path.Combine("sub", "inner.txt"), copyItem.RelativePath);
        Assert.Equal(Path.Combine(ws.TargetDir, "sub", "inner.txt"), copyItem.TargetPath, ignoreCase: true);
        Assert.Equal(Path.Combine(ws.SourceDir, "sub", "inner.txt"), copyItem.SourcePath, ignoreCase: true);
    }

    /// <summary>目标已有大小与修改时间都一致的文件时，计划中没有任何动作。</summary>
    [Fact]
    public void BuildPlan_WhenTargetFileIsIdentical_PlansNoAction()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "same.txt", SharedContent, DateTime.UtcNow.AddHours(-1));
        var target = CreateTargetFile(ws, "same.txt", SharedContent, DateTime.UtcNow.AddHours(-5));
        SyncTimestamps(source, target);

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws));

        Assert.Null(plan.Error);
        Assert.Equal(0, plan.CopyCount);
        Assert.Empty(plan.Items);
        Assert.False(plan.HasChanges);
    }

    /// <summary>源文件比目标文件大（内容不同）时计划里是 UpdateFile。</summary>
    [Fact]
    public void BuildPlan_WhenSourceFileSizeDiffers_PlansUpdateFile()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "grow.txt", "much-longer-content", DateTime.UtcNow.AddMinutes(-1));
        var target = CreateTargetFile(ws, "grow.txt", "short", DateTime.UtcNow.AddHours(-5));

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.CopyOnly));

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal(SyncActionKind.UpdateFile, item.Kind);
        Assert.Equal("grow.txt", item.RelativePath);
        Assert.Equal(source, item.SourcePath, ignoreCase: true);
        Assert.Equal(target, item.TargetPath, ignoreCase: true);
        Assert.Equal(new FileInfo(source).Length, item.Size);
    }

    /// <summary>大小相同但源文件修改时间更新时（默认按大小 + 时间比较）计划里是 UpdateFile。</summary>
    [Fact]
    public void BuildPlan_WhenSourceFileIsNewer_PlansUpdateFile()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "time.txt", "abcd", DateTime.UtcNow.AddMinutes(-1));
        var target = CreateTargetFile(ws, "time.txt", "wxyz", DateTime.UtcNow.AddHours(-3));

        Assert.Equal(new FileInfo(source).Length, new FileInfo(target).Length);

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.CopyOnly));

        var item = Assert.Single(plan.Items);
        Assert.Equal(SyncActionKind.UpdateFile, item.Kind);
    }

    // ------------------------------------------------------------------
    // BuildPlan：三种模式下的多余内容
    // ------------------------------------------------------------------

    /// <summary>CopyOnly 模式下目标中多余的文件不会被删除。</summary>
    [Fact]
    public void BuildPlan_InCopyOnlyMode_DoesNotPlanDeleteFile()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", SharedContent, DateTime.UtcNow.AddHours(-1));
        var extra = CreateTargetFile(ws, "extra.txt", "extra-content", DateTime.UtcNow.AddHours(-2));

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.CopyOnly));

        Assert.Null(plan.Error);
        Assert.Equal(0, plan.DeleteFileCount);
        Assert.True(File.Exists(extra), "生成计划不应删除磁盘上的文件");
    }

    /// <summary>OneWaySync 模式下目标中多余的文件会产生 DeleteFile 动作。</summary>
    [Fact]
    public void BuildPlan_InOneWaySyncMode_PlansDeleteFileForExtraTargetFile()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "keep.txt", SharedContent, DateTime.UtcNow.AddHours(-1));
        var targetKeep = CreateTargetFile(ws, "keep.txt", SharedContent, DateTime.UtcNow.AddHours(-5));
        SyncTimestamps(source, targetKeep);
        var extra = CreateTargetFile(ws, "extra.txt", "extra-content", DateTime.UtcNow.AddHours(-2));

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.OneWaySync));

        Assert.Null(plan.Error);
        Assert.Equal(1, plan.DeleteFileCount);
        var item = Assert.Single(plan.Items);
        Assert.Equal(SyncActionKind.DeleteFile, item.Kind);
        Assert.Equal("extra.txt", item.RelativePath);
        Assert.Equal(extra, item.TargetPath, ignoreCase: true);
        Assert.True(File.Exists(extra), "生成计划不应删除磁盘上的文件");
    }

    /// <summary>Mirror 模式下所有已存在的文件都被安排为 UpdateFile（强制重写）。</summary>
    [Fact]
    public void BuildPlan_InMirrorMode_PlansUpdateForEveryExistingFile()
    {
        using var ws = new TempWorkspace();
        var first = CreateSourceFile(ws, "a.txt", "content-a", DateTime.UtcNow.AddHours(-1));
        var second = CreateSourceFile(ws, "b.txt", "content-b", DateTime.UtcNow.AddHours(-1));
        var targetFirst = CreateTargetFile(ws, "a.txt", "content-a", DateTime.UtcNow.AddHours(-5));
        var targetSecond = CreateTargetFile(ws, "b.txt", "content-b", DateTime.UtcNow.AddHours(-5));
        SyncTimestamps(first, targetFirst);
        SyncTimestamps(second, targetSecond);

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.Mirror));

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.CopyCount);
        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, item => Assert.Equal(SyncActionKind.UpdateFile, item.Kind));
        Assert.Equal(0, plan.DeleteFileCount);
    }

    /// <summary>OneWaySync 模式下目标中多余的目录会产生 DeleteDirectory 动作，相对路径即该目录。</summary>
    [Fact]
    public void BuildPlan_InOneWaySyncMode_PlansDeleteDirectoryForExtraTargetDirectory()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", SharedContent, DateTime.UtcNow.AddHours(-1));
        CreateTargetFile(ws, Path.Combine("extra", "leftover.txt"), "leftover", DateTime.UtcNow.AddHours(-2));

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.OneWaySync));

        Assert.Null(plan.Error);
        Assert.Equal(1, plan.DeleteDirectoryCount);
        var item = plan.Items.Single(planItem => planItem.Kind == SyncActionKind.DeleteDirectory);
        Assert.Equal("extra", item.RelativePath);
        Assert.Equal(Path.Combine(ws.TargetDir, "extra"), item.TargetPath, ignoreCase: true);
    }

    // ------------------------------------------------------------------
    // BuildPlan：CompareByHash 与忽略名单
    // ------------------------------------------------------------------

    /// <summary>CompareByHash 为 true 时，大小相同、时间不同但内容相同的文件不产生更新动作。</summary>
    [Fact]
    public void BuildPlan_WithCompareByHash_IgnoresWriteTimeDifference()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "same-content.txt", SharedContent, DateTime.UtcNow.AddMinutes(-1));
        CreateTargetFile(ws, "same-content.txt", SharedContent, DateTime.UtcNow.AddHours(-5));

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, compareByHash: true));

        Assert.Null(plan.Error);
        Assert.Equal(0, plan.CopyCount);
        Assert.Empty(plan.Items);
    }

    /// <summary>CompareByHash 为 true 时，大小相同但内容不同的文件仍然产生更新动作。</summary>
    [Fact]
    public void BuildPlan_WithCompareByHash_DetectsContentDifference()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "diff.txt", "aaaa", DateTime.UtcNow.AddMinutes(-1));
        var target = CreateTargetFile(ws, "diff.txt", "bbbb", DateTime.UtcNow.AddHours(-5));

        Assert.Equal(new FileInfo(source).Length, new FileInfo(target).Length);

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, compareByHash: true));

        var item = Assert.Single(plan.Items);
        Assert.Equal(SyncActionKind.UpdateFile, item.Kind);
        Assert.Equal("diff.txt", item.RelativePath);
    }

    /// <summary>ExcludeNames 命中的文件不参与同步计划。</summary>
    [Fact]
    public void BuildPlan_WhenExcludeNamesMatch_SkipsExcludedFile()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "keep-content", DateTime.UtcNow.AddHours(-1));
        CreateSourceFile(ws, "skip.log", "skip-content", DateTime.UtcNow.AddHours(-1));

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, excludeNames: "skip.log"));

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal("keep.txt", item.RelativePath);
    }

    // ------------------------------------------------------------------
    // Apply
    // ------------------------------------------------------------------

    /// <summary>执行计划会真实创建目录并复制文件，目标文件内容与源文件一致。</summary>
    [Fact]
    public void Apply_CopiesFilesAndCreatesDirectories()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "a.txt", "hello", DateTime.UtcNow.AddHours(-1));
        CreateSourceFile(ws, Path.Combine("sub", "inner.txt"), "inner-content", DateTime.UtcNow.AddHours(-1));

        var options = CreateOptions(ws, SyncMode.CopyOnly, useRecycleBin: false);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.CopyCount);
        Assert.Equal(1, plan.CreateDirectoryCount);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);

        var copiedFile = Path.Combine(ws.TargetDir, "a.txt");
        var copiedInner = Path.Combine(ws.TargetDir, "sub", "inner.txt");
        Assert.True(File.Exists(copiedFile), "应复制出 a.txt");
        Assert.True(File.Exists(copiedInner), "应复制出 sub\\inner.txt");
        Assert.Equal("hello", File.ReadAllText(copiedFile));
        Assert.Equal("inner-content", File.ReadAllText(copiedInner));

        var expectedBytes = new FileInfo(Path.Combine(ws.SourceDir, "a.txt")).Length
                            + new FileInfo(Path.Combine(ws.SourceDir, "sub", "inner.txt")).Length;
        Assert.Equal(expectedBytes, result.CopiedBytes);
    }

    /// <summary>执行计划会真实删除目标中多余的文件（UseRecycleBin 为 false 时为永久删除）。</summary>
    [Fact]
    public void Apply_DeletesExtraTargetFileFromDisk()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "keep.txt", SharedContent, DateTime.UtcNow.AddHours(-1));
        var targetKeep = CreateTargetFile(ws, "keep.txt", SharedContent, DateTime.UtcNow.AddHours(-5));
        SyncTimestamps(source, targetKeep);
        var extra = CreateTargetFile(ws, "extra.txt", "extra-content", DateTime.UtcNow.AddHours(-2));

        var options = CreateOptions(ws, SyncMode.OneWaySync, useRecycleBin: false);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.DeleteFileCount);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.False(File.Exists(extra), "多余的目标文件应被真正删除");
        Assert.True(File.Exists(targetKeep), "同名文件不应被删除");
    }

    /// <summary>计划生成后源文件被删除时，执行返回失败项而不抛异常。</summary>
    [Fact]
    public void Apply_WhenSourceFileDisappearsAfterPlanning_ReturnsFailedItem()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "vanish.txt", "hello", DateTime.UtcNow.AddHours(-1));

        var options = CreateOptions(ws, SyncMode.CopyOnly, useRecycleBin: false);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.CopyCount);

        File.Delete(source);

        var result = service.Apply(plan, options);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedCount);
        var item = Assert.Single(result.Items);
        Assert.False(item.Success);
        Assert.False(string.IsNullOrWhiteSpace(item.Error), "失败项应带有原因");
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "vanish.txt")), "源文件缺失时不应生成目标文件");
    }

    // ------------------------------------------------------------------
    // 说明文字与摘要
    // ------------------------------------------------------------------

    /// <summary>DescribeAction 返回中文动作说明。</summary>
    [Theory]
    [InlineData(SyncActionKind.CreateDirectory, "创建目录")]
    [InlineData(SyncActionKind.CopyFile, "复制文件")]
    [InlineData(SyncActionKind.UpdateFile, "更新文件")]
    [InlineData(SyncActionKind.DeleteFile, "删除多余文件")]
    [InlineData(SyncActionKind.DeleteDirectory, "删除多余目录")]
    public void DescribeAction_ReturnsChineseText(SyncActionKind kind, string expected)
    {
        Assert.Equal(expected, FolderSyncService.DescribeAction(kind));
    }

    /// <summary>SyncPlan.Summary 包含复制与删除等关键字。</summary>
    [Fact]
    public void SyncPlanSummary_ContainsChineseKeywords()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "a.txt", "hello", DateTime.UtcNow.AddHours(-1));

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.OneWaySync));

        Assert.Null(plan.Error);
        Assert.Contains("复制", plan.Summary);
        Assert.Contains("删除文件", plan.Summary);
        Assert.Contains("新建目录", plan.Summary);
    }

    /// <summary>计划带错误时 Summary 给出失败提示。</summary>
    [Fact]
    public void SyncPlanSummary_WhenPlanHasError_ContainsFailureKeyword()
    {
        var plan = new SyncPlan { Error = "磁盘不可用" };

        Assert.Contains("生成同步计划失败", plan.Summary);
        Assert.Contains("磁盘不可用", plan.Summary);
        Assert.False(plan.HasChanges);
    }
}
