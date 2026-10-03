using System.Diagnostics;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.FileMaster.Tests;

/// <summary>
/// 阶段三-A 的专项测试。
/// <list type="bullet">
/// <item>P1-13：同步必须先复制、后删除；复制阶段一旦失败就整体跳过删除（<see cref="FolderSyncService"/>）。</item>
/// <item>P1-14：删除「目标中多余的目录」不得绕过忽略名单（整目录递归删除会连带删掉 <c>.git</c> 之类的忽略内容）。</item>
/// <item>P1-1：目录联接 / 符号链接（重解析点）不递归；<c>IsSubPathOf</c> 的尾分隔符与包含判定。</item>
/// <item>P1-3：存在不可枚举目录时其余文件仍被复制（<see cref="FileCopier"/>）。</item>
/// </list>
/// <para>所有用例都在各自的临时目录中执行，删除一律 <c>UseRecycleBin = false</c>，不产生回收站残留。
/// 每条「修复前会失败」的用例都在注释中写明了旧行为。</para>
/// </summary>
public sealed class FolderSyncStage3Tests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

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
    private static string CreateSourceFile(TempWorkspace ws, string relativePath, string content)
        => CreateFile(ws.SourceDir, relativePath, content);

    /// <summary>在目标目录下创建文件。</summary>
    private static string CreateTargetFile(TempWorkspace ws, string relativePath, string content)
        => CreateFile(ws.TargetDir, relativePath, content, DateTime.UtcNow.AddHours(-2));

    /// <summary>构造同步选项：源 / 目标固定为临时工作区中的 source / target，删除一律永久删除（不碰回收站）。</summary>
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

    /// <summary>同步进度记录器（<see cref="IProgress{T}"/> 的实现是同步调用，不经过 UI 消息循环）。</summary>
    private sealed class RecordingProgress : IProgress<SyncProgress>
    {
        /// <summary>按上报顺序记录的动作说明。</summary>
        public List<string> Actions { get; } = new();

        /// <summary>按上报顺序记录的完成百分比。</summary>
        public List<double> Percentages { get; } = new();

        public void Report(SyncProgress value)
        {
            Actions.Add(value.CurrentAction);
            Percentages.Add(value.Percent);
        }
    }

    /// <summary>
    /// 尽力创建目录联接 / 符号链接（P1-1 用例需要真实的重解析点）。
    /// <para>先试 <see cref="Directory.CreateSymbolicLink"/>（需要开发者模式或管理员权限），
    /// 失败则退回 <c>cmd /c mklink /J</c>（普通用户即可创建 junction）。
    /// 环境确实不支持时返回 false，用例提前返回并在注释中说明 —— 这是环境限制，不是产品缺陷。</para>
    /// </summary>
    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            if (IsReparsePointDirectory(linkPath))
            {
                return true;
            }
        }
        catch
        {
            // 忽略：继续尝试 junction
        }

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

        return IsReparsePointDirectory(linkPath);
    }

    /// <summary>路径是否存在、且是重解析点目录（目录联接 / 符号链接）。</summary>
    private static bool IsReparsePointDirectory(string path)
    {
        try
        {
            return Directory.Exists(path)
                   && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 原子复制：覆盖失败绝不能毁掉目标里已有的旧版本
    // ------------------------------------------------------------------

    /// <summary>
    /// 复制中途失败时，目标里原有的旧版本必须逐字节保持不动，且不留任何 .tmp 残骸。
    /// <para>修复前用的是 <c>File.Copy(src, dst, overwrite: true)</c>：它先截断目标再写内容，
    /// 因此中途失败会把旧版本变成一个「长度等于源文件、内容全 0」的坏文件。</para>
    /// <para>用 <c>FileShare.None</c> 独占锁住源文件是最稳定的失败构造方式（不依赖磁盘异常）。</para>
    /// </summary>
    [Fact]
    public void CopyAtomically_WhenSourceIsLocked_KeepsExistingTargetIntactAndLeavesNoTemp()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "src.txt", "SOURCE-CONTENT");
        var targetFile = CreateTargetFile(ws, "dst.txt", "ORIGINAL-TARGET");

        using (File.Open(sourceFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => FolderSyncService.CopyAtomically(sourceFile, targetFile, null));
        }

        // 旧版本逐字节不变
        Assert.Equal("ORIGINAL-TARGET", File.ReadAllText(targetFile));

        // 不留临时残骸
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(targetFile)!, "*" + FolderSyncService.TempExtension));
    }

    /// <summary>成功路径：内容被替换成源内容，且不留 .tmp 残骸。</summary>
    [Fact]
    public void CopyAtomically_WhenSuccessful_ReplacesTargetAndLeavesNoTemp()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "src.txt", "NEW-CONTENT");
        var targetFile = CreateTargetFile(ws, "dst.txt", "OLD-CONTENT");

        FolderSyncService.CopyAtomically(sourceFile, targetFile, null);

        Assert.Equal("NEW-CONTENT", File.ReadAllText(targetFile));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(targetFile)!, "*" + FolderSyncService.TempExtension));
        Assert.Equal("NEW-CONTENT", File.ReadAllText(sourceFile));
    }

    /// <summary>
    /// 上一轮中断留下的陈旧暂存文件（本工具专属扩展名）不会让本次复制失败，且会被清理。
    /// <para>暂存名现在是 <c>{目标名}.{随机}.wxtmp</c>，因此不存在「与用户文件同名」的碰撞问题；
    /// 这条用例改成验证「陈旧残骸可被清理、且不影响本次复制」。</para>
    /// </summary>
    [Fact]
    public void CopyAtomically_WhenStaleTempExists_StillSucceedsAndSweepsIt()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "src.txt", "FRESH");
        var targetFile = CreateTargetFile(ws, "dst.txt", "OLD");

        // 造一个「足够老」的陈旧暂存文件（清理阈值是 1 小时）
        var stale = targetFile + ".0123456789abcdef0123456789abcdef" + FolderSyncService.TempExtension;
        File.WriteAllText(stale, "STALE-GARBAGE");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));

        FolderSyncService.CopyAtomically(sourceFile, targetFile, null);

        Assert.Equal("FRESH", File.ReadAllText(targetFile));

        // 陈旧残骸被清掉，且不残留本次的暂存文件
        Assert.False(File.Exists(stale), "超过 1 小时的陈旧暂存文件应被清理");
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(targetFile)!,
            "*" + FolderSyncService.TempExtension));
    }

    /// <summary>
    /// 目标里恰好有一个叫 <c>{目标名}.wxtmp</c> 的【用户文件】时，绝不能把它当残骸删掉。
    /// <para>这是对「无条件 File.Delete({目标}.tmp)」那次缺陷的回归保护。</para>
    /// </summary>
    [Fact]
    public void CopyAtomically_DoesNotDeleteUserFileThatLooksLikeLegacyStagingName()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "src.txt", "NEW");
        var targetFile = CreateTargetFile(ws, "dst.txt", "OLD");

        // 用户自己的文件，名字恰好是旧版暂存名
        var bystander = targetFile + ".tmp";
        File.WriteAllText(bystander, "USER-DATA");

        FolderSyncService.CopyAtomically(sourceFile, targetFile, null);

        Assert.Equal("NEW", File.ReadAllText(targetFile));
        Assert.True(File.Exists(bystander), "用户文件绝不能被暂存清理删掉");
        Assert.Equal("USER-DATA", File.ReadAllText(bystander));
    }

    /// <summary>
    /// 端到端：更新已有目标文件时源被独占锁 → 该文件必须记为失败，且目标旧内容完好。
    /// </summary>
    [Fact]
    public void Apply_WhenUpdateFailsMidway_KeepsExistingTargetVersion()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "data.txt", "NEW-VERSION-CONTENT");
        var targetFile = CreateTargetFile(ws, "data.txt", "OLD-VERSION");

        var options = CreateOptions(ws, SyncMode.OneWaySync);

        // 先建立一次成功同步的目标状态：直接改目标内容，使计划判定为「需要更新」
        Assert.Equal(1, new FolderSyncService().BuildPlan(options).CopyCount);

        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        using (File.Open(sourceFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = service.ApplyWithReport(plan, options);

            Assert.Equal(1, report.Result.FailedCount);
        }

        // 关键：旧版本不能被毁
        Assert.Equal("OLD-VERSION", File.ReadAllText(targetFile));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(targetFile)!, "*" + FolderSyncService.TempExtension));
    }

    // ------------------------------------------------------------------
    // 范围 A：P1-13 先复制、后删除
    // ------------------------------------------------------------------

    /// <summary>
    /// P1-13：复制阶段失败时整体跳过删除，目标中多余的文件必须保留。
    /// <para>修复前 <c>Apply</c> 按计划顺序执行（删除排在复制之前）：先真的删掉 extra.txt、
    /// 然后复制失败，目标内容比同步前更少 —— 本用例断言 extra.txt 仍然存在，修复前必然失败。</para>
    /// </summary>
    [Fact]
    public void Apply_WhenCopyFails_SkipsDeleteAndKeepsExtraTargetFile()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "data.txt", "important-data");
        CreateTargetFile(ws, "extra.txt", "target-only-content");

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        // 计划展示顺序仍是「删除在前、复制在后」——这正是旧实现的数据丢失窗口
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(SyncActionKind.DeleteFile, plan.Items[0].Kind);
        Assert.Equal(SyncActionKind.CopyFile, plan.Items[1].Kind);

        // 计划生成后源文件消失：复制必然失败
        File.Delete(sourceFile);

        var report = service.ApplyWithReport(plan, options);
        var result = report.Result;

        Assert.True(
            File.Exists(TargetPathOf(ws, "extra.txt")),
            "复制失败时不得删除目标中多余的文件（否则目标比同步前更少）");
        Assert.False(File.Exists(TargetPathOf(ws, "data.txt")));

        // 跳过数量必须暴露到结果里
        Assert.Equal(1, report.SkippedDeleteCount);
        Assert.True(report.DeletesSkipped);
        Assert.True(report.CopyPhaseFailed);
        Assert.False(result.Success);

        // 「保护性跳过」不是失败：失败数只包含真正复制失败的那 1 项，
        // 跳过的删除项单独计入 SkippedCount（否则界面会把保护动作显示成错误）。
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.SkippedCount);

        Assert.NotNull(report.Note);
        Assert.Contains("跳过删除", report.Note);
        Assert.Contains("因复制失败已跳过删除", report.Summary);
        Assert.Contains("跳过 1 项", result.Summary);

        var skippedDelete = result.Items.Single(static item => item.Kind == SyncActionKind.DeleteFile);
        Assert.True(skippedDelete.Skipped, "被保护性跳过的删除项必须标记为 Skipped");
        Assert.False(skippedDelete.Success);
        Assert.Contains("因复制失败已跳过删除", skippedDelete.Error);

        // 结果条目顺序必须与计划一致，便于 UI 对照
        Assert.Equal(
            plan.Items.Select(static item => item.RelativePath),
            result.Items.Select(static item => item.RelativePath));
        Assert.Equal(
            plan.Items.Select(static item => item.Kind),
            result.Items.Select(static item => item.Kind));
    }

    /// <summary>
    /// P1-13：复制失败时连「删除多余目录」也一起跳过，避免整棵子树被删掉。
    /// <para>修复前 extra\ 目录（含 leftover.txt）会先被递归删除，然后复制失败，目标比同步前更少。</para>
    /// </summary>
    [Fact]
    public void Apply_WhenCopyFails_SkipsDeleteDirectoryToo()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "data.txt", "important-data");
        CreateTargetFile(ws, Path.Combine("extra", "leftover.txt"), "leftover");

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(3, plan.Items.Count);
        Assert.Equal(1, plan.DeleteFileCount);
        Assert.Equal(1, plan.DeleteDirectoryCount);

        File.Delete(sourceFile);

        var report = service.ApplyWithReport(plan, options);

        Assert.Equal(2, report.SkippedDeleteCount);
        Assert.True(Directory.Exists(TargetPathOf(ws, "extra")), "复制失败时不得删除目标中多余的目录");
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("extra", "leftover.txt"))));
        Assert.False(File.Exists(TargetPathOf(ws, "data.txt")));
    }

    /// <summary>
    /// P1-13：复制全部成功时删除照常执行，并且执行顺序确实是「先复制、后删除」。
    /// <para>用进度回调记录动作顺序：最后一个动作必须是删除，删除之前不得出现任何删除动作。</para>
    /// </summary>
    [Fact]
    public void Apply_WhenAllCopiesSucceed_ExecutesDeletesAfterCopies()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "data.txt", "data");
        CreateSourceFile(ws, Path.Combine("sub", "inner.txt"), "inner");
        CreateTargetFile(ws, "extra.txt", "extra");

        var options = CreateOptions(ws, SyncMode.OneWaySync);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(4, plan.Items.Count);
        Assert.Equal(1, plan.DeleteFileCount);

        var progress = new RecordingProgress();
        var report = service.ApplyWithReport(plan, options, progress);

        Assert.True(report.Result.Success, report.Result.Summary);
        Assert.Equal(0, report.SkippedDeleteCount);
        Assert.Equal(plan.Items.Count, progress.Actions.Count);
        Assert.Equal(100d, progress.Percentages[^1]);

        // 删除必须是最后一个动作；之前只能有创建目录 / 复制
        Assert.Contains("删除", progress.Actions[^1]);
        Assert.DoesNotContain(progress.Actions.Take(progress.Actions.Count - 1), static action => action.Contains("删除"));

        // 复制成功后删除照常生效
        Assert.False(File.Exists(TargetPathOf(ws, "extra.txt")));
        Assert.True(File.Exists(TargetPathOf(ws, "data.txt")));
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("sub", "inner.txt"))));
    }

    /// <summary>
    /// P1-13：CopyOnly 模式仍然不产生删除项，复制失败也不会删除任何东西（既有语义不变）。
    /// </summary>
    [Fact]
    public void Apply_InCopyOnlyMode_WhenCopyFails_DeletesNothing()
    {
        using var ws = new TempWorkspace();
        var sourceFile = CreateSourceFile(ws, "data.txt", "data");
        CreateTargetFile(ws, "extra.txt", "extra");

        var options = CreateOptions(ws, SyncMode.CopyOnly);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(0, plan.DeleteFileCount);
        Assert.Equal(0, plan.DeleteDirectoryCount);

        File.Delete(sourceFile);

        var report = service.ApplyWithReport(plan, options);

        Assert.Equal(0, report.SkippedDeleteCount);
        Assert.Equal(1, report.Result.FailedCount);
        Assert.True(File.Exists(TargetPathOf(ws, "extra.txt")));
    }

    /// <summary>P1-13：空计划执行成功、不产生条目、不报错。</summary>
    [Fact]
    public void Apply_WithEmptyPlan_ReturnsSuccessWithoutItems()
    {
        using var ws = new TempWorkspace();

        var report = new FolderSyncService().ApplyWithReport(new SyncPlan(), CreateOptions(ws));

        Assert.True(report.Result.Success);
        Assert.Empty(report.Result.Items);
        Assert.Equal(0, report.Result.CopiedBytes);
        Assert.Equal(0, report.SkippedDeleteCount);
        Assert.False(report.CopyPhaseFailed);
        Assert.Null(report.Note);
    }

    // ------------------------------------------------------------------
    // 范围 B：P1-14 删除多余目录不得绕过忽略名单
    // ------------------------------------------------------------------

    /// <summary>
    /// P1-14：目标中多余的目录里含被忽略的目录（.git）时，放弃整目录递归删除，退化为逐项删除，
    /// 并在计划项的 Reason 里写明原因。
    /// <para>修复前这里会生成 <c>DeleteDirectory extra</c>，执行时把 <c>extra\.git</c> 一起递归删掉。</para>
    /// </summary>
    [Fact]
    public void BuildPlan_WhenExtraTargetDirectoryContainsIgnoredEntries_SkipsWholeDirectoryDelete()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "keep");
        CreateTargetFile(ws, Path.Combine("extra", ".git", "config"), "[core]");
        CreateTargetFile(ws, Path.Combine("extra", "plain.txt"), "plain");
        CreateTargetFile(ws, Path.Combine(".git", "config"), "[core]");

        var plan = new FolderSyncService().BuildPlan(CreateOptions(ws, SyncMode.OneWaySync, excludeNames: ".git"));

        Assert.Null(plan.Error);

        // 不得对 extra（或任何含忽略内容的目录）生成整目录递归删除
        Assert.Equal(0, plan.DeleteDirectoryCount);
        Assert.DoesNotContain(plan.Items, static item => item.Kind == SyncActionKind.DeleteDirectory);
        Assert.DoesNotContain(plan.Items, static item => item.RelativePath.Contains(".git", StringComparison.OrdinalIgnoreCase));

        // 未被忽略的文件退化为逐项删除，并写明原因
        var plain = plan.Items.Single(static item => item.RelativePath == Path.Combine("extra", "plain.txt"));
        Assert.Equal(SyncActionKind.DeleteFile, plain.Kind);
        Assert.Equal(FolderSyncService.DegradedDeleteReason, plain.Reason);
    }

    /// <summary>
    /// P1-14：执行后忽略内容必须原样保留，未被忽略的内容照常删除，干净的子目录仍可整目录删除。
    /// <para>修复前 <c>target\extra</c> 整棵被删，<c>.git\config</c> 丢失 —— 本用例修复前必然失败。</para>
    /// </summary>
    [Fact]
    public void Apply_WhenExtraTargetDirectoryContainsIgnoredEntries_KeepsIgnoredContent()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "keep");
        CreateTargetFile(ws, Path.Combine("extra", ".git", "config"), "[core]");
        CreateTargetFile(ws, Path.Combine("extra", "plain.txt"), "plain");
        CreateTargetFile(ws, Path.Combine("extra", "sub", "nested.txt"), "nested");

        var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: ".git");
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.DeleteDirectoryCount);
        var subDelete = plan.Items.Single(static item => item.Kind == SyncActionKind.DeleteDirectory);
        Assert.Equal(Path.Combine("extra", "sub"), subDelete.RelativePath);
        Assert.Equal(FolderSyncService.DegradedDeleteReason, subDelete.Reason);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.True(
            File.Exists(TargetPathOf(ws, Path.Combine("extra", ".git", "config"))),
            "被忽略的目录内容不得被删除");
        Assert.False(File.Exists(TargetPathOf(ws, Path.Combine("extra", "plain.txt"))));
        Assert.False(Directory.Exists(TargetPathOf(ws, Path.Combine("extra", "sub"))), "不含忽略内容的子目录仍应整目录删除");
        Assert.True(Directory.Exists(TargetPathOf(ws, "extra")), "仍持有忽略内容的父目录必须保留");
    }

    /// <summary>
    /// P1-14：被忽略的是「文件」时同样退化为逐项删除，忽略文件本身不得被删除。
    /// </summary>
    [Fact]
    public void BuildPlan_WhenExtraTargetDirectoryContainsIgnoredFile_DegradesToItemWiseDelete()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "keep");
        CreateTargetFile(ws, Path.Combine("extra", "secret.dat"), "secret");
        CreateTargetFile(ws, Path.Combine("extra", "normal.txt"), "normal");

        var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: "secret.dat");
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(0, plan.DeleteDirectoryCount);
        Assert.DoesNotContain(plan.Items, static item => item.RelativePath.Contains("secret.dat", StringComparison.OrdinalIgnoreCase));

        var normal = plan.Items.Single(static item => item.RelativePath == Path.Combine("extra", "normal.txt"));
        Assert.Equal(FolderSyncService.DegradedDeleteReason, normal.Reason);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(TargetPathOf(ws, Path.Combine("extra", "secret.dat"))), "被忽略的文件不得被删除");
        Assert.False(File.Exists(TargetPathOf(ws, Path.Combine("extra", "normal.txt"))));
    }

    /// <summary>
    /// P1-14：忽略名单为空时行为与修复前完全一致 —— 多余的目录仍然整目录递归删除，
    /// 计划项数量与原因文本都不变（不引入语义 / 性能回归）。
    /// </summary>
    [Fact]
    public void BuildPlan_WhenExcludeNamesIsEmpty_PlansWholeDirectoryDeleteAsBefore()
    {
        using var ws = new TempWorkspace();
        CreateSourceFile(ws, "keep.txt", "keep");
        CreateTargetFile(ws, Path.Combine("extra", "leftover.txt"), "leftover");

        var options = CreateOptions(ws, SyncMode.OneWaySync, excludeNames: null);
        var service = new FolderSyncService();
        var plan = service.BuildPlan(options);

        Assert.Equal(1, plan.DeleteFileCount);
        Assert.Equal(1, plan.DeleteDirectoryCount);

        var fileDelete = plan.Items.Single(static item => item.Kind == SyncActionKind.DeleteFile);
        Assert.Equal("目标中多余的文件", fileDelete.Reason);

        var directoryDelete = plan.Items.Single(static item => item.Kind == SyncActionKind.DeleteDirectory);
        Assert.Equal("extra", directoryDelete.RelativePath);
        Assert.Equal("目标中多余的目录", directoryDelete.Reason);

        var result = service.Apply(plan, options);

        Assert.True(result.Success, result.Summary);
        Assert.False(Directory.Exists(TargetPathOf(ws, "extra")), "忽略名单为空时应保持整目录删除的既有行为");
    }

    // ------------------------------------------------------------------
    // 范围 C：P1-1 目录联接 / 子路径判定，P1-3 不可枚举目录
    // ------------------------------------------------------------------

    /// <summary>
    /// P1-1：源目录里的目录联接（重解析点）不得被递归进去 —— 既不同步链接目标的内容，也不创建对应目录。
    /// <para>修复前用的是 <c>Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)</c>，
    /// 它会跟随联接找到 <c>link\secret.txt</c> 并复制到目标，本用例修复前必然失败。</para>
    /// </summary>
    [Fact]
    public void BuildPlan_WhenSourceContainsDirectoryJunction_SkipsJunctionContent()
    {
        using var ws = new TempWorkspace();
        var source = Directory.CreateDirectory(ws.SourceDir).FullName;
        var outside = Directory.CreateDirectory(ws.PathOf("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(source, "normal.txt"), "normal");

        var link = Path.Combine(source, "link");
        if (!TryCreateDirectoryLink(link, outside))
        {
            // 本机无法创建目录联接 / 符号链接：xunit v2 没有动态 Skip，直接返回（环境限制，非产品缺陷）
            return;
        }

        try
        {
            var options = CreateOptions(ws, SyncMode.OneWaySync);
            var service = new FolderSyncService();
            var plan = service.BuildPlan(options);

            Assert.Null(plan.Error);
            Assert.Single(plan.Items);
            Assert.Equal("normal.txt", plan.Items[0].RelativePath);
            Assert.DoesNotContain(plan.Items, static item => item.RelativePath.Contains("link", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(plan.Items, static item => item.RelativePath.Contains("secret", StringComparison.OrdinalIgnoreCase));

            var result = service.Apply(plan, options);

            Assert.True(result.Success, result.Summary);
            Assert.True(File.Exists(TargetPathOf(ws, "normal.txt")));
            Assert.False(
                File.Exists(TargetPathOf(ws, Path.Combine("link", "secret.txt"))),
                "不得把目录联接目标的内容同步到目标目录");
        }
        finally
        {
            try
            {
                Directory.Delete(link);
            }
            catch
            {
                // 联接删除失败不影响断言结果
            }
        }
    }

    /// <summary>
    /// P1-1：同一个目录（一侧带尾分隔符）必须判定为「同一个目录」。
    /// <para>这是 <c>IsSubPathOf</c> 修复的回归护栏：去掉尾分隔符后两者相等，
    /// 若只让 <c>IsSubPathOf</c> 返回 false 而不同时归一化相等判定，这里会漏判。</para>
    /// </summary>
    [Fact]
    public void ValidateDirectories_WhenSameDirectoryWithTrailingSeparator_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var source = Directory.CreateDirectory(ws.SourceDir).FullName;
        var separator = Path.DirectorySeparatorChar;

        Assert.NotNull(FolderSyncService.ValidateDirectories(source + separator, source));
        Assert.NotNull(FolderSyncService.ValidateDirectories(source, source + separator));
    }

    /// <summary>
    /// P1-1：嵌套路径用正斜杠（Windows 也接受）时仍必须判定为「位于内部」。
    /// <para>修复前只去掉尾分隔符、不统一分隔符，<c>D:/a/source/inner</c> 不会被识别为
    /// <c>D:\a\source</c> 的子路径，校验会漏判 —— 本用例修复前失败。</para>
    /// </summary>
    [Fact]
    public void ValidateDirectories_WhenNestedPathUsesAltSeparator_ReturnsError()
    {
        using var ws = new TempWorkspace();
        var nested = Path
            .Combine(ws.SourceDir, "inner")
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.NotNull(FolderSyncService.ValidateDirectories(ws.SourceDir, nested));
    }

    /// <summary>P1-1：带尾分隔符的路径仍能正确判定「互相包含 / 互不包含」。</summary>
    [Fact]
    public void ValidateDirectories_WithTrailingSeparators_StillDetectsNesting()
    {
        using var ws = new TempWorkspace();
        var separator = Path.DirectorySeparatorChar;

        Assert.NotNull(FolderSyncService.ValidateDirectories(
            ws.SourceDir + separator,
            Path.Combine(ws.SourceDir, "inner") + separator));

        Assert.NotNull(FolderSyncService.ValidateDirectories(
            Path.Combine(ws.TargetDir, "inner") + separator,
            ws.TargetDir + separator));

        Assert.Null(FolderSyncService.ValidateDirectories(
            ws.SourceDir + separator,
            ws.TargetDir + separator));
    }

    /// <summary>
    /// P1-1：名字只共享前缀的兄弟目录（data 与 data-old）不能互相判定为子路径。
    /// <para>这是「补一个分隔符再比较」的边界护栏：只做字符串前缀比较会把 data-old 误判成 data 的子目录。</para>
    /// </summary>
    [Fact]
    public void ValidateDirectories_WhenSiblingNamesSharePrefix_ReturnsNull()
    {
        using var ws = new TempWorkspace();
        var data = Directory.CreateDirectory(ws.PathOf("data")).FullName;
        var dataOld = Directory.CreateDirectory(ws.PathOf("data-old")).FullName;

        Assert.Null(FolderSyncService.ValidateDirectories(data, dataOld));
    }

    /// <summary>
    /// P1-3：扫描过程中出现无法枚举的子目录时，异常必须被吞掉、其余文件仍被复制。
    /// <para>构造方式说明：本机无法稳定构造「ACL 拒绝列出」的目录（需要改 ACL / 提权），
    /// 因此这里用等价场景 —— 在遍历进行到一半时把子目录 <c>doomed</c> 换成同名文件，
    /// 使 <c>Directory.GetDirectories</c> 对该路径抛 <see cref="IOException"/>。
    /// 旧实现用惰性枚举，异常会推迟到 <c>foreach</c> 才抛出（已经不在 try 里），
    /// 一个坏目录会让整轮复制 0 文件失败。</para>
    /// </summary>
    [Fact]
    public void CopyDirectory_WhenSubDirectoryBecomesUnenumerableMidScan_CopiesRemainingFiles()
    {
        using var ws = new TempWorkspace();
        var source = Directory.CreateDirectory(ws.SourceDir).FullName;
        var target = ws.TargetDir;

        File.WriteAllText(Path.Combine(source, "aaa.txt"), "aaa");
        Directory.CreateDirectory(Path.Combine(source, "doomed"));
        File.WriteAllText(Path.Combine(source, "doomed", "inner.txt"), "inner");
        Directory.CreateDirectory(Path.Combine(source, "zzz"));
        File.WriteAllText(Path.Combine(source, "zzz", "zzz.txt"), "zzz");

        var sabotaged = false;
        var copier = new FileCopier
        {
            OnFileDecision = (_, _, _, _) =>
            {
                if (sabotaged)
                {
                    return;
                }

                sabotaged = true;

                // 把 doomed 目录换成同名文件：遍历到它时 Directory.GetDirectories 会抛 IOException，
                // 与「目录不可访问」走到同一个 catch 分支
                Directory.Delete(Path.Combine(source, "doomed"), recursive: true);
                File.WriteAllText(Path.Combine(source, "doomed"), "not-a-directory");
            }
        };

        var result = copier.CopyDirectory(source, target);

        Assert.True(sabotaged, "用例前提未生效：回调没有机会制造不可枚举场景");
        Assert.Equal(0, result.FailedFiles);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(target, "aaa.txt")), "坏目录不应阻止其余文件被复制");
        Assert.True(File.Exists(Path.Combine(target, "zzz", "zzz.txt")), "坏目录之后的文件也必须被复制");
        Assert.False(Directory.Exists(Path.Combine(target, "doomed")));
    }
}
