using System.Collections;
using System.Reflection;
using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// 阶段一 P0-1 / P0-2 修复的独立对抗性验证（verifier 角色，非实现者）。
/// <para>目的不是复述实现，而是构造「修复前会通过/失败、修复后仍可能漏掉」的用例：</para>
/// <list type="bullet">
/// <item>P0-1：大小相同但内容被改写（含 mtime 更旧）、大文件首尾相同中段不同、
/// 非严格模式的摘要措辞、StreamCopy 失败/取消时是否残留 .tmp 或截断目标。</item>
/// <item>P0-2：扫描后目录被塞入文件是否真的跳过、SafeDelete.DeleteEmptyDirectory 对非空目录的行为。</item>
/// </list>
/// <para>FileMaster 是独立工具程序集（未被本测试工程引用），这里通过反射加载它，
/// 以便在不改动任何产品代码 / 既有测试的前提下做真实调用。</para>
/// </summary>
public sealed class FileCopierP0VerificationTests
{
    /// <summary>固定时间基准（过去），避免“目标时间 &gt;= 源时间”造成判定歧义。</summary>
    private static readonly DateTime BaseTimeUtc = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>FileMaster.dll 中承载 P0-2 修复的类型全名。</summary>
    private const string EmptyFolderCleanerTypeName = "WinToolBox.Tools.FileMaster.Services.EmptyFolderCleanerService";

    /// <summary>FileMaster.dll 中只删空目录的内部辅助类型全名。</summary>
    private const string SafeDeleteTypeName = "WinToolBox.Tools.FileMaster.Services.SafeDelete";

    // ==================================================================
    // P0-1 默认值与配置契约
    // ==================================================================

    /// <summary>默认必须开启严格内容校验；BackupConfig 同样默认 true 且 Clone 保留显式 false。</summary>
    [Fact]
    public void P0_1_StrictContentVerification_IsEnabledByDefault_AndClonedFaithfully()
    {
        Assert.True(new FileCopier().StrictContentVerification, "FileCopier 默认应开启严格内容校验");
        Assert.True(new BackupConfig().StrictContentVerification, "BackupConfig 默认应开启严格内容校验");

        var config = new BackupConfig { StrictContentVerification = false };
        Assert.False(config.Clone().StrictContentVerification, "Clone 必须保留显式的 false");
    }

    // ==================================================================
    // P0-1 对抗点 a：同大小改写（含 mtime 被改早）
    // ==================================================================

    /// <summary>
    /// 对抗点 a：源文件与目标大小相同、内容不同、且源 mtime 比目标更旧（甚至同刻）。
    /// 修复前（只看 size + mtime）会被永久静默跳过；修复后必须重拷。
    /// </summary>
    [Fact]
    public void P0_1_SameSize_OlderSourceMtime_DifferentContent_IsCopied()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile("same.txt", "AAAA-OLD-C", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "same.txt");
        ws.CreateFile(targetFile, "BBBB-NEW-C", BaseTimeUtc.AddHours(2), ws.Root);

        // 前置条件：两侧大小完全相同，只有内容不同
        Assert.Equal(new FileInfo(sourceFile).Length, new FileInfo(targetFile).Length);
        Assert.True(File.GetLastWriteTimeUtc(sourceFile) < File.GetLastWriteTimeUtc(targetFile));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(0, result.UnchangedAssumed);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Equal("AAAA-OLD-C", TempWorkspace.ReadAllText(targetFile));
    }

    /// <summary>对抗点 a（同刻变体）：大小相同、mtime 完全相同、内容不同 → 必须重拷。</summary>
    [Fact]
    public void P0_1_SameSize_IdenticalMtime_DifferentContent_IsCopied()
    {
        using var ws = new TempWorkspace();
        var stamp = BaseTimeUtc.AddMinutes(30);
        ws.CreateFile("tie.txt", "11111111", stamp);
        var targetFile = Path.Combine(ws.TargetDir, "tie.txt");
        ws.CreateFile(targetFile, "22222222", stamp, ws.Root);

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal("11111111", TempWorkspace.ReadAllText(targetFile));
    }

    /// <summary>
    /// 反向保护：内容确实相同（且目标更新）时必须跳过，不能因为“改成内容校验”就无脑重拷，
    /// 并且要如实计入 VerifiedFiles（已 SHA256 校验）。
    /// </summary>
    [Fact]
    public void P0_1_IdenticalContent_TargetNewer_IsSkippedAndCountedAsVerified()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("same.txt", "CONTENT-IDENTICAL", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "same.txt");
        ws.CreateFile(targetFile, "CONTENT-IDENTICAL", BaseTimeUtc.AddHours(3), ws.Root);

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(1, result.VerifiedFiles);
        Assert.Equal(0, result.UnchangedAssumed);
        Assert.Contains("已 SHA256 校验", result.VerificationNote);
        Assert.Contains("已 SHA256 校验", result.Summary);
    }

    /// <summary>
    /// 阈值边界：正好 16 MiB（&lt;= 阈值）必须走 SHA256 全量比对，
    /// 因此「中段不同」的文件必须被重拷，不能被抽样策略放过。
    /// </summary>
    [Fact]
    public void P0_1_Exactly16MiB_MiddleDiffers_TargetNewer_IsCopied()
    {
        using var ws = new TempWorkspace();
        var size = FileCopier.LargeFileThresholdBytes;
        var sourceFile = Path.Combine(ws.SourceDir, "edge.bin");
        var targetFile = Path.Combine(ws.TargetDir, "edge.bin");

        WriteSampledFile(sourceFile, size, head: 0x11, tail: 0x22, seed: 101);
        WriteSampledFile(targetFile, size, head: 0x11, tail: 0x22, seed: 202);
        File.SetLastWriteTimeUtc(sourceFile, BaseTimeUtc);
        File.SetLastWriteTimeUtc(targetFile, BaseTimeUtc.AddHours(1));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(
            TempWorkspace.ComputeFileSha256(sourceFile),
            TempWorkspace.ComputeFileSha256(targetFile));
    }

    // ==================================================================
    // P0-1 对抗点 b：大文件首尾相同、中段不同
    // ==================================================================

    /// <summary>
    /// 对抗点 b：&gt;16 MiB 的文件，首尾各 64 KiB 完全相同、只有中段不同，且目标 mtime 更新。
    /// 抽样策略下必然被跳过——这属于文档化的折中；本用例锁死「不能被当作已校验」这一点：
    /// 必须如实计入 UnchangedAssumed 且摘要写明「未做内容校验」。
    /// </summary>
    [Fact]
    public void P0_1_LargeFile_SampledEqualButMiddleDiffers_IsCountedAsAssumedNotVerified()
    {
        using var ws = new TempWorkspace();
        var size = 17L * 1024 * 1024;
        var sourceFile = Path.Combine(ws.SourceDir, "big.bin");
        var targetFile = Path.Combine(ws.TargetDir, "big.bin");

        WriteSampledFile(sourceFile, size, head: 0xAA, tail: 0xBB, seed: 1);
        WriteSampledFile(targetFile, size, head: 0xAA, tail: 0xBB, seed: 2);
        File.SetLastWriteTimeUtc(sourceFile, BaseTimeUtc);
        File.SetLastWriteTimeUtc(targetFile, BaseTimeUtc.AddHours(1));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 抽样相同 → 跳过（折中），但绝不能声称做过内容校验
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(1, result.UnchangedAssumed);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Contains("未做内容校验", result.VerificationNote);
        Assert.Contains("未做内容校验", result.Summary);

        // 残余风险留痕：中段差异确实没有被同步（这是 >16 MiB 抽样策略的已知代价，不是崩溃）
        Assert.NotEqual(
            TempWorkspace.ComputeFileSha256(sourceFile),
            TempWorkspace.ComputeFileSha256(targetFile));
    }

    /// <summary>大文件首部不同（中段相同）时抽样能发现差异 → 必须重拷，且复制后内容完全一致。</summary>
    [Fact]
    public void P0_1_LargeFile_HeadDiffers_TargetNewer_IsCopiedAndMatchesByteForByte()
    {
        using var ws = new TempWorkspace();
        var size = 17L * 1024 * 1024;
        var sourceFile = Path.Combine(ws.SourceDir, "big.bin");
        var targetFile = Path.Combine(ws.TargetDir, "big.bin");

        WriteSampledFile(sourceFile, size, head: 0xAA, tail: 0xBB, seed: 7);
        WriteSampledFile(targetFile, size, head: 0xCC, tail: 0xBB, seed: 7);
        File.SetLastWriteTimeUtc(sourceFile, BaseTimeUtc);
        File.SetLastWriteTimeUtc(targetFile, BaseTimeUtc.AddHours(1));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(size, new FileInfo(targetFile).Length);
        Assert.Equal(
            TempWorkspace.ComputeFileSha256(sourceFile),
            TempWorkspace.ComputeFileSha256(targetFile));
    }

    /// <summary>大文件源 mtime 严格更新时走「必定重拷」快路径，不做抽样。</summary>
    [Fact]
    public void P0_1_LargeFile_SourceNewer_IsCopied()
    {
        using var ws = new TempWorkspace();
        var size = 17L * 1024 * 1024;
        var sourceFile = Path.Combine(ws.SourceDir, "big.bin");
        var targetFile = Path.Combine(ws.TargetDir, "big.bin");

        WriteSampledFile(sourceFile, size, head: 0xAA, tail: 0xBB, seed: 11);
        WriteSampledFile(targetFile, size, head: 0xAA, tail: 0xBB, seed: 11);
        File.SetLastWriteTimeUtc(targetFile, BaseTimeUtc);
        File.SetLastWriteTimeUtc(sourceFile, BaseTimeUtc.AddHours(5));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(File.GetLastWriteTimeUtc(sourceFile), File.GetLastWriteTimeUtc(targetFile));
    }

    /// <summary>混合场景（1 个已校验 + 1 个抽样放行）时摘要必须同时点明两种口径。</summary>
    [Fact]
    public void P0_1_MixedVerifiedAndAssumed_SummaryMentionsBoth()
    {
        using var ws = new TempWorkspace();
        var size = 17L * 1024 * 1024;

        ws.CreateFile("small.txt", "SMALL-IDENTICAL", BaseTimeUtc);
        ws.CreateFile(Path.Combine(ws.TargetDir, "small.txt"), "SMALL-IDENTICAL", BaseTimeUtc.AddHours(1), ws.Root);

        var sourceBig = Path.Combine(ws.SourceDir, "big.bin");
        var targetBig = Path.Combine(ws.TargetDir, "big.bin");
        WriteSampledFile(sourceBig, size, head: 0x31, tail: 0x32, seed: 5);
        WriteSampledFile(targetBig, size, head: 0x31, tail: 0x32, seed: 6);
        File.SetLastWriteTimeUtc(sourceBig, BaseTimeUtc);
        File.SetLastWriteTimeUtc(targetBig, BaseTimeUtc.AddHours(1));

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(2, result.SkippedFiles);
        Assert.Equal(1, result.VerifiedFiles);
        Assert.Equal(1, result.UnchangedAssumed);
        Assert.Contains("已 SHA256 校验 1 个", result.VerificationNote);
        Assert.Contains("未做内容校验 1 个", result.VerificationNote);
    }

    // ==================================================================
    // P0-1 对抗点 c：StrictContentVerification=false 的措辞
    // ==================================================================

    /// <summary>
    /// 对抗点 c：显式关闭严格校验后，同大小 + 目标更新 ⇒ 仍然跳过（用户自担风险），
    /// 但摘要必须明确写「未做内容校验」，不能让人误以为比对过内容。
    /// </summary>
    [Fact]
    public void P0_1_NonStrict_SameSizeOlderSource_IsSkippedButNoteSaysNotVerified()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile("same.txt", "AAAA-OLD-C", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "same.txt");
        ws.CreateFile(targetFile, "BBBB-NEW-C", BaseTimeUtc.AddHours(2), ws.Root);

        var result = new FileCopier { StrictContentVerification = false }
            .CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(1, result.UnchangedAssumed);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Contains("未做内容校验", result.VerificationNote);
        Assert.Contains("未做内容校验", result.Summary);
        Assert.Equal("BBBB-NEW-C", TempWorkspace.ReadAllText(targetFile)); // 记录：内容差异被跳过
        Assert.True(new FileInfo(sourceFile).Length == new FileInfo(targetFile).Length);
    }

    /// <summary>非严格模式下源 mtime 更新时必须重拷（时间戳仍是唯一依据）。</summary>
    [Fact]
    public void P0_1_NonStrict_SourceNewer_IsCopied()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile("same.txt", "SOURCE-NEWER", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "same.txt");
        ws.CreateFile(targetFile, "TARGET-OLDER", BaseTimeUtc, ws.Root);
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow.AddMinutes(10));

        var result = new FileCopier { StrictContentVerification = false }
            .CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal("SOURCE-NEWER", TempWorkspace.ReadAllText(targetFile));
    }

    /// <summary>
    /// 口径留痕：禁用覆盖时，因「目标已存在」而跳过的文件既不进 VerifiedFiles 也不进 UnchangedAssumed，
    /// 摘要因此不会标注校验口径，但仍然用「跳过 N 个未修改文件」的措辞。
    /// 这不属于 P0（没有声称做过校验），但口径偏乐观，记录在此供实现者决定是否收紧文案。
    /// </summary>
    [Fact]
    public void P0_1_OverwriteDisabled_SkippedExistingFiles_HaveNoVerificationNote()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("keep.txt", "SOURCE-NEW", BaseTimeUtc);
        ws.CreateFile(Path.Combine(ws.TargetDir, "keep.txt"), "TARGET-OLD", BaseTimeUtc, ws.Root);

        var result = new FileCopier { OverwriteExistingFiles = false }
            .CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Equal(0, result.UnchangedAssumed);
        Assert.Equal(string.Empty, result.VerificationNote);
        Assert.Contains("跳过 1 个未修改文件", result.Summary);
    }

    // ==================================================================
    // P0-1 对抗点 d / e：StreamCopy 的 .tmp 残留、目标截断、回调异常
    // ==================================================================

    /// <summary>
    /// 对抗点 e：OnBeforeOverwrite 抛异常时必须记为失败、不写目标、不留 .tmp，
    /// 也不能把旧目标截断（否则就是「旧版本已丢、新版本没写入」）。
    /// </summary>
    [Fact]
    public void P0_1_OnBeforeOverwriteThrows_FailsCleanlyWithoutTempOrTargetDamage()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "SOURCE-CONTENT-LONGER", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "a.txt");
        ws.CreateFile(targetFile, "OLD", BaseTimeUtc, ws.Root);

        var copier = new FileCopier
        {
            OnBeforeOverwrite = (_, _, _) => throw new InvalidOperationException("归档失败（模拟）")
        };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Equal("OLD", TempWorkspace.ReadAllText(targetFile));
        Assert.Empty(Directory.GetFiles(ws.TargetDir, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 对抗点 d：复制中途取消（BufferSize=1 保证取消发生在写入过程中）。
    /// 必须不留 .tmp、不产生截断的目标文件。
    /// </summary>
    [Fact]
    public async Task P0_1_CancelledMidCopy_LeavesNoTempFileAndNoPartialTarget()
    {
        using var ws = new TempWorkspace();
        var data = new byte[1024 * 1024];
        new Random(99).NextBytes(data);
        ws.CreateFile("big.bin", data, BaseTimeUtc);

        using var cts = new CancellationTokenSource();
        var canceller = Task.Run(async () =>
        {
            await Task.Delay(200);
            cts.Cancel();
        });

        var copier = new FileCopier { BufferSize = 1 };
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir, null, null, cts.Token);
        await canceller;

        Assert.True(result.Cancelled, "200ms 时 1 字节缓冲不可能复制完 1 MiB，必须已取消");
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "big.bin")), "取消后不得留下被截断的目标文件");
        Assert.Empty(Directory.GetFiles(ws.TargetDir, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// 对抗点 d：临时文件无法写入（用同名目录占位模拟）时必须失败退出，
    /// 且旧目标文件保持原样、不被截断。
    /// </summary>
    [Fact]
    public void P0_1_TempWriteFails_DoesNotTruncateExistingTarget()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "SOURCE-CONTENT-LONGER", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "a.txt");
        ws.CreateFile(targetFile, "OLD", BaseTimeUtc, ws.Root);

        // {target}.tmp 被目录占位 → 临时文件写入必然失败
        Directory.CreateDirectory(targetFile + ".tmp");

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal("OLD", TempWorkspace.ReadAllText(targetFile));
        Assert.Equal(3, new FileInfo(targetFile).Length);
    }

    /// <summary>对抗点 d（正向）：正常复制结束后目标目录里不得残留任何 .tmp。</summary>
    [Fact]
    public void P0_1_SuccessfulCopy_LeavesNoTempResidue()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "aaa", BaseTimeUtc);
        ws.CreateFile(@"sub\b.bin", "bbb", BaseTimeUtc);

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(2, result.CopiedFiles);
        Assert.Empty(Directory.GetFiles(ws.TargetDir, "*.tmp", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "sub", "b.bin")));
    }

    /// <summary>CopyFile（单文件补拷）同样走 .tmp 原子替换，成功与失败都不得留残留。</summary>
    [Fact]
    public void P0_1_CopyFile_SingleFile_LeavesNoTempResidue()
    {
        using var ws = new TempWorkspace();
        var sourceFile = ws.CreateFile("only.txt", "only-content", BaseTimeUtc);
        var targetFile = Path.Combine(ws.TargetDir, "only.txt");

        new FileCopier().CopyFile(sourceFile, targetFile);

        Assert.Equal("only-content", TempWorkspace.ReadAllText(targetFile));
        Assert.Empty(Directory.GetFiles(ws.TargetDir, "*.tmp", SearchOption.AllDirectories));
    }

    // ==================================================================
    // P0-2 对抗点 f：扫描后目录被塞入文件
    // ==================================================================

    /// <summary>
    /// 对抗点 f：先扫描（此时 A\B 被判为空），随后往 A\B 里放入文件，再执行删除。
    /// 必须跳过而不是连带删除——目录与文件都要原样保留。
    /// </summary>
    [Fact]
    public void P0_2_Delete_FileAddedAfterScan_IsSkippedAndContentPreserved()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("A");
        var child = ws.CreateDirectory(Path.Combine("A", "B"));

        var service = CreateEmptyFolderCleaner();
        var paths = ScanEmptyFolderPaths(service, ws.SourceDir);
        Assert.Equal(2, paths.Count); // A\B 与 A 都被判为空

        // 扫描之后才出现的文件（模拟“扫描后有人往目录里塞数据”）
        var lateFile = Path.Combine(child, "late.txt");
        File.WriteAllText(lateFile, "late-arrival");

        var summary = DeleteFolders(service, paths, useRecycleBin: false);

        Assert.True(summary.Skipped >= 2, $"A 与 A\\B 都应被跳过，实际 {summary.Skipped}");
        Assert.Equal(0, summary.Deleted);
        Assert.Equal(0, summary.Failed);
        Assert.True(File.Exists(lateFile), "后来放入的文件必须原样保留");
        Assert.Equal("late-arrival", File.ReadAllText(lateFile));
        Assert.True(Directory.Exists(child));
        Assert.True(Directory.Exists(parent));
    }

    /// <summary>
    /// 对抗点 f（对照）：目录确实为空时仍要正常删除，避免「复查」把正常清理也挡掉。
    /// </summary>
    [Fact]
    public void P0_2_Delete_StillDeletesGenuinelyEmptyDirectories()
    {
        using var ws = new TempWorkspace();
        var parent = ws.CreateDirectory("A");
        var child = ws.CreateDirectory(Path.Combine("A", "B"));

        var service = CreateEmptyFolderCleaner();
        var paths = ScanEmptyFolderPaths(service, ws.SourceDir);
        Assert.Equal(2, paths.Count);

        var summary = DeleteFolders(service, paths, useRecycleBin: false);

        Assert.Equal(2, summary.Deleted);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.False(Directory.Exists(child));
        Assert.False(Directory.Exists(parent));
    }

    /// <summary>对抗点 f（直接调用 Delete，不经 Scan）：非空目录必须跳过且文件无损。</summary>
    [Fact]
    public void P0_2_Delete_NonEmptyDirectory_IsSkipped()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("notEmpty");
        var file = Path.Combine(directory, "data.txt");
        File.WriteAllText(file, "keep-me");

        var service = CreateEmptyFolderCleaner();
        var summary = DeleteFolders(service, new[] { directory }, useRecycleBin: false);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Deleted);
        Assert.Equal(0, summary.Failed);
        Assert.True(File.Exists(file));
        Assert.Equal("keep-me", File.ReadAllText(file));
    }

    // ==================================================================
    // P0-2 对抗点 g：SafeDelete.DeleteEmptyDirectory 对非空目录
    // ==================================================================

    /// <summary>
    /// 对抗点 g：永久删除路径下，非空目录必须抛 IOException 且一个字节都不能删。
    /// </summary>
    [Fact]
    public void P0_2_SafeDelete_DeleteEmptyDirectory_NonEmpty_PermanentPath_ThrowsAndKeepsEverything()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("notEmpty");
        var file = Path.Combine(directory, "data.txt");
        File.WriteAllText(file, "precious");

        var exception = InvokeSafeDeleteEmptyDirectory(directory, useRecycleBin: false);

        Assert.IsAssignableFrom<IOException>(exception);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(file));
        Assert.Equal("precious", File.ReadAllText(file));
    }

    /// <summary>永久删除路径下真正为空的目录必须能删掉（避免“安全”变成“不干活”）。</summary>
    [Fact]
    public void P0_2_SafeDelete_DeleteEmptyDirectory_Empty_PermanentPath_DeletesIt()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("reallyEmpty");

        var exception = InvokeSafeDeleteEmptyDirectory(directory, useRecycleBin: false);

        Assert.Null(exception);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// 对抗点 g（回收站路径探针 / 特征化用例）：
    /// <c>DeleteEmptyDirectory</c> 的文档承诺「即使判空与删除之间存在竞态，也绝不会连带删除内容」，
    /// 但 <paramref name="useRecycleBin"/> 为 true 时走的是 VisualBasic 的整目录删除。
    /// 本用例把观察到的行为写进临时报告文件，便于人工复核：
    /// 若观察到「目录连内容一起被移走」，说明默认（回收站）路径并没有这个保证。
    /// 行为一旦被修复为抛异常，本用例会失败并提示结论已过期。
    /// </summary>
    [Fact]
    public void P0_2_SafeDelete_DeleteEmptyDirectory_NonEmpty_RecycleBinPath_Probe()
    {
        using var ws = new TempWorkspace();
        var directory = ws.CreateDirectory("notEmpty");
        var file = Path.Combine(directory, "data.txt");
        File.WriteAllText(file, "would-be-deleted");

        var exception = InvokeSafeDeleteEmptyDirectory(directory, useRecycleBin: true);
        var directoryExists = Directory.Exists(directory);
        var fileExists = File.Exists(file);

        var observation =
            $"回收站路径 DeleteEmptyDirectory(非空目录)：exception={(exception?.GetType().Name ?? "<none>")}" +
            $"，目录仍存在={directoryExists}，文件仍存在={fileExists}";
        AppendProbeReport(observation);

        if (exception is not null)
        {
            // 安全行为：抛异常，未删除任何东西
            Assert.True(directoryExists && fileExists, observation);
        }
        else
        {
            // 观察到的不安全行为：整个目录（含后来放入的文件）被移入回收站
            Assert.False(directoryExists, observation);
            Assert.False(fileExists, observation);
        }
    }

    // ==================================================================
    // 辅助方法
    // ==================================================================

    /// <summary>写一个「首尾 64 KiB 固定、中段伪随机」的大文件，并把 mtime 固定到过去。</summary>
    private static void WriteSampledFile(string path, long size, byte head, byte tail, int seed)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var data = new byte[(int)size];
        new Random(seed).NextBytes(data);

        for (var index = 0; index < FileCopier.SampleWindowBytes; index++)
        {
            data[index] = head;
            data[(int)size - FileCopier.SampleWindowBytes + index] = tail;
        }

        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, BaseTimeUtc);
    }

    /// <summary>把探针观察结果追加到临时报告文件（不写入仓库源码树）。</summary>
    private static void AppendProbeReport(string line)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "p0-verification-probes.txt");
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // 报告写入失败不影响用例判定
        }
    }

    // ---------------------------------------------------------------- FileMaster 反射桥

    private static readonly Lazy<Assembly> FileMasterAssembly = new(LoadFileMasterAssembly, isThreadSafe: true);

    /// <summary>从仓库构建输出加载 FileMaster.dll（本测试工程未引用它，只能反射加载）。</summary>
    private static Assembly LoadFileMasterAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinToolBox.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("找不到仓库根目录（WinToolBox.sln），无法定位 FileMaster.dll");
        }

        var candidates = new[]
        {
            Path.Combine(directory.FullName, "tests", "FileMaster.Tests", "bin", "Release", "net8.0-windows", "FileMaster.dll"),
            Path.Combine(directory.FullName, "src", "Tools", "FileMaster", "bin", "Release", "net8.0-windows", "FileMaster.dll"),
            Path.Combine(directory.FullName, "src", "Tools", "FileMaster", "bin", "Release", "net8.0-windows", "win-x64", "FileMaster.dll")
        };

        var found = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("找不到 FileMaster.dll，请先 dotnet build WinToolBox.sln -c Release");

        return Assembly.LoadFrom(found);
    }

    private static object CreateEmptyFolderCleaner()
    {
        var type = FileMasterAssembly.Value.GetType(EmptyFolderCleanerTypeName, throwOnError: true)!;
        return Activator.CreateInstance(type, new object?[] { null })!;
    }

    /// <summary>调用 Scan 并取回空目录完整路径列表。</summary>
    private static List<string> ScanEmptyFolderPaths(object service, string root)
    {
        var scan = service.GetType().GetMethod("Scan")!.Invoke(
            service,
            new object?[] { root, true, null, CancellationToken.None })!;

        Assert.Null(scan.GetType().GetProperty("Error")!.GetValue(scan));

        var items = (IEnumerable)scan.GetType().GetProperty("Items")!.GetValue(scan)!;
        return items.Cast<object>()
            .Select(item => (string)item.GetType().GetProperty("FullPath")!.GetValue(item)!)
            .ToList();
    }

    /// <summary>调用 Delete 并把结果压成与测试无关的纯值元组。</summary>
    private static (int Skipped, int Deleted, int Failed) DeleteFolders(
        object service,
        IEnumerable<string> paths,
        bool useRecycleBin)
    {
        var result = service.GetType().GetMethod("Delete")!.Invoke(
            service,
            new object?[] { paths, useRecycleBin, null, CancellationToken.None })!;

        var type = result.GetType();
        return (
            (int)type.GetProperty("SkippedCount")!.GetValue(result)!,
            (int)type.GetProperty("DeletedCount")!.GetValue(result)!,
            (int)type.GetProperty("FailedCount")!.GetValue(result)!);
    }

    /// <summary>反射调用 internal 的 SafeDelete.DeleteEmptyDirectory，返回其抛出的原始异常（未抛出则为 null）。</summary>
    private static Exception? InvokeSafeDeleteEmptyDirectory(string path, bool useRecycleBin)
    {
        var type = FileMasterAssembly.Value.GetType(SafeDeleteTypeName, throwOnError: true)!;
        var method = type.GetMethod(
            "DeleteEmptyDirectory",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        try
        {
            method.Invoke(null, new object?[] { path, useRecycleBin });
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return ex.InnerException;
        }
    }
}
