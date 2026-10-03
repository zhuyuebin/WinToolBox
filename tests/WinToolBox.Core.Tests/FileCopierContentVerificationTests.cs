using System.Security.Cryptography;
using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// P0-1 回归测试：增量判定不能只看「大小 + 修改时间」。
/// 修复前，同大小改写 + 时间戳回退的文件会被永久静默跳过，用户的改动永远进不了备份。
/// </summary>
public sealed class FileCopierContentVerificationTests
{
    private static readonly DateTime BaseTimeUtc = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // 核心场景：同大小改写 + 时间戳回退
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_CopiesFile_WhenContentChangedAndTimestampWentBackwards()
    {
        using var ws = new TempWorkspace();

        // 第一次：复制成功
        var source = ws.CreateFile("report.txt", "AAAA-1111", BaseTimeUtc);
        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var target = Path.Combine(ws.TargetDir, "report.txt");
        Assert.Equal("AAAA-1111", TempWorkspace.ReadAllText(target));

        // 同大小改写，并把源时间改成【比目标更旧】——旧实现会永久跳过
        File.WriteAllText(source, "BBBB-2222");
        File.SetLastWriteTimeUtc(source, BaseTimeUtc.AddDays(-10));
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow);

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal("BBBB-2222", TempWorkspace.ReadAllText(target));
    }

    [Fact]
    public void CopyDirectory_CopiesFile_WhenContentChangedAndTimestampsAreIdentical()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateFile("same-time.txt", "12345678", BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var target = Path.Combine(ws.TargetDir, "same-time.txt");

        // 同大小改写，两边时间戳完全一致（>= 判定在旧实现里会跳过）
        File.WriteAllText(source, "87654321");
        File.SetLastWriteTimeUtc(source, BaseTimeUtc);
        File.SetLastWriteTimeUtc(target, BaseTimeUtc);

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal("87654321", TempWorkspace.ReadAllText(target));
    }

    // ------------------------------------------------------------------
    // 计数与摘要文案
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_CountsVerifiedFiles_WhenContentIdentical()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "same-content", BaseTimeUtc);
        ws.CreateFile(@"sub\b.txt", "other-content", BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(2, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var second = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, second.CopiedFiles);
        Assert.Equal(2, second.SkippedFiles);
        Assert.Equal(2, second.VerifiedFiles);
        Assert.Equal(0, second.UnchangedAssumed);
        Assert.Contains("已 SHA256 校验", second.Summary);
        Assert.DoesNotContain("未做内容校验", second.Summary);
    }

    [Fact]
    public void CopyDirectory_ReportsUnchangedAssumed_WhenStrictVerificationDisabled()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "content", BaseTimeUtc);

        var copier = new FileCopier { StrictContentVerification = false };
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var second = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, second.CopiedFiles);
        Assert.Equal(1, second.SkippedFiles);
        Assert.Equal(0, second.VerifiedFiles);
        Assert.Equal(1, second.UnchangedAssumed);
        Assert.Contains("未做内容校验", second.Summary);
        Assert.DoesNotContain("已 SHA256 校验", second.Summary);
    }

    [Fact]
    public void CopyDirectory_StrictVerificationOff_StillCopiesWhenSizeDiffers()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateFile("a.txt", "short", BaseTimeUtc);

        var copier = new FileCopier { StrictContentVerification = false };
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        // 长度变化是唯一仍然可靠的廉价信号：即使关闭严格校验也必须重拷
        File.WriteAllText(source, "much-longer-content");
        File.SetLastWriteTimeUtc(source, BaseTimeUtc.AddDays(-5));

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal("much-longer-content", TempWorkspace.ReadAllText(Path.Combine(ws.TargetDir, "a.txt")));
    }

    // ------------------------------------------------------------------
    // 大文件：大小 + mtime + 首尾抽样哈希
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_DetectsLargeFileChange_WhenHeadDiffers()
    {
        using var ws = new TempWorkspace();
        var length = FileCopier.LargeFileThresholdBytes + (1024 * 1024); // 17 MiB，超过 16 MiB 阈值
        var source = ws.CreateFile("big.bin", CreatePattern(length, seed: 1), BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var target = Path.Combine(ws.TargetDir, "big.bin");

        // 只改写文件头（落在首 64 KiB 抽样窗口内），并把两边时间戳设成一致
        var mutated = CreatePattern(length, seed: 1);
        mutated[0] ^= 0xFF;
        File.WriteAllBytes(source, mutated);
        File.SetLastWriteTimeUtc(source, BaseTimeUtc);
        File.SetLastWriteTimeUtc(target, BaseTimeUtc);

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
    }

    [Fact]
    public void CopyDirectory_DetectsLargeFileChange_WhenTailDiffers()
    {
        using var ws = new TempWorkspace();
        var length = FileCopier.LargeFileThresholdBytes + (1024 * 1024);
        var source = ws.CreateFile("big.bin", CreatePattern(length, seed: 2), BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var target = Path.Combine(ws.TargetDir, "big.bin");

        var mutated = CreatePattern(length, seed: 2);
        mutated[^1] ^= 0xFF;   // 只改最后一个字节
        File.WriteAllBytes(source, mutated);
        File.SetLastWriteTimeUtc(source, BaseTimeUtc);
        File.SetLastWriteTimeUtc(target, BaseTimeUtc);

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
    }

    [Fact]
    public void CopyDirectory_LargeFileWithMiddleOnlyChange_IsReportedAsNotVerified()
    {
        using var ws = new TempWorkspace();
        var length = FileCopier.LargeFileThresholdBytes + (1024 * 1024);
        var source = ws.CreateFile("big.bin", CreatePattern(length, seed: 3), BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var target = Path.Combine(ws.TargetDir, "big.bin");

        // 只改文件中段：首尾抽样窗口看不到，因此会走「折中接受」分支。
        // 关键是不能谎称「已校验」——必须如实计入 UnchangedAssumed 并在摘要标注。
        var mutated = CreatePattern(length, seed: 3);
        mutated[length / 2] ^= 0xFF;
        File.WriteAllBytes(source, mutated);
        File.SetLastWriteTimeUtc(source, BaseTimeUtc);
        File.SetLastWriteTimeUtc(target, BaseTimeUtc);

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(0, result.VerifiedFiles);
        Assert.Equal(1, result.UnchangedAssumed);
        Assert.Contains("未做内容校验", result.Summary);
    }

    [Fact]
    public void CopyDirectory_LargeFileWithNewerTimestamp_IsCopiedWithoutReadingContent()
    {
        using var ws = new TempWorkspace();
        var length = FileCopier.LargeFileThresholdBytes + (1024 * 1024);
        var source = ws.CreateFile("big.bin", CreatePattern(length, seed: 4), BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var target = Path.Combine(ws.TargetDir, "big.bin");

        // 大文件 + 源时间严格更新 => 直接判为已更新
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(target));
    }

    // ------------------------------------------------------------------
    // 覆盖前归档回调（P1-8 的 history 语义依赖它）
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_InvokesOnBeforeOverwrite_OnlyWhenReplacingExistingFile()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateFile("a.txt", "OLD-CONTENT", BaseTimeUtc);
        ws.CreateFile("b.txt", "brand-new", BaseTimeUtc);

        var copier = new FileCopier();
        var overwritten = new List<string>();
        copier.OnBeforeOverwrite = (_, dst, _) => overwritten.Add(Path.GetFileName(dst));

        // 首次运行：目标不存在，不应该触发归档回调
        var first = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);
        Assert.Equal(2, first.CopiedFiles);
        Assert.Empty(overwritten);

        // 改写 a.txt（内容变、大小变），目标已存在 → 覆盖前必须触发归档
        File.WriteAllText(source, "NEW-CONTENT-LONGER");
        var second = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, second.CopiedFiles);
        Assert.Equal(new[] { "a.txt" }, overwritten);
    }

    [Fact]
    public void CopyDirectory_FailsFile_WhenOnBeforeOverwriteThrows()
    {
        using var ws = new TempWorkspace();
        var source = ws.CreateFile("a.txt", "OLD", BaseTimeUtc);

        var copier = new FileCopier();
        Assert.Equal(1, copier.CopyDirectory(ws.SourceDir, ws.TargetDir).CopiedFiles);

        var target = Path.Combine(ws.TargetDir, "a.txt");
        File.WriteAllText(source, "NEW-LONGER");
        copier.OnBeforeOverwrite = (_, _, _) => throw new IOException("归档失败");

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 归档失败必须让本次复制失败，而不是「旧版本丢了、新版本没写」的静默数据丢失
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal("OLD", TempWorkspace.ReadAllText(target));
        Assert.False(result.Success);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    /// <summary>生成固定长度的、可重复的字节图案（避免大文件测试依赖随机数）。</summary>
    private static byte[] CreatePattern(long length, int seed)
    {
        var bytes = new byte[length];
        for (long i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + seed * 17) % 251);
        }

        return bytes;
    }
}
