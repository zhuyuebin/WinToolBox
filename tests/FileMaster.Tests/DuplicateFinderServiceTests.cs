using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// DuplicateFinderService 单元测试。
/// 覆盖：目录缺失 / 为空的错误返回；「先按大小分组、再按哈希分组」的两阶段策略
/// （不同大小、同大小不同内容都不误判）；重复组数量、可回收空间与建议保留文件（修改时间最早）；
/// MinFileSize 过滤、递归开关、ExcludeNames 忽略；HashedFileCount 只统计同大小候选文件；
/// Delete 的真实删除、FreedBytes、已不存在路径视为成功、只读文件不抛异常；
/// 以及 FormatSize 的 B / KB / MB / GB 边界。
/// 所有用例都在各自的临时目录中执行，删除操作一律 <c>useRecycleBin: false</c>，不产生回收站残留。
/// </summary>
public sealed class DuplicateFinderServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>扫描目标目录名（位于临时工作区中）。</summary>
    private const string ScanDirectoryName = "scan";

    /// <summary>重复文件与保留文件共用的内容。</summary>
    private const string SharedContent = "duplicate-content-2026";

    /// <summary>在临时工作区中创建文本文件（自动补齐父目录并固定修改时间），返回绝对路径。</summary>
    private static string CreateFile(
        TempWorkspace ws,
        string relativePath,
        string content,
        DateTime? lastWriteTimeUtc = null)
    {
        var fullPath = ws.PathOf(relativePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc ?? DateTime.UtcNow.AddMinutes(-10));
        return fullPath;
    }

    /// <summary>在扫描目录下创建文件。</summary>
    private static string CreateScanFile(
        TempWorkspace ws,
        string fileName,
        string content,
        DateTime? lastWriteTimeUtc = null)
        => CreateFile(ws, Path.Combine(ScanDirectoryName, fileName), content, lastWriteTimeUtc);

    /// <summary>扫描目录的绝对路径。</summary>
    private static string ScanDirectory(TempWorkspace ws) => ws.PathOf(ScanDirectoryName);

    /// <summary>构造扫描选项：默认扫描临时工作区中的 scan 目录，其余参数按需覆盖。</summary>
    private static DuplicateFinderOptions CreateOptions(
        TempWorkspace ws,
        bool includeSubDirectories = true,
        long minFileSize = 1,
        HashAlgorithmKind algorithm = HashAlgorithmKind.SHA256,
        string? excludeNames = null)
        => new()
        {
            Directories = new[] { ScanDirectory(ws) },
            IncludeSubDirectories = includeSubDirectories,
            MinFileSize = minFileSize,
            Algorithm = algorithm,
            ExcludeNames = excludeNames
        };

    // ------------------------------------------------------------------
    // 目录校验
    // ------------------------------------------------------------------

    /// <summary>扫描目录不存在时返回错误说明，而不是抛异常。</summary>
    [Fact]
    public void Find_WhenDirectoryMissing_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var missing = ScanDirectory(ws);

        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { missing }
        });

        Assert.NotNull(result.Error);
        Assert.Empty(result.Groups);
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>目录列表为空时返回错误说明。</summary>
    [Fact]
    public void Find_WhenDirectoryListIsEmpty_ReturnsError()
    {
        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = Array.Empty<string>()
        });

        Assert.NotNull(result.Error);
        Assert.Empty(result.Groups);
    }

    /// <summary>目录列表只有空白项时同样返回错误说明。</summary>
    [Fact]
    public void Find_WhenDirectoryListContainsBlankEntries_ReturnsError()
    {
        var result = new DuplicateFinderService().Find(new DuplicateFinderOptions
        {
            Directories = new[] { "   ", string.Empty }
        });

        Assert.NotNull(result.Error);
        Assert.Empty(result.Groups);
    }

    // ------------------------------------------------------------------
    // 重复识别
    // ------------------------------------------------------------------

    /// <summary>两个内容相同（名字不同）的文件产生 1 组，保留修改时间最早的那个。</summary>
    [Fact]
    public void Find_WithTwoIdenticalFiles_ReturnsSingleGroupKeepingOldestFile()
    {
        using var ws = new TempWorkspace();
        var older = CreateScanFile(ws, "copy-old.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        var newer = CreateScanFile(ws, "copy-new.txt", SharedContent, DateTime.UtcNow.AddMinutes(-5));
        var size = new FileInfo(older).Length;

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(size, group.FileSize);
        Assert.Equal(size, group.WastedBytes);
        Assert.Equal(1, result.DuplicateFileCount);
        Assert.Equal(size, result.WastedBytes);
        Assert.Equal(older, group.SuggestedKeepPath, ignoreCase: true);
        Assert.NotEqual(newer, group.SuggestedKeepPath);
    }

    /// <summary>三个内容相同的文件产生 1 组，可删除 2 个，可回收空间为两份文件大小。</summary>
    [Fact]
    public void Find_WithThreeIdenticalFiles_ReturnsSingleGroupWithTwoDuplicates()
    {
        using var ws = new TempWorkspace();
        var first = CreateScanFile(ws, "same-1.txt", SharedContent, DateTime.UtcNow.AddHours(-3));
        CreateScanFile(ws, "same-2.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateScanFile(ws, "same-3.txt", SharedContent, DateTime.UtcNow.AddHours(-1));
        var size = new FileInfo(first).Length;

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        var group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(2, result.DuplicateFileCount);
        Assert.Equal(size * 2, result.WastedBytes);
        Assert.Equal(first, group.SuggestedKeepPath, ignoreCase: true);
    }

    /// <summary>大小不同（内容也不同）的文件不产生重复组。</summary>
    [Fact]
    public void Find_WithDifferentSizes_ReturnsNoGroups()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "short.txt", "aaaa");
        CreateScanFile(ws, "long.txt", "bbbbbb");

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        Assert.Empty(result.Groups);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Equal(0, result.HashedFileCount);
    }

    /// <summary>大小相同但内容不同时不产生重复组（验证「先比大小、再比哈希」不会误判）。</summary>
    [Fact]
    public void Find_WithSameSizeButDifferentContent_ReturnsNoGroups()
    {
        using var ws = new TempWorkspace();
        var first = CreateScanFile(ws, "aaaa.txt", "aaaa", DateTime.UtcNow.AddHours(-2));
        var second = CreateScanFile(ws, "bbbb.txt", "bbbb", DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        Assert.Equal(new FileInfo(first).Length, new FileInfo(second).Length);
        Assert.Empty(result.Groups);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Equal(2, result.HashedFileCount);
    }

    /// <summary>使用 MD5 算法时同样能找出重复文件。</summary>
    [Fact]
    public void Find_WithMd5Algorithm_StillFindsDuplicates()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "md5-a.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateScanFile(ws, "md5-b.txt", SharedContent, DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(ws, algorithm: HashAlgorithmKind.MD5));

        Assert.Null(result.Error);
        var group = Assert.Single(result.Groups);
        Assert.Equal(32, group.Hash.Length);
        Assert.Equal(1, result.DuplicateFileCount);
    }

    // ------------------------------------------------------------------
    // 过滤条件
    // ------------------------------------------------------------------

    /// <summary>MinFileSize 大于文件长度时这些文件被跳过，找不到重复。</summary>
    [Fact]
    public void Find_WhenMinFileSizeExceedsFileLength_SkipsFiles()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "tiny-a.txt", "aaaa");
        CreateScanFile(ws, "tiny-b.txt", "aaaa");

        var result = new DuplicateFinderService().Find(CreateOptions(ws, minFileSize: 64));

        Assert.Null(result.Error);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.ScannedFileCount);
    }

    /// <summary>关闭递归时不扫描子目录，子目录中的相同文件不参与比较。</summary>
    [Fact]
    public void Find_WhenRecursionDisabled_DoesNotScanSubDirectories()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "top.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine(ScanDirectoryName, "sub", "inner.txt"), SharedContent, DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(ws, includeSubDirectories: false));

        Assert.Null(result.Error);
        Assert.Empty(result.Groups);
        Assert.Equal(1, result.ScannedFileCount);
    }

    /// <summary>开启递归时子目录中的相同文件参与比较，得到 1 组重复。</summary>
    [Fact]
    public void Find_WhenRecursionEnabled_ScansSubDirectories()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "top.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateFile(ws, Path.Combine(ScanDirectoryName, "sub", "inner.txt"), SharedContent, DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(2, result.ScannedFileCount);
    }

    /// <summary>ExcludeNames 命中的文件不参与扫描，因此找不到重复。</summary>
    [Fact]
    public void Find_WhenExcludeNamesMatch_SkipsExcludedFile()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "keep.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateScanFile(ws, "skip.txt", SharedContent, DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(ws, excludeNames: "skip.txt"));

        Assert.Null(result.Error);
        Assert.Empty(result.Groups);
        Assert.Equal(1, result.ScannedFileCount);
    }

    /// <summary>所有文件大小互不相同时没有候选文件，HashedFileCount 为 0。</summary>
    [Fact]
    public void Find_WhenAllFileSizesAreDistinct_HashedFileCountIsZero()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "size-1.txt", "a");
        CreateScanFile(ws, "size-2.txt", "bb");
        CreateScanFile(ws, "size-3.txt", "ccc");

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        Assert.Equal(3, result.ScannedFileCount);
        Assert.Equal(0, result.HashedFileCount);
        Assert.Empty(result.Groups);
    }

    /// <summary>大小相同的候选文件全部参与哈希，HashedFileCount 等于候选文件数。</summary>
    [Fact]
    public void Find_WhenFileSizesMatch_HashedFileCountCountsCandidates()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "candidate-1.txt", SharedContent, DateTime.UtcNow.AddHours(-3));
        CreateScanFile(ws, "candidate-2.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateScanFile(ws, "candidate-3.txt", SharedContent, DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        Assert.Equal(3, result.HashedFileCount);

        // 三个内容相同的文件构成一组：保留 1 个，可删除 2 个
        Assert.Equal(2, result.DuplicateFileCount);
        Assert.Single(result.Groups);
    }

    /// <summary>扫描结果的摘要包含组数与可删除数量等关键字。</summary>
    [Fact]
    public void Find_ResultSummary_MentionsGroupCount()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "summary-a.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateScanFile(ws, "summary-b.txt", SharedContent, DateTime.UtcNow.AddHours(-1));

        var result = new DuplicateFinderService().Find(CreateOptions(ws));

        Assert.Null(result.Error);
        Assert.Contains("发现 1 组重复", result.Summary);
        Assert.Contains("可删除 1 个", result.Summary);
    }

    // ------------------------------------------------------------------
    // 删除
    // ------------------------------------------------------------------

    /// <summary>useRecycleBin 为 false 时真实删除磁盘文件，FreedBytes 等于被删文件大小，保留文件不受影响。</summary>
    [Fact]
    public void Delete_WithoutRecycleBin_RemovesFileFromDiskAndReportsFreedBytes()
    {
        using var ws = new TempWorkspace();
        CreateScanFile(ws, "keep-me.txt", SharedContent, DateTime.UtcNow.AddHours(-2));
        CreateScanFile(ws, "delete-me.txt", SharedContent, DateTime.UtcNow.AddHours(-1));

        var finder = new DuplicateFinderService();
        var scanResult = finder.Find(CreateOptions(ws));
        var group = Assert.Single(scanResult.Groups);
        var duplicate = group.Files.Single(file =>
            !string.Equals(file.Path, group.SuggestedKeepPath, StringComparison.OrdinalIgnoreCase));

        var deleteResult = finder.Delete(new[] { duplicate.Path }, useRecycleBin: false);

        Assert.True(deleteResult.Success, deleteResult.Summary);
        Assert.False(deleteResult.UsedRecycleBin);
        Assert.Equal(duplicate.Size, deleteResult.FreedBytes);
        Assert.False(File.Exists(duplicate.Path), "重复文件应被真正删除");
        Assert.True(File.Exists(group.SuggestedKeepPath), "建议保留的文件不应被删除");
        Assert.Contains("已永久删除", deleteResult.Summary);
    }

    /// <summary>路径已经不存在时视为删除成功，且不抛异常。</summary>
    [Fact]
    public void Delete_WhenFileAlreadyMissing_CountsAsSuccess()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf(Path.Combine(ScanDirectoryName, "already-gone.txt"));

        var result = new DuplicateFinderService().Delete(new[] { missing }, useRecycleBin: false);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(0L, result.FreedBytes);
        Assert.Equal(missing, Assert.Single(result.DeletedPaths), ignoreCase: true);
        Assert.Empty(result.FailedItems);
    }

    /// <summary>只读文件也能被删除，且不抛异常。</summary>
    [Fact]
    public void Delete_WhenFileIsReadOnly_DeletesWithoutThrowing()
    {
        using var ws = new TempWorkspace();
        var path = CreateScanFile(ws, "readonly.txt", SharedContent);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var result = RecordException(() =>
            new DuplicateFinderService().Delete(new[] { path }, useRecycleBin: false));

        Assert.Null(result);
        Assert.False(File.Exists(path), "只读文件在清除只读属性后应被删除");
    }

    /// <summary>传入空路径集合时返回成功但不删除任何文件。</summary>
    [Fact]
    public void Delete_WithEmptyPathList_ReturnsEmptySuccessResult()
    {
        var result = new DuplicateFinderService().Delete(Array.Empty<string>(), useRecycleBin: false);

        Assert.True(result.Success, result.Summary);
        Assert.Empty(result.DeletedPaths);
        Assert.Empty(result.FailedItems);
        Assert.Equal(0L, result.FreedBytes);
    }

    // ------------------------------------------------------------------
    // FormatSize 边界
    // ------------------------------------------------------------------

    /// <summary>FormatSize 在 B / KB / MB / GB 的边界上选择正确的单位与精度。</summary>
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1048575L, "1024.0 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(2621440L, "2.5 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    public void FormatSize_AtUnitBoundaries_ReturnsExpectedText(long bytes, string expected)
    {
        Assert.Equal(expected, DuplicateScanResult.FormatSize(bytes));
    }

    /// <summary>执行动作并捕获其抛出的异常；没有异常时返回 null（本地实现，避免依赖不同 xUnit 版本的助手 API 差异）。</summary>
    private static Exception? RecordException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
