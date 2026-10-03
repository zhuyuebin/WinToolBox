using System.Text;
using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// 阶段二 P1-8（固定目录 + 真增量 + 软删除历史）/ P1-20（惰性枚举）的独立对抗性验证。
/// <para>verifier 角色：只读产品代码，本文件是本轮唯一新增产物；不修改 src/ 与既有测试。</para>
/// <para>验证主线（与 task-2 的 a)~g) 对齐）：</para>
/// <list type="bullet">
/// <item>a) U 盘删除的文件是否【一定】进入 history 而不是消失（嵌套 / 同一批大量删除 / 跨天 / 删除后重建）。</item>
/// <item>b) 修改文件时旧版本是否一定先归档再覆盖；归档失败时是否会出现「旧版本已丢」。</item>
/// <item>c) history 清理边界（恰好保留期 / 非日期目录 / 只读文件 / retention=0）。</item>
/// <item>d) manifest 四类计数与归档记录的准确性；损坏 / 不可写时是否降级。</item>
/// <item>e) 惰性枚举是否真的没有物化清单；进度分母是否稳定、是否排除被排除文件。</item>
/// <item>f) 连续失败早停与「成功即清零」。</item>
/// </list>
/// <para>标注 <c>[数据丢失]</c> 的用例断言的是【产品承诺的行为】，若红灯即为真实缺陷。</para>
/// </summary>
public sealed class BackupMirrorVerificationTests
{
    /// <summary>固定“当前时间”，让 history 目录名与清理边界完全可控。</summary>
    private static readonly DateTime FixedNow = new(2026, 10, 3, 14, 30, 0, DateTimeKind.Local);

    /// <summary>固定的文件修改时间基准（过去），避免与目标时间戳比较产生歧义。</summary>
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

    private static (string Source, string BackupRoot) Prepare(TempWorkspace ws)
    {
        var source = Path.Combine(ws.Root, "usb");
        var backupRoot = Path.Combine(ws.Root, "backup");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(backupRoot);
        return (source, backupRoot);
    }

    private static string WriteSourceFile(string source, string relativePath, string content, DateTime? mtimeUtc = null)
    {
        var full = Path.Combine(source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(full, mtimeUtc ?? BaseTimeUtc);
        return full;
    }

    private static MirrorResult Run(
        string source,
        string backupRoot,
        int retentionDays = 30,
        DateTime? now = null,
        bool strict = true,
        Action<CopyProgress>? progress = null,
        Action<int>? scan = null,
        int maxConsecutiveFailures = BackupMirrorService.ConsecutiveFailureAbortThreshold)
        => new BackupMirrorService().Mirror(
            sourceDirectory: source,
            backupRootDirectory: backupRoot,
            device: Device(),
            excludeRules: BackupRules.CreateDefaultExcludeRules(),
            historyRetentionDays: retentionDays,
            strictContentVerification: strict,
            now: now ?? FixedNow,
            progressCallback: progress,
            scanProgress: scan,
            maxConsecutiveFailures: maxConsecutiveFailures);

    private static string CurrentDir(string backupRoot) => BackupRules.BuildCurrentDirectory(backupRoot, Device());

    private static string HistoryToday(string backupRoot, DateTime? now = null)
        => BackupRules.BuildHistoryDirectory(backupRoot, Device(), now ?? FixedNow);

    private static string HistoryRoot(string backupRoot) => BackupRules.BuildHistoryRootDirectory(backupRoot, Device());

    private static IEnumerable<string> AllFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();

    private static int CountFiles(string directory) => AllFiles(directory).Count();

    // ==================================================================
    // a) 数据丢失：U 盘删除的文件是否一定进入 history
    // ==================================================================

    /// <summary>
    /// a-1 深层嵌套 + 中文/空格文件名：删除后必须完整进出 history，current 里不能残留、也不能消失。
    /// </summary>
    [Fact]
    public void A_DeepNestedUnicodeNames_DeletedFileIsArchivedIntact()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        const string relative = "一级 目录/二级/third level/四级 带空格/五级/最终 文档 2026.txt";
        const string content = "深层中文内容-必须一字不差地进 history";
        WriteSourceFile(source, relative, content);

        var first = Run(source, backupRoot);
        Assert.Equal(1, first.Copy.CopiedFiles);

        File.Delete(Path.Combine(source, relative));
        var second = Run(source, backupRoot);

        Assert.Equal(0, CountFiles(CurrentDir(backupRoot)));
        var archived = Path.Combine(HistoryToday(backupRoot), relative);
        Assert.True(File.Exists(archived), "深层中文路径的软删除必须正常，文件不能消失");
        Assert.Equal(content, File.ReadAllText(archived, Encoding.UTF8));
        Assert.Equal(1, second.ArchivedFiles);
        Assert.Equal(1, BackupMirrorService.ReadManifest(second.ManifestPath)!.Comparison.DeletedFiles);
    }

    /// <summary>
    /// a-2 一次性删除大量文件（扁平 60 + 嵌套 24）：
    /// <c>MoveDeletedToHistory</c> 是「边枚举 current 边把文件移出去」，必须全部归档、一个都不能漏。
    /// </summary>
    [Fact]
    public void A_MassDeletionInOneRun_EveryCopyIsArchived()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var expected = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            var rel = $"flat/f{i:D3}.txt";
            WriteSourceFile(source, rel, $"flat-{i}");
            expected.Add(rel);
        }

        for (var i = 0; i < 24; i++)
        {
            var rel = $"nested/g{i % 3}/deep/h{i:D2}.dat";
            WriteSourceFile(source, rel, $"nested-{i}");
            expected.Add(rel);
        }

        var first = Run(source, backupRoot);
        Assert.Equal(expected.Count, first.Copy.CopiedFiles);

        foreach (var file in AllFiles(source).ToList())
        {
            File.Delete(file);
        }

        var second = Run(source, backupRoot);

        Assert.Equal(0, CountFiles(CurrentDir(backupRoot)));
        Assert.Equal(expected.Count, second.ArchivedFiles);
        Assert.Equal(expected.Count, CountFiles(HistoryToday(backupRoot)));

        // 逐个核对：任何一条被「边枚举边搬走」漏掉的文件都会在这里暴露
        foreach (var rel in expected)
        {
            Assert.True(
                File.Exists(Path.Combine(HistoryToday(backupRoot), rel)),
                $"U 盘删除后必须进入 history，但漏掉了：{rel}（current 中还剩 {CountFiles(CurrentDir(backupRoot))} 个文件）");
        }

        Assert.Equal(expected.Count, BackupMirrorService.ReadManifest(second.ManifestPath)!.Comparison.DeletedFiles);
        Assert.Equal(0, second.DeletedFilesKnownLost);
    }

    /// <summary>a-3 跨多天各删一个文件：每个都必须归档到「删除发生那天」的 history 目录，且不被保留期清理误伤。</summary>
    [Fact]
    public void A_DeletionsAcrossMultipleDays_EachGoesToItsOwnDateFolder()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "P.txt", "P1");
        WriteSourceFile(source, "Q.txt", "Q1");
        WriteSourceFile(source, "R.txt", "R1");

        Assert.Equal(3, Run(source, backupRoot, now: FixedNow).Copy.CopiedFiles);

        File.Delete(Path.Combine(source, "P.txt"));
        var day1 = FixedNow.AddDays(1);
        Run(source, backupRoot, now: day1);

        File.Delete(Path.Combine(source, "Q.txt"));
        var day2 = FixedNow.AddDays(2);
        Run(source, backupRoot, now: day2);

        File.Delete(Path.Combine(source, "R.txt"));
        var day3 = FixedNow.AddDays(3);
        var last = Run(source, backupRoot, now: day3);

        Assert.Equal(0, CountFiles(CurrentDir(backupRoot)));
        Assert.Equal("P1", File.ReadAllText(Path.Combine(HistoryToday(backupRoot, day1), "P.txt")));
        Assert.Equal("Q1", File.ReadAllText(Path.Combine(HistoryToday(backupRoot, day2), "Q.txt")));
        Assert.Equal("R1", File.ReadAllText(Path.Combine(HistoryToday(backupRoot, day3), "R.txt")));
        Assert.Equal(3, CountFiles(HistoryRoot(backupRoot)));
        Assert.Empty(last.PurgedHistoryFolders);
    }

    /// <summary>
    /// a-4 先删除、再在 U 盘上重建同名但内容不同的文件：
    /// current 必须是新内容，history 必须保留被删除的旧内容（两者都不能丢）。
    /// </summary>
    [Fact]
    public void A_DeletedThenRecreatedBeforeNextBackup_BothVersionsSurvive()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "note.txt", "ORIGINAL-CONTENT");
        Run(source, backupRoot);

        File.Delete(Path.Combine(source, "note.txt"));
        var afterDelete = Run(source, backupRoot);
        Assert.Equal(1, afterDelete.ArchivedFiles);

        WriteSourceFile(source, "note.txt", "RECREATED-DIFFERENT");
        var afterRecreate = Run(source, backupRoot);

        Assert.Equal("RECREATED-DIFFERENT", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "note.txt")));
        Assert.Equal("ORIGINAL-CONTENT", File.ReadAllText(Path.Combine(HistoryToday(backupRoot), "note.txt")));

        // 重建属于「U 盘里又出现了」，不是「U 盘已删除」——顺序不能反
        var manifest = BackupMirrorService.ReadManifest(afterRecreate.ManifestPath)!;
        Assert.Equal(0, manifest.Comparison.DeletedFiles);
        Assert.Equal(1, CountFiles(CurrentDir(backupRoot)));
    }

    /// <summary>
    /// a-5【数据丢失】同一天内「删除 → 重建 → 再删除」：
    /// current 里当时放着的最新版本（V2）会被 <c>File.Delete(sourcePath)</c> 直接删掉，
    /// 而 history 里保留的是最早的 V1 —— V2 在任何地方都不复存在。
    /// 这直接违反产品承诺「U 盘删除的文件 → current 中的副本移入 history（软删除，绝不直接删）」。
    /// </summary>
    [Fact]
    public void A_DeleteRecreateDeleteOnSameDay_NewestVersionMustNotVanish()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "A.txt", "V1");
        Run(source, backupRoot);

        File.Delete(Path.Combine(source, "A.txt"));
        Run(source, backupRoot);                              // history/今天/A.txt = V1

        WriteSourceFile(source, "A.txt", "V2-CONTENT");
        var afterRecreate = Run(source, backupRoot);          // current/A.txt = V2
        Assert.Equal("V2-CONTENT", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "A.txt")));
        Assert.Equal("V1", File.ReadAllText(Path.Combine(HistoryToday(backupRoot), "A.txt")));

        File.Delete(Path.Combine(source, "A.txt"));
        var afterSecondDelete = Run(source, backupRoot);      // V2 应当被移入 history

        Assert.False(File.Exists(Path.Combine(CurrentDir(backupRoot), "A.txt")));

        var recoverable = AllFiles(HistoryRoot(backupRoot))
            .Any(f => File.ReadAllText(f, Encoding.UTF8) == "V2-CONTENT");

        var historyDump = string.Join(
            "；",
            AllFiles(HistoryRoot(backupRoot)).Select(f => $"{Path.GetFileName(f)}={File.ReadAllText(f, Encoding.UTF8)}"));
        var manifestSummary = BackupMirrorService.ReadManifest(afterSecondDelete.ManifestPath)?.Comparison.Summary ?? "(无)";

        Assert.True(
            recoverable,
            "【数据丢失】同一天内「删除→重建→再删除」时，current 中最新的 V2 被 File.Delete 直接删除，" +
            "history 里只有最早的 V1；V2 在任何地方都无法恢复。" +
            $"（current\\A.txt 存在={File.Exists(Path.Combine(CurrentDir(backupRoot), "A.txt"))}；" +
            $"history：{historyDump}；ArchiveErrors=[{string.Join(" | ", afterSecondDelete.ArchiveErrors)}]；" +
            $"manifest：{manifestSummary}）");
    }

    /// <summary>
    /// a-6 同一天内连续修改两次：历史归档【每一版都保留】，任何版本都不会被静默丢弃。
    /// 原始用例曾把「只留最早版本、中间版本静默丢弃」当作设计取舍钉住；
    /// 该取舍会导致中间版本不可恢复，已被修复为「同日冲突时归档到唯一名字」。
    /// </summary>
    [Fact]
    public void A_SameDayRepeatedModification_KeepsEveryVersion()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "v.txt", "V1");
        Run(source, backupRoot);

        WriteSourceFile(source, "v.txt", "V2-longer");
        Run(source, backupRoot);

        WriteSourceFile(source, "v.txt", "V3-even-longer");
        var third = Run(source, backupRoot);

        Assert.Equal("V3-even-longer", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "v.txt")));
        Assert.Equal(1, third.Copy.CopiedFiles);

        // history 里 V1 与 V2 都必须留得下来（同一天多个版本各自归档，谁也不覆盖谁）
        var archived = AllFiles(HistoryRoot(backupRoot))
            .Select(f => File.ReadAllText(f, Encoding.UTF8))
            .ToList();

        Assert.Contains("V1", archived);
        Assert.Contains("V2-longer", archived);

        // 正常路径（归档成功、复制成功）下改判不应发生：删除计数必须保持 0，本轮只算「更新 1」
        var manifest = BackupMirrorService.ReadManifest(third.ManifestPath)!;
        var c = manifest.Comparison;
        Assert.True(
            c.DeletedFiles == 0,
            $"归档成功且复制成功时不得改判为「U 盘已删除」：新增 {c.NewFiles}，更新 {c.UpdatedFiles}，" +
            $"删除 {c.DeletedFiles}，未变 {c.UnchangedFiles}，失败 {c.FailedFiles}");
        Assert.Equal(1, c.UpdatedFiles);
        Assert.Equal(1, third.ArchivedFiles);   // 本轮只归档了 V2（V1 是上一轮归档的）
    }

    /// <summary>
    /// a-7 接近 MAX_PATH 的超长路径：本用例只断言「内容绝不消失」。
    /// 若归档因路径过长失败，文件必须仍留在 current（宁可留着，也不能丢），并把失败记入 ArchiveErrors。
    /// </summary>
    [Fact]
    public void A_NearMaxPathDeletion_ContentIsNeverLost()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var current = CurrentDir(backupRoot);
        var history = HistoryToday(backupRoot);

        // 构造 rel 长度，使 current 路径合法、history 路径超 MAX_PATH(260)
        const int maxPath = 260;
        var relLength = 250 - current.Length;
        if (relLength < 40 || relLength > 200)
        {
            return;   // 临时目录前缀过长/过短，无法构造稳定用例：跳过（不把环境差异当缺陷）
        }

        var rel = BuildRelativePathOfLength(relLength);
        var sourceFile = Path.Combine(source, rel);
        var currentFile = Path.Combine(current, rel);
        var historyFile = Path.Combine(history, rel);
        var historyExceeds = historyFile.Length >= maxPath;

        try
        {
            WriteSourceFile(source, rel, "LONG-PATH-CONTENT");
        }
        catch (Exception)
        {
            return;   // 连源文件的超长路径都建不出来：跳过
        }

        var first = Run(source, backupRoot);
        if (!File.Exists(currentFile))
        {
            // current 路径也超限，文件从未进入备份：不构成本用例主题
            Assert.True(first.Copy.FailedFiles >= 1 || first.Copy.CopiedFiles == 0);
            return;
        }

        File.Delete(sourceFile);
        var second = Run(source, backupRoot);

        var inHistory = File.Exists(historyFile);
        var inCurrent = File.Exists(currentFile);

        Assert.True(
            inHistory || inCurrent,
            "【数据丢失】删除后文件既不在 history 也不在 current：" +
            $"rel={rel.Length} 字符，history 路径 {historyFile.Length} 字符（超限={historyExceeds}）");

        Assert.Equal("LONG-PATH-CONTENT", File.ReadAllText(inHistory ? historyFile : currentFile, Encoding.UTF8));

        if (!inHistory)
        {
            // 归档失败被按「安全方向」降级：文件留在 current（内容没丢）。
            // 是否被如实上报由 B_DeletionArchiveFailure_MustBeReportedNotSilentlySwallowed 专门负责（本用例只保内容不丢）。
            Assert.True(inCurrent);
        }

        Assert.Equal(0, second.DeletedFilesKnownLost);   // 新增的「确实丢了」计数器必须恒为 0
    }

    /// <summary>构造一条由多个目录段拼成的、总长度恰为 <paramref name="totalLength"/> 的相对路径。</summary>
    private static string BuildRelativePathOfLength(int totalLength)
    {
        const string leaf = "长路径 文件.dat";
        var builder = new StringBuilder();

        while (builder.Length + 21 + leaf.Length <= totalLength)
        {
            builder.Append(new string('L', 20)).Append('/');
        }

        builder.Append(leaf);
        return builder.ToString();
    }

    // ==================================================================
    // b) 旧版本保全：归档失败时绝不能「旧版本已丢」
    // ==================================================================

    /// <summary>
    /// b-1 让 history 的日期目录无法创建（被同名文件占位），再修改一个已有文件：
    /// 必须复制失败 + current 保持旧内容完整，绝不能出现「旧版本已丢、新版本没写」。
    /// </summary>
    [Fact]
    public void B_HistoryDateFolderUnavailable_OldVersionIsNotOverwritten()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "A.txt", "OLD-CONTENT-16B");
        Run(source, backupRoot);

        var currentFile = Path.Combine(CurrentDir(backupRoot), "A.txt");
        Assert.Equal("OLD-CONTENT-16B", File.ReadAllText(currentFile));

        // 用同名【文件】占住 history\今天：Directory.CreateDirectory 必然失败（等价于归档目录不可写）
        Directory.CreateDirectory(HistoryRoot(backupRoot));
        File.WriteAllText(HistoryToday(backupRoot), "not a directory");

        WriteSourceFile(source, "A.txt", "NEW");
        var second = Run(source, backupRoot);

        Assert.Equal("OLD-CONTENT-16B", File.ReadAllText(currentFile));
        Assert.Equal(1, second.Copy.FailedFiles);
        Assert.Equal(1, second.FailedFiles);
        Assert.Contains(second.Copy.Errors, e => e.Contains("A.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, second.Copy.CopiedFiles);
    }

    /// <summary>
    /// b-2【计数口径】覆盖阶段的归档失败：只算一次。
    /// 归档失败 → 该文件不复制 → FileCopier 记 1 个失败；不能再额外累加一次（否则失败数翻倍）。
    /// </summary>
    [Fact]
    public void B_OverwriteArchiveFailure_CountedExactlyOnce()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "A.txt", "OLD-CONTENT-16B");
        Run(source, backupRoot);

        Directory.CreateDirectory(HistoryRoot(backupRoot));
        File.WriteAllText(HistoryToday(backupRoot), "not a directory");

        WriteSourceFile(source, "A.txt", "NEW");
        var second = Run(source, backupRoot);
        var manifest = BackupMirrorService.ReadManifest(second.ManifestPath)!;

        Assert.Equal(1, second.Copy.FailedFiles);              // 复制阶段记 1 次
        Assert.Equal(0, second.DeletionArchiveFailures);       // 不是「U 盘已删除」阶段
        Assert.Equal(1, second.FailedFiles);                   // 总数 = 1，不许翻倍
        Assert.Contains(second.Copy.Errors, e => e.Contains("A.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, manifest.Comparison.FailedFiles);      // manifest 口径一致
    }

    /// <summary>
    /// b-3 history 根目录被同名文件占位：必须在动手前就失败，current 里一个文件都不能少（也不能悄悄降级）。
    /// </summary>
    [Fact]
    public void B_HistoryRootBlockedByFile_MirrorFailsBeforeTouchingAnything()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "A.txt", "A");

        var deviceRoot = BackupRules.BuildDeviceRootDirectory(backupRoot, Device());
        Directory.CreateDirectory(deviceRoot);
        File.WriteAllText(HistoryRoot(backupRoot), "I am a file");   // history 根被占位

        var exception = Record.Exception(() => Run(source, backupRoot));

        Assert.NotNull(exception);
        Assert.IsAssignableFrom<IOException>(exception);
        Assert.False(File.Exists(Path.Combine(CurrentDir(backupRoot), "A.txt")));
        Assert.False(File.Exists(BackupRules.BuildManifestPath(backupRoot, Device())));
        Assert.Equal("A", File.ReadAllText(Path.Combine(source, "A.txt")));   // 源只读，绝不被动过
    }

    /// <summary>
    /// b-4【修复点 3 聚焦复验】删除归档失败必须被如实上报，不再是「静默成功」：
    /// history 日期目录不可创建时，U 盘上已删除的文件留在 current（内容没丢），
    /// 但本轮必须计入 ArchiveFailures / FailedFiles，manifest 也不能再说「失败 0」。
    /// </summary>
    [Fact]
    public void B_DeletionArchiveFailure_MustBeReportedNotSilentlySwallowed()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "gone.txt", "MUST-BE-KEPT");
        Run(source, backupRoot);

        // history\今天 被同名文件占位 → 归档目录无法创建
        Directory.CreateDirectory(HistoryRoot(backupRoot));
        File.WriteAllText(HistoryToday(backupRoot), "not a directory");

        File.Delete(Path.Combine(source, "gone.txt"));
        var second = Run(source, backupRoot);

        // 安全方向正确：内容还在 current，没丢
        Assert.Equal("MUST-BE-KEPT", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "gone.txt")));
        Assert.Equal(0, second.DeletedFilesKnownLost);

        // 失败必须可见（这是本次修复点 3 的核心承诺），且只算一次
        Assert.Equal(0, second.Copy.FailedFiles);            // 复制阶段没有失败
        Assert.Equal(1, second.DeletionArchiveFailures);     // 「U 盘已删除」阶段恰好 1 个失败
        Assert.Equal(1, second.FailedFiles);                 // 总数恰好 1，不许漏计
        Assert.NotEmpty(second.ArchiveErrors);
        Assert.False(second.Success, "归档失败时不能报告「全部成功」");

        var manifest = BackupMirrorService.ReadManifest(second.ManifestPath)!;
        Assert.Equal(1, manifest.Comparison.FailedFiles);
    }

    // ==================================================================
    // 附加：新加入的「.tmp 残骸清理」也是一条删除路径，必须只删自己的残骸
    // ==================================================================

    /// <summary>
    /// history 与 current 都绝不能被这条清理误伤：
    /// 只删「去掉 .tmp 后、对应源文件确实存在」的暂存残骸；对不上号的一律留着。
    /// </summary>
    [Fact]
    public void H_OrphanedStagingCleanup_OnlyRemovesArtifactsWithLiveSourceCounterpart()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "REAL-CONTENT");
        Run(source, backupRoot);

        var current = CurrentDir(backupRoot);
        var realArtifact = Path.Combine(current, "a.txt.tmp");        // 对应 a.txt 存在 → 本程序的残骸
        var ghostArtifact = Path.Combine(current, "ghost.txt.tmp");   // 源里没有 ghost.txt → 不是残骸
        File.WriteAllText(realArtifact, "half-written");
        File.WriteAllText(ghostArtifact, "not-ours");

        var second = Run(source, backupRoot);

        Assert.False(File.Exists(realArtifact), "上次中断留下的 .tmp 残骸应被清理");
        Assert.Equal(1, second.CleanupRemovedFiles);
        Assert.True(File.Exists(ghostArtifact), "无法确认归属的 .tmp 绝不能被删除");
        Assert.Equal("REAL-CONTENT", File.ReadAllText(Path.Combine(current, "a.txt")));
        Assert.Empty(AllFiles(HistoryRoot(backupRoot)));
    }

    /// <summary>
    /// b-5 旧版本已成功归档、但紧接着新版本的复制失败（源文件被独占锁定）：
    /// 旧版本没丢（在 history 里可恢复，这是安全底线），
    /// 但 current 里该文件会「整体消失」，且 manifest 同时把它记成「更新 1」和「失败 1」。
    /// </summary>
    [Fact]
    public void B_ArchiveSucceededThenCopyFailed_OldVersionRecoverableButCurrentLosesCopy()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var sourceFile = WriteSourceFile(source, "A.txt", "V1-CONTENT");
        Run(source, backupRoot);

        var historyFile = Path.Combine(HistoryToday(backupRoot), "A.txt");
        var currentFile = Path.Combine(CurrentDir(backupRoot), "A.txt");

        // 新版本长度不同 → 判定为需要复制；独占锁定源文件 → StreamCopy 必然失败
        File.WriteAllText(sourceFile, "V2-LONGER-CONTENT", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(sourceFile, BaseTimeUtc.AddHours(1));

        MirrorResult second;
        using (var exclusive = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            second = Run(source, backupRoot);
        }

        Assert.Equal(1, second.Copy.FailedFiles);

        // 安全底线：被覆盖的旧版本确实进了 history，可以恢复
        Assert.True(File.Exists(historyFile), "覆盖前必须先归档旧版本");
        Assert.Equal("V1-CONTENT", File.ReadAllText(historyFile));

        // 但 current 里该文件已经没有副本了：旧版本被搬走、新版本没写进来
        Assert.False(File.Exists(currentFile));

        var manifest = BackupMirrorService.ReadManifest(second.ManifestPath)!;
        var c = manifest.Comparison;
        var counters = $"新增 {c.NewFiles}，更新 {c.UpdatedFiles}，删除 {c.DeletedFiles}，未变 {c.UnchangedFiles}，失败 {c.FailedFiles}";

        // 修复点 4：归档后复制失败的文件被改判为 DeletedOnSource —— 计数必须仍然自洽：
        // current 里没有该文件、内容在 history 中可恢复；但它【并没有被更新】（新版本从未写入）。
        Assert.True(c.DeletedFiles == 1, $"该文件已移出 current 且内容在 history 中：{counters}");
        Assert.True(c.FailedFiles == 1, $"新版本复制失败必须保留在计数里：{counters}");
        Assert.True(c.UpdatedFiles == 0, $"新版本从未写入 current，不能算「更新」：{counters}");

        // 再备份一次：current 里没有它，于是被当成「U 盘新增」。
        // 注意：改判只影响【当轮】manifest，下一轮的 NewFiles 由下一轮自己的复制/归档结果计算，
        // 因此「避免下一轮误报新增」这一目标并未达成 —— 这里如实钉住现状。
        var third = Run(source, backupRoot);
        var thirdManifest = BackupMirrorService.ReadManifest(third.ManifestPath)!;
        Assert.Equal("V2-LONGER-CONTENT", File.ReadAllText(currentFile));
        Assert.Equal(1, thirdManifest.Comparison.NewFiles);
    }

    // ==================================================================
    // c) history 清理边界
    // ==================================================================

    /// <summary>c-1 保留期 = 7 天：恰好第 7 天保留、第 8 天清理；非日期目录一律不动。</summary>
    [Fact]
    public void C_RetentionBoundary_ExactlySevenDaysKept_EighthPurged()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var root = HistoryRoot(backupRoot);
        var day8 = Path.Combine(root, FixedNow.Date.AddDays(-8).ToString("yyyy-MM-dd"));
        var day7 = Path.Combine(root, FixedNow.Date.AddDays(-7).ToString("yyyy-MM-dd"));
        var day1 = Path.Combine(root, FixedNow.Date.AddDays(-1).ToString("yyyy-MM-dd"));
        var today = Path.Combine(root, FixedNow.Date.ToString("yyyy-MM-dd"));
        var future = Path.Combine(root, FixedNow.Date.AddDays(3).ToString("yyyy-MM-dd"));
        var notDate = Path.Combine(root, "user-folder");
        var badMonth = Path.Combine(root, "2026-13-01");
        var notPadded = Path.Combine(root, "2026-9-01");

        foreach (var dir in new[] { day8, day7, day1, today, future, notDate, badMonth, notPadded })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "DATA");
        }

        var result = Run(source, backupRoot, retentionDays: 7);

        Assert.False(Directory.Exists(day8), "超过 7 天的历史目录应被清理");
        Assert.True(Directory.Exists(day7), "恰好第 7 天必须保留");
        Assert.True(Directory.Exists(day1));
        Assert.True(Directory.Exists(today));
        Assert.True(Directory.Exists(future), "未来日期的目录不能清理");
        Assert.True(Directory.Exists(notDate), "非日期目录（用户自己的）绝不能被清理");
        Assert.True(Directory.Exists(badMonth), "非法月份目录不能被当成日期目录清理");
        Assert.True(Directory.Exists(notPadded), "非零填充目录名不是本工具生成的，不能清理");
        Assert.Contains(Path.GetFileName(day8), result.PurgedHistoryFolders);
    }

    /// <summary>c-2 保留期边界解析：只有严格的 yyyy-MM-dd 才算日期目录。</summary>
    [Fact]
    public void C_HistoryFolderNameParsing_IsStrict()
    {
        var parsed = BackupRules.TryParseHistoryFolderName("2026-10-03");
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, 10, 3), parsed!.Value.Date);

        Assert.Null(BackupRules.TryParseHistoryFolderName("2026-10-3"));
        Assert.Null(BackupRules.TryParseHistoryFolderName("20261003"));
        Assert.Null(BackupRules.TryParseHistoryFolderName("2026-13-01"));
        Assert.Null(BackupRules.TryParseHistoryFolderName("2026-02-30"));
        Assert.Null(BackupRules.TryParseHistoryFolderName("user-folder"));
        Assert.Null(BackupRules.TryParseHistoryFolderName(" 2026-09-01"));
    }

    /// <summary>c-3 retention=0（永久）：任何历史目录都不能被清理，包括极老的目录。</summary>
    [Fact]
    public void C_RetentionForever_NeverPurgesAnything()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var ancient = Path.Combine(HistoryRoot(backupRoot), "1999-12-31");
        Directory.CreateDirectory(ancient);
        File.WriteAllText(Path.Combine(ancient, "very-old.txt"), "OLD");
        File.SetAttributes(Path.Combine(ancient, "very-old.txt"), FileAttributes.ReadOnly);

        var result = Run(source, backupRoot, retentionDays: BackupConfig.HistoryRetentionForever);

        Assert.True(Directory.Exists(ancient), "永久保留时任何历史目录都不能被清理");
        Assert.Empty(result.PurgedHistoryFolders);
        var manifest = BackupMirrorService.ReadManifest(result.ManifestPath)!;
        Assert.True(manifest.HistoryKeptForever);
        Assert.Contains("永久保留", manifest.HistoryPolicyText);
    }

    /// <summary>
    /// c-4 过期历史目录里有只读文件：清理失败必须安全（不抛异常、不半删、不影响本轮备份结论）。
    /// </summary>
    [Fact]
    public void C_ReadOnlyFileInExpiredHistory_PurgeFailureIsNonFatalAndNeverLosesData()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var expired = Path.Combine(HistoryRoot(backupRoot), FixedNow.Date.AddDays(-40).ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(expired);
        var readOnlyFile = Path.Combine(expired, "locked.txt");
        File.WriteAllText(readOnlyFile, "READONLY-DATA");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        var result = Run(source, backupRoot, retentionDays: 1);

        Assert.True(result.Success);
        Assert.Equal(0, result.FailedFiles);

        if (Directory.Exists(expired))
        {
            // 清理失败 → 目录原样保留，内容一个字节都不能少
            Assert.Equal("READONLY-DATA", File.ReadAllText(readOnlyFile));
        }
        else
        {
            // 清理成功 → 必须记入 PurgedHistoryFolders，不能悄悄删
            Assert.Contains(Path.GetFileName(expired), result.PurgedHistoryFolders);
        }
    }

    // ==================================================================
    // d) manifest 正确性
    // ==================================================================

    /// <summary>d-1 混合场景（新增 / 修改 / 删除 / 未变）四个计数必须精确。</summary>
    [Fact]
    public void D_MixedScenario_AllFourCountersAreExact()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        foreach (var name in new[] { "A.txt", "B.txt", "C.txt", "D.txt", "E.txt", "X.txt" })
        {
            WriteSourceFile(source, name, "1234");
        }

        var first = Run(source, backupRoot);
        Assert.Equal(6, first.Copy.CopiedFiles);

        // 3 改、2 删、1 未变、2 新增
        WriteSourceFile(source, "B.txt", "B-changed-longer");
        WriteSourceFile(source, "C.txt", "C-changed-longer");
        WriteSourceFile(source, "D.txt", "D-changed-longer");
        File.Delete(Path.Combine(source, "E.txt"));
        File.Delete(Path.Combine(source, "X.txt"));
        WriteSourceFile(source, "F.txt", "F");
        WriteSourceFile(source, "G.txt", "G");

        var second = Run(source, backupRoot);
        var manifest = BackupMirrorService.ReadManifest(second.ManifestPath)!;

        Assert.Equal(2, manifest.Comparison.NewFiles);
        Assert.Equal(3, manifest.Comparison.UpdatedFiles);
        Assert.Equal(2, manifest.Comparison.DeletedFiles);
        Assert.Equal(1, manifest.Comparison.UnchangedFiles);
        Assert.Equal(0, manifest.Comparison.FailedFiles);
        Assert.Equal(5, second.ArchivedFiles);
        Assert.Equal(6, manifest.TotalFiles);
        Assert.Equal(CountFiles(CurrentDir(backupRoot)), manifest.TotalFiles);
        Assert.Equal(AllFiles(CurrentDir(backupRoot)).Sum(f => new FileInfo(f).Length), manifest.TotalBytes);
    }

    /// <summary>
    /// d-2 manifest 损坏 / 不可写时必须降级（备份本身照常成功），
    /// 包括：非法 JSON、非法 UTF-8、空文件、清单路径被目录占位。
    /// </summary>
    [Fact]
    public void D_ManifestCorruptOrUnwritable_BackupStillSucceeds()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        var manifestPath = BackupRules.BuildManifestPath(backupRoot, Device());

        // 1) 非法 JSON
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "{ this is not valid json", new UTF8Encoding(false));
        WriteSourceFile(source, "a.txt", "A");
        var run1 = Run(source, backupRoot);
        Assert.Equal(1, run1.Copy.CopiedFiles);
        Assert.Equal(0, run1.FailedFiles);
        Assert.NotNull(BackupMirrorService.ReadManifest(run1.ManifestPath));

        // 2) 非法 UTF-8 字节
        File.WriteAllBytes(manifestPath, new byte[] { 0x80, 0x81, 0xFE, 0xFF, 0x7B });
        Assert.Null(BackupMirrorService.ReadManifest(manifestPath));
        var run2 = Run(source, backupRoot);
        Assert.Equal(0, run2.Copy.FailedFiles);
        Assert.NotNull(BackupMirrorService.ReadManifest(run2.ManifestPath));

        // 3) 空文件
        File.WriteAllText(manifestPath, string.Empty);
        Assert.Null(BackupMirrorService.ReadManifest(manifestPath));
        var run3 = Run(source, backupRoot);
        Assert.Equal(0, run3.Copy.FailedFiles);

        // 4) 清单路径被【目录】占位 → 写清单失败，但备份结果必须保持完整
        File.Delete(manifestPath);
        Directory.CreateDirectory(manifestPath);
        WriteSourceFile(source, "b.txt", "B");
        var run4 = Run(source, backupRoot);

        Assert.Equal(0, run4.FailedFiles);
        Assert.Equal(1, run4.Copy.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(CurrentDir(backupRoot), "b.txt")));
        Assert.Equal(string.Empty, run4.ManifestPath);
    }

    /// <summary>
    /// d-3【manifest 失真】同一天内第二次覆盖时，history 里实际躺着的是【更早】的版本，
    /// 而 manifest 记录的长度是【被删掉的那一版】的长度 —— 两者对不上，
    /// 用户按 manifest 追溯会拿到错误结论。
    /// </summary>
    [Fact]
    public void D_HistoryMoveRecord_MustMatchTheFileActuallyInHistory()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "A.txt", "OLD-CONTENT");     // 11 字节
        Run(source, backupRoot);

        WriteSourceFile(source, "A.txt", "NEW");             // 3 字节 → 归档 11 字节版本
        Run(source, backupRoot);

        WriteSourceFile(source, "A.txt", "THIRD-VERSION-16");// 16 字节 → 触发同日重复覆盖
        var third = Run(source, backupRoot);

        var manifest = BackupMirrorService.ReadManifest(third.ManifestPath)!;
        var record = Assert.Single(manifest.HistoryMoves, m => m.RelativePath == "A.txt");

        var actuallyArchived = new FileInfo(record.ArchivedTo).Length;

        Assert.Equal(
            actuallyArchived,
            record.Length);
    }

    // ==================================================================
    // e) 惰性枚举 / 进度分母
    // ==================================================================

    /// <summary>
    /// e-1 惰性枚举：在第一个文件被判定时，往「尚未枚举到的另一个目录」里新建文件，
    /// 本轮必须把它纳入（说明清单没有被提前物化）。
    /// </summary>
    [Fact]
    public void E_LazyEnumeration_FileCreatedMidRunInPendingDirectoryIsIncluded()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("d1/a.txt", "A", BaseTimeUtc);
        ws.CreateFile("d2/b.txt", "B", BaseTimeUtc);

        var progress = new List<CopyProgress>();
        var lateFile = string.Empty;

        var copier = new FileCopier();
        copier.OnFileDecision = (_, _, relativePath, _) =>
        {
            if (lateFile.Length > 0)
            {
                return;
            }

            var otherDirectory = relativePath.StartsWith("d1", StringComparison.OrdinalIgnoreCase) ? "d2" : "d1";
            lateFile = ws.CreateFile(otherDirectory + "/late.txt", "LATE", BaseTimeUtc);
        };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir, progress.Add);

        Assert.NotEmpty(lateFile);
        Assert.Equal(3, result.CopiedFiles);
        Assert.True(
            File.Exists(Path.Combine(ws.TargetDir, Path.GetRelativePath(ws.SourceDir, lateFile))),
            "惰性枚举必须把运行中新建的文件纳入本轮，否则说明清单被提前物化");

        Assert.Equal(3, progress[^1].TotalFiles);      // 分母 = max(预扫描 2, 实际发现 3)
        Assert.Equal(3, progress[^1].ProcessedFiles);
    }

    /// <summary>e-2 进度分母只统计参与复制的文件，字节数精确；被排除文件既不复制也不计入。</summary>
    [Fact]
    public void E_ProgressDenominator_ExcludesExcludedFiles()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "12345", BaseTimeUtc);              // 5 B
        ws.CreateFile("sub/b.bin", new byte[10], BaseTimeUtc);     // 10 B
        ws.CreateFile("skip.tmp", "tmp", BaseTimeUtc);             // 排除后缀
        ws.CreateFile("autorun.inf", "auto", BaseTimeUtc);         // 排除文件名
        ws.CreateFile("c.part", "part", BaseTimeUtc);              // 排除后缀

        var progress = new List<CopyProgress>();
        var scan = new List<int>();
        var copier = new FileCopier { OnScanProgress = scan.Add };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir, progress.Add);

        Assert.Equal(2, result.CopiedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(2, progress.Count);
        Assert.All(progress, p => Assert.Equal(2, p.TotalFiles));
        Assert.All(progress, p => Assert.Equal(15L, p.TotalBytes));
        Assert.Equal(new[] { 1, 2 }, scan);
        Assert.Equal(100d, progress[^1].Percent, 3);
    }

    // ==================================================================
    // f) 连续失败早停
    // ==================================================================

    /// <summary>
    /// f-1 失败之间夹着成功时，连续失败计数必须清零：
    /// 7 个文件里 4 个失败（第 0/2/4/6 个）且互不相邻，阈值 3 也不能早停。
    /// </summary>
    [Fact]
    public void F_ConsecutiveFailureCounter_ResetsAfterEverySuccess()
    {
        using var ws = new TempWorkspace();
        for (var i = 0; i < 7; i++)
        {
            ws.CreateFile($"f{i}.txt", $"payload-{i}", BaseTimeUtc);
        }

        var order = ProbeEnumerationOrder(ws.SourceDir, Path.Combine(ws.Root, "probe-target"));
        var expected = Enumerable.Range(0, 7)
            .Select(i => $"f{i}.txt")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!order.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        {
            return;   // 枚举顺序与预期不符：跳过，不把文件系统差异当成缺陷
        }

        Directory.CreateDirectory(ws.TargetDir);

        // 用同名【目录】占住目标位置：File.Move(tmp, target, overwrite:true) 必然失败，
        // 是真实 I/O 失败，且不依赖权限 / 磁盘满 / 硬件。
        foreach (var index in new[] { 0, 2, 4, 6 })
        {
            Directory.CreateDirectory(Path.Combine(ws.TargetDir, order[index]));
        }

        var copier = new FileCopier { MaxConsecutiveFailures = 3 };
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.False(
            result.AbortedByConsecutiveFailures,
            $"失败之间夹着成功，连续计数必须清零；实际失败 {result.FailedFiles} 个");
        Assert.Equal(4, result.FailedFiles);
        Assert.Equal(3, result.CopiedFiles);
    }

    /// <summary>
    /// f-2 全部失败时：达到阈值立即早停、AbortedByConsecutiveFailures=true、不写 manifest、
    /// 且不会被误报成「用户取消」。
    /// </summary>
    [Fact]
    public void F_AbortAtThreshold_StopsImmediately_AndSkipsManifest()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        for (var i = 0; i < 12; i++)
        {
            WriteSourceFile(source, $"f{i:D2}.txt", $"p{i}");
        }

        var current = CurrentDir(backupRoot);
        Directory.CreateDirectory(current);
        foreach (var i in Enumerable.Range(0, 12))
        {
            Directory.CreateDirectory(Path.Combine(current, $"f{i:D2}.txt"));
        }

        var result = Run(source, backupRoot, maxConsecutiveFailures: 3);

        Assert.True(result.AbortedByFailureThreshold);
        Assert.True(result.Copy.AbortedByConsecutiveFailures);
        Assert.Equal(3, result.Copy.FailedFiles);
        Assert.False(result.Cancelled);
        Assert.False(result.Copy.Cancelled);
        Assert.Equal(string.Empty, result.ManifestPath);
        Assert.False(File.Exists(BackupRules.BuildManifestPath(backupRoot, Device())));
        Assert.Equal(0, result.CurrentFileCount);
    }

    /// <summary>探测源目录的真实枚举顺序（用一次「全部成功」的复制，把决策回调里的相对路径记下来）。</summary>
    private static List<string> ProbeEnumerationOrder(string source, string probeTarget)
    {
        var order = new List<string>();
        var copier = new FileCopier();
        copier.OnFileDecision = (_, _, relativePath, _) => order.Add(relativePath);
        copier.CopyDirectory(source, probeTarget);
        return order;
    }

    // ==================================================================
    // 附加：非严格模式的边界（默认关闭，仅记录口径）
    // ==================================================================

    /// <summary>
    /// 关闭严格内容校验后：同大小、源 mtime 不更新的改写会被静默跳过（既不更新 current，也不进 history）。
    /// 默认配置下不会发生；此用例把「关闭开关的代价」钉住。
    /// </summary>
    [Fact]
    public void G_StrictVerificationDisabled_SameSizeChangeWithOlderTimestampIsSkipped()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var file = WriteSourceFile(source, "same.txt", "AAAA-OLD", BaseTimeUtc);
        var first = Run(source, backupRoot);
        Assert.Equal(1, first.Copy.CopiedFiles);

        // 同大小改写，且 mtime 更旧
        File.WriteAllText(file, "BBBB-NEW", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(file, BaseTimeUtc.AddHours(-2));

        var second = Run(source, backupRoot, strict: false);

        Assert.Equal(0, second.Copy.CopiedFiles);
        Assert.Equal(1, second.Copy.SkippedFiles);
        Assert.Equal(1, second.Copy.UnchangedAssumed);
        Assert.Equal(0, second.ArchivedFiles);
        Assert.Equal("AAAA-OLD", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "same.txt")));
        Assert.Contains("未做内容校验", second.Copy.Summary);

        // 同样的场景在默认严格模式下必须被纠正
        var third = Run(source, backupRoot, strict: true);
        Assert.Equal(1, third.Copy.CopiedFiles);
        Assert.Equal("BBBB-NEW", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "same.txt")));
        Assert.Equal("AAAA-OLD", File.ReadAllText(Path.Combine(HistoryToday(backupRoot), "same.txt")));
    }

    // ==================================================================
    // 修复点聚焦复验（Lead 第二轮修复）：同日唯一归档名 / 报告口径 / 改判计数
    // ==================================================================

    /// <summary>
    /// 修复点 1 聚焦：同一天里同一路径的多个版本各自留档，文件名必须唯一且内容可还原。
    /// 覆盖：同名不同扩展名、无扩展名、点文件、深层子目录（含空格）。
    /// </summary>
    [Fact]
    public void Fix1_SameDayMultipleVersions_VariousNames_AllPreservedUniquely()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var cases = new (string Rel, string V1, string V2, string V3)[]
        {
            ("report.txt", "txt-v1", "txt-v2-longer", "txt-v3-longest"),
            ("report.md", "md-v1", "md-v2-longer", "md-v3-longest"),
            ("README", "noext-v1", "noext-v2-longer", "noext-v3-longest"),
            ("deep/level two/deep3/data.bin", "bin-v1", "bin-v2-longer", "bin-v3-longest"),
            (".env", "dot-v1", "dot-v2-longer", "dot-v3-longest")
        };

        foreach (var c in cases)
        {
            WriteSourceFile(source, c.Rel, c.V1);
        }

        Run(source, backupRoot);                       // current = V1

        foreach (var c in cases)
        {
            WriteSourceFile(source, c.Rel, c.V2);
        }

        Run(source, backupRoot);                       // history = V1（无冲突名），current = V2

        foreach (var c in cases)
        {
            WriteSourceFile(source, c.Rel, c.V3);
        }

        var third = Run(source, backupRoot);           // history += V2（必须走唯一名）

        // current 全部是新版本
        foreach (var c in cases)
        {
            Assert.Equal(c.V3, File.ReadAllText(Path.Combine(CurrentDir(backupRoot), c.Rel), Encoding.UTF8));
        }

        var historyContents = AllFiles(HistoryRoot(backupRoot))
            .Select(f => File.ReadAllText(f, Encoding.UTF8))
            .ToList();

        foreach (var c in cases)
        {
            Assert.Contains(c.V1, historyContents);
            Assert.Contains(c.V2, historyContents);
        }

        // 每条归档记录都必须指向真实文件、长度一致、路径互不重复
        Assert.Equal(cases.Length, third.HistoryMoves.Count);
        foreach (var move in third.HistoryMoves)
        {
            Assert.True(File.Exists(move.ArchivedTo), $"归档记录指向的文件不存在：{move.ArchivedTo}");
            Assert.Equal(new FileInfo(move.ArchivedTo).Length, move.Length);
        }

        // 归档名必须「可读、可还原」：{原名}@{HHmmss}{原扩展名}，目录结构原样保留
        var deepRelative = Path.Combine("deep", "level two", "deep3", "data.bin");
        Assert.Matches(@"^report@\d{6}\.txt$", Path.GetFileName(third.HistoryMoves.Single(m => m.RelativePath == "report.txt").ArchivedTo));
        Assert.Matches(@"^report@\d{6}\.md$", Path.GetFileName(third.HistoryMoves.Single(m => m.RelativePath == "report.md").ArchivedTo));
        Assert.Matches(@"^README@\d{6}$", Path.GetFileName(third.HistoryMoves.Single(m => m.RelativePath == "README").ArchivedTo));
        Assert.Equal(
            Path.Combine(HistoryToday(backupRoot), "deep", "level two", "deep3"),
            Path.GetDirectoryName(third.HistoryMoves.Single(m => m.RelativePath == deepRelative).ArchivedTo));
        Assert.Matches(
            @"^data@\d{6}\.bin$",
            Path.GetFileName(third.HistoryMoves.Single(m => m.RelativePath == deepRelative).ArchivedTo));

        var distinct = third.HistoryMoves
            .Select(m => m.ArchivedTo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        Assert.Equal(third.HistoryMoves.Count, distinct);

        Assert.Equal(0, third.DeletedFilesKnownLost);
    }

    /// <summary>
    /// 修复点 1 聚焦：同一秒内多个候选名都已被占用时，必须靠 <c>-n</c> 继续让位，
    /// 谁都不覆盖谁（这是「同一天每一版都留档」的最后一道保证）。
    /// 命名契约：<c>{原名}@{HHmmss}{扩展名}</c>，同秒再冲突则 <c>{原名}@{HHmmss}-2</c>、<c>-3</c>…
    /// （若这样会让路径超过 MAX_PATH，则退化为 <c>history\{日期}\_conflicts\{n}\{原名}</c>）。
    /// </summary>
    [Fact]
    public void Fix1_SameSecondArchiveCollisions_FallBackToCounterSuffix()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "A.txt", "V1");
        Run(source, backupRoot);

        var history = HistoryToday(backupRoot);
        Directory.CreateDirectory(history);

        // 占住「无后缀名」（= 之前某次同日归档）
        File.WriteAllText(Path.Combine(history, "A.txt"), "EARLIER-ARCHIVE");

        // 预占当前秒与随后几秒的候选名，消除「跨秒导致命名不同」的不确定性
        var stampBase = DateTime.Now;
        for (var i = 0; i <= 4; i++)
        {
            var stamp = stampBase.AddSeconds(i).ToString("HHmmss");
            File.WriteAllText(Path.Combine(history, $"A@{stamp}.txt"), $"occupied-{stamp}");
            File.WriteAllText(Path.Combine(history, $"A@{stamp}-2.txt"), $"occupied-{stamp}-2");
        }

        WriteSourceFile(source, "A.txt", "V2-longer");
        var second = Run(source, backupRoot);

        var record = Assert.Single(second.HistoryMoves);
        var archivedName = Path.GetFileName(record.ArchivedTo);

        Assert.StartsWith("A@", archivedName);
        Assert.EndsWith("-3.txt", archivedName);
        Assert.Equal("V1", File.ReadAllText(record.ArchivedTo));
        Assert.Equal("V2-longer", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "A.txt")));

        // 被预占的文件一个都不能被覆盖
        Assert.Equal("EARLIER-ARCHIVE", File.ReadAllText(Path.Combine(history, "A.txt")));
    }

    /// <summary>
    /// 修复点 1 的 MaxPath 边界：唯一归档名比原名长 7 个字符（<c>@HHmmss</c>），
    /// 可能把「本来刚好能写」的归档路径推过 MAX_PATH。
    /// 结论要求：无论成功还是失败，current 里那一版内容都不允许消失，且不得计入 DeletedFilesKnownLost。
    /// </summary>
    [Fact]
    public void Fix1_MaxPathBoundary_UniqueSuffixMustNotCauseContentLoss()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        const int maxPath = 260;
        var current = CurrentDir(backupRoot);
        var history = HistoryToday(backupRoot);

        // 令「无冲突归档路径」= 256 字符（可写），加 @HHmmss 后 = 263 字符（超限）
        var nameLength = 255 - history.Length;
        if (nameLength < 20 || nameLength > 200)
        {
            return;   // 临时目录前缀不合适，无法构造稳定用例：跳过
        }

        var fileName = new string('N', nameLength - 4) + ".txt";
        if (source.Length + 1 + nameLength >= maxPath || current.Length + 1 + nameLength + 4 >= maxPath)
        {
            return;
        }

        WriteSourceFile(source, fileName, "V1-CONTENT");
        var first = Run(source, backupRoot);
        var currentFile = Path.Combine(current, fileName);
        if (!File.Exists(currentFile) || first.Copy.FailedFiles > 0)
        {
            return;   // 本机路径前缀过长，构造不出有效用例
        }

        // 第二次修改：V1 归档到无冲突名（256 字符，应成功）
        WriteSourceFile(source, fileName, "V2-LONGER-CONTENT");
        var second = Run(source, backupRoot);
        var plainArchive = Path.Combine(history, fileName);
        Assert.True(
            File.Exists(plainArchive),
            $"无冲突归档路径 {plainArchive.Length} 字符（< {maxPath}）应能写入；" +
            $"失败={second.FailedFiles}，错误={string.Join(" | ", second.Copy.Errors.Take(2))}");

        // 第三次修改：同名冲突 → 唯一名（+7 字符）
        WriteSourceFile(source, fileName, "V3-LONGER-CONTENT!!");
        var third = Run(source, backupRoot);

        var uniqueLength = plainArchive.Length + 7;
        var inCurrent = File.Exists(currentFile) ? File.ReadAllText(currentFile, Encoding.UTF8) : null;
        var v2Archived = AllFiles(HistoryRoot(backupRoot))
            .Any(f => File.ReadAllText(f, Encoding.UTF8) == "V2-LONGER-CONTENT");

        Assert.True(
            inCurrent == "V2-LONGER-CONTENT" || v2Archived,
            $"唯一归档名 {uniqueLength} 字符超限时，current 里的 V2 必须原样保留（内容绝不能消失）；" +
            $"current={inCurrent ?? "(不存在)"}，ArchiveErrors={string.Join(" | ", third.ArchiveErrors)}");
        Assert.Equal(0, third.DeletedFilesKnownLost);

        if (v2Archived)
        {
            // 本机运行时支持超长路径：归档成功，current 应推进到 V3
            Assert.Equal("V3-LONGER-CONTENT!!", inCurrent);
        }
        else
        {
            // 运行时不支持超长路径：唯一名与兜底都会失败 → 必须安全降级（current 保留 V2）
            Assert.Equal("V2-LONGER-CONTENT", inCurrent);
            Assert.Equal(1, third.Copy.FailedFiles);
            Assert.Equal(1, third.FailedFiles);   // 覆盖阶段的失败只算一次，不翻倍
        }
    }

    /// <summary>
    /// 修复点 4 聚焦：<c>ReclassifyArchivedButNotCopied</c> 改判后，四个计数仍必须自洽 ——
    /// 一个文件不能同时被算作「更新」和「删除」，被归档+复制失败的文件也不能挤掉真正的新增计数。
    /// </summary>
    [Fact]
    public void Fix4_ReclassifyArchivedButNotCopied_CountersMustStayConsistent()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var aFile = WriteSourceFile(source, "A.txt", "A-V1");
        Run(source, backupRoot);                       // current/A.txt = A-V1

        // 新增 B.txt，同时让 A.txt 的新版本写不进去（独占锁定）
        WriteSourceFile(source, "B.txt", "B-NEW");
        File.WriteAllText(aFile, "A-V2-LONGER", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(aFile, BaseTimeUtc.AddHours(1));

        MirrorResult second;
        using (var exclusive = new FileStream(aFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            second = Run(source, backupRoot);
        }

        Assert.Equal(1, second.Copy.FailedFiles);
        Assert.True(File.Exists(Path.Combine(CurrentDir(backupRoot), "B.txt")));
        Assert.False(File.Exists(Path.Combine(CurrentDir(backupRoot), "A.txt")));
        Assert.True(File.Exists(Path.Combine(HistoryToday(backupRoot), "A.txt")));

        var manifest = BackupMirrorService.ReadManifest(second.ManifestPath)!;
        var c = manifest.Comparison;
        var counters = $"新增 {c.NewFiles}，更新 {c.UpdatedFiles}，删除 {c.DeletedFiles}，未变 {c.UnchangedFiles}，失败 {c.FailedFiles}";

        // 事实：本轮 2 个文件 —— B.txt 是 U 盘新增，A.txt 未更新成功（失败），其旧版内容在 history。
        Assert.True(c.NewFiles == 1, $"B.txt 是真实新增，不能被 A.txt 的归档挤掉：{counters}");
        Assert.True(c.UpdatedFiles == 0, $"A.txt 的新版本从未写入 current，不能算「更新」：{counters}");
        Assert.True(c.DeletedFiles == 1, $"A.txt 的内容已移出 current（在 history 里可恢复）：{counters}");
        Assert.True(c.FailedFiles == 1, $"A.txt 的复制失败必须保留：{counters}");
    }

    /// <summary>
    /// 计数口径：复制阶段的归档失败 + 「U 盘已删除」阶段的归档失败同时发生时，
    /// FailedFiles 必须恰好等于【真实失败的文件数】（2 个不同的文件 → 2），既不翻倍也不漏计。
    /// </summary>
    [Fact]
    public void Fix2_MixedFailures_FailedFilesEqualsRealFailedFileCount()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        WriteSourceFile(source, "A.txt", "A-OLD-16-CHARS!");
        WriteSourceFile(source, "B.txt", "B-MUST-KEEP");
        Run(source, backupRoot);                                   // current 里有 A、B

        // 让 history 的日期目录无法创建：复制阶段与删除阶段都会归档失败
        Directory.CreateDirectory(HistoryRoot(backupRoot));
        File.WriteAllText(HistoryToday(backupRoot), "not a directory");

        WriteSourceFile(source, "A.txt", "A-NEW");                 // A 被修改 → 覆盖归档失败 → 复制失败
        File.Delete(Path.Combine(source, "B.txt"));                // B 被删除 → 软删除归档失败

        var second = Run(source, backupRoot);
        var manifest = BackupMirrorService.ReadManifest(second.ManifestPath)!;

        Assert.Equal(1, second.Copy.FailedFiles);                  // A.txt 的复制失败
        Assert.Equal(1, second.DeletionArchiveFailures);           // B.txt 的软删除归档失败
        Assert.Equal(2, second.FailedFiles);                       // 2 个真实失败文件，恰好 2
        Assert.Equal(2, manifest.Comparison.FailedFiles);          // manifest 口径一致
        Assert.Equal(0, second.DeletedFilesKnownLost);             // 两份内容都还在 current

        // 内容都没丢：A 保持旧版本（未被覆盖），B 仍在 current
        Assert.Equal("A-OLD-16-CHARS!", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "A.txt")));
        Assert.Equal("B-MUST-KEEP", File.ReadAllText(Path.Combine(CurrentDir(backupRoot), "B.txt")));
        Assert.Equal(0, second.Copy.CopiedFiles);
    }

    /// <summary>
    /// 运行时能力探测：本机 .NET 8 是否能写超过经典 MAX_PATH(260) 的路径。
    /// 这决定下面 MaxPath 用例走「兜底归档成功」还是「安全降级」分支，也决定归档名 +7 字符是否真的是风险。
    /// </summary>
    [Fact]
    public void Probe_RuntimeSupportsLongPaths()
    {
        using var ws = new TempWorkspace();

        var builder = new StringBuilder(ws.Root);
        while (builder.Length < 300)
        {
            builder.Append('\\').Append(new string('L', 30));
        }

        var longFile = builder.ToString();
        Assert.True(longFile.Length > 260, $"探测路径长度 {longFile.Length} 应超过 260");

        var directory = Path.GetDirectoryName(longFile)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(longFile, "LONG-PATH-OK");

        Assert.True(File.Exists(longFile));
        Assert.Equal("LONG-PATH-OK", File.ReadAllText(longFile));
    }

    // ==================================================================
    // 修复点 3 聚焦：_conflicts 长路径兜底 + 历史清理副作用
    // ==================================================================

    /// <summary>
    /// 修复点 3：唯一归档名超过 MaxClassicPathLength 时改用 <c>history\{日期}\h{n}\{哈希}{扩展名}</c> 兜底。
    /// <para>新契约：兜底【故意不保留原文件名】——只有丢掉原目录、把名字压成哈希，路径才真的变短。</para>
    /// <para>本用例检验：兜底真的被选中、真实哈希名形态、真实长度确实更短且 ≤260、
    /// 内容不丢、manifest 仍可用原相对路径追溯。</para>
    /// </summary>
    [Fact]
    public void Fix3_ConflictFallback_IsUsedAndUsesShortHashedName()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var current = CurrentDir(backupRoot);
        var history = HistoryToday(backupRoot);

        // 令「无冲突归档路径」= 256 字符：唯一名（+7）= 263 > 260 → 必须走兜底
        var nameLength = 255 - history.Length;
        if (nameLength < 20 || nameLength > 200)
        {
            return;   // 临时目录前缀不合适：跳过
        }

        var fileName = new string('N', nameLength - 4) + ".txt";
        if (source.Length + 1 + nameLength >= BackupMirrorService.MaxClassicPathLength
            || current.Length + 1 + nameLength + 4 >= BackupMirrorService.MaxClassicPathLength)
        {
            return;
        }

        WriteSourceFile(source, fileName, "V1-CONTENT");
        if (Run(source, backupRoot).Copy.FailedFiles > 0)
        {
            return;   // 本机路径前缀过长，构造不出有效用例
        }

        WriteSourceFile(source, fileName, "V2-LONGER-CONTENT");
        var second = Run(source, backupRoot);
        var plainArchive = Path.Combine(history, fileName);
        Assert.True(File.Exists(plainArchive), $"无冲突归档路径 {plainArchive.Length} 字符应能写入");

        WriteSourceFile(source, fileName, "V3-LONGER-CONTENT!!");
        var third = Run(source, backupRoot);

        Assert.Equal(0, third.DeletedFilesKnownLost);

        var v2Files = AllFiles(HistoryRoot(backupRoot))
            .Where(f => File.ReadAllText(f, Encoding.UTF8) == "V2-LONGER-CONTENT")
            .ToList();

        if (v2Files.Count == 0)
        {
            // 运行时不支持长路径且兜底也放不下：必须安全降级（current 保留 V2、失败被如实上报）
            Assert.Equal("V2-LONGER-CONTENT", File.ReadAllText(Path.Combine(current, fileName)));
            Assert.Equal(1, third.Copy.FailedFiles);
            Assert.Equal(1, third.FailedFiles);
            return;
        }

        // 兜底落地：真实哈希名（8 位十六进制 + 原扩展名），放在 h{n} 下
        var archived = Assert.Single(v2Files);
        Assert.Matches(@"^[0-9a-f]{8}\.txt$", Path.GetFileName(archived));
        Assert.NotEqual(fileName, Path.GetFileName(archived));
        Assert.Contains(
            Path.DirectorySeparatorChar + BackupMirrorService.ShortFallbackFolderName,
            archived);

        // 真实长度检验：必须 ≤260 且比唯一名短（否则这个分支就是死代码）
        var uniqueLength = plainArchive.Length + "@HHmmss".Length;
        Assert.Equal(history.Length + 1 + 2 + 1 + 12, archived.Length);   // history\{日期}\h1\{8hex}.txt
        Assert.True(
            archived.Length <= BackupMirrorService.MaxClassicPathLength,
            $"真实兜底路径必须落在经典上限内：{archived.Length} 字符（上限 {BackupMirrorService.MaxClassicPathLength}）");
        Assert.True(
            archived.Length < uniqueLength,
            $"真实兜底路径必须比唯一名短：兜底 {archived.Length}，唯一名 {uniqueLength}，无冲突名 {plainArchive.Length}");

        Assert.Equal("V3-LONGER-CONTENT!!", File.ReadAllText(Path.Combine(current, fileName)));
        Assert.Equal(1, third.Copy.CopiedFiles);
        Assert.Equal(0, third.FailedFiles);   // 兜底成功后本次归档不应产生失败

        // 可追溯性：文件名被哈希了，但 manifest 记录仍保留原相对路径与真实落盘位置
        var thirdManifest = BackupMirrorService.ReadManifest(third.ManifestPath)!;
        Assert.Contains(
            thirdManifest.HistoryMoves,
            m => m.RelativePath == fileName && m.ArchivedTo == archived);
    }

    /// <summary>
    /// 新兜底契约的对抗点：<c>h{n}</c> 的名字只由「相对路径」的哈希决定，
    /// 因此同一天同一长路径的第 2、第 3 个版本必须逐级落到 h1、h2……，一个都不能覆盖另一个。
    /// </summary>
    [Fact]
    public void Fix3_ShortFallback_RepeatedSameDayConflicts_AllVersionsKept()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);

        var current = CurrentDir(backupRoot);
        var history = HistoryToday(backupRoot);

        var nameLength = 255 - history.Length;
        if (nameLength < 20 || nameLength > 200)
        {
            return;
        }

        var fileName = new string('N', nameLength - 4) + ".txt";
        if (source.Length + 1 + nameLength >= BackupMirrorService.MaxClassicPathLength
            || current.Length + 1 + nameLength + 4 >= BackupMirrorService.MaxClassicPathLength)
        {
            return;
        }

        var versions = new[] { "V1-CONTENT", "V2-LONGER-CONTENT", "V3-LONGER-CONTENT!!", "V4-LONGER-CONTENT!!!!" };

        WriteSourceFile(source, fileName, versions[0]);
        Assert.Equal(0, Run(source, backupRoot).Copy.FailedFiles);

        for (var i = 1; i < versions.Length; i++)
        {
            WriteSourceFile(source, fileName, versions[i]);
            var result = Run(source, backupRoot);

            if (result.Copy.FailedFiles > 0)
            {
                return;   // 运行时不支持长路径且兜底放不下：跳过（安全降级路径由别的用例覆盖）
            }

            Assert.Equal(versions[i], File.ReadAllText(Path.Combine(current, fileName)));
            Assert.Equal(0, result.DeletedFilesKnownLost);
        }

        // 前三个版本必须全部留档（无冲突名 + h1 + h2），current 是 V4
        var archivedContents = AllFiles(HistoryRoot(backupRoot))
            .Select(f => File.ReadAllText(f, Encoding.UTF8))
            .ToList();

        foreach (var expected in versions[..^1])
        {
            Assert.True(
                archivedContents.Contains(expected),
                $"同一天的第 {Array.IndexOf(versions, expected) + 1} 版必须留档，当前 history 内容=" +
                $"{string.Join(" | ", archivedContents.Select(c => c[..Math.Min(12, c.Length)]))}");
        }

        // 兜底目录必须按序号分开放（h1、h2 各一份），不能互相覆盖
        var shortDirs = AllFiles(HistoryRoot(backupRoot))
            .Where(f => Path.GetDirectoryName(f) is { } d
                        && Path.GetFileName(d).StartsWith(BackupMirrorService.ShortFallbackFolderName, StringComparison.Ordinal))
            .Select(Path.GetDirectoryName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(shortDirs.Count >= 2, $"同日多版本必须逐级落到 h1/h2…，实际兜底目录={string.Join(",", shortDirs)}");
    }

    /// <summary>
    /// 兜底路径的算术检验：兜底必须【真的比唯一名短】且落在经典 MAX_PATH 之内，
    /// 否则这个分支等于死代码（在受经典路径限制的宿主上一样会失败）。
    /// </summary>
    [Fact]
    public void Fix3_ConflictFallback_MustActuallyFitWithinClassicMaxPath()
    {
        using var ws = new TempWorkspace();
        var (_, backupRoot) = Prepare(ws);

        var history = HistoryToday(backupRoot);
        var nameLength = 255 - history.Length;
        if (nameLength < 20 || nameLength > 200)
        {
            return;
        }

        var fileName = new string('N', nameLength - 4) + ".txt";

        var plain = Path.Combine(history, fileName);                                     // 无冲突
        var unique = Path.Combine(history, fileName[..^4] + "@143005.txt");              // 唯一名（+7）

        // 兜底真实形态：history\{日期}\h{n}\{8 位哈希}.txt（丢目录 + 哈希名）
        var fallback = Path.Combine(
            history,
            BackupMirrorService.ShortFallbackFolderName + "1",
            "01234567.txt");

        Assert.True(unique.Length > BackupMirrorService.MaxClassicPathLength, "本用例前提：唯一名必须超限");
        Assert.True(
            fallback.Length <= BackupMirrorService.MaxClassicPathLength,
            $"兜底路径必须落在经典上限内：unique={unique.Length}，fallback={fallback.Length}（上限 {BackupMirrorService.MaxClassicPathLength}）");
        Assert.True(
            fallback.Length < unique.Length,
            $"兜底必须真的更短：unique={unique.Length}，fallback={fallback.Length}（无冲突名 {plain.Length}）");
    }

    /// <summary>
    /// 兜底第 ②/③ 级落点的清理安全：<c>history\{日期}\{哈希}</c> 在日期目录内（随日期目录清理），
    /// <c>history\{哈希}</c> 在 history 根下 —— 清理只删能解析成 yyyy-MM-dd 的目录，绝不能误删它。
    /// </summary>
    [Fact]
    public void Fix3_ShortFallbackFilesOutsideDateFolders_SurvivePurge()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var root = HistoryRoot(backupRoot);
        var expiredDate = Path.Combine(root, FixedNow.Date.AddDays(-40).ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(expiredDate);

        var level2 = Path.Combine(expiredDate, "a1b2c3d4.txt");   // 第②级形态：日期目录内的文件
        File.WriteAllText(level2, "LEVEL2");

        var level3 = Path.Combine(root, "deadbeef.txt");          // 第③级形态：history 根下的文件
        File.WriteAllText(level3, "LEVEL3");

        var userDir = Path.Combine(root, "user-folder");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "keep.txt"), "USER");

        var result = Run(source, backupRoot, retentionDays: 7);

        Assert.False(Directory.Exists(expiredDate), "过期日期目录（含其中的第②级文件）必须被整体清理");
        Assert.True(File.Exists(level3), "history 根下的第③级兜底文件不能被清理逻辑误删");
        Assert.Equal("LEVEL3", File.ReadAllText(level3));
        Assert.True(File.Exists(Path.Combine(userDir, "keep.txt")), "非日期目录不能被清理");
        Assert.Contains(Path.GetFileName(expiredDate), result.PurgedHistoryFolders);
        Assert.Equal(0, result.DeletedFilesKnownLost);
    }

    /// <summary>
    /// 兜底目录的清理副作用：兜底目录位于 <c>history\{yyyy-MM-dd}\</c> 之内，
    /// 因此它必须随所属日期目录一起被清理；保留期内的日期目录（含兜底目录）不能被动。
    /// </summary>
    [Fact]
    public void Fix3_ConflictFolder_IsPurgedWithItsDateFolderOnly()
    {
        using var ws = new TempWorkspace();
        var (source, backupRoot) = Prepare(ws);
        WriteSourceFile(source, "a.txt", "A");

        var root = HistoryRoot(backupRoot);
        var expired = Path.Combine(root, FixedNow.Date.AddDays(-40).ToString("yyyy-MM-dd"));
        var kept = Path.Combine(root, FixedNow.Date.AddDays(-1).ToString("yyyy-MM-dd"));

        foreach (var dateFolder in new[] { expired, kept })
        {
            var conflicts = Path.Combine(dateFolder, BackupMirrorService.ShortFallbackFolderName + "1");
            Directory.CreateDirectory(conflicts);
            File.WriteAllText(Path.Combine(conflicts, "old-version.txt"), "ARCHIVED");
        }

        var result = Run(source, backupRoot, retentionDays: 7);

        Assert.False(Directory.Exists(expired), "过期日期目录（含兜底目录）必须被整体清理");
        Assert.True(Directory.Exists(kept), "保留期内的日期目录不能被清理");
        Assert.True(
            File.Exists(Path.Combine(kept, BackupMirrorService.ShortFallbackFolderName + "1", "old-version.txt")),
            "保留期内的兜底目录内容一个字节都不能少");
        Assert.Contains(Path.GetFileName(expired), result.PurgedHistoryFolders);
        Assert.Equal(0, result.DeletedFilesKnownLost);
    }
}
