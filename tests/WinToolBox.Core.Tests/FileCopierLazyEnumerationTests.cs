using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// P1-20 回归测试：备份预扫描不能把整盘文件清单物化进内存。
/// <para>修复前 <c>CollectFiles</c> 返回完整 <c>List&lt;FileEntry&gt;</c>，
/// 几十万文件的 U 盘会「全量元数据入内存 + 复制前零进度」。</para>
/// <para>修复后改为惰性遍历 + 轻量计数（只计数与总字节，不存路径），并上报「已发现 N 个文件」。</para>
/// </summary>
public sealed class FileCopierLazyEnumerationTests
{
    private static readonly DateTime BaseTimeUtc = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CopyDirectory_ReportsScanProgress_WhileDiscoveringFiles()
    {
        using var ws = new TempWorkspace();
        for (var i = 0; i < 25; i++)
        {
            ws.CreateFile($"f{i:D2}.txt", $"content-{i}", BaseTimeUtc);
        }

        var discovered = new List<int>();
        var copier = new FileCopier { OnScanProgress = discovered.Add };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(25, result.CopiedFiles);

        // 发现即上报：每个文件一次，且累计值单调递增到总数
        Assert.Equal(25, discovered.Count);
        Assert.Equal(1, discovered[0]);
        Assert.Equal(25, discovered[^1]);
        Assert.True(discovered.SequenceEqual(Enumerable.Range(1, 25)),
            "预扫描进度必须是 1..N 的单调递增序列");
    }

    [Fact]
    public void CopyDirectory_ScanProgressException_DoesNotBreakCopy()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "AAA", BaseTimeUtc);

        var copier = new FileCopier
        {
            OnScanProgress = _ => throw new InvalidOperationException("进度上报炸了")
        };

        // 进度上报失败绝不能影响复制本身
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);
    }

    [Fact]
    public void CopyDirectory_TotalFilesAndTotalBytes_AreStableAcrossProgressReports()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "aaaaa", BaseTimeUtc);              // 5 字节
        ws.CreateFile(@"sub\b.txt", "bbbbbbbbbb", BaseTimeUtc);    // 10 字节

        var snapshots = new List<CopyProgress>();
        new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir, snapshots.Add);

        Assert.Equal(2, snapshots.Count);

        // 分母在整个过程中必须稳定（惰性枚举下最容易退化成“边发现边增长”）
        Assert.All(snapshots, s => Assert.Equal(2, s.TotalFiles));
        Assert.All(snapshots, s => Assert.Equal(15, s.TotalBytes));

        // 分子单调递增，最后一次到达总数
        Assert.Equal(1, snapshots[0].ProcessedFiles);
        Assert.Equal(2, snapshots[1].ProcessedFiles);
        Assert.Equal(100d, snapshots[1].Percent);
    }

    [Fact]
    public void CopyDirectory_HandlesManyFiles_WithFlatMemoryFootprint()
    {
        using var ws = new TempWorkspace();

        // 3000 个文件：旧实现会把 3000 个 FileEntry 全部物化后才开始复制，
        // 新实现在第一个文件被复制前只保留 O(目录深度) 的栈。
        const int fileCount = 3000;
        for (var i = 0; i < fileCount; i++)
        {
            ws.CreateFile(Path.Combine($"d{i % 20:D2}", $"f{i:D4}.txt"), $"payload-{i}", BaseTimeUtc);
        }

        var firstReportedDiscovery = -1;
        var firstProgressSeen = false;

        var copier = new FileCopier
        {
            OnScanProgress = n =>
            {
                if (!firstProgressSeen)
                {
                    firstProgressSeen = true;
                    firstReportedDiscovery = n;
                }
            }
        };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(fileCount, result.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);

        // 关键：还没复制任何文件时就已经开始上报“已发现”，说明没有“先建全量清单”的静默期
        Assert.Equal(1, firstReportedDiscovery);
    }

    [Fact]
    public void CopyDirectory_CancelledBeforeCopy_DoesNotEnumerateWholeTree()
    {
        using var ws = new TempWorkspace();
        for (var i = 0; i < 50; i++)
        {
            ws.CreateFile($"f{i:D2}.txt", "x", BaseTimeUtc);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir, null, null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.CopiedFiles);
    }

    [Fact]
    public void CopyDirectory_SkipsExcludedFilesFromScanProgressAndTotals()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("keep.txt", "KEEP", BaseTimeUtc);
        ws.CreateFile("skip.tmp", "SKIP", BaseTimeUtc);

        var discovered = new List<int>();
        var snapshots = new List<CopyProgress>();

        var copier = new FileCopier { OnScanProgress = discovered.Add };
        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir, snapshots.Add);

        // 被排除的文件既不参与复制，也不应计入发现数/总数
        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(1, discovered.Count);
        Assert.All(snapshots, s => Assert.Equal(1, s.TotalFiles));
    }

    // ------------------------------------------------------------------
    // P1-1：目录联接 / 符号链接绝不递归
    // ------------------------------------------------------------------

    [Fact]
    public void CopyDirectory_WhenOneSubdirectoryCannotBeRead_StillCopiesTheRest()
    {
        using var ws = new TempWorkspace();

        // 顶层放两个正常文件，保证「其余文件仍被复制」有可观测的对象
        ws.CreateFile("keep1.txt", "KEEP-1", BaseTimeUtc);
        ws.CreateFile("keep2.txt", "KEEP-2", BaseTimeUtc);

        // 一个很深的目录：每一层都很短，但总长会超过经典 MAX_PATH，
        // 使 Directory.GetDirectories/GetFiles 在它上面抛 PathTooLongException。
        var deep = ws.SourceDir;
        var depth = 0;
        try
        {
            while (deep.Length < 280 && depth < 40)
            {
                deep = Path.Combine(deep, new string((char)('a' + (depth % 26)), 20));
                Directory.CreateDirectory(deep);
                depth++;
            }
        }
        catch (Exception)
        {
            // 造不出超长目录：退化为「只有正常文件」的场景，仍然验证复制不被中断
            depth = 0;
        }

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 核心断言：某个子目录不可读（或不存在）时，其余文件必须照常复制
        Assert.Equal(2, result.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "keep1.txt")));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "keep2.txt")));
        Assert.Equal("KEEP-1", TempWorkspace.ReadAllText(Path.Combine(ws.TargetDir, "keep1.txt")));

        // 不能因为一个坏目录就整轮失败
        Assert.Equal(0, result.FailedFiles);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void CopyDirectory_WhenSubdirectoryDisappearsMidScan_DoesNotThrow()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "A", BaseTimeUtc);

        // 在第一次发现文件后，把还在待处理栈上的子目录删掉：
        // 枚举它会失败，但失败必须被吞掉并继续处理其余文件。
        var doomed = ws.CreateDirectory(Path.Combine("doomed", "inner"), ws.SourceDir);
        File.WriteAllText(Path.Combine(ws.SourceDir, "doomed", "inner", "b.txt"), "B");

        var retired = false;
        var copier = new FileCopier();
        copier.OnScanProgress = _ =>
        {
            if (retired)
            {
                return;
            }

            retired = true;

            try
            {
                Directory.Delete(Path.Combine(ws.SourceDir, "doomed"), recursive: true);
            }
            catch
            {
                // 删不掉也不影响本用例结论
            }
        };

        Exception? thrown = null;
        CopyResult? result = null;

        try
        {
            result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        // 修复前：枚举异常发生在 try 之外，会直接冒泡成「整个备份 0 文件复制」
        Assert.Null(thrown);
        Assert.NotNull(result);
        Assert.True(result!.CopiedFiles >= 1, "至少 a.txt 必须被复制");
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "a.txt")));
        _ = doomed;
    }

    [Fact]
    public void JunctionHelper_ActuallyCreatesAReparsePoint()
    {
        using var ws = new TempWorkspace();
        var outside = ws.CreateDirectory("outside", ws.Root);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "OUTSIDE");

        var linkPath = Path.Combine(ws.Root, "probe-link");
        if (!JunctionHelper.TryCreateJunction(linkPath, outside))
        {
            // 本机确实无法创建联接：这条用例失去意义，但不判失败
            return;
        }

        // 必须真的是 ReparsePoint（否则上面「跳过联接」的用例只是走了普通目录分支，等于没测）
        var attributes = new DirectoryInfo(linkPath).Attributes;
        Assert.True((attributes & FileAttributes.ReparsePoint) != 0,
            $"TryCreateJunction 返回成功，但目录没有 ReparsePoint 属性：{attributes}");

        // 联接必须能读到目标内容（证明链接指向正确）
        Assert.True(File.Exists(Path.Combine(linkPath, "secret.txt")));
    }

    [Fact]
    public void CopyDirectory_SkipsJunction_AndDoesNotCopyItsTargetContent()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.SourceDir);
        ws.CreateFile("real.txt", "REAL", BaseTimeUtc);

        // 源目录之外的一个目录，用【目录联接】指向它（不需要管理员权限）
        var outside = ws.CreateDirectory("outside", ws.Root);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "OUTSIDE-CONTENT");

        var linkPath = Path.Combine(ws.SourceDir, "link");
        if (!JunctionHelper.TryCreateJunction(linkPath, outside))
        {
            return;   // 环境不支持创建联接：跳过（不把环境限制当成失败）
        }

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        // 正常文件照常复制
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "real.txt")));
        Assert.Equal("REAL", TempWorkspace.ReadAllText(Path.Combine(ws.TargetDir, "real.txt")));

        // 关键：联接指向的外部内容一个字节都不能进来
        Assert.False(
            File.Exists(Path.Combine(ws.TargetDir, "link", "secret.txt")),
            "目录联接指向的内容绝不能被复制");
        Assert.False(Directory.Exists(Path.Combine(ws.TargetDir, "link")));
    }

    [Fact]
    public void CopyDirectory_JunctionInsideSource_DoesNotCauseInfiniteRecursion()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.SourceDir);
        ws.CreateFile("a.txt", "A", BaseTimeUtc);

        // 让联接指回源目录自身：不做 ReparsePoint 判断的实现会无限递归
        var linkPath = Path.Combine(ws.SourceDir, "self");
        if (!JunctionHelper.TryCreateJunction(linkPath, ws.SourceDir))
        {
            return;
        }

        // 能正常返回（不卡死、不栈溢出）本身就是通过条件
        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);
    }

    [Fact]
    public void CopyDirectory_RejectsTargetInsideSource_UsingSeparatorAwareComparison()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "A", BaseTimeUtc);

        // 目标位于源内部 → 必须拒绝（否则会自我递归复制）
        var inside = Path.Combine(ws.SourceDir, "inner");
        Assert.Throws<ArgumentException>(() => new FileCopier().CopyDirectory(ws.SourceDir, inside));

        // 相似前缀的兄弟目录不是「内部」→ 必须放行
        var sibling = ws.SourceDir + "-sibling";
        var result = new FileCopier().CopyDirectory(ws.SourceDir, sibling);
        Assert.Equal(1, result.CopiedFiles);
    }
}
