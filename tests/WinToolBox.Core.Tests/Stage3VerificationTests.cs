using System.Diagnostics;
using System.Reflection;
using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// 阶段三验收（独立验证者 verifier3）：取消令牌透传 / 退出安全 / 早停与取消不混淆。
/// <para>本文件只做「对抗性验证」，不修改任何产品代码。对应 task-4 的对抗点：</para>
/// <list type="number">
/// <item>对抗点 1：取消是否真的中断 I/O、是否在数秒内停止、是否残留 .tmp / 截断目标。</item>
/// <item>对抗点 2：退出安全的前提（取消后确实能很快收尾）。</item>
/// <item>对抗点 3：<c>Cancelled</c> / <c>AbortedByConsecutiveFailures</c> / <c>AbortedByFailureThreshold</c> 不得互相混淆。</item>
/// <item>对抗点 6：<c>JunctionHelper</c> 是否真的创建了带 ReparsePoint 的联接（否则相关测试是空跑）。</item>
/// </list>
/// <para>环境说明：本机沙箱只有工作目录（D:\github\WinToolBox）可写，因此所有临时数据都放在 <see cref="TempWorkspace"/> 里。</para>
/// </summary>
public sealed class Stage3VerificationTests
{
    /// <summary>固定的过去时间，避免「复制后目标时间 &gt;= 源时间」造成判定歧义。</summary>
    private static readonly DateTime BaseTimeUtc = DateTime.UtcNow.AddHours(-1);

    /// <summary>大文件尺寸：要足够大到「取消一定落在 I/O 中途」，又不能拖慢整轮测试。</summary>
    private const long BigFileBytes = 24L * 1024 * 1024;

    // ==================================================================
    // 公共辅助
    // ==================================================================

    /// <summary>创建一个指定长度的文件（用 SetLength 快速占位，内容为 0），并把时间戳设为过去。</summary>
    private static string CreateSizedFile(string path, long length)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(length);
        }

        File.SetLastWriteTimeUtc(path, BaseTimeUtc);
        return path;
    }

    /// <summary>轮询等待路径出现（用于「I/O 已经开始」这一前提的判定）。</summary>
    private static bool WaitForPath(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }

            Thread.Sleep(0);
        }

        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>轮询等待文件长度达到 <paramref name="minLength"/>（证明复制已经写了相当一段数据）。</summary>
    private static bool WaitForFileLength(string path, long minLength, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length >= minLength)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // 文件正被写入：忽略，继续轮询
            }

            Thread.Sleep(0);
        }

        return false;
    }

    /// <summary>文件当前长度（不存在时返回 -1）。</summary>
    private static long LengthOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    /// <summary>目录下所有以 .tmp 结尾的文件（只看文件，不看同名目录）。</summary>
    private static string[] FindTempResidue(string directory)
        => !Directory.Exists(directory)
            ? Array.Empty<string>()
            : Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(static file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();

    /// <summary>断言目录里没有 .tmp 残骸。</summary>
    private static void AssertNoTempResidue(string directory)
    {
        var residue = FindTempResidue(directory);
        Assert.True(
            residue.Length == 0,
            "取消/失败后不得残留 .tmp 暂存文件，实际残留：" + string.Join(" | ", residue));
    }

    /// <summary>
    /// 记录一条实测指标到 <c>.tmp\stage3-verify3-metrics.txt</c>（供验证报告引用真实数字）。
    /// <para>运行器只打印失败用例，通过的用例没有输出通道，因此用文件记录。
    /// 记录失败绝不影响断言结论。</para>
    /// </summary>
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
            // 指标记录失败不影响测试结论
        }
    }

    /// <summary>测试用假通知器（记录通知，不做任何 UI 操作）。</summary>
    private sealed class RecordingNotifier : INotifier
    {
        /// <summary>收到的通知标题。</summary>
        public List<string> Titles { get; } = new();

        /// <summary>收到的通知正文。</summary>
        public List<string> Messages { get; } = new();

        public void ShowNotification(string title, string message)
        {
            Titles.Add(title);
            Messages.Add(message);
        }
    }

    // ==================================================================
    // 对抗点 6：JunctionHelper 必须真的能创建目录联接
    // ==================================================================

    /// <summary>
    /// 对抗点 6：<c>JunctionHelper.TryCreateJunction</c> 成功返回时，目录必须真的带
    /// <see cref="FileAttributes.ReparsePoint"/>，且能通过联接读到目标内容。
    /// <para>本用例故意【硬断言】而不是「失败就 return」：如果它总是返回 false，
    /// 那么 FileCopierLazyEnumerationTests 里 3 条依赖它的用例全是空跑（必然通过）。</para>
    /// </summary>
    [Fact]
    public void JunctionHelper_ReturnsTrueAndCreatesRealReparsePoint()
    {
        using var ws = new TempWorkspace();
        var outside = Directory.CreateDirectory(Path.Combine(ws.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "OUTSIDE-CONTENT");

        var link = Path.Combine(ws.Root, "probe-link");
        var created = JunctionHelper.TryCreateJunction(link, outside);

        Assert.True(
            created,
            "JunctionHelper.TryCreateJunction 返回 false —— 所有依赖它的「跳过目录联接」用例都会静默 return，等于空跑。");

        var attributes = new DirectoryInfo(link).Attributes;
        Assert.True(
            (attributes & FileAttributes.ReparsePoint) != 0,
            $"TryCreateJunction 返回成功，但目录没有 ReparsePoint 属性：{attributes}");

        // 能通过联接读到目标内容：证明它真的指向 outside，而不是一个空目录
        Assert.True(Directory.Exists(link), "联接目录必须可访问");
        Assert.Equal("OUTSIDE-CONTENT", File.ReadAllText(Path.Combine(link, "secret.txt")));

        // .NET 自身也认它是链接，且最终目标就是 outside
        var resolved = Directory.ResolveLinkTarget(link, returnFinalTarget: true);
        Assert.NotNull(resolved);
        Assert.Equal(
            outside.TrimEnd(Path.DirectorySeparatorChar),
            resolved!.FullName.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
    }

    /// <summary>
    /// 对抗点 6 + P1-1：在深两层子目录里放一个【真实存在】的目录联接，
    /// 复制时必须跳过它（既不复制链接目标的内容，也不在目标里创建该目录）。
    /// </summary>
    [Fact]
    public void CopyDirectory_SkipsRealJunctionAtAnyDepth()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.SourceDir);
        ws.CreateFile("real.txt", "REAL", BaseTimeUtc);
        ws.CreateFile(Path.Combine("sub", "inner.txt"), "INNER", BaseTimeUtc);

        var outside = Directory.CreateDirectory(Path.Combine(ws.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET");

        var deep = Directory.CreateDirectory(Path.Combine(ws.SourceDir, "sub", "deep")).FullName;
        var link = Path.Combine(deep, "link");

        Assert.True(
            JunctionHelper.TryCreateJunction(link, outside),
            "无法创建目录联接：本用例会失去意义（这是环境限制，但必须显式暴露，不能静默跳过）");

        var result = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.Equal(2, result.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "real.txt")));
        Assert.True(File.Exists(Path.Combine(ws.TargetDir, "sub", "inner.txt")));

        Assert.False(Directory.Exists(Path.Combine(ws.TargetDir, "sub", "deep", "link")), "不得在目标里创建联接对应目录");
        Assert.False(
            File.Exists(Path.Combine(ws.TargetDir, "sub", "deep", "link", "secret.txt")),
            "目录联接指向的外部内容绝不能被复制");
        Assert.Empty(
            Directory.EnumerateFiles(ws.TargetDir, "*", SearchOption.AllDirectories)
                .Where(static file => file.EndsWith("secret.txt", StringComparison.OrdinalIgnoreCase)));
    }

    // ==================================================================
    // 对抗点 1：取消必须真的中断 I/O
    // ==================================================================

    /// <summary>
    /// 对抗点 1：大文件复制【中途】取消。
    /// <para>判定手法：轮询 <c>{目标}.tmp</c> 出现（它的出现证明 StreamCopy 已开始写盘），
    /// 此刻再 Cancel —— 这就把「取消发生在 I/O 中途」变成了可判定的事实，而不是靠 sleep 猜。</para>
    /// <para>必须同时满足：数秒内停止、Cancelled=true、没有截断的目标文件、没有 .tmp 残骸。</para>
    /// </summary>
    [Fact]
    public void Cancel_DuringLargeFileCopy_StopsWithinSeconds_NoTempNoPartialTarget()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.SourceDir);
        var big = CreateSizedFile(Path.Combine(ws.SourceDir, "big.bin"), BigFileBytes);

        using var cts = new CancellationTokenSource();
        var holder = new CopyResult?[1];
        var copyTask = Task.Run(() => holder[0] = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir, null, null, cts.Token));

        var staging = Path.Combine(ws.TargetDir, "big.bin.tmp");
        // 先等 .tmp 出现（证明 StreamCopy 已开始），再等它长到 8 MiB
        // （证明取消发生在「大文件 I/O 真正进行中」，而不是刚建文件那一刻）
        var observedStaging = WaitForPath(staging, TimeSpan.FromSeconds(30));
        var grewMidStream = WaitForFileLength(staging, 8L * 1024 * 1024, TimeSpan.FromSeconds(30));
        var stagingLengthAtCancel = LengthOf(staging);

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();
        var finished = copyTask.Wait(TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.True(
            observedStaging,
            "用例前提未生效：没有观察到正在写入的 .tmp（复制过快），本用例无法证明取消能中断 I/O");
        Assert.True(
            grewMidStream,
            $"用例前提未生效：取消时 .tmp 只有 {stagingLengthAtCancel} 字节（未达到 8 MiB），无法证明取消发生在 I/O 中途");
        Assert.True(finished, "取消后复制必须在 20 秒内返回");
        Assert.True(
            stopwatch.ElapsedMilliseconds < 5000,
            $"从 Cancel() 到复制返回耗时 {stopwatch.ElapsedMilliseconds} ms，必须能在数秒内停止");

        var result = holder[0];
        Assert.NotNull(result);
        Assert.True(result!.Cancelled, "中途取消必须置 Cancelled=true");
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.False(result.Success);

        Assert.False(File.Exists(Path.Combine(ws.TargetDir, "big.bin")), "取消后不得留下被截断的目标文件");
        Assert.False(File.Exists(big + ".tmp"));
        AssertNoTempResidue(ws.TargetDir);

        RecordMetric(
            $"[Core] Cancel_DuringLargeFileCopy 文件={BigFileBytes / (1024 * 1024)}MiB observedTmp={observedStaging} " +
            $"stagingBytesAtCancel={stagingLengthAtCancel} stopMs={stopwatch.ElapsedMilliseconds} copied={result.CopiedFiles} " +
            $"failed={result.FailedFiles} residue={FindTempResidue(ws.TargetDir).Length} truncatedTarget={File.Exists(Path.Combine(ws.TargetDir, "big.bin"))}");
    }

    /// <summary>
    /// 对抗点 1 / 2：目标已存在【完整旧版本】时中途取消，旧版本必须一个字节都不能变
    /// （这是「退出不会留下半截文件」的核心保证：写 .tmp + 原子替换）。
    /// </summary>
    [Fact]
    public void Cancel_MidCopy_LeavesExistingTargetByteIdentical()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.SourceDir);
        CreateSizedFile(Path.Combine(ws.SourceDir, "data.bin"), BigFileBytes);

        const string oldContent = "OLD-COMPLETE-VERSION";
        var target = ws.CreateFile("data.bin", oldContent, BaseTimeUtc, ws.TargetDir);

        using var cts = new CancellationTokenSource();
        var holder = new CopyResult?[1];
        var copyTask = Task.Run(() => holder[0] = new FileCopier().CopyDirectory(ws.SourceDir, ws.TargetDir, null, null, cts.Token));

        var observedStaging = WaitForPath(target + ".tmp", TimeSpan.FromSeconds(30));
        cts.Cancel();
        var finished = copyTask.Wait(TimeSpan.FromSeconds(20));

        Assert.True(observedStaging, "用例前提未生效：没有观察到 .tmp");
        Assert.True(finished, "取消后复制必须在 20 秒内返回");
        Assert.True(holder[0]!.Cancelled);

        Assert.Equal(oldContent, TempWorkspace.ReadAllText(target));
        Assert.Equal(oldContent.Length, new FileInfo(target).Length);
        AssertNoTempResidue(ws.TargetDir);

        RecordMetric($"[Core] Cancel_MidCopy_OldTargetIntact observedTmp={observedStaging} targetBytes={new FileInfo(target).Length}");
    }

    /// <summary>
    /// 对抗点 1：返回的必须是「已执行清单」而不是「打算做的清单」。
    /// <para>手法：第 2 个文件进入判定回调（复制前）时取消 —— 第 1 个文件必然已经复制完成，
    /// 第 2 个必然没写完。因此 CopiedFiles 必须恰好等于目标里真实存在的文件数。</para>
    /// </summary>
    [Fact]
    public void Cancel_AfterFirstFile_ReportedListEqualsActuallyCopiedFiles()
    {
        using var ws = new TempWorkspace();
        ws.CreateFile("a.txt", "AAA", BaseTimeUtc);
        ws.CreateFile("b.txt", "BBB", BaseTimeUtc);
        ws.CreateFile("c.txt", "CCC", BaseTimeUtc);

        using var cts = new CancellationTokenSource();
        var decisions = new List<string>();
        var copier = new FileCopier
        {
            OnFileDecision = (_, _, relativePath, _) =>
            {
                decisions.Add(relativePath);
                if (decisions.Count == 2)
                {
                    cts.Cancel();
                }
            }
        };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir, null, null, cts.Token);

        Assert.Equal(2, decisions.Count);
        Assert.True(result.Cancelled, "判定回调里取消后必须置 Cancelled=true");
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal(1, result.CopiedFiles);

        // 「清单」与真实产物一致：只有第 1 个决策的文件被写进目标
        var produced = Directory.EnumerateFiles(ws.TargetDir, "*", SearchOption.AllDirectories)
            .Select(static file => Path.GetFileName(file))
            .ToArray();
        Assert.Single(produced);
        Assert.Equal(Path.GetFileName(decisions[0]), produced[0]);
        Assert.False(File.Exists(Path.Combine(ws.TargetDir, Path.GetFileName(decisions[1]))));
        AssertNoTempResidue(ws.TargetDir);

        RecordMetric(
            $"[Core] Cancel_AfterFirstFile decisions={decisions.Count} copied={result.CopiedFiles} " +
            $"produced={produced.Length} first={decisions[0]} cancelledAt={decisions[1]}");
    }

    // ==================================================================
    // 对抗点 3：取消 与 连续失败早停 不得混淆
    // ==================================================================

    /// <summary>
    /// 对抗点 3：连续失败早停 —— <c>AbortedByConsecutiveFailures=true</c>、<c>Cancelled=false</c>，
    /// 摘要只能说「连续失败过多已中止」，绝不能出现「已取消」。
    /// <para>制造失败：把每个目标的 <c>{目标}.tmp</c> 路径用同名目录占位，写入 .tmp 必然失败。</para>
    /// </summary>
    [Fact]
    public void ConsecutiveFailureAbort_NotConfusedWithUserCancel()
    {
        using var ws = new TempWorkspace();
        var names = new[] { "f1.txt", "f2.txt", "f3.txt", "f4.txt" };
        foreach (var name in names)
        {
            ws.CreateFile(name, name, BaseTimeUtc);
        }

        Directory.CreateDirectory(ws.TargetDir);
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(ws.TargetDir, name + ".tmp"));
        }

        var result = new FileCopier { MaxConsecutiveFailures = 2 }.CopyDirectory(ws.SourceDir, ws.TargetDir);

        Assert.True(result.AbortedByConsecutiveFailures, "连续 2 个失败后必须置 AbortedByConsecutiveFailures=true");
        Assert.False(result.Cancelled, "早停不是用户取消，Cancelled 必须为 false");
        Assert.Equal(2, result.FailedFiles);
        Assert.Equal(0, result.CopiedFiles);
        Assert.False(result.Success);
        Assert.Contains("连续失败过多已中止", result.Summary);
        Assert.DoesNotContain("已取消", result.Summary);
    }

    /// <summary>
    /// 对抗点 3：先失败 1 次、再用户取消 —— <c>Cancelled=true</c> 且 <c>AbortedByConsecutiveFailures=false</c>
    /// （失败计数没有达到阈值就不该早停，取消也不该被记成早停）。
    /// </summary>
    [Fact]
    public void UserCancelAfterOneFailure_NotConfusedWithFailureAbort()
    {
        using var ws = new TempWorkspace();
        var names = new[] { "f1.txt", "f2.txt", "f3.txt" };
        foreach (var name in names)
        {
            ws.CreateFile(name, name, BaseTimeUtc);
        }

        Directory.CreateDirectory(ws.TargetDir);
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(ws.TargetDir, name + ".tmp"));
        }

        using var cts = new CancellationTokenSource();
        var copier = new FileCopier
        {
            MaxConsecutiveFailures = 5,
            OnCopyFailed = _ => cts.Cancel()
        };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir, null, null, cts.Token);

        Assert.Equal(1, result.FailedFiles);
        Assert.True(result.Cancelled, "用户取消必须置 Cancelled=true");
        Assert.False(result.AbortedByConsecutiveFailures, "只有 1 次失败（阈值 5），绝不能置早停标志");
        Assert.Contains("已取消", result.Summary);
        Assert.DoesNotContain("连续失败过多已中止", result.Summary);
    }

    // ==================================================================
    // 对抗点 3（边界）：取消请求与「阈值那一刻的失败」同时发生时的优先级
    // ==================================================================

    /// <summary>
    /// 对抗点 3 的边界（特征化测试）：阈值设为 1，第 1 个文件失败的<b>同一时刻</b>调用方请求取消。
    /// <para>实测结论：早停判定在 catch 内先于下一轮循环的取消检查，因此结论是
    /// <c>AbortedByConsecutiveFailures=true</c> + <c>Cancelled=false</c> —— 用户的「取消」在措辞上被早停盖过。</para>
    /// <para>影响评估：只是提示文案层面的差异（两种路径都已经停止复制、都不会留下半截文件），
    /// 不存在数据风险；记录在这里以便评审判断是否需要调整优先级。</para>
    /// </summary>
    [Fact]
    public void CancelRequestedAtTheSameMomentAsThresholdFailure_AbortFlagWins()
    {
        using var ws = new TempWorkspace();
        var names = new[] { "f1.txt", "f2.txt" };
        foreach (var name in names)
        {
            ws.CreateFile(name, name, BaseTimeUtc);
        }

        Directory.CreateDirectory(ws.TargetDir);
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(ws.TargetDir, name + ".tmp"));
        }

        using var cts = new CancellationTokenSource();
        var copier = new FileCopier
        {
            MaxConsecutiveFailures = 1,
            OnCopyFailed = _ => cts.Cancel()
        };

        var result = copier.CopyDirectory(ws.SourceDir, ws.TargetDir, null, null, cts.Token);

        Assert.True(
            result.AbortedByConsecutiveFailures,
            "阈值那一刻的失败会先把 AbortedByConsecutiveFailures 置真（早停判定在 catch 内）");
        Assert.False(
            result.Cancelled,
            "此时不会走到下一轮的取消检查，因此 Cancelled 保持 false —— 已知的措辞级优先级边界");
        Assert.Equal(1, result.FailedFiles);
        Assert.True(cts.IsCancellationRequested, "用例前提：取消确实已经被请求");

        RecordMetric(
            $"[Core] Boundary_CancelVsThreshold aborted={result.AbortedByConsecutiveFailures} " +
            $"cancelled={result.Cancelled} failed={result.FailedFiles}");
    }

    // ==================================================================
    // 对抗点 3（BackupMirrorService 层）：MirrorResult 的两个标志
    // ==================================================================

    /// <summary>构造一个假 U 盘设备（只需要稳定标识，用于计算目标目录）。</summary>
    private static UsbDeviceInfo CreateDevice(string rootPath) => new()
    {
        RootPath = rootPath,
        VolumeLabel = "VERIFY",
        VolumeSerialNumber = "ABCDEF12",
        FileSystem = "NTFS",
        TotalSize = 1L << 30,
        FreeSpace = 1L << 29
    };

    /// <summary>
    /// 对抗点 1 / 3：Mirror 中途取消 → <c>MirrorResult.Cancelled=true</c>、
    /// <c>AbortedByFailureThreshold=false</c>，且不留 .tmp / 不产生截断目标。
    /// </summary>
    [Fact]
    public void Mirror_Cancel_ReportsCancelledOnly()
    {
        using var ws = new TempWorkspace();
        Directory.CreateDirectory(ws.SourceDir);
        CreateSizedFile(Path.Combine(ws.SourceDir, "big.bin"), BigFileBytes);

        var device = CreateDevice(ws.SourceDir);
        var backupRoot = Directory.CreateDirectory(Path.Combine(ws.Root, "backup")).FullName;
        var current = BackupRules.BuildCurrentDirectory(backupRoot, device);

        using var cts = new CancellationTokenSource();
        var holder = new MirrorResult?[1];
        var mirrorTask = Task.Run(() => holder[0] = new BackupMirrorService().Mirror(
            sourceDirectory: ws.SourceDir,
            backupRootDirectory: backupRoot,
            device: device,
            excludeRules: BackupRules.CreateDefaultExcludeRules(),
            historyRetentionDays: 30,
            cancellationToken: cts.Token));

        var observedStaging = WaitForPath(Path.Combine(current, "big.bin.tmp"), TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();
        var finished = mirrorTask.Wait(TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.True(observedStaging, "用例前提未生效：没有观察到 .tmp");
        Assert.True(finished, "取消后 Mirror 必须在 20 秒内返回");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"取消到返回耗时 {stopwatch.ElapsedMilliseconds} ms");

        var mirror = holder[0];
        Assert.NotNull(mirror);
        Assert.True(mirror!.Cancelled, "Mirror 被取消时 MirrorResult.Cancelled 必须为 true");
        Assert.False(mirror.AbortedByFailureThreshold, "取消不是早停");
        Assert.True(mirror.Copy.Cancelled);
        Assert.False(mirror.Copy.AbortedByConsecutiveFailures);
        Assert.False(mirror.Success);

        Assert.False(File.Exists(Path.Combine(current, "big.bin")), "取消后不得留下截断的目标文件");
        AssertNoTempResidue(current);

        RecordMetric(
            $"[Core] Mirror_Cancel observedTmp={observedStaging} stopMs={stopwatch.ElapsedMilliseconds} " +
            $"cancelled={mirror.Cancelled} aborted={mirror.AbortedByFailureThreshold} manifest=\"{mirror.ManifestPath}\"");
    }

    /// <summary>
    /// 对抗点 3：Mirror 连续失败早停 → <c>AbortedByFailureThreshold=true</c>、<c>Cancelled=false</c>。
    /// </summary>
    [Fact]
    public void Mirror_FailureAbort_ReportsThresholdOnly()
    {
        using var ws = new TempWorkspace();
        var names = new[] { "f1.txt", "f2.txt", "f3.txt" };
        foreach (var name in names)
        {
            ws.CreateFile(name, name, BaseTimeUtc);
        }

        var device = CreateDevice(ws.SourceDir);
        var backupRoot = Directory.CreateDirectory(Path.Combine(ws.Root, "backup")).FullName;
        var current = BackupRules.BuildCurrentDirectory(backupRoot, device);
        Directory.CreateDirectory(current);
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(current, name + ".tmp"));
        }

        var mirror = new BackupMirrorService().Mirror(
            sourceDirectory: ws.SourceDir,
            backupRootDirectory: backupRoot,
            device: device,
            excludeRules: BackupRules.CreateDefaultExcludeRules(),
            historyRetentionDays: 30,
            maxConsecutiveFailures: 2);

        Assert.True(mirror.AbortedByFailureThreshold, "连续失败达到阈值时 AbortedByFailureThreshold 必须为 true");
        Assert.False(mirror.Cancelled, "早停不是取消，Cancelled 必须为 false");
        Assert.False(mirror.Copy.Cancelled);
        Assert.Equal(2, mirror.Copy.FailedFiles);
        Assert.False(mirror.Success);

        // 早停时必须提前返回：不写 manifest（避免把不完整的一轮当成一次完成的备份）
        Assert.Equal(string.Empty, mirror.ManifestPath);
        Assert.False(File.Exists(BackupRules.BuildManifestPath(backupRoot, device)));
    }

    // ==================================================================
    // 对抗点 1 / 2（BackupService 层）：CancelCurrentBackup / IsBackupRunning / BackupOutcome.Cancelled
    // ==================================================================

    /// <summary>
    /// 定位已构建的 UsbBackup 程序集。
    /// <para>原因：<c>WinToolBox.Core.Tests</c> 只引用 WinToolBox.Core，没有引用 UsbBackup 项目，
    /// 而本验证只允许新增测试文件（不能改 csproj），因此这里用反射直接驱动真实产品类型。</para>
    /// </summary>
    private static Assembly LoadUsbBackupAssembly()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var candidates = new[]
        {
            Path.Combine(repoRoot, "src", "Tools", "UsbBackup", "bin", "Release", "net8.0-windows", "win-x64", "UsbBackup.dll"),
            Path.Combine(repoRoot, "src", "Tools", "UsbBackup", "bin", "Release", "net8.0-windows", "UsbBackup.dll")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        Assert.True(
            path is not null,
            "找不到 UsbBackup.dll，无法验证 BackupService 层（请先执行：dotnet build WinToolBox.sln -c Release --no-restore -m:1）。已尝试：" +
            string.Join(" | ", candidates));

        return Assembly.LoadFrom(path!);
    }

    /// <summary>
    /// 对抗点 1 / 2：真实 <c>BackupService</c> 在备份进行中被 <c>CancelCurrentBackup()</c> 取消：
    /// 必须数秒内停止、返回 <c>Cancelled=true</c> 的 BackupOutcome（且 Copy 里带真实统计）、
    /// 结束后 <c>IsBackupRunning</c> 归 false、不留 .tmp / 不产生截断目标。
    /// <para>前置条件说明：本机只有 D: 一个可写卷，而 <c>BackupService</c> 会拒绝「源与目标同卷」。
    /// 这里给源目录加 <c>\\?\</c> 前缀（同一份文件，只是路径形态不同），只为绕开这一条前置检查，
    /// 不改变被测的取消链路。</para>
    /// </summary>
    [Fact]
    public void BackupService_CancelCurrentBackup_StopsRunningBackupAndReturnsCancelledOutcome()
    {
        var assembly = LoadUsbBackupAssembly();
        var serviceType = assembly.GetType("WinToolBox.Tools.UsbBackup.BackupService", throwOnError: true)!;
        var outcomeType = assembly.GetType("WinToolBox.Tools.UsbBackup.BackupOutcome", throwOnError: true)!;

        using var ws = new TempWorkspace();
        var sourceDirectory = Directory.CreateDirectory(ws.SourceDir).FullName;
        CreateSizedFile(Path.Combine(sourceDirectory, "big.bin"), BigFileBytes);
        ws.CreateFile("small.txt", "SMALL", BaseTimeUtc);

        var backupRoot = Directory.CreateDirectory(Path.Combine(ws.Root, "backup")).FullName;
        var configManager = new ConfigManager(Path.Combine(ws.Root, "cfg", "config.json"));
        configManager.Save(new BackupConfig { BackupTargetDirectory = backupRoot, HistoryRetentionDays = 30 });

        var logger = new Logger(Path.Combine(ws.Root, "logs"), 7);
        var notifier = new RecordingNotifier();
        var service = Activator.CreateInstance(serviceType, configManager, logger, notifier, new UsbDetector(logger))!;

        var isRunningProperty = serviceType.GetProperty("IsBackupRunning")!;
        var cancelMethod = serviceType.GetMethod("CancelCurrentBackup")!;
        var backupDeviceMethod = serviceType.GetMethod("BackupDevice")!;

        Assert.False((bool)isRunningProperty.GetValue(service)!, "空闲时 IsBackupRunning 必须为 false");
        Assert.False((bool)cancelMethod.Invoke(service, null)!, "没有备份在跑时 CancelCurrentBackup 必须返回 false");

        var device = CreateDevice(@"\\?\" + sourceDirectory);
        var current = BackupRules.BuildCurrentDirectory(backupRoot, device);

        object? outcome = null;
        var backupTask = Task.Run(() => outcome = backupDeviceMethod.Invoke(service, new object?[] { device, CancellationToken.None }));

        var observedStaging = WaitForPath(Path.Combine(current, "big.bin.tmp"), TimeSpan.FromSeconds(30));
        Assert.True((bool)isRunningProperty.GetValue(service)!, "备份运行中 IsBackupRunning 必须为 true");

        var stopwatch = Stopwatch.StartNew();
        var accepted = (bool)cancelMethod.Invoke(service, null)!;
        var finished = backupTask.Wait(TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.True(observedStaging, "用例前提未生效：没有观察到 .tmp（无法证明取消发生在 I/O 中途）");
        Assert.True(accepted, "备份运行中 CancelCurrentBackup() 必须返回 true");
        Assert.True(finished, "取消后备份任务必须在 20 秒内结束");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"取消到结束耗时 {stopwatch.ElapsedMilliseconds} ms，应在数秒内");

        Assert.NotNull(outcome);
        Assert.True((bool)outcomeType.GetProperty("Cancelled")!.GetValue(outcome)!, "被取消的备份必须返回 Cancelled=true");
        Assert.False((bool)outcomeType.GetProperty("Success")!.GetValue(outcome)!);
        Assert.False((bool)outcomeType.GetProperty("Skipped")!.GetValue(outcome)!);
        Assert.Equal(current, (string)outcomeType.GetProperty("TargetDirectory")!.GetValue(outcome)!);

        var copy = outcomeType.GetProperty("Copy")!.GetValue(outcome);
        Assert.NotNull(copy);
        Assert.True((bool)copy!.GetType().GetProperty("Cancelled")!.GetValue(copy)!);

        Assert.False((bool)isRunningProperty.GetValue(service)!, "备份结束后 IsBackupRunning 必须归 false");
        Assert.False((bool)cancelMethod.Invoke(service, null)!, "备份结束后 CancelCurrentBackup 必须返回 false");
        Assert.False(File.Exists(Path.Combine(current, "big.bin")), "取消后不得留下截断的目标文件");
        AssertNoTempResidue(current);

        // 「已执行清单」必须等于真实产物：current 里的文件数 == Copy.CopiedFiles
        var copiedFiles = (int)copy.GetType().GetProperty("CopiedFiles")!.GetValue(copy)!;
        var producedFiles = Directory.GetFiles(current, "*", SearchOption.AllDirectories).Length;
        Assert.Equal(producedFiles, copiedFiles);

        RecordMetric(
            $"[Core] BackupService_CancelCurrentBackup observedTmp={observedStaging} accepted={accepted} " +
            $"stopMs={stopwatch.ElapsedMilliseconds} copied={copiedFiles} produced={producedFiles} " +
            $"cancelled={(bool)outcomeType.GetProperty("Cancelled")!.GetValue(outcome)!} " +
            $"isRunningAfter={(bool)isRunningProperty.GetValue(service)!}");
    }

    /// <summary>
    /// 对抗点 1 / 2：外部令牌（MainForm 的 <c>_backupCts</c> 走的就是这条路径）取消同样能中止备份，
    /// 并且返回 Cancelled 结果 —— 证明 <c>BeginBackupScope</c> 的 linked token 真的透传到了 I/O。
    /// </summary>
    [Fact]
    public void BackupService_ExternalTokenCancellation_AlsoStopsBackup()
    {
        var assembly = LoadUsbBackupAssembly();
        var serviceType = assembly.GetType("WinToolBox.Tools.UsbBackup.BackupService", throwOnError: true)!;
        var outcomeType = assembly.GetType("WinToolBox.Tools.UsbBackup.BackupOutcome", throwOnError: true)!;

        using var ws = new TempWorkspace();
        var sourceDirectory = Directory.CreateDirectory(ws.SourceDir).FullName;
        CreateSizedFile(Path.Combine(sourceDirectory, "big.bin"), BigFileBytes);

        var backupRoot = Directory.CreateDirectory(Path.Combine(ws.Root, "backup")).FullName;
        var configManager = new ConfigManager(Path.Combine(ws.Root, "cfg", "config.json"));
        configManager.Save(new BackupConfig { BackupTargetDirectory = backupRoot, HistoryRetentionDays = 30 });

        var logger = new Logger(Path.Combine(ws.Root, "logs"), 7);
        var service = Activator.CreateInstance(serviceType, configManager, logger, new RecordingNotifier(), new UsbDetector(logger))!;
        var backupDeviceMethod = serviceType.GetMethod("BackupDevice")!;

        var device = CreateDevice(@"\\?\" + sourceDirectory);
        var current = BackupRules.BuildCurrentDirectory(backupRoot, device);

        using var cts = new CancellationTokenSource();
        object? outcome = null;
        var backupTask = Task.Run(() => outcome = backupDeviceMethod.Invoke(service, new object?[] { device, cts.Token }));

        var observedStaging = WaitForPath(Path.Combine(current, "big.bin.tmp"), TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();
        var finished = backupTask.Wait(TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.True(observedStaging, "用例前提未生效：没有观察到 .tmp");
        Assert.True(finished, "外部令牌取消后备份任务必须结束");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"外部取消到结束耗时 {stopwatch.ElapsedMilliseconds} ms");

        Assert.NotNull(outcome);
        Assert.True((bool)outcomeType.GetProperty("Cancelled")!.GetValue(outcome)!, "外部令牌取消也必须返回 Cancelled=true");
        AssertNoTempResidue(current);

        RecordMetric($"[Core] BackupService_ExternalTokenCancel observedTmp={observedStaging} stopMs={stopwatch.ElapsedMilliseconds}");
    }

    /// <summary>
    /// 对抗点 3（端到端）：目标盘写不进去（模拟「U 盘接触不良 / 目标写满 / 权限全无」）时，
    /// 用户看到的结论必须是「连续失败已中止」，绝不能是「已取消」：
    /// <c>BackupOutcome.Cancelled=false</c>、<c>Success=false</c>、<c>Copy.AbortedByConsecutiveFailures=true</c>。
    /// <para>制造失败：把 current 下每个目标文件的 <c>{名字}.tmp</c> 用同名目录占位，
    /// 复制写暂存文件必然失败；20 个文件正好触发默认阈值
    /// <see cref="BackupMirrorService.ConsecutiveFailureAbortThreshold"/>。</para>
    /// </summary>
    [Fact]
    public void BackupService_ConsecutiveFailures_ReportsAbortNotCancel()
    {
        var assembly = LoadUsbBackupAssembly();
        var serviceType = assembly.GetType("WinToolBox.Tools.UsbBackup.BackupService", throwOnError: true)!;
        var outcomeType = assembly.GetType("WinToolBox.Tools.UsbBackup.BackupOutcome", throwOnError: true)!;

        using var ws = new TempWorkspace();
        var sourceDirectory = Directory.CreateDirectory(ws.SourceDir).FullName;

        var names = Enumerable.Range(1, BackupMirrorService.ConsecutiveFailureAbortThreshold)
            .Select(static index => $"f{index:00}.txt")
            .ToArray();

        foreach (var name in names)
        {
            ws.CreateFile(name, "CONTENT-" + name, BaseTimeUtc);
        }

        var backupRoot = Directory.CreateDirectory(Path.Combine(ws.Root, "backup")).FullName;
        var configManager = new ConfigManager(Path.Combine(ws.Root, "cfg", "config.json"));
        configManager.Save(new BackupConfig { BackupTargetDirectory = backupRoot, HistoryRetentionDays = 30 });

        var logger = new Logger(Path.Combine(ws.Root, "logs"), 7);
        var service = Activator.CreateInstance(serviceType, configManager, logger, new RecordingNotifier(), new UsbDetector(logger))!;
        var backupDeviceMethod = serviceType.GetMethod("BackupDevice")!;

        var device = CreateDevice(@"\\?\" + sourceDirectory);
        var current = BackupRules.BuildCurrentDirectory(backupRoot, device);
        Directory.CreateDirectory(current);
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(current, name + ".tmp"));
        }

        var outcome = backupDeviceMethod.Invoke(service, new object?[] { device, CancellationToken.None });
        Assert.NotNull(outcome);

        var copy = outcomeType.GetProperty("Copy")!.GetValue(outcome);
        Assert.NotNull(copy);

        Assert.False((bool)outcomeType.GetProperty("Cancelled")!.GetValue(outcome)!, "写盘失败不是用户取消：Cancelled 必须为 false");
        Assert.False((bool)outcomeType.GetProperty("Success")!.GetValue(outcome)!);
        Assert.False((bool)outcomeType.GetProperty("Skipped")!.GetValue(outcome)!);
        Assert.True((bool)copy!.GetType().GetProperty("AbortedByConsecutiveFailures")!.GetValue(copy)!);
        Assert.False((bool)copy.GetType().GetProperty("Cancelled")!.GetValue(copy)!);

        var message = (string)outcomeType.GetProperty("Message")!.GetValue(outcome)!;
        Assert.Contains("已提前中止本轮备份", message);
        Assert.Contains("连续失败", message);

        RecordMetric(
            $"[Core] BackupService_ConsecutiveFailureAbort files={names.Length} " +
            $"failed={copy.GetType().GetProperty("FailedFiles")!.GetValue(copy)} " +
            $"abortedByConsecutiveFailures={(bool)copy.GetType().GetProperty("AbortedByConsecutiveFailures")!.GetValue(copy)!} " +
            $"outcomeCancelled={(bool)outcomeType.GetProperty("Cancelled")!.GetValue(outcome)!}");
    }
}
