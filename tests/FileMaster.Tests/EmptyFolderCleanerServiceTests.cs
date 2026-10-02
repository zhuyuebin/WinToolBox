using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// EmptyFolderCleanerService 单元测试。
/// 覆盖：IsEmptyDirectory 的「真空 / 含文件 / 只含空子目录 / 子目录含文件 / 目录不存在」判定；
/// Scan 的递归发现嵌套空目录、非递归只返回直接子目录、结果按深度从深到浅、根目录不存在时报错；
/// Delete 的真实删除（useRecycleBin: false）、目录已不存在时算成功、嵌套目录不报失败与去重。
/// 所有用例都在各自的临时目录中执行，删除一律永久删除，不污染用户回收站。
/// </summary>
public sealed class EmptyFolderCleanerServiceTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // IsEmptyDirectory
    // ------------------------------------------------------------------

    /// <summary>没有任何内容的目录是空目录。</summary>
    [Fact]
    public void IsEmptyDirectory_WhenDirectoryHasNoEntries_ReturnsTrue()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("empty");

        Assert.True(EmptyFolderCleanerService.IsEmptyDirectory(directory));
    }

    /// <summary>直接包含文件的目录不是空目录。</summary>
    [Fact]
    public void IsEmptyDirectory_WhenDirectoryContainsFile_ReturnsFalse()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("withFile");
        CreateFile(ws, Path.Combine("withFile", "data.txt"));

        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(directory));
    }

    /// <summary>只含空子目录（多层）的目录同样算空目录。</summary>
    [Fact]
    public void IsEmptyDirectory_WhenOnlyEmptySubdirectories_ReturnsTrue()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("A");
        ws.CreateDirectory(Path.Combine("A", "B"));
        ws.CreateDirectory(Path.Combine("A", "B", "C"));

        Assert.True(EmptyFolderCleanerService.IsEmptyDirectory(directory));
        Assert.True(EmptyFolderCleanerService.IsEmptyDirectory(ws.PathOf(Path.Combine("A", "B"))));
    }

    /// <summary>子目录（哪怕层级很深）里存在文件时，父目录不是空目录。</summary>
    [Fact]
    public void IsEmptyDirectory_WhenNestedSubdirectoryContainsFile_ReturnsFalse()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("A");
        CreateFile(ws, Path.Combine("A", "B", "C", "deep.txt"));

        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(directory));
        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(ws.PathOf(Path.Combine("A", "B"))));
        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(ws.PathOf(Path.Combine("A", "B", "C"))));
    }

    /// <summary>目录不存在时保守地返回 false，而不是抛异常。</summary>
    [Fact]
    public void IsEmptyDirectory_WhenDirectoryMissing_ReturnsFalse()
    {
        using var ws = new TempWorkspace();

        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(ws.PathOf("missing")));
    }

    /// <summary>路径为 null / 空 / 空白时返回 false。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsEmptyDirectory_WhenPathIsBlank_ReturnsFalse(string? directory)
    {
        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(directory!));
    }

    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------

    /// <summary>递归扫描能发现嵌套的空目录，以及「只含空子目录」的父目录。</summary>
    [Fact]
    public void Scan_FindsNestedEmptyDirectories()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(Path.Combine("A", "B"));
        ws.CreateDirectory("C");

        var result = new EmptyFolderCleanerService().Scan(ws.Root);

        Assert.Null(result.Error);
        Assert.Equal(3, result.TotalCount);
        Assert.Contains(result.Items, item => item.RelativePath == Path.Combine("A", "B"));
        Assert.Contains(result.Items, item => item.RelativePath == "A");
        Assert.Contains(result.Items, item => item.RelativePath == "C");

        var nested = result.Items.First(item => item.RelativePath == Path.Combine("A", "B"));
        Assert.Equal(2, nested.Depth);
        Assert.Equal(ws.PathOf(Path.Combine("A", "B")), nested.FullPath, ignoreCase: true);
    }

    /// <summary>非递归扫描只返回直接子目录，但「空」的判定仍然看整棵子树。</summary>
    [Fact]
    public void Scan_NonRecursive_ReturnsOnlyDirectChildren()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(Path.Combine("A", "B"));
        ws.CreateDirectory("C");
        CreateFile(ws, Path.Combine("C", "file.txt"));

        var result = new EmptyFolderCleanerService().Scan(ws.Root, includeSubDirectories: false);

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal("A", item.RelativePath);
        Assert.Equal(1, item.Depth);
        Assert.DoesNotContain(result.Items, i => i.RelativePath == Path.Combine("A", "B"));
    }

    /// <summary>扫描结果按深度从深到浅排列，父目录不会出现在其子目录之前。</summary>
    [Fact]
    public void Scan_OrdersResultsFromDeepestToShallowest()
    {
        using var ws = new TempWorkspace();
        var grandParent = ws.CreateDirectory("A");
        var parent = ws.CreateDirectory(Path.Combine("A", "B"));
        var child = ws.CreateDirectory(Path.Combine("A", "B", "C"));

        var result = new EmptyFolderCleanerService().Scan(ws.Root);

        Assert.Null(result.Error);
        Assert.Equal(new[] { 3, 2, 1 }, result.Items.Select(item => item.Depth).ToArray());

        var paths = result.Items.Select(item => item.FullPath).ToList();
        Assert.True(paths.IndexOf(child) < paths.IndexOf(parent), "子目录应排在父目录之前");
        Assert.True(paths.IndexOf(parent) < paths.IndexOf(grandParent), "子目录应排在父目录之前");
    }

    /// <summary>根目录自身不会作为空目录条目返回，只返回它下面的目录。</summary>
    [Fact]
    public void Scan_WhenRootItselfIsEmpty_DoesNotReportRoot()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("emptyRoot");

        var result = new EmptyFolderCleanerService().Scan(root);

        Assert.Null(result.Error);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    /// <summary>只有含文件的目录才被排除，空目录的兄弟目录仍会被发现。</summary>
    [Fact]
    public void Scan_WhenOneSiblingContainsFile_ReportsOnlyTheEmptyOne()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("emptyOne");
        ws.CreateDirectory("withFile");
        CreateFile(ws, Path.Combine("withFile", "data.txt"));

        var result = new EmptyFolderCleanerService().Scan(ws.Root);

        Assert.Null(result.Error);
        var item = Assert.Single(result.Items);
        Assert.Equal("emptyOne", item.RelativePath);
        Assert.Equal(ws.PathOf("emptyOne"), item.FullPath, ignoreCase: true);
    }

    /// <summary>根目录下只有文件时不会报出任何空目录。</summary>
    [Fact]
    public void Scan_WhenRootContainsOnlyFiles_ReportsNothing()
    {
        using var ws = new TempWorkspace();
        CreateFile(ws, "a.txt");
        CreateFile(ws, "b.txt");

        var result = new EmptyFolderCleanerService().Scan(ws.Root);

        Assert.Null(result.Error);
        Assert.Empty(result.Items);
    }

    /// <summary>根目录不存在时返回错误说明，不抛异常。</summary>
    [Fact]
    public void Scan_WhenRootDirectoryMissing_ReturnsErrorWithoutThrowing()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("missing");

        var result = new EmptyFolderCleanerService().Scan(missing);

        Assert.NotNull(result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Error), "应给出目录不存在的说明");
        Assert.Empty(result.Items);
    }

    /// <summary>根目录为 null / 空 / 空白时返回错误说明。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Scan_WhenRootIsBlank_ReturnsError(string? root)
    {
        var result = new EmptyFolderCleanerService().Scan(root!);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Items);
    }

    // ------------------------------------------------------------------
    // Delete
    // ------------------------------------------------------------------

    /// <summary>永久删除（useRecycleBin: false）后目录在磁盘上消失。</summary>
    [Fact]
    public void Delete_WhenRecycleBinDisabled_ReallyDeletesDirectory()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("only");

        var result = new EmptyFolderCleanerService().Delete(new[] { directory }, useRecycleBin: false);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.UsedRecycleBin);
        Assert.False(Directory.Exists(directory), "目录应已被永久删除");
        Assert.False(ws.Exists("only"));
        Assert.Contains("永久删除", result.Summary);
    }

    /// <summary>目录已经不存在时仍算删除成功（可能已被父目录一并删除）。</summary>
    [Fact]
    public void Delete_WhenDirectoryAlreadyMissing_CountsAsSuccess()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("gone");

        var result = new EmptyFolderCleanerService().Delete(new[] { missing }, useRecycleBin: false);

        var item = Assert.Single(result.Items);
        Assert.True(item.Success);
        Assert.Null(item.Error);
        Assert.True(result.Success);
    }

    /// <summary>删除嵌套空目录时先删子目录再删父目录，两条都成功且子目录不报失败。</summary>
    [Fact]
    public void Delete_NestedDirectories_DeletesChildFirstWithoutFailures()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("A");
        var child = ws.CreateDirectory(Path.Combine("A", "B"));
        var grandChild = ws.CreateDirectory(Path.Combine("A", "B", "C"));

        var service = new EmptyFolderCleanerService();
        var scan = service.Scan(ws.Root);

        Assert.Equal(3, scan.TotalCount);

        var result = service.Delete(scan.Items.Select(item => item.FullPath), useRecycleBin: false);

        Assert.Equal(3, result.DeletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.Success, result.Summary);
        Assert.False(Directory.Exists(grandChild));
        Assert.False(Directory.Exists(child));
        Assert.False(Directory.Exists(parent));
    }

    /// <summary>扫描 + 删除的组合用完后根目录下不再残留空目录。</summary>
    [Fact]
    public void Delete_AfterScan_ParentKeepsContainingFile()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(Path.Combine("keep", "empty"));
        CreateFile(ws, Path.Combine("keep", "note.txt"));

        var service = new EmptyFolderCleanerService();
        var scan = service.Scan(ws.Root);

        var item = Assert.Single(scan.Items);
        Assert.Equal(Path.Combine("keep", "empty"), item.RelativePath);

        var result = service.Delete(scan.Items.Select(entry => entry.FullPath), useRecycleBin: false);

        Assert.Equal(1, result.DeletedCount);
        Assert.False(ws.Exists(Path.Combine("keep", "empty")));
        Assert.True(Directory.Exists(ws.PathOf("keep")), "仍含文件的目录不应被删除");
    }

    /// <summary>重复路径与空白路径被去重 / 忽略，不会重复产生结果条目。</summary>
    [Fact]
    public void Delete_DuplicateAndBlankPaths_AreProcessedOnce()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("dup");

        var result = new EmptyFolderCleanerService().Delete(
            new[] { directory, directory.ToUpperInvariant(), string.Empty, "   " },
            useRecycleBin: false);

        var item = Assert.Single(result.Items);
        Assert.True(item.Success);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>没有任何目标时返回空结果且整体成功。</summary>
    [Fact]
    public void Delete_WhenListIsEmpty_ReturnsEmptySuccessResult()
    {
        var result = new EmptyFolderCleanerService().Delete(Array.Empty<string>(), useRecycleBin: false);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.Success);
        Assert.False(result.UsedRecycleBin);
    }
}
