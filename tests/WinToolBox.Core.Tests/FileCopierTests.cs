using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// FileCopier 单元测试：覆盖增量复制判定、追加式复制、排除规则、流式复制完整性、
/// 时间戳对齐、取消、进度回调与异常参数校验。
/// 所有测试都在独立临时目录中进行，不接触真实 U 盘，也不使用系统 %AppData%。
/// </summary>
public sealed class FileCopierTests
{
    // 时间基准固定为过去，避免“目标时间 &gt;= 源时间”造成增量判定歧义
    private static readonly DateTime BaseTimeUtc = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // 基础复制与目录结构
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_CopiesNewFilesAndKeepsDirectoryStructure()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile(@"docs\readme.txt", "readme", BaseTimeUtc);
        ws.CreateFile(@"docs\sub\note.txt", "note", BaseTimeUtc);
        ws.CreateFile("root.txt", "root", BaseTimeUtc);

        var copier = new FileCopier();
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 首次复制：3 个文件全部复制，无跳过、无失败
        Assert.Equal(3, result.CopiedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.False(result.Cancelled);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);

        // 目录结构保持一致
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "docs", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "docs", "sub", "note.txt")));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "root.txt")));

        // 内容一致
        Assert.Equal("readme", TempWorkspace.ReadAllText(Path.Combine(ws.TargetDir, "docs", "readme.txt")));
        Assert.Equal("note", TempWorkspace.ReadAllText(Path.Combine(ws.TargetDir, "docs", "sub", "note.txt")));
        Assert.Equal("root", TempWorkspace.ReadAllText(Path.Combine(ws.TargetDir, "root.txt")));

        // 新建 docs、docs\sub 两个目录
        Assert.Equal(2, result.CreatedDirectories);

        // 源目录未被污染
        Assert.False(Directory.Exists(Path.Combine(ws.SourceDir, "target")));
    }

    [Fact]
    public void CopyDirectory_CreatesTargetDirectoryAutomatically()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "a", BaseTimeUtc);

        Assert.False(Directory.Exists(ws.TargetDir));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.True(Directory.Exists(ws.TargetDir));
        Assert.Equal(1, result.CopiedFiles);
    }

    [Fact]
    public void CopyDirectory_EmptySourceDirectory_SucceedsWithoutCopying()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.SourceDir);

        var progressCount = 0;
        var result = new FileCopier().CopyDirectory(
            ws.SourceDir,
            ws.TargetDir,
            _ => progressCount++);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(0, progressCount);
        Assert.True(result.Success);
    }

    // ------------------------------------------------------------------
    // 增量复制判定
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_SkipsUnchangedFiles_OnSecondRun()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "content-a", BaseTimeUtc);
        ws.CreateFile(@"sub\b.txt", "content-b", BaseTimeUtc);

        var copier = new FileCopier();
        var first = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);
        Assert.Equal(2, first.CopiedFiles);

        var hashBefore = TempWorkspace.ComputeFileSha256(Path.Combine(ws.TargetDir, "a.txt"));

        var second = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 第二次复制：大小相同且目标时间不早于源时间 → 全部跳过
        Assert.Equal(0, second.CopiedFiles);
        Assert.Equal(2, second.SkippedFiles);
        Assert.Equal(0, second.FailedFiles);
        Assert.Equal(0, second.CopiedBytes);
        Assert.Equal(hashBefore, TempWorkspace.ComputeFileSha256(Path.Combine(ws.TargetDir, "a.txt")));
    }

    [Fact]
    public void CopyDirectory_RecopiesOnlyUpdatedFile_WhenSourceTimestampIsNewer()
    {
        using var ws = new TempWorkspace();
        var sourceA = ws.CreateFile("a.txt", "content-a", BaseTimeUtc);
        ws.CreateFile("b.txt", "content-b", BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(2, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var targetA = Path.Combine(ws.TargetDir, "a.txt");
        var targetB = Path.Combine(ws.TargetDir, "b.txt");
        var bTimeBefore = File.GetLastWriteTimeUtc(targetB);

        // 让 a.txt 在源目录“看起来更新”
        File.SetLastWriteTimeUtc(sourceA, DateTime.UtcNow.AddMinutes(5));

        var second = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 只重拷 a.txt，b.txt 被跳过
        Assert.Equal(1, second.CopiedFiles);
        Assert.Equal(1, second.SkippedFiles);
        Assert.Equal("content-a", TempWorkspace.ReadAllText(targetA));
        Assert.Equal(File.GetLastWriteTimeUtc(sourceA), File.GetLastWriteTimeUtc(targetA));
        Assert.Equal(bTimeBefore, File.GetLastWriteTimeUtc(targetB));
    }

    [Fact]
    public void CopyDirectory_RecopiesFile_WhenSizeChangedEvenIfTargetIsNewer()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile("data.bin", "A", BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var targetFile = Path.Combine(ws.TargetDir, "data.bin");
        Assert.Equal("A", TempWorkspace.ReadAllText(targetFile));

        // 只改内容（长度变化），刻意让源时间反而更旧：大小不同必须重拷
        File.WriteAllText(sourceFile, "ABCDEFGHIJ");
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow.AddMinutes(-30));
        File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow);

        var second = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, second.CopiedFiles);
        Assert.Equal(0, second.SkippedFiles);
        Assert.Equal("ABCDEFGHIJ", TempWorkspace.ReadAllText(targetFile));
        Assert.Equal(10, new FileInfo(targetFile).Length);
    }

    [Fact]
    public void CopyDirectory_OverwritesOlderTarget_WhenSourceIsNewerAndSizeEqual()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile("same.txt", "OLDOLDOLD", BaseTimeUtc);

        var targetFile = Path.Combine(ws.TargetDir, "same.txt");
        ws.CreateFile(targetFile, "NEWNEWNEW", BaseTimeUtc.AddHours(1), ws.Root);

        Assert.Equal(9, new FileInfo(targetFile).Length);

        // 源更旧 + 大小相同 → 无需复制
        Assert.Equal(0, new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        // 源变新（大小仍相同）→ 覆盖
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow.AddHours(1));
        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal("OLDOLDOLD", TempWorkspace.ReadAllText(targetFile));
    }

    [Fact]
    public void CopyDirectory_WithOverwriteDisabled_KeepsExistingTargetContent()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile("keep.txt", "SOURCE-NEW", BaseTimeUtc);

        var targetFile = Path.Combine(ws.TargetDir, "keep.txt");
        ws.CreateFile(targetFile, "TARGET-OLD", BaseTimeUtc, ws.Root);

        // 即使源文件明显更新，禁用覆盖后也必须跳过并保持旧内容
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow.AddMinutes(10));

        var copier = new FileCopier { OverwriteExistingFiles = false };
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal("TARGET-OLD", TempWorkspace.ReadAllText(targetFile));
    }

    [Fact]
    public void CopyDirectory_IsAppendOnly_DoesNotDeleteExtraTargetFiles()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("new.txt", "new-file", BaseTimeUtc);

        var extraFile = Path.Combine(ws.TargetDir, "extra-old.txt");
        var extraNested = Path.Combine(ws.TargetDir, "old-folder", "keep.bin");
        ws.CreateFile(extraFile, "should-stay", BaseTimeUtc, ws.Root);
        ws.CreateFile(extraNested, "should-stay-too", BaseTimeUtc, ws.Root);

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);

        // 追加式复制：目标中多出来的文件/目录一律保留
        Assert.True(File.Exists(extraFile));
        Assert.Equal("should-stay", TempWorkspace.ReadAllText(extraFile));
        Assert.True(File.Exists(extraNested));
        Assert.Equal("should-stay-too", TempWorkspace.ReadAllText(extraNested));
        Assert.Equal(0, result.SkippedFiles);
    }

    // ------------------------------------------------------------------
    // 排除规则
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_AppliesDefaultExcludeRules()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("autorun.inf", "[autorun]", BaseTimeUtc);
        ws.CreateFile("desktop.ini", "[.ShellClassInfo]", BaseTimeUtc);
        ws.CreateFile("Thumbs.db", "thumb-cache", BaseTimeUtc);
        ws.CreateFile("draft.tmp", "temporary", BaseTimeUtc);
        ws.CreateFile("download.part", "partial", BaseTimeUtc);
        ws.CreateFile(@"System Volume Information\tracking.log", "system", BaseTimeUtc);
        ws.CreateFile(@"$RECYCLE.BIN\deleted.txt", "recycled", BaseTimeUtc);
        ws.CreateFile("keep.txt", "keep-me", BaseTimeUtc);
        ws.CreateFile(@"photos\pic.jpg", "fake-jpeg", BaseTimeUtc);

        // excludeRules 传 null 时必须自动使用默认规则
        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(2, result.CopiedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(0, result.FailedFiles);

        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "photos", "pic.jpg")));

        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "autorun.inf")));
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "desktop.ini")));
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "Thumbs.db")));
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "draft.tmp")));
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "download.part")));
        Assert.False(Directory.Exists(Path.Combine(ws.TargetDir, "System Volume Information")));
        Assert.False(Directory.Exists(Path.Combine(ws.TargetDir, "$RECYCLE.BIN")));
    }

    [Fact]
    public void CopyDirectory_WithCustomExtensionRules_ExcludesOnlyThatExtension()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("note.txt", "text", BaseTimeUtc);
        ws.CreateFile("note.md", "markdown", BaseTimeUtc);
        ws.CreateFile("cache.log", "log", BaseTimeUtc);

        var rules = new ExcludeRules(extensions: new[] { ".log" });
        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir, null, rules);

        Assert.Equal(2, result.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "note.txt")));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "note.md")));
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "cache.log")));
    }

    [Fact]
    public void CopyDirectory_AppliesCustomDirectoryExcludeRule()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile(@"secret\hidden.txt", "hidden", BaseTimeUtc);
        ws.CreateFile("visible.txt", "visible", BaseTimeUtc);

        var rules = new ExcludeRules(
            directoryNames: new[] { "secret" },
            fileNames: ExcludeRules.DefaultFileNames,
            extensions: ExcludeRules.DefaultExtensions);

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir, null, rules);

        Assert.Equal(1, result.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "visible.txt")));
        Assert.False(Directory.Exists(Path.Combine(ws.TargetDir, "secret")));
    }

    // ------------------------------------------------------------------
    // 时间戳、内容完整性与缓冲
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_AlignsTargetTimestampWithSource()
    {
        using var ws = new TempWorkspace();
        var expected = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var sourceFile = ws.CreateFile(@"nested\stamp.txt", "stamped", expected);

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        var targetFile = Path.Combine(ws.TargetDir, "nested", "stamp.txt");
        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(expected, File.GetLastWriteTimeUtc(sourceFile));
        Assert.Equal(File.GetLastWriteTimeUtc(sourceFile), File.GetLastWriteTimeUtc(targetFile));
    }

    [Fact]
    public void CopyDirectory_WhenPreserveTimestampsDisabled_TargetTimestampDiffersFromSource()
    {
        using var ws = new TempWorkspace();
        var expected = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        ws.CreateFile(@"nested\stamp.txt", "stamped", expected);

        var copier = new FileCopier { PreserveTimestamps = false };
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        var targetFile = Path.Combine(ws.TargetDir, "nested", "stamp.txt");
        Assert.Equal(1, result.CopiedFiles);
        Assert.NotEqual(expected, File.GetLastWriteTimeUtc(targetFile));

        // 内容仍然正确，只是时间戳没有被对齐
        Assert.Equal("stamped", TempWorkspace.ReadAllText(targetFile));
    }

    [Fact]
    public void CopyDirectory_CopiesLargeBinaryFile_ByteForByte()
    {
        using var ws = new TempWorkspace();
        const int size = 4 * 1024 * 1024; // 4 MiB，跨越多个 1 MiB 缓冲区
        ws.CreateRandomBinaryFile("big.bin", size, seed: 42);

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        var sourceFile = Path.Combine(ws.SourceDir, "big.bin");
        var targetFile = Path.Combine(ws.TargetDir, "big.bin");

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(size, result.CopiedBytes);
        Assert.Equal(size, new FileInfo(targetFile).Length);

        // SHA256 完全一致 → 流式复制内容无损坏
        Assert.Equal(
            TempWorkspace.ComputeFileSha256(sourceFile),
            TempWorkspace.ComputeFileSha256(targetFile));
    }

    [Fact]
    public void CopyDirectory_WithSmallBufferSize_StillCopiesContentCorrectly()
    {
        using var ws = new TempWorkspace();
        const int size = 256 * 1024;
        ws.CreateRandomBinaryFile("chunked.bin", size, seed: 7);

        var copier = new FileCopier { BufferSize = 1024 }; // 强制多次读写循环
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        var targetFile = Path.Combine(ws.TargetDir, "chunked.bin");
        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(size, new FileInfo(targetFile).Length);
        Assert.Equal(
            TempWorkspace.ComputeFileSha256(Path.Combine(ws.SourceDir, "chunked.bin")),
            TempWorkspace.ComputeFileSha256(targetFile));
    }

    [Fact]
    public void CopyFile_CopiesSingleFileAndCreatesTargetDirectory()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile(@"nested\single.txt", "single-file", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "deep", "single.txt");

        new FileCopier().CopyFile(sourceFile, targetFile);

        Assert.True(File.Exists(targetFile));
        Assert.Equal("single-file", TempWorkspace.ReadAllText(targetFile));
        Assert.Equal(File.GetLastWriteTimeUtc(sourceFile), File.GetLastWriteTimeUtc(targetFile));
    }

    [Fact]
    public void CopyFile_WhenSourceMissing_ThrowsFileNotFoundException()
    {
        using var ws = new TempWorkspace();
        var missing = Path.Combine(ws.SourceDir, "not-exists.txt");

        Assert.Throws<FileNotFoundException>(() =>
            new FileCopier().CopyFile(missing, Path.Combine(ws.TargetDir, "out.txt")));
    }

    [Fact]
    public void CopyFile_WhenPathIsEmpty_ThrowsArgumentException()
    {
        using var ws = new TempWorkspace();

        Assert.Throws<ArgumentException>(() => new FileCopier().CopyFile("", Path.Combine(ws.TargetDir, "out.txt")));
        Assert.Throws<ArgumentException>(() => new FileCopier().CopyFile("   ", Path.Combine(ws.TargetDir, "out.txt")));
    }

    // ------------------------------------------------------------------
    // 异常与参数校验
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_WhenSourceDirectoryMissing_ThrowsDirectoryNotFoundException()
    {
        using var ws = new TempWorkspace();
        var missingSource = Path.Combine(ws.Root, "no-such-source");

        Assert.Throws<DirectoryNotFoundException>(() =>
            new FileCopier().CopyDirectory(missingSource, ws.TargetDir));
    }

    [Fact]
    public void CopyDirectory_WhenTargetIsInsideSource_ThrowsArgumentException()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "a", BaseTimeUtc);

        // 目标位于源内部会造成自我递归复制
        Assert.Throws<ArgumentException>(() =>
            new FileCopier().CopyDirectory(ws.SourceDir, Path.Combine(ws.SourceDir, "backup")));

        // 目标等于源同样非法
        Assert.Throws<ArgumentException>(() =>
            new FileCopier().CopyDirectory(ws.SourceDir, ws.SourceDir));
    }

    [Fact]
    public void CopyDirectory_WhenArgumentIsNullOrWhitespace_ThrowsArgumentException()
    {
        using var ws = new TempWorkspace();
        var copier = new FileCopier();

        Assert.Throws<ArgumentException>(() => copier.CopyDirectory("", ws.TargetDir));
        Assert.Throws<ArgumentException>(() => copier.CopyDirectory("   ", ws.TargetDir));
        Assert.Throws<ArgumentException>(() => copier.CopyDirectory(ws.SourceDir, ""));
        Assert.Throws<ArgumentException>(() => copier.CopyDirectory(ws.SourceDir, "   "));
    }

    [Fact]
    public void CopyDirectory_WhenSingleFileFails_RecordsErrorAndContinues()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "aaa", BaseTimeUtc);
        ws.CreateFile("b.txt", "bbb", BaseTimeUtc);

        // 在目标位置预先造一个同名“目录”，让 a.txt 的写入必然失败
        Directory.CreateDirectory(Path.Combine(ws.TargetDir, "a.txt"));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 失败被记录但不中断整体复制
        Assert.Equal(1, result.FailedFiles);
        Assert.Single(result.Errors);
        Assert.Equal("bbb", TempWorkspace.ReadAllText(Path.Combine(ws.TargetDir, "b.txt")));
        Assert.False(result.Success);

        // 摘要里包含失败数量
        Assert.Contains("失败 1", result.Summary);
    }

    // ------------------------------------------------------------------
    // 取消与进度回调
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_WhenTokenAlreadyCancelled_ReturnsCancelledWithoutCopying()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "a", BaseTimeUtc);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new FileCopier().CopyDirectory(
            ws.SourceDir,
            ws.TargetDir,
            null,
            null,
            cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.CopiedFiles);
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "a.txt")));
        Assert.False(result.Success);
        Assert.Contains("已取消", result.Summary);
    }

    [Fact]
    public void CopyDirectory_ReportsProgressForEveryFileInMonotonicOrder()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "aaaaa", BaseTimeUtc);
        ws.CreateFile(@"sub\b.txt", "bbbbbbbbbb", BaseTimeUtc);

        var snapshots = new List<CopyProgress>();
        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir, snapshots.Add);

        // 每个文件恰好回调一次
        Assert.Equal(result.CopiedFiles + result.SkippedFiles + result.FailedFiles, snapshots.Count);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(2, snapshots[0].TotalFiles);
        Assert.Equal(2, snapshots[1].TotalFiles);
        Assert.Equal(15, snapshots[0].TotalBytes);

        // ProcessedFiles 单调递增，最后一次等于总文件数
        Assert.Equal(1, snapshots[0].ProcessedFiles);
        Assert.Equal(2, snapshots[1].ProcessedFiles);
        Assert.True(snapshots[1].ProcessedFiles == snapshots[1].TotalFiles);
        Assert.True(snapshots[1].Percent > snapshots[0].Percent);

        // CurrentFile 是相对源目录的路径
        Assert.Equal("a.txt", snapshots[0].CurrentFile);
        Assert.Equal(Path.Combine("sub", "b.txt"), snapshots[1].CurrentFile);
    }
}
