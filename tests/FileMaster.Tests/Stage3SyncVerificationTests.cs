using System.Diagnostics;
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// 阶段三验收（独立验证者 verifier3）：同步的「先复制、后删除」与「忽略名单不可被整目录删除绕过」。
/// <para>本文件只做对抗性验证，不修改任何产品代码。对应 task-4 的对抗点：</para>
/// <list type="number">
/// <item>对抗点 4：复制失败时删除绝不发生；全部成功时删除确实在所有复制之后。</item>
/// <item>对抗点 5：目标侧 <c>.git</c> 等被忽略内容不得被整目录递归删除；退化删除后
/// 「未被忽略的仍删、被忽略的必留」。</item>
/// </list>
/// <para>所有删除一律 <c>UseRecycleBin = false</c>（永久删除），避免污染回收站，
/// 同时让「文件还在不在」成为唯一且直接的判定依据。</para>
/// </summary>
public sealed class Stage3SyncVerificationTests
{
    // ==================================================================
    // 公共辅助
    // ==================================================================

    /// <summary>在指定根目录下创建文件（自动补齐父目录），时间戳固定在过去，返回绝对路径。</summary>
    private static string CreateFileIn(string baseDirectory, string relativePath, string content)
    {
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddHours(-2));
        return fullPath;
    }

    /// <summary>在源目录下创建文件。</summary>
    private static string CreateSourceFile(TempWorkspace ws, string relativePath, string content)
        => CreateFileIn(ws.SourceDir, relativePath, content);

    /// <summary>在目标目录下创建文件。</summary>
    private static string CreateTargetFile(TempWorkspace ws, string relativePath, string content)
        => CreateFileIn(ws.TargetDir, relativePath, content);

    /// <summary>构造同步选项（源 / 目标固定为临时工作区的 source / target）。</summary>
    private static FolderSyncOptions CreateOptions(
        TempWorkspace ws,
        SyncMode mode = SyncMode.OneWaySync,
        string? excludeNames = null)
        => new()
        {
            SourceDirectory = ws.SourceDir,
            TargetDirectory = ws.TargetDir,
            Mode = mode,
            IncludeSubDirectories = true,
            CompareByHash = false,
            UseRecycleBin = false,
            ExcludeNames = excludeNames
        };

    /// <summary>目标目录下的绝对路径。</summary>
    private static string TargetPathOf(TempWorkspace ws, string relativePath)
        => Path.Combine(ws.TargetDir, relativePath);

    /// <summary>记录进度回调里上报的动作顺序（<see cref="IProgress{T}"/> 是同步调用，不经过消息循环）。</summary>
    private sealed class RecordingProgress : IProgress<SyncProgress>
    {
        /// <summary>按上报顺序记录的动作文本。</summary>
        public List<string> Actions { get; } = new();

        public void Report(SyncProgress value) => Actions.Add(value.CurrentAction);
    }

    /// <summary>第一次收到进度上报时立刻取消（用于验证「取消后不得再执行删除」）。</summary>
    private sealed class CancelOnFirstReport : IProgress<SyncProgress>
    {
        private readonly CancellationTokenSource _cts;

        /// <summary>创建取消触发器。</summary>
        public CancelOnFirstReport(CancellationTokenSource cts) => _cts = cts;

        /// <summary>收到的上报次数。</summary>
        public int Reports { get; private set; }

        public void Report(SyncProgress value)
        {
            Reports++;
            _cts.Cancel();
        }
    }

    /// <summary>是否为删除类动作。</summary>
    private static bool IsDelete(SyncActionKind kind)
        => kind is SyncActionKind.DeleteFile or SyncActionKind.DeleteDirectory;

    /// <summary>目录下所有以本工具暂存扩展名结尾的【文件】（同名目录不算残骸）。</summary>
    private static string[] FindTempFileResidue(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(static file => file.EndsWith(FolderSyncService.TempExtension, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();

    /// <summary>断言目录里没有 .tmp 文件残骸。</summary>
    private static void AssertNoTempFileResidue(string directory)
    {
        var residue = FindTempFileResidue(directory);
        Assert.True(residue.Length == 0, "不得残留 .tmp 文件：" + string.Join(" | ", residue));
    }

    /// <summary>把一条实测指标追加到 <c>.tmp\stage3-verify3-metrics.txt</c>（供验证报告引用真实数字）。</summary>
    private static void RecordMetric(string line)
    {
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var directory = Path.Combine(repoRoot, ".tmp");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "stage3-verify3-metrics.txt"), line + Environment.NewLine);
        }
        catch
        {
            // 记录失败不影响断言
        }
    }

    /// <summary>把计划项重新排序（用于验证「执行顺序不依赖计划顺序」）。</summary>
    private static SyncPlan Reorder(SyncPlan plan, Func<SyncPlanItem, int> keySelector)
        => new() { Items = plan.Items.OrderBy(keySelector).ToArray() };

    /// <summary>
    /// 用 <c>mklink /J</c> 创建目录联接（普通用户即可，不需要管理员权限）。
    /// </summary>
    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(15000);
        }
        catch
        {
            return false;
        }

        return Directory.Exists(linkPath)
               && (new DirectoryInfo(linkPath).Attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>尽力删除目录联接本身（绝不递归进链接目标）。</summary>
    private static void TryRemoveLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    // ==================================================================
    // 对抗点 4：复制失败时删除绝不发生
    // ==================================================================

    /// <summary>
    /// 对抗点 4：源文件被其它程序独占（真实复制失败）时，删除阶段必须整体跳过。
    /// <para>目标里放了 3 个「canary」（1 个多余文件 + 1 个多余目录里的 2 个文件），
    /// 其中 2 个 canary 位于计划要整目录删除的目录里：只要递归删除被执行，它们必然消失。
    /// 修复前 <c>Apply</c> 先执行删除 → canary 全部消失（真实的数据丢失窗口）。</para>
    /// </summary>
    [Fact]
    public void Apply_WhenCopyFails_DeletePhaseNeverRuns_CanariesSurvive()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "data.txt", "IMPORTANT-DATA");
        CreateTargetFile(ws, "extra.txt", "canary-file");
        CreateTargetFile(ws, Path.Combine("extra-dir", "canary.txt"), "canary-in-dir");
        CreateTargetFile(ws, Path.Combine("extra-dir", "sub", "deep.txt"), "canary-deep");

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Null(plan.Error);
        Assert.Equal(1, plan.CopyCount);
        Assert.Equal(3, plan.DeleteFileCount);
        Assert.Equal(2, plan.DeleteDirectoryCount);

        // 用独占锁制造「复制一定失败」：File.Copy 打不开源文件
        using var exclusive = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var report = service.ApplyWithReport(plan, options);
        var result = report.Result;

        // 1) 删除绝不发生：3 个 canary 必须全部存活
        Assert.True(File.Exists(TargetPathOf(ws, "extra.txt")), "复制失败时不得删除目标中多余的文件");
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("extra-dir", "canary.txt"))), "复制失败时不得删除多余目录里的内容");
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("extra-dir", "sub", "deep.txt"))), "复制失败时不得删除多层子目录里的内容");
        Assert.True(Directory.Exists(TargetPathOf(ws, "extra-dir")), "复制失败时不得删除多余目录");

        // 2) 5 个删除项全部标记为「保护性跳过」，而不是失败
        Assert.True(report.CopyPhaseFailed, "复制阶段失败必须暴露到报告里");
        Assert.Equal(5, report.SkippedDeleteCount);
        Assert.True(report.DeletesSkipped);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(5, result.SkippedCount);
        Assert.False(result.Success);

        foreach (var item in result.Items.Where(static item => IsDelete(item.Kind)))
        {
            Assert.True(item.Skipped, $"删除项 {item.RelativePath} 必须标记为 Skipped");
            Assert.False(item.Success);
            Assert.Contains("因复制失败已跳过删除", item.Error);
        }

        // 3) 结果顺序与计划一致（UI 对照用）
        Assert.Equal(plan.Items.Select(static item => item.RelativePath), result.Items.Select(static item => item.RelativePath));

        // 4) 恢复正常后重跑：删除照常执行，目标最终正确
        exclusive.Dispose();
        var retry = service.ApplyWithReport(plan, options);

        Assert.Equal(0, retry.SkippedDeleteCount);
        Assert.True(retry.Result.Success, retry.Result.Summary);
        Assert.False(File.Exists(TargetPathOf(ws, "extra.txt")));
        Assert.False(Directory.Exists(TargetPathOf(ws, "extra-dir")));
        Assert.True(File.Exists(TargetPathOf(ws, "data.txt")));
    }

    /// <summary>
    /// 对抗点 4：目标里是【同名目录】而源里是【同名文件】时，复制必然失败
    /// （<c>File.Copy</c> 不能覆盖目录），因此计划中的「删除该目录」也绝不能被提前执行。
    /// <para>这里额外记录了修复后的一个行为变化：这类计划在「先复制」的顺序下永远无法执行成功
    /// （复制项失败 → 删除被跳过），详见验证报告。</para>
    /// </summary>
    [Fact]
    public void Apply_WhenTargetPathIsADirectory_CopyFailsAndDirectorySurvives()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "node", "NEW-FILE-CONTENT");
        CreateTargetFile(ws, Path.Combine("node", "keep.txt"), "OLD-DIR-CONTENT");

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Null(plan.Error);
        Assert.Equal(1, plan.CopyCount);
        Assert.Equal(1, plan.DeleteFileCount);
        Assert.Equal(1, plan.DeleteDirectoryCount);

        var report = service.ApplyWithReport(plan, options);

        Assert.True(report.CopyPhaseFailed);
        Assert.Equal(2, report.SkippedDeleteCount);
        Assert.True(Directory.Exists(TargetPathOf(ws, "node")), "复制失败时不得删除同名目录");
        Assert.Equal("OLD-DIR-CONTENT", File.ReadAllText(TargetPathOf(ws, Path.Combine("node", "keep.txt"))));
        Assert.Equal(1, report.Result.FailedCount);
    }

    /// <summary>
    /// 对抗点 4：全部复制成功时，删除【确实】在复制全部完成之后执行 —— 并且这一点与
    /// <c>plan.Items</c> 的顺序无关（故意把删除项挪到计划最前面，模拟旧实现依赖的计划顺序）。
    /// <para>判定依据：进度回调按执行顺序上报动作文本，删除动作不得出现在任何复制/建目录动作之前。</para>
    /// </summary>
    [Fact]
    public void Apply_AllCopiesSucceed_DeletesRunOnlyAfterEveryCopy_EvenWhenPlanListsDeletesFirst()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "data.txt", "DATA");
        CreateSourceFile(ws, Path.Combine("sub", "inner.txt"), "INNER");
        CreateTargetFile(ws, "extra.txt", "EXTRA");
        CreateTargetFile(ws, Path.Combine("extra-dir", "junk.txt"), "JUNK");

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(2, plan.DeleteFileCount);
        Assert.Equal(1, plan.DeleteDirectoryCount);
        Assert.Equal(2, plan.CopyCount);

        // 把删除项排到最前面：执行顺序必须仍然由 Apply 的阶段决定，而不是由计划顺序决定
        var reversed = Reorder(plan, static item => IsDelete(item.Kind) ? 0 : 1);
        Assert.True(IsDelete(reversed.Items[0].Kind), "用例前提：重排后删除项确实排在最前");

        var progress = new RecordingProgress();
        var report = service.ApplyWithReport(reversed, options, progress);

        Assert.True(report.Result.Success, report.Result.Summary);
        Assert.Equal(0, report.SkippedDeleteCount);
        Assert.False(report.CopyPhaseFailed);

        var firstDelete = progress.Actions.FindIndex(static action => action.Contains("删除"));
        var lastCopy = progress.Actions.FindLastIndex(static action =>
            action.Contains("复制文件") || action.Contains("更新文件") || action.Contains("创建目录"));

        Assert.True(firstDelete >= 0, "必须至少执行过一次删除");
        Assert.True(lastCopy >= 0, "必须至少执行过一次复制/建目录");
        Assert.True(
            firstDelete > lastCopy,
            $"删除动作必须全部晚于复制动作；实际顺序： {string.Join(" -> ", progress.Actions)}");

        // 最终状态正确（说明重排没有把删除弄丢）
        Assert.True(File.Exists(TargetPathOf(ws, "data.txt")));
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("sub", "inner.txt"))));
        Assert.False(File.Exists(TargetPathOf(ws, "extra.txt")));
        Assert.False(Directory.Exists(TargetPathOf(ws, "extra-dir")));
    }

    /// <summary>
    /// 对抗点 4 的补充：复制阶段完成之后、删除阶段之前取消 —— 删除不得执行，
    /// 目标绝不能比同步前更少（取消也必须走「只增不减」的路径）。
    /// </summary>
    [Fact]
    public void Apply_CancelledBeforeDeletePhase_ThrowsAndNeverDeletes()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "data.txt", "DATA");
        CreateTargetFile(ws, "extra.txt", "EXTRA");

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);
        Assert.Equal(1, plan.DeleteFileCount);
        Assert.Equal(1, plan.CopyCount);

        using var cts = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cts);

        Exception? thrown = null;
        try
        {
            service.ApplyWithReport(plan, options, progress, cts.Token);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Equal(1, progress.Reports);
        Assert.NotNull(thrown);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);

        Assert.True(File.Exists(TargetPathOf(ws, "extra.txt")), "取消后不得执行删除（目标绝不能比同步前更少）");
        Assert.True(File.Exists(TargetPathOf(ws, "data.txt")), "取消前已完成的复制必须保留");
    }

    /// <summary>
    /// 对抗点 4 的数据完整性（复验）：复制【中途】失败时，目标里原有的完整旧版本必须逐字节保留，
    /// 且不留 .tmp 残骸。
    /// <para>第一轮复核时本用例是红的：<c>File.Copy(src, dst, overwrite: true)</c> 会先把目标截断，
    /// 失败后目标变成「长度等于源文件、内容全 0」的坏文件（实测 524288 → 1048576，全 0）。
    /// 现在 <c>Apply</c> 改走 <c>CopyAtomically</c>（写 <c>{目标}.tmp</c> 成功后原子替换），
    /// 本用例应转为通过。</para>
    /// <para>制造真实的【中途】失败：源文件中段加 64 KiB 字节范围锁 —— 不是打开阶段就失败，
    /// 而是已经开始复制之后才失败（比 FileShare.None 更严格）。</para>
    /// </summary>
    [Fact]
    public void Apply_WhenCopyFailsMidway_ExistingTargetVersionIsByteIdenticalAndNoTemp()
    {
        using var ws = new TempWorkspace();
        const int oldLength = 512 * 1024;
        const int lockOffset = 256 * 1024;
        const int lockLength = 64 * 1024;

        var source = CreateSourceFile(ws, "data.bin", new string('A', 1024 * 1024));
        var target = CreateTargetFile(ws, "data.bin", new string('B', oldLength));
        var expectedOldContent = new string('B', oldLength);

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.CopyCount);
        Assert.Equal(SyncActionKind.UpdateFile, plan.Items[0].Kind);

        var copyFailed = false;
        SyncResultItem? copyItem = null;
        using (var locker = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            locker.Lock(lockOffset, lockLength);
            try
            {
                var report = service.ApplyWithReport(plan, options);
                copyFailed = report.CopyPhaseFailed;
                copyItem = report.Result.Items.Single(static item => item.Kind == SyncActionKind.UpdateFile);
            }
            finally
            {
                locker.Unlock(lockOffset, lockLength);
            }
        }

        var targetLengthAfter = File.Exists(target) ? new FileInfo(target).Length : -1;
        var contentAfter = File.Exists(target) ? File.ReadAllText(target) : string.Empty;
        var residue = FindTempFileResidue(ws.TargetDir);

        RecordMetric(
            $"[FileMaster] Apply_CopyFailsMidway copyFailed={copyFailed} itemError={(copyItem?.Error ?? "<null>").Replace(Environment.NewLine, " ")} " +
            $"targetLengthAfter={targetLengthAfter} oldLength={oldLength} contentIdentical={contentAfter == expectedOldContent} " +
            $"tempResidue={residue.Length}");

        Assert.True(copyFailed, "用例前提未生效：字节范围锁没有让复制失败");
        Assert.NotNull(copyItem);
        Assert.False(copyItem!.Success, "中途失败的更新项必须记为失败");

        // 1) 旧版本必须逐字节保持不动（长度 + 内容）
        Assert.True(
            targetLengthAfter == oldLength,
            $"复制中途失败后目标长度必须保持 {oldLength}；实际={targetLengthAfter}（-1=已被删除，其他=被截断/写成空洞文件）");
        Assert.True(
            contentAfter == expectedOldContent,
            $"复制中途失败后目标内容必须逐字节等于旧版本；实际前 16 字节（HEX）=" +
            string.Join(" ", System.Text.Encoding.ASCII.GetBytes(contentAfter).Take(16).Select(static b => b.ToString("X2"))));

        // 2) 不留任何 .tmp 残骸
        AssertNoTempFileResidue(ws.TargetDir);
    }

    // ==================================================================
    // 对抗点 5：目标侧 .git 等被忽略内容不得被整目录删除
    // ==================================================================

    /// <summary>
    /// 对抗点 5：镜像模式下，目标侧的 <c>.git</c>（含多层嵌套文件）必须原样保留，
    /// 同时「未被忽略的多余内容」照常删除。
    /// <para>其中 <c>only-ignored\.git\HEAD</c> 是「该目录里只有被忽略内容」的形态：
    /// 整目录递归删除会把 <c>.git</c> 一起删掉，必须退化为逐项删除。</para>
    /// </summary>
    [Fact]
    public void Apply_Mirror_KeepsTargetGitDirectory_AndStillRemovesNonIgnoredExtras()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "KEEP");

        CreateTargetFile(ws, Path.Combine(".git", "config"), "[core]");
        CreateTargetFile(ws, Path.Combine(".git", "objects", "ab", "cdef"), "OBJ");
        CreateTargetFile(ws, Path.Combine("only-ignored", ".git", "HEAD"), "ref: refs/heads/main");
        CreateTargetFile(ws, "plain.txt", "PLAIN");
        CreateTargetFile(ws, Path.Combine("sub", "nested.txt"), "NESTED");

        var options = CreateOptions(ws, SyncMode.Mirror, excludeNames: ".git");
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Null(plan.Error);
        Assert.DoesNotContain(plan.Items, static item => item.RelativePath.Contains(".git", StringComparison.OrdinalIgnoreCase));

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);

        // 被忽略的内容一个都不能少
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine(".git", "config"))), "目标侧 .git 内容不得被删除");
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine(".git", "objects", "ab", "cdef"))), "目标侧 .git 深层内容不得被删除");
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("only-ignored", ".git", "HEAD"))), "只含忽略内容的目录也不得被整目录删除");
        Assert.True(Directory.Exists(TargetPathOf(ws, ".git")));
        Assert.True(Directory.Exists(TargetPathOf(ws, "only-ignored")));

        // 未被忽略的多余内容照常删除
        Assert.False(File.Exists(TargetPathOf(ws, "plain.txt")), "未被忽略的多余文件仍应删除");
        Assert.False(File.Exists(TargetPathOf(ws, Path.Combine("sub", "nested.txt"))), "未被忽略的多余文件仍应删除");
        Assert.False(Directory.Exists(TargetPathOf(ws, "sub")), "不含忽略内容的目录仍应整目录删除");

        // 源内容照常复制
        Assert.True(File.Exists(TargetPathOf(ws, "keep.txt")));
    }

    /// <summary>
    /// 对抗点 5（两层嵌套）：目标 <c>a\b\.git\config</c> 只有被忽略内容，
    /// 且 <c>a</c>、<c>a\b</c> 都在源中不存在 —— 任何层级都不得生成整目录递归删除；
    /// 同一目录里未被忽略的 <c>junk.txt</c> 仍必须被删除（退化为逐项删除）。
    /// </summary>
    [Fact]
    public void Apply_ExtraDirectoryNestedTwoLevelsWithOnlyIgnoredContent_IsNeverDeletedWhole()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "KEEP");
        CreateTargetFile(ws, Path.Combine("a", "b", ".git", "config"), "[core]");
        CreateTargetFile(ws, Path.Combine("a", "b", "junk.txt"), "JUNK");

        var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: ".git");
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(0, plan.DeleteDirectoryCount);
        Assert.Equal(1, plan.DeleteFileCount);

        var junk = plan.Items.Single(static item => item.Kind == SyncActionKind.DeleteFile);
        Assert.Equal(Path.Combine("a", "b", "junk.txt"), junk.RelativePath);
        Assert.Equal(FolderSyncService.DegradedDeleteReason, junk.Reason);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("a", "b", ".git", "config"))), "两层嵌套的忽略内容同样不得被删除");
        Assert.False(File.Exists(TargetPathOf(ws, Path.Combine("a", "b", "junk.txt"))), "未被忽略的文件仍应逐项删除");
        Assert.True(Directory.Exists(TargetPathOf(ws, Path.Combine("a", "b"))), "仍持有忽略内容的目录必须保留");
    }

    /// <summary>
    /// 对抗点 5 / P1-1 交叉（复验）：目标里多余的目录中含【目录联接】时：
    /// <list type="number">
    /// <item>安全底线：整目录递归删除绝不能穿透到联接指向的外部内容（那已不是「目标目录里的数据」）；</item>
    /// <item>修复验收：不再报删除失败（应只删链接本身），并把父目录一并清掉。</item>
    /// </list>
    /// <para>第一轮复核时的情况：<c>CollectDegradedSubtree</c> 对 ReparsePoint 只 <c>continue</c>，
    /// 父目录仍生成整目录 <c>DeleteDirectory</c> → <c>Directory.Delete(recursive: true)</c>
    /// 先删掉链接、再抛「对路径 link 的访问被拒绝」→ 该删除项报失败、父目录残留
    /// （链接目标数据未受损，但同步结果显示失败）。</para>
    /// </summary>
    [Fact]
    public void Apply_ExtraTargetDirectoryContainingJunction_NoFailureAndTargetDataIntact()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "KEEP");

        var extra = Directory.CreateDirectory(TargetPathOf(ws, "extra")).FullName;
        var outside = Directory.CreateDirectory(ws.PathOf("outside")).FullName;
        var canary = Path.Combine(outside, "canary-outside-the-target.txt");
        File.WriteAllText(canary, "MUST-SURVIVE");

        var link = Path.Combine(extra, "link");
        Assert.True(
            TryCreateJunction(link, outside),
            "无法创建目录联接：本用例会失去意义（环境限制，但必须显式暴露，不能静默跳过）");

        try
        {
            var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: ".git");
            var service = new FolderSyncService();
            var plan = service.BuildPlan(options);

            var plannedDeleteDirs = plan.Items
                .Where(static item => item.Kind == SyncActionKind.DeleteDirectory)
                .Select(static item => item.RelativePath)
                .ToArray();

            var result = service.Apply(plan, options);

            var copyItem = result.Items.Single(static item => item.Kind == SyncActionKind.CopyFile);
            Assert.True(copyItem.Success, copyItem.Error);

            var deleteItems = result.Items.Where(static item => item.Kind == SyncActionKind.DeleteDirectory).ToArray();
            var failedDeletes = deleteItems
                .Where(static item => !item.Success)
                .Select(static item => $"{item.RelativePath} => {item.Error}")
                .ToArray();

            RecordMetric(
                $"[FileMaster] JunctionInExtraDir plannedDeleteDirs=[{string.Join(",", plannedDeleteDirs)}] " +
                $"resultDeleteDirs=[{string.Join(",", deleteItems.Select(static item => item.RelativePath))}] " +
                $"failedDeletes={failedDeletes.Length} extraDirExistsAfter={Directory.Exists(TargetPathOf(ws, "extra"))} " +
                $"canaryOutsideExists={File.Exists(canary)}");

            // ① 安全底线（最高优先）：联接目标位于【目标目录之外】，一个字节都不能少
            Assert.True(
                File.Exists(canary),
                "目标中多余目录里的目录联接，其链接目标的内容绝不能被递归删除波及（这是目标目录之外的数据）。" +
                $"失败删除项：{string.Join(" | ", failedDeletes)}");

            // ② 修复验收：含联接的目录删除不得再报失败（只删链接本身，不递归穿透）
            Assert.True(
                failedDeletes.Length == 0,
                "含目录联接的多余目录删除不得报失败（应只删链接本身）；失败项：" + string.Join(" | ", failedDeletes));

            // ③ 修复验收：链接被单独删除后，父目录应当能被清掉
            Assert.False(
                Directory.Exists(TargetPathOf(ws, "extra")),
                "联接被删除后父目录 extra 应被一并清掉，而不是残留一个空目录");

            // ④ 联接本身必须已被删除
            Assert.False(Directory.Exists(link), "联接本身必须被删除");
        }
        finally
        {
            TryRemoveLink(link);
        }
    }

    /// <summary>
    /// 对抗点 5 回归护栏：忽略名单为空时行为必须与修复前完全一致
    /// （不引入子树复核，多余的目录仍然整目录删除、原因文本不变）。
    /// </summary>
    [Fact]
    public void BuildPlan_WithEmptyExcludeList_StillPlansWholeDirectoryDelete()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "KEEP");
        CreateTargetFile(ws, Path.Combine("extra", ".git", "config"), "[core]");

        var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: null);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        // 忽略名单为空 ⇒ .git 不是「被忽略内容」，按普通多余目录处理（既有语义，不做子树复核）
        Assert.Equal(2, plan.DeleteDirectoryCount);
        Assert.Contains(
            plan.Items,
            static item => item.Kind == SyncActionKind.DeleteDirectory
                           && item.RelativePath == "extra"
                           && item.Reason == "目标中多余的目录");

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.False(Directory.Exists(TargetPathOf(ws, "extra")));
    }

    // ==================================================================
    // 复验：ReparsePoint 分支本身是否正确（与「是否可达」分开判定）
    // ==================================================================

    /// <summary>
    /// 复验：手工构造一个「删除项直接指向目录联接」的计划，验证 <c>Apply</c> 新增的
    /// <c>Directory.Delete(recursive: false)</c> 分支本身是正确且安全的。
    /// <para>为什么要手工构造：<c>BuildPlan</c> 走的是 <c>EnumerateTree</c>，而它对 ReparsePoint
    /// 目录是 <c>continue</c>（不加入 <c>targetDirectories</c>），因此**当前没有任何路径**
    /// 能让联接本身成为计划项 —— 本用例把「分支坏了」和「分支不可达」两件事分开判定。</para>
    /// </summary>
    [Fact]
    public void Apply_DeleteDirectoryItemPointingAtJunction_DeletesOnlyTheLink()
    {
        using var ws = new TempWorkspace();
        var outside = Directory.CreateDirectory(ws.PathOf("outside")).FullName;
        var canary = Path.Combine(outside, "canary.txt");
        File.WriteAllText(canary, "MUST-SURVIVE");

        var holder = Directory.CreateDirectory(TargetPathOf(ws, "holder")).FullName;
        var link = Path.Combine(holder, "link");
        Assert.True(TryCreateJunction(link, outside), "无法创建目录联接：本用例会失去意义");

        try
        {
            var plan = new SyncPlan
            {
                Items = new[]
                {
                    new SyncPlanItem
                    {
                        Kind = SyncActionKind.DeleteDirectory,
                        RelativePath = Path.Combine("holder", "link"),
                        TargetPath = link,
                        Reason = "手工构造：验证 Apply 的 ReparsePoint 分支"
                    }
                }
            };

            var result = new FolderSyncService().Apply(plan, CreateOptions(ws));

            Assert.True(result.Success, result.Summary);
            Assert.False(Directory.Exists(link), "联接本身应被删除");
            Assert.True(File.Exists(canary), "链接目标数据必须完好");
        }
        finally
        {
            TryRemoveLink(link);
        }
    }

    // ==================================================================
    // 复验：新暂存名 {目标}.tmp 带来的新删除路径
    // ==================================================================

    /// <summary>
    /// 复验（新引入的风险）：<c>CopyAtomically</c> 会先无条件删除 <c>{目标}.tmp</c>。
    /// 如果目标侧本来就有一个同名文件（例如编辑器留下的 <c>report.docx.tmp</c>），
    /// 它会被当成「上一轮残骸」删掉 —— 即使本次复制随后失败、即使处于 CopyOnly 模式
    /// （CopyOnly 承诺「不动目标中的多余内容」）。
    /// </summary>
    [Fact]
    public void Apply_CopyOnly_WhenCopyFails_TargetSideFileNamedLikeStagingPathMustSurvive()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "data.txt", "NEW-CONTENT-LONGER");
        CreateTargetFile(ws, "data.txt", "OLD");
        var bystander = CreateTargetFile(ws, "data.txt.tmp", "USER-FILE-MUST-SURVIVE");

        var options = CreateOptions(ws, SyncMode.CopyOnly);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(0, plan.DeleteFileCount);
        Assert.Equal(1, plan.CopyCount);

        using (File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = service.ApplyWithReport(plan, options);
            Assert.True(report.CopyPhaseFailed, "用例前提未生效：复制应当失败");
        }

        RecordMetric(
            $"[FileMaster] CopyOnly_BystanderTempNamedFile bystanderExistsAfter={File.Exists(bystander)} " +
            $"targetIntact={File.ReadAllText(TargetPathOf(ws, "data.txt")) == "OLD"}");

        Assert.True(
            File.Exists(bystander),
            "CopyOnly 模式下复制失败时，目标里任何文件都不该消失；" +
            "但 CopyAtomically 会无条件删除 {目标}.tmp（把用户文件当成自己的残骸）");
    }

    /// <summary>
    /// 复验（新引入的风险）：把 <c>data.txt.tmp</c> 写进忽略名单后，计划里确实没有任何关于它的项
    /// （忽略名单生效），但 <c>CopyAtomically</c> 仍会在成功复制 <c>data.txt</c> 时把它删掉
    /// —— 等于忽略名单被暂存清理绕过。
    /// </summary>
    [Fact]
    public void Apply_WithIgnoredFileNamedLikeStagingPath_IgnoredFileMustSurvive()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "data.txt", "NEW-CONTENT-LONGER");
        CreateTargetFile(ws, "data.txt", "OLD");
        var bystander = CreateTargetFile(ws, "data.txt.tmp", "IGNORED-USER-FILE");

        var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: "data.txt.tmp");
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.DoesNotContain(
            plan.Items,
            static item => item.RelativePath.Contains(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, plan.CopyCount);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);

        RecordMetric(
            $"[FileMaster] IgnoredBystanderTempNamedFile bystanderExistsAfter={File.Exists(bystander)} targetUpdated=" +
            $"{File.ReadAllText(TargetPathOf(ws, "data.txt")) == "NEW-CONTENT-LONGER"}");

        Assert.True(
            File.Exists(bystander),
            "被忽略名单保护的目标文件不得被 CopyAtomically 的暂存清理删除（忽略名单不能被绕过）");
    }

    // ==================================================================
    // 复验：CopyAtomically 的边界场景（目标只读 / 目标被独占 / .tmp 位置不可写）
    // ==================================================================

    /// <summary>
    /// 复验：旧版暂存名 <c>{目标}.tmp</c> 被【目录】占位时的行为。
    /// <para>说明：新实现改用随机暂存名 <c>{目标名}.{随机}.wxtmp</c>，
    /// 因此「用目录占住 <c>{目标}.tmp</c>」不再能让复制失败（该路径已与本工具无关）。
    /// 这条用例原本依赖的碰撞前提已随缺陷一起消失，故改为验证「用户目录不受影响、复制照常成功」。</para>
    /// </summary>
    [Fact]
    public void CopyAtomically_WhenLegacyTempNameIsOccupiedByDirectory_StillSucceeds()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "src.txt", "NEW-CONTENT");
        var target = CreateTargetFile(ws, "dst.txt", "OLD-CONTENT");

        // 旧版暂存名被一个用户目录占住：新实现不再使用该路径
        Directory.CreateDirectory(target + ".tmp");

        FolderSyncService.CopyAtomically(source, target, null);

        Assert.Equal("NEW-CONTENT", File.ReadAllText(target));
        Assert.True(Directory.Exists(target + ".tmp"), "用户目录不能被当作暂存位置清掉");
        AssertNoTempFileResidue(ws.TargetDir);

        RecordMetric("[FileMaster] CopyAtomically_LegacyTempNameOccupiedByDirectory succeeded=True residue=0");
    }

    /// <summary>
    /// 复验：目标文件被其它进程独占（<c>FileShare.None</c>）时，
    /// <c>File.Move(temp, target, overwrite: true)</c> 必然失败 —— 旧版本必须完好、不留 .tmp。
    /// </summary>
    [Fact]
    public void CopyAtomically_WhenTargetIsLocked_MoveFailsAndKeepsOldTarget()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "src.txt", "NEW-CONTENT");
        var target = CreateTargetFile(ws, "dst.txt", "OLD-CONTENT");

        Exception? thrown = null;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                FolderSyncService.CopyAtomically(source, target, null);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        }

        Assert.NotNull(thrown);
        Assert.Equal("OLD-CONTENT", File.ReadAllText(target));
        AssertNoTempFileResidue(ws.TargetDir);

        RecordMetric($"[FileMaster] CopyAtomically_TargetLocked threw={thrown!.GetType().Name} oldTargetIntact=True residue=0");
    }

    /// <summary>
    /// 复验：目标文件是【只读】时，<c>CopyAtomically</c> 要么成功替换（内容=新版本），
    /// 要么失败（内容=旧版本），**绝不能**出现半截/空洞内容，也不能留下 .tmp 残骸。
    /// <para>只读目标在 Windows 上通常会让 <c>File.Move(overwrite: true)</c> 报访问被拒绝，
    /// 因此本用例只断言「非旧即新、绝不损坏」这一数据完整性不变量，并记录实际走的是哪条路径。</para>
    /// </summary>
    [Fact]
    public void CopyAtomically_WhenTargetIsReadOnly_NeverCorruptsOldVersion()
    {
        using var ws = new TempWorkspace();
        const string oldContent = "OLD-READONLY-CONTENT";
        const string newContent = "NEW-CONTENT";

        var source = CreateSourceFile(ws, "src.txt", newContent);
        var target = CreateTargetFile(ws, "dst.txt", oldContent);

        File.SetAttributes(target, FileAttributes.ReadOnly);

        var threw = false;
        try
        {
            try
            {
                FolderSyncService.CopyAtomically(source, target, null);
            }
            catch (Exception)
            {
                threw = true;
            }

            var content = File.ReadAllText(target);
            Assert.True(
                content == oldContent || content == newContent,
                $"只读目标场景下内容必须是完整的旧版本或完整的新版本，实际长度={content.Length}，" +
                $"前 16 字节（HEX）=" + string.Join(" ", System.Text.Encoding.ASCII.GetBytes(content).Take(16).Select(static b => b.ToString("X2"))));
            AssertNoTempFileResidue(ws.TargetDir);

            RecordMetric(
                $"[FileMaster] CopyAtomically_TargetReadOnly threw={threw} contentIsNew={content == newContent} " +
                $"oldTargetIntact={content == oldContent} residue=0");
        }
        finally
        {
            // 必须清掉只读属性，否则临时工作区无法清理
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// 复验：成功路径仍然正确 —— 内容替换为新版本、源文件保持不动、无暂存残骸；
    /// 并且上一轮中断留下的【陈旧】暂存文件会被清理，同时**不会**动到用户的同名文件。
    /// </summary>
    [Fact]
    public void CopyAtomically_SuccessAndStaleTemp_ReplacesTargetAndLeavesNoTemp()
    {
        using var ws = new TempWorkspace();
        var source = CreateSourceFile(ws, "src.txt", "FRESH-CONTENT");
        var target = CreateTargetFile(ws, "dst.txt", "OLD-CONTENT");

        // 模拟上一轮被强杀留下的【陈旧】残骸（新命名：{目标名}.{随机}.wxtmp）
        var stale = target + ".0123456789abcdef0123456789abcdef" + FolderSyncService.TempExtension;
        File.WriteAllText(stale, "STALE-GARBAGE");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));

        // 用户自己的文件，名字恰好是旧版暂存名：绝不能被清理逻辑误删
        var bystander = target + ".tmp";
        File.WriteAllText(bystander, "USER-DATA");

        FolderSyncService.CopyAtomically(source, target, null);

        Assert.Equal("FRESH-CONTENT", File.ReadAllText(target));
        Assert.Equal("FRESH-CONTENT", File.ReadAllText(source));
        Assert.False(File.Exists(stale), "陈旧暂存文件应被清理");
        Assert.True(File.Exists(bystander), "用户文件不能被暂存清理删掉");
        Assert.Equal("USER-DATA", File.ReadAllText(bystander));
        AssertNoTempFileResidue(ws.TargetDir);
    }

    // ==================================================================
    // 复验（第三轮）：摘除联接不得穿透到目标目录之外的联接；
    //                  陈旧暂存清理不得误删用户文件 / 不得删别人正在写的暂存文件
    // ==================================================================

    /// <summary>
    /// 复验（安全底线，最重要的一条）：目标多余目录里的联接被摘除时，绝不能顺藤摸瓜
    /// 走进链接目标，把【目标目录之外】的目录联接也删掉。
    /// <para>构造：<c>target\extra\link → outside</c>，而 <c>outside</c> 里还有一个
    /// <c>nested → outside2</c> 的联接。如果摘除逻辑用
    /// <c>Directory.EnumerateDirectories(..., AllDirectories)</c> 之类的递归枚举（会跟随联接），
    /// 就会把 <c>outside\nested</c> 也删掉 —— 那是目标目录之外的数据。</para>
    /// </summary>
    [Fact]
    public void Apply_JunctionRemoval_MustNotReachNestedJunctionsOutsideTheTarget()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "KEEP");

        var extra = Directory.CreateDirectory(TargetPathOf(ws, "extra")).FullName;
        var outside = Directory.CreateDirectory(ws.PathOf("outside")).FullName;
        var outside2 = Directory.CreateDirectory(ws.PathOf("outside2")).FullName;
        var canary = Path.Combine(outside, "canary.txt");
        var canary2 = Path.Combine(outside2, "canary2.txt");
        File.WriteAllText(canary, "OUTSIDE-DATA");
        File.WriteAllText(canary2, "OUTSIDE2-DATA");

        var link = Path.Combine(extra, "link");
        var nested = Path.Combine(outside, "nested");
        Assert.True(TryCreateJunction(link, outside), "无法创建目录联接：本用例会失去意义");
        Assert.True(TryCreateJunction(nested, outside2), "无法创建第二层目录联接：本用例会失去意义");

        try
        {
            var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: ".git");
            var service = new FolderSyncService();
            var result = service.Apply(service.BuildPlan(options), options);

            RecordMetric(
                $"[FileMaster] JunctionRemoval_NestedOutside success={result.Success} " +
                $"extraRemoved={!Directory.Exists(extra)} linkRemoved={!Directory.Exists(link)} " +
                $"outsideCanary={File.Exists(canary)} outsideNestedJunctionSurvives={Directory.Exists(nested)} " +
                $"outside2Canary={File.Exists(canary2)}");

            Assert.True(result.Success, result.Summary);
            Assert.False(Directory.Exists(extra), "联接被摘除后父目录应被清掉");
            Assert.False(Directory.Exists(link), "目标里的联接本身应被删除");

            // 以下三条是安全底线：目标目录之外的数据与联接必须一个都不能少
            Assert.True(File.Exists(canary), "联接目标里的文件绝不能被删");
            Assert.True(Directory.Exists(nested), "目标目录【之外】的第二层联接绝不能被摘除逻辑波及");
            Assert.True(File.Exists(canary2), "第二层联接的目标数据必须完好");
        }
        finally
        {
            TryRemoveLink(link);
            TryRemoveLink(nested);
        }
    }

    /// <summary>
    /// 复验：暂存清理必须放过【刚刚创建】的暂存文件（那是另一次并发复制正在写的），
    /// 即使它就落在同一个目标目录里；同时旧版暂存名 <c>{目标}.tmp</c> 的用户文件也不能被碰。
    /// </summary>
    [Fact]
    public void Apply_Sweep_MustNotDeleteFreshStagingFileOrLegacyUserFile()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "data.txt", "NEW-CONTENT");

        Directory.CreateDirectory(ws.TargetDir);
        var inFlight = Path.Combine(ws.TargetDir, "other.txt.0123456789abcdef0123456789abcdef" + FolderSyncService.TempExtension);
        File.WriteAllText(inFlight, "IN-FLIGHT-STAGING-OF-ANOTHER-COPY");

        var legacyUserFile = Path.Combine(ws.TargetDir, "data.txt.tmp");
        File.WriteAllText(legacyUserFile, "LEGACY-USER-FILE");
        File.SetLastWriteTimeUtc(legacyUserFile, DateTime.UtcNow.AddHours(-5));

        var options = CreateOptions(ws, SyncMode.CopyOnly);
        var service = new FolderSyncService();
        var result = service.Apply(service.BuildPlan(options), options);

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(inFlight), "刚创建的暂存文件（另一路并发的复制正在写）不得被清理");
        Assert.True(File.Exists(legacyUserFile), "旧版暂存名的用户文件不得被清理");
        Assert.Equal("LEGACY-USER-FILE", File.ReadAllText(legacyUserFile));
        Assert.Equal("NEW-CONTENT", File.ReadAllText(TargetPathOf(ws, "data.txt")));

        RecordMetric(
            $"[FileMaster] Sweep_FreshAndLegacy inFlightSurvives={File.Exists(inFlight)} " +
            $"legacyUserFileSurvives={File.Exists(legacyUserFile)}");
    }

    /// <summary>
    /// 复验：暂存清理不得删掉【正被别的进程打开】的暂存文件（模拟并发复制正在写盘）：
    /// 文件很老（满足年龄条件），但句柄没有 FILE_SHARE_DELETE → <c>File.Delete</c> 必然失败，
    /// 清理必须安全跳过、且不影响本次同步。
    /// </summary>
    [Fact]
    public void Apply_Sweep_MustNotDeleteStagingFileHeldOpenByAnotherWriter()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "data.txt", "NEW-CONTENT");
        Directory.CreateDirectory(ws.TargetDir);

        var held = Path.Combine(ws.TargetDir, "busy.txt.0123456789abcdef0123456789abcdef" + FolderSyncService.TempExtension);
        File.WriteAllText(held, "BEING-WRITTEN");
        File.SetLastWriteTimeUtc(held, DateTime.UtcNow.AddHours(-2));

        var options = CreateOptions(ws, SyncMode.CopyOnly);
        var service = new FolderSyncService();

        SyncResult result;
        using (new FileStream(held, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            result = service.Apply(service.BuildPlan(options), options);
        }

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(held), "正被其它写入者打开的暂存文件不得被清理（删除会因共享冲突失败，必须安全跳过）");
        Assert.Equal("NEW-CONTENT", File.ReadAllText(TargetPathOf(ws, "data.txt")));

        RecordMetric($"[FileMaster] Sweep_HeldOpen heldSurvives={File.Exists(held)}");
    }

    /// <summary>
    /// 复验：按目录节流的清理（1 小时/目录）必须对**每个**目标目录各自生效，
    /// 只删「本工具扩展名 + 超过 1 小时」的文件：两个目录里的陈旧暂存文件都要被清掉，
    /// 新文件与用户文件都要保留。
    /// </summary>
    [Fact]
    public void Apply_Sweep_WorksPerDirectoryAndOnlyForToolExtension()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, Path.Combine("a", "x.txt"), "X");
        CreateSourceFile(ws, Path.Combine("b", "y.txt"), "Y");

        var staleA = Path.Combine(ws.CreateDirectory(Path.Combine("target", "a")), "old-a.0123456789abcdef0123456789abcdef" + FolderSyncService.TempExtension);
        var staleB = Path.Combine(ws.CreateDirectory(Path.Combine("target", "b")), "old-b.0123456789abcdef0123456789abcdef" + FolderSyncService.TempExtension);
        foreach (var stale in new[] { staleA, staleB })
        {
            File.WriteAllText(stale, "STALE");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        }

        var freshA = Path.Combine(ws.TargetDir, "a", "fresh.0123456789abcdef0123456789abcdef" + FolderSyncService.TempExtension);
        File.WriteAllText(freshA, "FRESH");

        var userFileB = Path.Combine(ws.TargetDir, "b", "user-data.tmp");
        File.WriteAllText(userFileB, "USER-DATA");
        File.SetLastWriteTimeUtc(userFileB, DateTime.UtcNow.AddHours(-3));

        var options = CreateOptions(ws, SyncMode.CopyOnly);
        var service = new FolderSyncService();
        var result = service.Apply(service.BuildPlan(options), options);

        Assert.True(result.Success, result.Summary);
        Assert.False(File.Exists(staleA), "目录 a 里的陈旧暂存文件应被清理");
        Assert.False(File.Exists(staleB), "目录 b 里的陈旧暂存文件也应被清理（节流是按目录的）");
        Assert.True(File.Exists(freshA), "刚创建的暂存文件不得被清理");
        Assert.True(File.Exists(userFileB), "非本工具扩展名的用户文件不得被清理");
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("a", "x.txt"))));
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("b", "y.txt"))));

        RecordMetric(
            $"[FileMaster] Sweep_PerDirectory staleARemoved={!File.Exists(staleA)} staleBRemoved={!File.Exists(staleB)} " +
            $"freshSurvives={File.Exists(freshA)} userFileSurvives={File.Exists(userFileB)}");
    }
}
