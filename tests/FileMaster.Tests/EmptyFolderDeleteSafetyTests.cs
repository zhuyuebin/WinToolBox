using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// P0-2 回归测试：清理空目录必须是「无差别递归删除」的安全替代品。
/// <para>修复前：<c>EmptyFolderCleanerService.Delete</c> 只检查「目录是否还在」，
/// 随后调用 <c>SafeDelete.DeleteDirectory</c> 做 <c>Directory.Delete(recursive: true)</c>；
/// 扫描与删除之间被放进目录的文件会被一起永久删除。</para>
/// <para>修复后：删除前重新判空，非空则跳过；永久删除路径改用
/// <c>Directory.Delete(recursive: false)</c>，非空即抛异常，天然不会连带删除内容。</para>
/// </summary>
public sealed class EmptyFolderDeleteSafetyTests
{
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
    // 核心场景：扫描之后往目录里塞文件
    // ------------------------------------------------------------------

    /// <summary>扫描后往空目录里放文件，再执行删除：该目录必须被跳过，文件必须保住。</summary>
    [Fact]
    public void Delete_SkipsDirectory_ThatBecameNonEmptyAfterScan()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("empty-then-filled");

        var service = new EmptyFolderCleanerService();
        var scan = service.Scan(ws.Root);
        Assert.Equal(1, scan.TotalCount);
        Assert.True(EmptyFolderCleanerService.IsEmptyDirectory(directory));

        // 扫描完成之后，用户 / 其它程序往目录里放了文件
        var lateFile = CreateFile(ws, Path.Combine("empty-then-filled", "late.txt"), "important");
        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(directory));

        var result = service.Delete(scan.Items.Select(item => item.FullPath), useRecycleBin: false);

        var item = Assert.Single(result.Items);
        Assert.True(item.Skipped, "目录已不再为空，必须被跳过");
        Assert.NotNull(item.SkipReason);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);

        // 最关键的一条：后来出现的文件必须还在
        Assert.True(File.Exists(lateFile), "后续放进去的文件绝不能被删除");
        Assert.Equal("important", File.ReadAllText(lateFile));
        Assert.True(Directory.Exists(directory));
        Assert.Contains("跳过", result.Summary);
    }

    /// <summary>扫描后子目录里出现文件时，父目录与子目录都不许被删。</summary>
    [Fact]
    public void Delete_SkipsParentAndChild_WhenNestedDirectoryBecameNonEmpty()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory(Path.Combine("parent", "child"));
        ws.CreateDirectory("sibling");

        var service = new EmptyFolderCleanerService();
        var scan = service.Scan(ws.Root);
        Assert.Equal(3, scan.TotalCount);

        // 在最深的子目录里塞一个文件
        var lateFile = CreateFile(ws, Path.Combine("parent", "child", "late.txt"));
        var sibling = ws.PathOf("sibling");

        var result = service.Delete(scan.Items.Select(item => item.FullPath), useRecycleBin: false);

        Assert.True(File.Exists(lateFile), "子目录里后加入的文件必须还在");
        Assert.True(Directory.Exists(ws.PathOf("parent")), "父目录已不再为空，不能被删");
        Assert.True(Directory.Exists(ws.PathOf(Path.Combine("parent", "child"))));

        // 没被影响的兄弟目录照常删除
        Assert.False(Directory.Exists(sibling));
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
    }

    /// <summary>扫描后目录里出现子目录（子目录里再有文件）同样要跳过。</summary>
    [Fact]
    public void Delete_SkipsDirectory_WhenLateSubDirectoryWithFileAppeared()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("target");

        var service = new EmptyFolderCleanerService();
        var scan = service.Scan(ws.Root);
        Assert.Equal(1, scan.TotalCount);

        CreateFile(ws, Path.Combine("target", "new", "deep.txt"));

        var result = service.Delete(scan.Items.Select(item => item.FullPath), useRecycleBin: false);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.True(File.Exists(ws.PathOf(Path.Combine("target", "new", "deep.txt"))));
    }

    // ------------------------------------------------------------------
    // 仍然为空时必须正常删除
    // ------------------------------------------------------------------

    /// <summary>复查仍然为空 → 照常删除（不要把安全检查变成「什么都不删」）。</summary>
    [Fact]
    public void Delete_StillDeletes_WhenDirectoryRemainsEmpty()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("really-empty");

        var service = new EmptyFolderCleanerService();
        var scan = service.Scan(ws.Root);

        var result = service.Delete(scan.Items.Select(item => item.FullPath), useRecycleBin: false);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.False(Directory.Exists(directory));
        Assert.Contains("永久删除", result.Summary);
    }

    /// <summary>目录已经不存在（被父目录一并删除）仍算成功，不计入跳过。</summary>
    [Fact]
    public void Delete_AlreadyMissingDirectory_IsNotCountedAsSkipped()
    {
        using var ws = new TempWorkspace();
        var missing = ws.PathOf("gone");

        var result = new EmptyFolderCleanerService().Delete(new[] { missing }, useRecycleBin: false);

        var item = Assert.Single(result.Items);
        Assert.True(item.Success);
        Assert.False(item.Skipped);
        Assert.True(item.Deleted);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    // ------------------------------------------------------------------
    // 回收站 / 永久删除两种模式
    // ------------------------------------------------------------------

    /// <summary>useRecycleBin: true 时走回收站：目录从原位置消失，但结果标记为已放入回收站。</summary>
    [Fact]
    public void Delete_WithRecycleBin_MovesDirectoryToRecycleBin()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("to-recycle");

        var result = new EmptyFolderCleanerService().Delete(new[] { directory }, useRecycleBin: true);

        Assert.True(result.Success, result.Summary);
        Assert.True(result.UsedRecycleBin);
        Assert.Equal(1, result.DeletedCount);
        Assert.False(Directory.Exists(directory), "放入回收站后原路径应已不存在");
        Assert.Contains("已放入回收站", result.Summary);
    }

    /// <summary>useRecycleBin: false 时永久删除，摘要必须明确写「已永久删除」而不是含糊的「已删除」。</summary>
    [Fact]
    public void Delete_WithPermanentDelete_SummarySaysPermanent()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("to-erase");

        var result = new EmptyFolderCleanerService().Delete(new[] { directory }, useRecycleBin: false);

        Assert.False(result.UsedRecycleBin);
        Assert.Contains("永久删除", result.Summary);
        Assert.DoesNotContain("回收站", result.Summary);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>空目录清理绝不能被用来删掉「非空目录」——这是 P0-2 的安全底线。</summary>
    [Fact]
    public void IsEmptyDirectory_ReturnsFalse_ForDirectoryContainingFile()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("has-file");
        CreateFile(ws, Path.Combine("has-file", "a.txt"));

        Assert.False(EmptyFolderCleanerService.IsEmptyDirectory(directory));
    }

    // ------------------------------------------------------------------
    // SafeDelete.DeleteEmptyDirectory：两条路径都必须有非空保护
    // ------------------------------------------------------------------

    /// <summary>永久删除路径：非空目录必须抛异常，且内容原封不动。</summary>
    [Fact]
    public void SafeDelete_DeleteEmptyDirectory_PermanentPath_RefusesNonEmptyDirectory()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("non-empty");
        var file = CreateFile(ws, Path.Combine("non-empty", "keep.txt"), "data");

        Assert.Throws<IOException>(() => SafeDelete.DeleteEmptyDirectory(directory, useRecycleBin: false));

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(file));
        Assert.Equal("data", File.ReadAllText(file));
    }

    /// <summary>
    /// 回收站路径同样必须拒绝非空目录。
    /// verifier 实测过修复前的行为：<c>FileSystem.DeleteDirectory</c> 是递归语义，
    /// 非空目录会被整体移入回收站，与「只删空目录」的承诺不符。
    /// </summary>
    [Fact]
    public void SafeDelete_DeleteEmptyDirectory_RecycleBinPath_RefusesNonEmptyDirectory()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("non-empty-recycle");
        var file = CreateFile(ws, Path.Combine("non-empty-recycle", "keep.txt"), "data");

        Assert.Throws<IOException>(() => SafeDelete.DeleteEmptyDirectory(directory, useRecycleBin: true));

        Assert.True(Directory.Exists(directory), "非空目录不得被移入回收站");
        Assert.True(File.Exists(file), "目录里的文件必须原封不动");
        Assert.Equal("data", File.ReadAllText(file));
    }

    /// <summary>空目录在两条路径下都要能正常删除。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SafeDelete_DeleteEmptyDirectory_DeletesEmptyDirectory(bool useRecycleBin)
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("vacant");

        SafeDelete.DeleteEmptyDirectory(directory, useRecycleBin);

        Assert.False(Directory.Exists(directory));
    }

    // ------------------------------------------------------------------
    // 永久删除的二次确认文案
    // ------------------------------------------------------------------

    /// <summary>永久删除的确认文案必须同时出现「永久删除」「不进回收站」「无法恢复」。</summary>
    [Fact]
    public void PermanentConfirmMessage_MentionsPermanentAndNoRecycleBin()
    {
        var message = EmptyFolderDeleteMessages.BuildConfirmMessage(3, useRecycleBin: false);

        Assert.Contains("3", message);
        Assert.Contains("永久删除", message);
        Assert.Contains("不进回收站", message);
        Assert.Contains("无法恢复", message);
    }

    /// <summary>回收站确认文案不应出现「永久删除」这类吓人的措辞。</summary>
    [Fact]
    public void RecycleConfirmMessage_DoesNotClaimPermanentDelete()
    {
        var message = EmptyFolderDeleteMessages.BuildConfirmMessage(3, useRecycleBin: true);

        Assert.Contains("回收站", message);
        Assert.Contains("还原", message);
        Assert.DoesNotContain("永久删除", message);
        Assert.DoesNotContain("无法恢复", message);
    }

    /// <summary>确认框标题同样要区分两种删除方式。</summary>
    [Fact]
    public void ConfirmTitles_DifferByDeletionMode()
    {
        Assert.Contains("永久删除", EmptyFolderDeleteMessages.GetConfirmTitle(useRecycleBin: false));
        Assert.Contains("不进回收站", EmptyFolderDeleteMessages.GetConfirmTitle(useRecycleBin: false));
        Assert.Contains("回收站", EmptyFolderDeleteMessages.GetConfirmTitle(useRecycleBin: true));
        Assert.DoesNotContain("永久删除", EmptyFolderDeleteMessages.GetConfirmTitle(useRecycleBin: true));
    }

    // ------------------------------------------------------------------
    // AutoArchiver 的清理空目录开关
    // ------------------------------------------------------------------

    /// <summary>ArchiveOptions 默认必须走回收站（不能被静默改成永久删除）。</summary>
    [Fact]
    public void ArchiveOptions_DefaultsToRecycleBinForEmptyFolderCleanup()
    {
        var options = new ArchiveOptions();

        Assert.True(options.CleanEmptyFoldersUseRecycleBin);
    }

    /// <summary>
    /// 移动后清理空目录时，应该沿用 <see cref="ArchiveOptions.CleanEmptyFoldersUseRecycleBin"/>，
    /// 而不是硬编码永久删除；这里验证「放入回收站」这一档确实不会真正抹掉目录。
    /// </summary>
    [Fact]
    public void Apply_CleanEmptyFoldersWithRecycleBin_DoesNotEraseDirectoryPermanently()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateDirectory("source");
        var emptyChild = Path.Combine(source, "empty-child");
        Directory.CreateDirectory(emptyChild);

        CreateFile(ws, Path.Combine("source", "a.txt"), "content");

        var options = new ArchiveOptions
        {
            SourceDirectory = source,
            TargetDirectory = ws.PathOf("target"),
            IncludeSubDirectories = false,
            Action = ArchiveAction.Move,
            Overwrite = false,
            CleanEmptyFolders = true,
            CleanEmptyFoldersUseRecycleBin = true,
            Rules = new[]
            {
                new ArchiveRule { Kind = ArchiveRuleKind.Fallback, TargetSubDirectory = "moved" }
            }
        };

        var service = new AutoArchiverService();
        var plan = service.BuildPlan(options);
        Assert.Null(plan.Error);

        var result = service.Apply(plan, options);
        Assert.Equal(0, result.FailedCount);

        // 文件确实被移走，空目录确实被清理
        Assert.True(File.Exists(ws.PathOf(Path.Combine("target", "moved", "a.txt"))));
        Assert.False(Directory.Exists(emptyChild));
        Assert.Equal(1, result.RemovedEmptyFolderCount);
    }
}
