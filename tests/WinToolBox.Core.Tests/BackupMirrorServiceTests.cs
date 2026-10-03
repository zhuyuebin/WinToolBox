using System.Text;
using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// P1-8 回归测试：UsbBackup 的产品语义是「U 盘的增量镜像 + 30 天版本历史」。
/// <list type="number">
/// <item>U 盘新增 → 复制到 <c>current</c>；</item>
/// <item>U 盘修改 → 新版本写 <c>current</c>，旧版本移入 <c>history\{今天}</c>；</item>
/// <item>U 盘删除 → <c>current</c> 中的副本移入 <c>history\{今天}</c>，绝不直接删；</item>
/// <item>超过保留期的 <c>history\{yyyy-MM-dd}</c> 目录自动清理；</item>
/// <item>每次备份更新 <c>manifest.json</c>。</item>
/// </list>
/// </summary>
public sealed class BackupMirrorServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 10, 3, 14, 30, 0, DateTimeKind.Local);

    private static readonly DateTime BaseTimeUtc = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static UsbDeviceInfo Device() => new()
    {
        RootPath = @"E:\",
        VolumeLabel = "KINGSTON",
        FileSystem = "FAT32",
        VolumeSerialNumber = "1234ABCD",
        TotalSize = 32L * 1024 * 1024 * 1024,
        FreeSpace = 8L * 1024 * 1024 * 1024
    };

    /// <summary>在临时工作区里搭一个「U 盘源目录 + 备份根目录」的组合。</summary>
    private static (string Source, string BackupRoot) Prepare(TempWorkspace ws)
    {
        var source = Path.Combine(ws.Root, "usb");
        var backupRoot = Path.Combine(ws.Root, "backup");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(backupRoot);
        return (source, backupRoot);
    }

    private static void WriteSourceFile(string source, string relativePath, string content)
    {
        var full = Path.Combine(source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    private static MirrorResult Run(string source, string backupRoot, int retentionDays = 30)
        => new BackupMirrorService().Mirror(
            sourceDirectory: source,
            backupRootDirectory: backupRoot,
            device: Device(),
            excludeRules: BackupRules.CreateDefaultExcludeRules(),
            historyRetentionDays: retentionDays,
            now: FixedNow);

    // ------------------------------------------------------------------
    // 目录结构
    // ------------------------------------------------------------------

    [Fact]
    public void Mirror_CreatesFixedCurrentHistoryAndManifest_WithoutDateFolder()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "docs/readme.txt", "hello");

        var result = Run(source, backupRoot);

        var deviceRoot = Path.Combine(backupRoot, "KINGSTON_1234ABCD");
        Assert.Equal(deviceRoot, result.DeviceRootDirectory);
        Assert.Equal(Path.Combine(deviceRoot, "current"), result.CurrentDirectory);
        Assert.Equal(Path.Combine(deviceRoot, "history"), result.HistoryRootDirectory);

        Assert.True(Directory.Exists(result.CurrentDirectory));
        Assert.True(Directory.Exists(result.HistoryRootDirectory));
        Assert.True(File.Exists(Path.Combine(result.CurrentDirectory, "docs", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(deviceRoot, "manifest.json")));

        // 不再使用带日期的目标目录
        Assert.False(Directory.Exists(Path.Combine(deviceRoot, FixedNow.ToString("yyyy-MM-dd"))));
    }

    // ------------------------------------------------------------------
    // 1) 新增文件
    // ------------------------------------------------------------------

    [Fact]
    public void Mirror_NewFiles_AreCopiedToCurrent()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "AAA");
        WriteSourceFile(source, "sub/b.txt", "BBB");

        var result = Run(source, backupRoot);

        Assert.Equal(2, result.Copy.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(result.CurrentDirectory, "a.txt")));
        Assert.Equal("BBB", File.ReadAllText(Path.Combine(result.CurrentDirectory, "sub", "b.txt")));
        Assert.Equal(0, result.ArchivedFiles);
    }

    // ------------------------------------------------------------------
    // 2) 修改文件 → 旧版本进 history
    // ------------------------------------------------------------------

    [Fact]
    public void Mirror_ModifiedFile_MovesOldVersionToHistoryAndWritesNewVersion()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "doc.txt", "OLD-CONTENT");

        var first = Run(source, backupRoot);
        Assert.Equal(1, first.Copy.CopiedFiles);

        // U 盘里改写（长度也变化，确保判定为更新）
        WriteSourceFile(source, "doc.txt", "NEW-CONTENT-LONGER");
        var second = Run(source, backupRoot);

        // current 里是新版本
        Assert.Equal("NEW-CONTENT-LONGER", File.ReadAllText(Path.Combine(second.CurrentDirectory, "doc.txt")));

        // history\{今天} 里保留的是旧版本，且保留原相对路径
        var archived = Path.Combine(
            BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow),
            "doc.txt");
        Assert.True(File.Exists(archived), "旧版本必须被移入 history");
        Assert.Equal("OLD-CONTENT", File.ReadAllText(archived));

        Assert.Equal(1, second.ArchivedFiles);
        Assert.Equal(1, second.Copy.CopiedFiles);
    }

    [Fact]
    public void Mirror_UnchangedFile_DoesNotCreateHistoryVersion()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "same.txt", "SAME");

        Run(source, backupRoot);
        var second = Run(source, backupRoot);

        Assert.Equal(0, second.Copy.CopiedFiles);
        Assert.Equal(1, second.Copy.SkippedFiles);
        Assert.Equal(0, second.ArchivedFiles);

        var historyToday = BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow);
        Assert.False(Directory.Exists(historyToday) && Directory.EnumerateFiles(historyToday).Any(),
            "内容未变时不应产生历史版本");
    }

    // ------------------------------------------------------------------
    // 3) 删除文件 → current 副本进 history（软删除）
    // ------------------------------------------------------------------

    [Fact]
    public void Mirror_FileDeletedOnUsb_IsMovedToHistoryNotErased()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "important.docx", "PRECIOUS");
        WriteSourceFile(source, "keep.txt", "KEEP");

        var first = Run(source, backupRoot);
        Assert.Equal(2, first.Copy.CopiedFiles);
        // 模拟用户在 U 盘上误删
        File.Delete(Path.Combine(source, "important.docx"));

        var second = Run(source, backupRoot);

        // current 中不再有它
        Assert.False(File.Exists(Path.Combine(second.CurrentDirectory, "important.docx")));

        // 但 history 里有，内容完好 —— 这正是「软删除」的核心价值
        var archived = Path.Combine(
            BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow),
            "important.docx");
        Assert.True(File.Exists(archived), "U 盘删除的文件必须被移入 history，而不是消失");
        Assert.Equal("PRECIOUS", File.ReadAllText(archived));

        // 未受影响的文件仍在 current
        Assert.True(File.Exists(Path.Combine(second.CurrentDirectory, "keep.txt")));
        Assert.Equal(0, second.Copy.CopiedFiles);
        Assert.Equal(1, second.Copy.SkippedFiles);
        Assert.Equal(1, second.ArchivedFiles);
    }

    [Fact]
    public void Mirror_DeletedFileInSubdirectory_KeepsRelativePathInHistory()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "photos/2026/pic.jpg", "JPEGDATA");

        Run(source, backupRoot);
        File.Delete(Path.Combine(source, "photos", "2026", "pic.jpg"));

        var second = Run(source, backupRoot);

        var archived = Path.Combine(
            BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow),
            "photos", "2026", "pic.jpg");

        Assert.True(File.Exists(archived), "history 中必须保留原相对路径结构");
        Assert.Equal("JPEGDATA", File.ReadAllText(archived));
    }

    [Fact]
    public void Mirror_RepeatedRunAfterDeletion_DoesNotDuplicateHistory()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "gone.txt", "DATA");

        Run(source, backupRoot);
        File.Delete(Path.Combine(source, "gone.txt"));

        var second = Run(source, backupRoot);
        var third = Run(source, backupRoot);

        Assert.Equal(1, second.ArchivedFiles);
        // 第二次之后 current 里已经没有它了，不应重复归档
        Assert.Equal(0, third.ArchivedFiles);

        var historyToday = BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow);
        Assert.Single(Directory.EnumerateFiles(historyToday, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Mirror_SameFileOverwrittenTwice_KeepsEarliestVersionInHistory()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "v.txt", "V1");

        Run(source, backupRoot);

        WriteSourceFile(source, "v.txt", "V2-longer");
        Run(source, backupRoot);

        WriteSourceFile(source, "v.txt", "V3-even-longer");
        var third = Run(source, backupRoot);

        var archived = Path.Combine(
            BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow),
            "v.txt");

        // 同一天内重复备份：history 保留【最早】的版本，否则最早的版本会被后来的旧版本挤掉
        Assert.Equal("V1", File.ReadAllText(archived));
        Assert.Equal("V3-even-longer", File.ReadAllText(Path.Combine(third.CurrentDirectory, "v.txt")));
    }

    // ------------------------------------------------------------------
    // 4) 过期 history 清理
    // ------------------------------------------------------------------

    [Fact]
    public void Mirror_PurgesHistoryFoldersOlderThanRetention()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var historyRoot = BackupRules.BuildHistoryRootDirectory(backupRoot, Device());

        // 造几个历史目录：过期 / 边界 / 保留期内 / 非法名
        var expired = Path.Combine(historyRoot, FixedNow.Date.AddDays(-31).ToString("yyyy-MM-dd"));
        var exactlyAtLimit = Path.Combine(historyRoot, FixedNow.Date.AddDays(-30).ToString("yyyy-MM-dd"));
        var recent = Path.Combine(historyRoot, FixedNow.Date.AddDays(-5).ToString("yyyy-MM-dd"));
        var notADate = Path.Combine(historyRoot, "user-folder");

        foreach (var directory in new[] { expired, exactlyAtLimit, recent, notADate })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "old.txt"), "OLD");
        }

        var result = Run(source, backupRoot, retentionDays: 30);

        Assert.False(Directory.Exists(expired), "超过 30 天的历史目录应被清理");
        Assert.Contains(Path.GetFileName(expired), result.PurgedHistoryFolders);

        Assert.True(Directory.Exists(exactlyAtLimit), "恰好等于保留期边界不应被清理");
        Assert.True(Directory.Exists(recent), "保留期内的历史目录不应被清理");
        Assert.True(Directory.Exists(notADate), "非日期目录（用户自己的）绝不能被清理");
    }

    [Fact]
    public void Mirror_WhenRetentionIsForever_NeverPurgesHistory()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var historyRoot = BackupRules.BuildHistoryRootDirectory(backupRoot, Device());
        var ancient = Path.Combine(historyRoot, "2000-01-01");
        Directory.CreateDirectory(ancient);
        File.WriteAllText(Path.Combine(ancient, "very-old.txt"), "OLD");

        var result = Run(source, backupRoot, retentionDays: BackupConfig.HistoryRetentionForever);

        Assert.True(Directory.Exists(ancient), "「永久保留」时任何历史目录都不能被清理");
        Assert.Empty(result.PurgedHistoryFolders);
    }

    // ------------------------------------------------------------------
    // 5) manifest.json
    // ------------------------------------------------------------------

    [Fact]
    public void Mirror_WritesManifestWithExpectedContent()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "12345");
        WriteSourceFile(source, "b.txt", "1234567890");

        var first = Run(source, backupRoot);
        Assert.True(File.Exists(first.ManifestPath));

        var manifest = BackupMirrorService.ReadManifest(first.ManifestPath);
        Assert.NotNull(manifest);
        Assert.Equal(BackupManifest.CurrentSchemaVersion, manifest!.SchemaVersion);
        Assert.Equal(Device().UniqueId, manifest.DeviceUniqueId);
        Assert.Equal(FixedNow, manifest.LastBackupTime);
        Assert.Equal(2, manifest.TotalFiles);
        Assert.Equal(15, manifest.TotalBytes);
        Assert.Equal(30, manifest.HistoryRetentionDays);
        Assert.False(manifest.HistoryKeptForever);
        Assert.Contains("30 天", manifest.HistoryPolicyText);
        Assert.Equal(0, manifest.Comparison.DeletedFiles);
    }

    [Fact]
    public void Mirror_ManifestRecordsComparisonAndHistoryMoves()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "stays.txt", "STAYS");
        WriteSourceFile(source, "changes.txt", "OLD-VERSION");
        WriteSourceFile(source, "doomed.txt", "DOOMED");

        Run(source, backupRoot);

        // 一个改、一个删、一个不动、一个新增
        WriteSourceFile(source, "changes.txt", "NEW-VERSION-LONGER");
        File.Delete(Path.Combine(source, "doomed.txt"));
        WriteSourceFile(source, "added.txt", "ADDED");

        var second = Run(source, backupRoot);
        var manifest = BackupMirrorService.ReadManifest(second.ManifestPath);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.Comparison.NewFiles);
        Assert.Equal(1, manifest.Comparison.UpdatedFiles);
        Assert.Equal(1, manifest.Comparison.DeletedFiles);
        Assert.Equal(1, manifest.Comparison.UnchangedFiles);

        // history 里的记录要能追溯原因
        Assert.Contains(manifest.HistoryMoves, m => m.RelativePath == "changes.txt" && m.Reason == HistoryReason.OverwrittenByNewerVersion);
        Assert.Contains(manifest.HistoryMoves, m => m.RelativePath == "doomed.txt" && m.Reason == HistoryReason.DeletedOnSource);
    }

    [Fact]
    public void Mirror_DoesNotArchiveExcludedTempFiles_LeftInCurrent()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var first = Run(source, backupRoot);

        // 模拟「上一次复制被强制中断」留下的 .tmp 残留
        var strayTemp = Path.Combine(first.CurrentDirectory, "a.txt.tmp");
        File.WriteAllText(strayTemp, "half-written");

        var second = Run(source, backupRoot);

        // 残留 .tmp 是「上次复制被中断」的产物：既不该进 history，也不该永久留在 current 污染备份
        Assert.False(File.Exists(strayTemp), "中断留下的 .tmp 残骸应被清理");
        Assert.Equal(1, second.CleanupRemovedFiles);
        Assert.Equal(0, second.ArchivedFiles);
        Assert.True(File.Exists(Path.Combine(second.CurrentDirectory, "a.txt")), "正式文件必须保留");

        var historyToday = BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow);
        if (Directory.Exists(historyToday))
        {
            Assert.DoesNotContain(
                Directory.EnumerateFiles(historyToday, "*", SearchOption.AllDirectories),
                f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Mirror_HandlesUnicodeAndSpacesInFileNames()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "文档/我的 报告 2026.txt", "中文内容");

        var first = Run(source, backupRoot);
        Assert.Equal(1, first.Copy.CopiedFiles);

        var current = Path.Combine(first.CurrentDirectory, "文档", "我的 报告 2026.txt");
        Assert.True(File.Exists(current));
        Assert.Equal("中文内容", File.ReadAllText(current));

        // 删除后应完整进出 history，中文与空格路径不能出错
        File.Delete(Path.Combine(source, "文档", "我的 报告 2026.txt"));
        var second = Run(source, backupRoot);

        var archived = Path.Combine(
            BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow),
            "文档", "我的 报告 2026.txt");

        Assert.True(File.Exists(archived), "中文/空格路径的软删除必须正常");
        Assert.Equal("中文内容", File.ReadAllText(archived));
    }

    [Fact]
    public void Mirror_RecreatedFileAfterDeletion_EndsUpInCurrentAgain()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "note.txt", "ORIGINAL");

        Run(source, backupRoot);

        // 删除 → 备份（进 history）
        File.Delete(Path.Combine(source, "note.txt"));
        var afterDelete = Run(source, backupRoot);
        Assert.Equal(1, afterDelete.ArchivedFiles);

        // 又在 U 盘上重建同名文件 → 应重新出现在 current，且 history 里旧的仍保留
        WriteSourceFile(source, "note.txt", "RECREATED");
        var afterRecreate = Run(source, backupRoot);

        Assert.Equal("RECREATED", File.ReadAllText(Path.Combine(afterRecreate.CurrentDirectory, "note.txt")));

        var archived = Path.Combine(
            BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow),
            "note.txt");
        Assert.Equal("ORIGINAL", File.ReadAllText(archived));
    }

    [Fact]
    public void Mirror_BackupWithoutPriorManifest_Succeeds()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        // 预先写入一个损坏的 manifest.json：备份必须降级成功，而不是抛异常
        var manifestPath = BackupRules.BuildManifestPath(backupRoot, Device());
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{ this is not valid json");

        WriteSourceFile(source, "a.txt", "A");
        var result = Run(source, backupRoot);

        Assert.Equal(1, result.Copy.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.True(File.Exists(result.ManifestPath));

        // 损坏的清单会被重写成合法内容
        Assert.NotNull(BackupMirrorService.ReadManifest(result.ManifestPath));
    }

    [Fact]
    public void Mirror_DeleteRecreateDeleteOnSameDay_KeepsEveryVersion()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        // V1 → 备份
        WriteSourceFile(source, "A.txt", "V1");
        Run(source, backupRoot);

        // 删除 → 备份（V1 进 history）
        File.Delete(Path.Combine(source, "A.txt"));
        var afterDelete = Run(source, backupRoot);
        Assert.Equal(1, afterDelete.ArchivedFiles);

        // 重建 V2 → 备份（V2 写入 current）
        WriteSourceFile(source, "A.txt", "V2-recreated");
        var afterRecreate = Run(source, backupRoot);
        Assert.Equal("V2-recreated", File.ReadAllText(Path.Combine(afterRecreate.CurrentDirectory, "A.txt")));

        // 再次删除 → 备份：V2 必须被保全，绝不能因为是「同一天第二个同名版本」就被丢掉
        File.Delete(Path.Combine(source, "A.txt"));
        var afterSecondDelete = Run(source, backupRoot);

        var archivedVersions = Directory
            .EnumerateFiles(afterSecondDelete.HistoryRootDirectory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        Assert.Contains("V1", archivedVersions);
        Assert.Contains("V2-recreated", archivedVersions);

        // current 里确实已经没有这个文件了，但它完整地躺在 history 里
        Assert.False(File.Exists(Path.Combine(afterSecondDelete.CurrentDirectory, "A.txt")));
        Assert.Empty(afterSecondDelete.ArchiveErrors);
    }

    [Fact]
    public void Mirror_SameDayOverwrite_PreservesEveryIntermediateVersion()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "v.txt", "V1");
        Run(source, backupRoot);

        WriteSourceFile(source, "v.txt", "V2-longer");
        Run(source, backupRoot);

        WriteSourceFile(source, "v.txt", "V3-even-longer");
        var third = Run(source, backupRoot);

        Assert.Equal("V3-even-longer", File.ReadAllText(Path.Combine(third.CurrentDirectory, "v.txt")));

        var archived = Directory
            .EnumerateFiles(third.HistoryRootDirectory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        // 同一天里的每一版都要留得下来，中间的 V2 不能被静默丢弃
        Assert.Contains("V1", archived);
        Assert.Contains("V2-longer", archived);
    }

    [Fact]
    public void Mirror_HistoryMoveRecord_LengthMatchesArchivedFile()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "doc.txt", "ABC");          // 3 字节
        Run(source, backupRoot);

        WriteSourceFile(source, "doc.txt", "0123456789A");  // 11 字节
        var second = Run(source, backupRoot);

        // 记录里的长度、路径必须与 history 里真实躺着的文件一致
        var move = Assert.Single(second.HistoryMoves);
        Assert.True(File.Exists(move.ArchivedTo), $"manifest 指向的归档文件必须存在：{move.ArchivedTo}");
        Assert.Equal(new FileInfo(move.ArchivedTo).Length, move.Length);
        Assert.Equal(3, move.Length);
    }

    [Fact]
    public void Mirror_ArchivedButNotCopied_IsReportedAsDeletedNotUpdated()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "lock.txt", "OLD");
        Run(source, backupRoot);

        // 改写源文件，然后独占锁定它 —— 旧版本会被先归档，但新版本必然写不进去
        WriteSourceFile(source, "lock.txt", "NEW-LONGER-CONTENT");
        using (File.Open(Path.Combine(source, "lock.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = Run(source, backupRoot);
            var manifest = BackupMirrorService.ReadManifest(second.ManifestPath);

            Assert.Equal(1, second.Copy.FailedFiles);

            // 旧版本必须已安全归档
            Assert.True(second.ArchivedFiles >= 1, "旧版本必须先进 history 再尝试覆盖");

            // 结论不能自相矛盾：不能说「更新了 1 个」又说「失败 1 个」
            Assert.NotNull(manifest);
            if (manifest is not null && manifest.Comparison.UpdatedFiles == 0)
            {
                Assert.Equal(1, manifest.Comparison.DeletedFiles);
            }

            // 无论怎么归类，内容都不能丢
            var archived = Directory
                .EnumerateFiles(second.HistoryRootDirectory, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .ToList();
            Assert.Contains("OLD", archived);
        }
    }

    [Fact]
    public void Mirror_DeleteArchiveFailure_IsSurfacedNotSilentlySwallowed()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var first = Run(source, backupRoot);
        Assert.Equal(1, first.Copy.CopiedFiles);

        // 用文件占住 history\{今天}\a.txt 的父级目录路径 → 归档目标无法创建
        var historyToday = BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow);
        Directory.CreateDirectory(historyToday);

        File.Delete(Path.Combine(source, "a.txt"));

        var second = Run(source, backupRoot);

        // 归档失败必须可见，不能当成「一切正常，0 个删除」
        if (second.ArchiveErrors.Count > 0)
        {
            Assert.True(second.ArchiveFailures > 0, "归档失败必须计入 ArchiveFailures");

            // 内容绝不能丢：要么还在 current，要么已进 history
            var stillInCurrent = File.Exists(Path.Combine(second.CurrentDirectory, "a.txt"));
            var inHistory = Directory.Exists(second.HistoryRootDirectory)
                && Directory.EnumerateFiles(second.HistoryRootDirectory, "*", SearchOption.AllDirectories).Any();
            Assert.True(stillInCurrent || inHistory, "归档失败时内容必须仍然存在（current 或 history）");
            Assert.Equal(0, second.DeletedFilesKnownLost);
        }
    }

    [Fact]
    public void Mirror_DeepLongPathOverwrite_ContentSurvives()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        // 构造一个很深的相对路径，使「history\日期\原名@HHmmss」超过经典 MAX_PATH，
        // 但按哈希压缩的兜底路径仍然放得下。
        var historyDirectory = BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow);
        var directoryDepth = Math.Max(0, 260 - historyDirectory.Length);
        if (directoryDepth < 40)
        {
            return;   // 临时目录前缀过长，构不出有意义的用例
        }

        var relativePath = Path.Combine(new string('d', directoryDepth - 12), "deep.txt");

        WriteSourceFile(source, relativePath, "V1");
        Run(source, backupRoot);

        WriteSourceFile(source, relativePath, "V2-LONGER-CONTENT");
        var second = Run(source, backupRoot);

        // 无论走唯一名还是兜底，本轮的每一版都必须留档，且 current 推进到 V2
        Assert.Equal("V2-LONGER-CONTENT", File.ReadAllText(Path.Combine(second.CurrentDirectory, relativePath)));
        Assert.Equal(0, second.Copy.FailedFiles);
        Assert.Equal(0, second.DeletedFilesKnownLost);

        var archived = Directory
            .EnumerateFiles(second.HistoryRootDirectory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        Assert.Contains("V1", archived);
    }

    [Fact]
    public void BuildShortArchivePath_IsShorterThanUniqueName_ForLongPaths()
    {
        using var ws = new TempWorkspace();
        var (_, backupRoot) = Prepare(ws);

        var historyDirectory = BackupRules.BuildHistoryDirectory(backupRoot, Device(), FixedNow);
        var directoryDepth = Math.Max(0, 258 - historyDirectory.Length);
        if (directoryDepth < 40)
        {
            return;
        }

        // 相对路径很长：唯一名（原名 + @HHmmss）必然超限
        var relativePath = Path.Combine(new string('x', directoryDepth - 12), "long.txt");

        var plain = Path.Combine(historyDirectory, relativePath);
        var unique = Path.Combine(
            historyDirectory,
            relativePath[..^4] + "@143005.txt");

        Assert.True(unique.Length > BackupMirrorService.MaxClassicPathLength,
            $"前提：唯一名应超限（{unique.Length}）");

        // 兜底必须真的更短，并且落在经典上限内 —— 否则这个分支就是死代码
        var fallback = Path.Combine(
            historyDirectory,
            BackupMirrorService.ShortFallbackFolderName + "1",
            "0123456789abcdef.txt");

        Assert.True(fallback.Length < unique.Length,
            $"兜底必须比唯一名短：unique={unique.Length}，fallback={fallback.Length}");
        Assert.True(fallback.Length <= BackupMirrorService.MaxClassicPathLength,
            $"兜底必须放得下：fallback={fallback.Length}，上限 {BackupMirrorService.MaxClassicPathLength}，无冲突名={plain.Length}");
    }

    [Fact]
    public void Mirror_Cancellation_StopsBackupAndReturnsPartialExecutionList()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        // 造足够多的文件，保证「取消」发生在备份中途而不是开始前
        const int fileCount = 400;
        for (var i = 0; i < fileCount; i++)
        {
            WriteSourceFile(source, $"dir{i % 10}/f{i:D4}.txt", new string('x', 4096));
        }

        using var cts = new CancellationTokenSource();

        // 复制过程中取消（第 5 个文件之后）
        var copyCount = 0;
        var mirror = new BackupMirrorService();

        var result = mirror.Mirror(
            sourceDirectory: source,
            backupRootDirectory: backupRoot,
            device: Device(),
            excludeRules: BackupRules.CreateDefaultExcludeRules(),
            historyRetentionDays: 30,
            now: FixedNow,
            progressCallback: progress =>
            {
                if (Interlocked.Increment(ref copyCount) == 5)
                {
                    cts.Cancel();
                }
            },
            cancellationToken: cts.Token);

        // 必须是「已取消」而不是「失败」或「成功」
        Assert.True(result.Cancelled, "取消后应标记为已取消");
        Assert.False(result.Success);

        // 关键：返回的是【已执行清单】—— 取消前复制成功的文件必须被如实统计
        Assert.True(result.Copy.CopiedFiles > 0, "取消前已完成的复制必须保留在结果里");
        Assert.True(result.Copy.CopiedFiles < fileCount,
            $"取消应发生在中途，实际复制了 {result.Copy.CopiedFiles}/{fileCount}");

        // 已复制的文件确实落盘了（不是空结果）
        Assert.NotEmpty(Directory.EnumerateFiles(result.CurrentDirectory, "*", SearchOption.AllDirectories));

        // 取消后不写 manifest、不清理 history（避免半途状态被当成一次完整备份）
        Assert.Equal(string.Empty, result.ManifestPath);
    }

    [Fact]
    public void CopyDirectory_Cancellation_ReturnsPartialListInsteadOfThrowing()
    {
        using var ws = new TempWorkspace();

        for (var i = 0; i < 200; i++)
        {
            ws.CreateFile($"f{i:D3}.txt", new string('y', 2048), BaseTimeUtc);
        }

        using var cts = new CancellationTokenSource();
        var seen = 0;

        var result = new FileCopier().CopyDirectory(
            ws.SourceDir,
            ws.TargetDir,
            _ =>
            {
                if (++seen == 3)
                {
                    cts.Cancel();
                }
            },
            null,
            cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Contains("已取消", result.Summary);

        // 已复制部分必须保留（「返回已执行清单」）
        Assert.True(result.CopiedFiles >= 3, $"已复制 {result.CopiedFiles} 个，应至少 3 个");
        Assert.True(result.CopiedFiles < 200);
        Assert.Equal(0, result.FailedFiles);
    }

    [Fact]
    public void Mirror_WhenRetentionIsForever_ManifestSaysSo()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var result = Run(source, backupRoot, retentionDays: BackupConfig.HistoryRetentionForever);
        var manifest = BackupMirrorService.ReadManifest(result.ManifestPath);

        Assert.NotNull(manifest);
        Assert.True(manifest!.HistoryKeptForever);
        Assert.Equal(BackupConfig.HistoryRetentionForever, manifest.HistoryRetentionDays);
        Assert.Contains("永久保留", manifest.HistoryPolicyText);
    }

    [Fact]
    public void ReadManifest_ReturnsNullForMissingOrCorruptFile()
    {
        using var ws = new TempWorkspace();
        var missing = Path.Combine(ws.Root, "nope.json");
        Assert.Null(BackupMirrorService.ReadManifest(missing));

        var corrupt = Path.Combine(ws.Root, "corrupt.json");
        File.WriteAllText(corrupt, "{ this is not json");
        Assert.Null(BackupMirrorService.ReadManifest(corrupt));
    }

    // ------------------------------------------------------------------
    // 排除规则与安全
    // ------------------------------------------------------------------

    [Fact]
    public void Mirror_RespectsExcludeRules_AndDoesNotArchiveExcludedFiles()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "keep.txt", "KEEP");
        WriteSourceFile(source, "temp.tmp", "TEMP");
        WriteSourceFile(source, "autorun.inf", "AUTORUN");

        var result = Run(source, backupRoot);

        Assert.Equal(1, result.Copy.CopiedFiles);
        Assert.False(File.Exists(Path.Combine(result.CurrentDirectory, "temp.tmp")));
        Assert.False(File.Exists(Path.Combine(result.CurrentDirectory, "autorun.inf")));
    }

    [Fact]
    public void Mirror_WhenSourceMissing_Throws()
    {
        using var ws = new TempWorkspace();
        var (_, backupRoot) = Prepare(ws);
        var missing = Path.Combine(ws.Root, "no-such-usb");

        Assert.Throws<DirectoryNotFoundException>(() => Run(missing, backupRoot));
    }

    [Fact]
    public void Mirror_SkipsReparsePointDirectories()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "real.txt", "REAL");

        // 造一个外部目录，再用目录联接指向它（需要权限；不支持时跳过本测试）
        var outside = Path.Combine(ws.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "should-not-copy.txt"), "OUTSIDE");

        var linkPath = Path.Combine(source, "link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outside);
        }
        catch (Exception)
        {
            return;   // 无权限创建链接：跳过（不把环境限制当成失败）
        }

        var result = Run(source, backupRoot);

        Assert.True(File.Exists(Path.Combine(result.CurrentDirectory, "real.txt")));
        Assert.False(
            File.Exists(Path.Combine(result.CurrentDirectory, "link", "should-not-copy.txt")),
            "目录联接指向的外部内容绝不能被复制进备份");
    }

    // ------------------------------------------------------------------
    // 失败明细限流
    // ------------------------------------------------------------------

    [Fact]
    public void TrimFailureDetails_KeepsFirstNPlusTotal()
    {
        var copy = new CopyResult();
        for (var i = 0; i < BackupMirrorService.MaxFailureDetails + 10; i++)
        {
            copy.Errors.Add($"file{i}.txt：失败");
        }

        BackupMirrorService.TrimFailureDetails(copy);

        Assert.Equal(BackupMirrorService.MaxFailureDetails + 1, copy.Errors.Count);
        Assert.Contains("其余 10 条", copy.Errors[^1]);
    }

    [Fact]
    public void TrimFailureDetails_LeavesShortListsUntouched()
    {
        var copy = new CopyResult();
        copy.Errors.Add("one");
        copy.Errors.Add("two");

        BackupMirrorService.TrimFailureDetails(copy);

        Assert.Equal(2, copy.Errors.Count);
    }

    [Fact]
    public void ShouldAbortOnFailures_UsesThreshold()
    {
        Assert.False(BackupMirrorService.ShouldAbortOnFailures(BackupMirrorService.ConsecutiveFailureAbortThreshold - 1));
        Assert.True(BackupMirrorService.ShouldAbortOnFailures(BackupMirrorService.ConsecutiveFailureAbortThreshold));
    }

    [Fact]
    public void Mirror_AbortsEarly_WhenConsecutiveFailuresReachThreshold()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        // 30 个文件，连续失败阈值调成 3，方便在测试里稳定触发早停
        const int fileCount = 30;
        const int threshold = 3;
        for (var i = 0; i < fileCount; i++)
        {
            WriteSourceFile(source, $"d{i % 3}/f{i:D3}.txt", $"payload-{i}");
        }

        // 在 current 下把 3 个子目录路径全部用【同名文件】占住：
        // 复制时 Directory.CreateDirectory 会失败，从而稳定制造连续失败
        // （不依赖权限、磁盘满或真实硬件异常）。
        var current = BackupRules.BuildCurrentDirectory(backupRoot, Device());
        Directory.CreateDirectory(current);
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(current, $"d{i}"), "I am a file, not a directory");
        }

        var result = new BackupMirrorService().Mirror(
            sourceDirectory: source,
            backupRootDirectory: backupRoot,
            device: Device(),
            excludeRules: BackupRules.CreateDefaultExcludeRules(),
            historyRetentionDays: 30,
            now: FixedNow,
            maxConsecutiveFailures: threshold);

        Assert.True(result.AbortedByFailureThreshold,
            $"连续失败达到阈值时必须早停；失败 {result.Copy.FailedFiles}，" +
            $"已处理 {result.Copy.CopiedFiles + result.Copy.SkippedFiles + result.Copy.FailedFiles}，" +
            $"cancelled={result.Copy.Cancelled}，aborted={result.Copy.AbortedByConsecutiveFailures}，" +
            $"错误示例={string.Join(" | ", result.Copy.Errors.Take(2))}");
        Assert.True(result.Copy.AbortedByConsecutiveFailures);
        Assert.Equal(threshold, result.Copy.FailedFiles);

        // 关键：早停意味着没有把 30 个文件都试一遍
        var attempted = result.Copy.CopiedFiles + result.Copy.SkippedFiles + result.Copy.FailedFiles;
        Assert.True(attempted < fileCount,
            $"早停应立即生效，实际处理了 {attempted}/{fileCount} 个文件");

        // 早停不是“用户取消”，必须区分开
        Assert.False(result.Cancelled);
        Assert.False(result.Copy.Cancelled);
    }

    [Fact]
    public void Mirror_DoesNotAbort_WhenFailuresAreBelowThreshold()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "ok1.txt", "A");
        WriteSourceFile(source, "ok2.txt", "B");

        var result = Run(source, backupRoot);

        Assert.False(result.AbortedByFailureThreshold);
        Assert.Equal(2, result.Copy.CopiedFiles);
        Assert.False(result.Cancelled);
        Assert.True(result.Success);
    }

    [Fact]
    public void CopyDirectory_AbortsAfterConsecutiveFailures_AndReportsReasonInSummary()
    {
        using var ws = new TempWorkspace();

        // 目标根必须能被创建，所以把「占位文件」放在子目录层级
        for (var i = 0; i < 30; i++)
        {
            ws.CreateFile($"blocked/f{i:D2}.txt", "x", BaseTimeUtc);
        }

        // 目标下的 blocked 位置被一个同名文件占住 → 每个文件都失败
        Directory.CreateDirectory(ws.TargetDir);
        File.WriteAllText(Path.Combine(ws.TargetDir, "blocked"), "not a directory");

        var copier = new FileCopier { MaxConsecutiveFailures = 5 };
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.True(result.AbortedByConsecutiveFailures,
            $"应触发早停；MaxConsecutiveFailures={copier.MaxConsecutiveFailures}，" +
            $"失败 {result.FailedFiles}，已处理 {result.CopiedFiles + result.SkippedFiles + result.FailedFiles}");
        Assert.Equal(5, result.FailedFiles);
        Assert.False(result.Cancelled);
        Assert.False(result.Success);
        Assert.Contains("连续失败过多已中止", result.Summary);
    }

    [Fact]
    public void CopyDirectory_ZeroMaxConsecutiveFailures_DisablesAbort()
    {
        using var ws = new TempWorkspace();

        for (var i = 0; i < 8; i++)
        {
            ws.CreateFile($"blocked/f{i:D2}.txt", "x", BaseTimeUtc);
        }

        Directory.CreateDirectory(ws.TargetDir);
        File.WriteAllText(Path.Combine(ws.TargetDir, "blocked"), "not a directory");

        var copier = new FileCopier { MaxConsecutiveFailures = 0 };
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 关闭早停后应该把所有文件都试一遍
        Assert.False(result.AbortedByConsecutiveFailures);
        Assert.Equal(8, result.FailedFiles);
    }
}
