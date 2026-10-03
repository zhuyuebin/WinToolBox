using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WinToolBox.Core;
using WinToolBox.Core.Services;

namespace WinToolBox.Core.Tests;

/// <summary>
/// 阶段五（P1-2 / P1-4 / P1-5 / P1-16 / P1-17 / P1-18）的**独立对抗性复核**用例。
/// </summary>
/// <remarks>
/// <para>本文件由独立验证者编写，只做「挑错」：既验证修复生效，也把**未修复**的残留问题固定成
/// 可重复的失败/特征化用例，便于 Lead 决策。</para>
/// <para>写作用域：只新增本文件，未修改任何产品代码、既有测试、README 与 workflow。</para>
/// <para>环境限制（诚实声明）：
/// ① 会话内运行器 <c>.tmp\runxunit</c> 的 runtimeconfig 只带 <c>Microsoft.NETCore.App</c>，
/// 加载 <c>System.Windows.Forms</c> 会直接 FileNotFoundException 并终止整个测试进程；
/// 因此本文件**不引用任何 WinForms 类型**，P1-2 在套件内只验证「上下文不流向后台线程 +
/// Notifier 构造时捕获 + Program.cs 语句顺序」；真实的 <c>WindowsFormsSynchronizationContext</c>
/// 消息循环派发与托盘气泡调度改在套件外的临时探针里验证（.tmp\logstress 的 winforms-ctx /
/// notifier-post 两个模式，见验证报告），结论不写进套件以免污染 Lead 的最终验收命令。
/// ② GitHub Actions 表达式无法在本机求值，P1-16 只能抽取步骤逻辑离线模拟。
/// ③ 托盘气泡的真正显示无法验证（受限上下文下 Windows 会静默拒绝注册托盘图标）。</para>
/// </remarks>
public sealed class Stage5VerificationTests
{
    /// <summary>合法的日志行：<c>2026-10-03 22:01:27.969 [INFO ] TAG-0001</c>（级别标签含尾随空格）。</summary>
    private static readonly Regex LogLinePattern = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(INFO |WARN |ERROR)\] [A-Za-z0-9_\-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string FindRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>反射加载已构建的 UsbBackup 程序集（internal 类型无法直接引用）。</summary>
    private static Assembly LoadUsbBackupAssembly()
    {
        var repoRoot = FindRepoRoot();
        var candidates = new[]
        {
            Path.Combine(repoRoot, "src", "Tools", "UsbBackup", "bin", "Release", "net8.0-windows", "win-x64", "UsbBackup.dll"),
            Path.Combine(repoRoot, "src", "Tools", "UsbBackup", "bin", "Release", "net8.0-windows", "UsbBackup.dll")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        Assert.True(path is not null, "找不到 UsbBackup.dll，请先执行 dotnet build WinToolBox.sln -c Release -m:1");

        return Assembly.LoadFrom(path!);
    }

    private static (string State, string? Description, string? FailureReason) InvokeClassify(int? rid)
    {
        var type = LoadUsbBackupAssembly()
            .GetType("WinToolBox.Tools.UsbBackup.ProcessIntegrity", throwOnError: true)!;
        var classify = type.GetMethod("Classify", BindingFlags.Public | BindingFlags.Static)!;

        var args = new object?[] { rid, null, null };
        var result = classify.Invoke(null, args);

        return (result!.ToString()!, args[1] as string, args[2] as string);
    }

    // ==================================================================
    // P1-4：并发写日志是否真的不丢行 / 重试是否真的生效 / 失败是否可见
    // ==================================================================

    /// <summary>
    /// 多实例 + 多线程 + 多轮：**同一进程内**两个以上 <see cref="Logger"/> 实例写同一个当天文件。
    /// 旧实现用 <c>FileShare.Read</c>，会互相抢锁抛异常并被吞掉 → 丢行。
    /// 这里每轮 2400 行、跑 3 轮，逐行断言：行数精确、无重复、无撕裂行。
    /// </summary>
    [Fact]
    public void Logger_MultiInstanceMultiThread_MultiRound_NoLineLostOrTorn()
    {
        using var ws = new TempWorkspace();
        var logDirectory = Path.Combine(ws.Root, "concurrent-logs");
        Directory.CreateDirectory(logDirectory);

        const int instanceCount = 4;
        const int threadsPerInstance = 4;
        const int messagesPerThread = 150;
        const int rounds = 3;
        var expectedTotal = instanceCount * threadsPerInstance * messagesPerThread * rounds;

        var loggers = Enumerable.Range(0, instanceCount)
            .Select(i => new Logger(logDirectory, Logger.DefaultRetentionDays, "inst" + i))
            .ToArray();

        for (var round = 1; round <= rounds; round++)
        {
            var tasks = new List<Task>();
            for (var i = 0; i < instanceCount; i++)
            {
                for (var t = 0; t < threadsPerInstance; t++)
                {
                    var logger = loggers[i];
                    var prefix = $"R{round}I{i}T{t}";
                    tasks.Add(Task.Run(() =>
                    {
                        for (var m = 0; m < messagesPerThread; m++)
                        {
                            logger.Info($"{prefix}-{m:D4}");
                        }
                    }));
                }
            }

            Assert.True(Task.WaitAll(tasks.ToArray(), TimeSpan.FromMinutes(3)), $"第 {round} 轮写入未在 3 分钟内完成");
            Assert.All(tasks, task => Assert.True(task.IsCompletedSuccessfully, "写入任务出现异常"));
            Assert.All(loggers, logger =>
                Assert.False(logger.HasWriteFailure, $"Logger 报告写入失败（{logger.FailedWriteCount} 条）：{logger.LastError}"));
        }

        // 按天切割：跨零点时可能落到两个文件，因此统计目录下全部日志
        var allLines = Directory.EnumerateFiles(logDirectory, Logger.FileNamePrefix + "*.log")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => File.ReadAllLines(path, Encoding.UTF8))
            .ToArray();

        Assert.Equal(expectedTotal, allLines.Length);
        Assert.Equal(allLines.Length, allLines.Distinct(StringComparer.Ordinal).Count());

        var torn = allLines.Where(line => !LogLinePattern.IsMatch(line)).ToArray();
        Assert.Empty(torn);
    }

    /// <summary>订阅者（界面）抛异常不能影响日志本身：那一行必须照样落盘。</summary>
    [Fact]
    public void Logger_SubscriberThrowing_DoesNotLoseTheLine()
    {
        using var ws = new TempWorkspace();
        var logDirectory = Path.Combine(ws.Root, "logs");
        var logger = new Logger(logDirectory, Logger.DefaultRetentionDays, "tool");

        logger.EntryWritten += (_, _) => throw new InvalidOperationException("订阅者炸了");
        logger.Info("订阅者抛异常也要写盘");

        Assert.False(logger.HasWriteFailure);
        var lines = File.ReadAllLines(logger.CurrentLogFilePath, Encoding.UTF8);
        Assert.Single(lines);
        Assert.Contains("订阅者抛异常也要写盘", lines[0]);
    }

    /// <summary>
    /// 重试是否真的覆盖「文件被短暂独占」：先以 <c>FileShare.None</c> 独占日志文件，
    /// 让前两次写入撞锁，再在第 3 次尝试前释放 → 该行最终必须成功落盘且不计数失败。
    /// </summary>
    [Fact]
    public void Logger_TransientExclusiveLock_IsRetriedAndLineLands()
    {
        using var ws = new TempWorkspace();
        var logDirectory = Path.Combine(ws.Root, "retry-logs");
        Directory.CreateDirectory(logDirectory);

        var logger = new Logger(logDirectory, Logger.DefaultRetentionDays, "retry");
        var path = logger.CurrentLogFilePath;

        var holder = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var started = new ManualResetEventSlim(false);
        Exception? unexpected = null;

        var writer = Task.Run(() =>
        {
            started.Set();
            try
            {
                logger.Info("transient-lock-line");
            }
            catch (Exception ex)
            {
                unexpected = ex;
            }
        });

        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "写入任务未能启动");
            // 重试节奏是 0 / 50 / 100 / 150ms，这里 80ms 释放，第 3 次尝试必然成功
            Thread.Sleep(80);
        }
        finally
        {
            holder.Dispose();
        }

        Assert.True(writer.Wait(TimeSpan.FromSeconds(10)), "写入任务未结束");
        Assert.Null(unexpected);
        Assert.False(logger.HasWriteFailure, $"短暂独占应被重试化解，但失败了：{logger.LastError}");
        Assert.Contains("transient-lock-line", File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// 独占锁一直不放（&gt; 3 次重试窗口）时：不得再「静默丢行」——必须如实计入
    /// <see cref="Logger.FailedWriteCount"/>，并把「已重试 3 次」写进异常消息。
    /// </summary>
    [Fact]
    public void Logger_PersistentExclusiveLock_ReportsFailureInsteadOfSilentLoss()
    {
        using var ws = new TempWorkspace();
        var logDirectory = Path.Combine(ws.Root, "blocked-logs");
        Directory.CreateDirectory(logDirectory);

        var logger = new Logger(logDirectory, Logger.DefaultRetentionDays, "blocked");
        var path = logger.CurrentLogFilePath;

        var holder = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var started = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            started.Set();
            logger.Info("must-not-silently-vanish");
        });

        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "写入任务未能启动");
            // 3 次重试共约 150ms，这里独占 400ms 确保 4 次尝试全部失败
            Thread.Sleep(400);
        }
        finally
        {
            holder.Dispose();
        }

        Assert.True(writer.Wait(TimeSpan.FromSeconds(10)), "写入任务未结束");

        Assert.True(logger.HasWriteFailure, "写入彻底失败时必须能被调用方察觉");
        Assert.Equal(1, logger.FailedWriteCount);
        var error = Assert.IsType<IOException>(logger.LastError);
        Assert.Contains("已重试", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-silently-vanish", File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// 失败后的恢复语义：后续写入成功会把 <see cref="Logger.LastError"/> 清空，
    /// 但 <see cref="Logger.FailedWriteCount"/> 只增不减（否则「本次运行丢了几条日志」会被抹掉）。
    /// </summary>
    [Fact]
    public void Logger_AfterRecovery_FailureCountStaysButLastErrorClears()
    {
        using var ws = new TempWorkspace();
        var logDirectory = Path.Combine(ws.Root, "recovery-logs");
        Directory.CreateDirectory(logDirectory);

        var logger = new Logger(logDirectory, Logger.DefaultRetentionDays, "recovery");
        var path = logger.CurrentLogFilePath;

        var holder = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var writer = Task.Run(() => logger.Info("failed-line"));

        try
        {
            Assert.True(writer.Wait(TimeSpan.FromSeconds(10)), "写入任务未结束");
            Assert.Equal(1, logger.FailedWriteCount);
        }
        finally
        {
            holder.Dispose();
        }

        logger.Info("recovered-line");

        Assert.Equal(1, logger.FailedWriteCount);
        Assert.True(logger.HasWriteFailure, "失败过就必须一直可见，直到用户看到提示");
        Assert.Null(logger.LastError);
        Assert.Contains("recovered-line", File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// 两个工具的日志目录必须真的分开，且工具名不能借非法字符 / 相对路径逃出 logs 根目录。
    /// </summary>
    [Fact]
    public void Logger_ToolDirectories_AreSeparatedAndCannotEscapeLogsRoot()
    {
        var logsRoot = Path.GetFullPath(AppPaths.LogDirectory);
        var prefix = logsRoot + Path.DirectorySeparatorChar;

        var usb = Path.GetFullPath(AppPaths.LogDirectoryFor("UsbBackup"));
        var fileMaster = Path.GetFullPath(AppPaths.LogDirectoryFor("FileMaster"));

        Assert.NotEqual(usb, fileMaster, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(prefix, usb, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(prefix, fileMaster, StringComparison.OrdinalIgnoreCase);

        // 两个工具当天日志文件必须落在不同路径（P1-4 的核心目标）
        Assert.NotEqual(
            Path.GetFullPath(new Logger(usb, 30, "UsbBackup").CurrentLogFilePath),
            Path.GetFullPath(new Logger(fileMaster, 30, "FileMaster").CurrentLogFilePath),
            StringComparer.OrdinalIgnoreCase);

        // 空工具名回落共享目录（兼容旧行为）
        Assert.Equal(logsRoot, Path.GetFullPath(AppPaths.LogDirectoryFor(null)), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(logsRoot, Path.GetFullPath(AppPaths.LogDirectoryFor("   ")), StringComparer.OrdinalIgnoreCase);

        // 恶意工具名不得逃出 logs 根（允许清洗后回落共享目录，但不允许上跳）
        foreach (var evil in new[] { "..", @"..\..\Windows", "../../etc", "a/b", "a:b", @"..\..\..\..\Users" })
        {
            var resolved = Path.GetFullPath(AppPaths.LogDirectoryFor(evil));
            var inside = resolved.Equals(logsRoot, StringComparison.OrdinalIgnoreCase)
                         || resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            Assert.True(inside, $"工具名「{evil}」逃出了 logs 根目录：{resolved}");
        }
    }

    /// <summary>
    /// **特征化用例（记录已知缺陷）**：<see cref="Logger"/> 的写入策略是
    /// <c>FileMode.Append + FileAccess.Write + FileShare.ReadWrite</c>，
    /// 句柄在**打开时**定位到文件尾，而不是由操作系统在写入时原子追加。
    /// 因此「A 打开 → B 追加 → A 写入」会让 A 覆盖 B 刚写入的内容，且**没有任何异常**。
    /// </summary>
    /// <remarks>
    /// 同进程内 Logger 用按路径共享的静态锁兜住了这种情况；但那是**进程内**锁，
    /// 跨进程（同目录下同时跑两个实例）依然会静默覆盖丢行。
    /// 本用例把机制固定下来：如果哪天真做成原子追加，本用例会失败，此时应同步更新结论。
    /// </remarks>
    [Fact]
    public void Logger_AppendStrategy_HasStalePositionOverwriteRiskAcrossProcesses()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "stale-append.log");
        File.WriteAllText(path, "seed" + Environment.NewLine, new UTF8Encoding(false));

        var lineB = new string('B', 40);
        var lineA = new string('A', 40);

        // 与 Logger.AppendWithRetry 完全相同的打开参数
        var handleA = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
        var handleB = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);

        var payloadB = Encoding.UTF8.GetBytes(lineB + Environment.NewLine);
        handleB.Write(payloadB, 0, payloadB.Length);
        handleB.Flush();
        handleB.Dispose();

        var payloadA = Encoding.UTF8.GetBytes(lineA + Environment.NewLine);
        handleA.Write(payloadA, 0, payloadA.Length);
        handleA.Flush();
        handleA.Dispose();

        var text = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains(lineA, text, StringComparison.Ordinal);
        // 现状：B 的行被 A 覆盖（无异常、无日志）——这是跨进程并发写同一文件的残余风险
        Assert.DoesNotContain(lineB, text, StringComparison.Ordinal);
    }

    // ==================================================================
    // P1-5：模板保存/删除失败必须可见
    // ==================================================================

    /// <summary>
    /// 目标文件只读时（<c>File.Move(..., overwrite: true)</c> 会以 UnauthorizedAccessException 失败）：
    /// <see cref="TemplateManager.SaveTemplate"/> 必须返回 false，磁盘内容保持不变，
    /// 同时把「内存已改、磁盘未改」的不一致如实暴露出来。
    /// </summary>
    [Fact]
    public void SaveTemplate_WhenTargetIsReadOnly_ReturnsFalseAndExposesMemoryDiskMismatch()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "templates.json");
        var manager = new TemplateManager(path);

        Assert.True(manager.SaveTemplate("只读目标", "-old"));
        var onDisk = File.ReadAllText(path, Encoding.UTF8);

        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var saved = manager.SaveTemplate("只读目标", "-new");

            Assert.False(saved, "写盘失败必须返回 false");
            Assert.NotNull(manager.LastPersistError);
            Assert.False(string.IsNullOrWhiteSpace(manager.LastPersistError!.Message));

            // 磁盘没变
            Assert.Equal(onDisk, File.ReadAllText(path, Encoding.UTF8));

            // 内存变了 → 调用方拿到的「不一致」是真实的，UI 必须提示「重启会丢失」
            Assert.Equal("-new", manager.GetTemplate("只读目标"));

            // 新实例（等价于重启）看到的是旧内容 → 证明「重启会丢失」这句话是准确的
            var reloaded = new TemplateManager(path);
            Assert.Equal("-old", reloaded.GetTemplate("只读目标"));
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// 删除模板时写盘失败（目标只读 → <c>File.Move(overwrite:true)</c> 抛
    /// <c>UnauthorizedAccessException</c>）：必须返回 false、磁盘上模板仍在，
    /// 且**内存也要回滚**（两边一致）。
    /// </summary>
    /// <remarks>
    /// 契约变更说明（Lead 在 verifier5 报告后修改）：原实现让内存与磁盘不一致
    /// （内存已删、磁盘还在），界面于是提示「已删除」而重启后模板「复活」。
    /// 现在 <c>DeleteTemplate</c> 在写盘失败时**回滚内存中的删除**，两边都保持「未删除」，
    /// 界面据返回值如实提示失败。因此本用例的断言从「内存已移除」改为「内存仍保留」。
    /// <para>失败方式：Windows 上 <c>File.Move(overwrite: true)</c> **无法覆盖只读目标文件**
    /// （与早期猜测相反，已由本用例实测固定）。</para>
    /// </remarks>
    [Fact]
    public void DeleteTemplate_WhenPersistFails_ReturnsFalseAndRollsBackMemory()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "delete-templates.json");
        var manager = new TemplateManager(path);

        Assert.True(manager.SaveTemplate("待删除", "-a"));
        var onDisk = File.ReadAllText(path, Encoding.UTF8);

        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        try
        {
            var deleted = manager.DeleteTemplate("待删除");

            Assert.False(deleted, "无法落盘时不能报告删除成功");

            // 内存已回滚 → 与磁盘一致（都还「存在」）
            Assert.True(manager.Contains("待删除"), "写盘失败必须回滚内存中的删除，避免内存/磁盘不一致");
            Assert.Equal("-a", manager.GetTemplate("待删除"));

            // 磁盘内容逐字节未变
            Assert.Equal(onDisk, File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// API 歧义（供 Lead 决策）：<c>DeleteTemplate</c> 在「模板不存在」与「写盘失败」两种情况下
    /// 都返回 false，调用方无法区分。当前 MainForm 先做 <c>Contains</c> 预检，所以线上行为可接受，
    /// 但任何新调用点都不能只看 bool。
    /// </summary>
    [Fact]
    public void DeleteTemplate_MissingTemplateAndPersistFailure_ReturnTheSameFalse()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "ambiguous.json");
        var manager = new TemplateManager(path);

        Assert.False(manager.DeleteTemplate("不存在"));
        Assert.Null(manager.LastPersistError); // 不是写盘失败，但没有其它信号能区分

        Assert.False(manager.DeleteTemplate(null));
        Assert.False(manager.DeleteTemplate("   "));
    }

    /// <summary>
    /// 失败之后再成功（同一个管理器）：<see cref="TemplateManager.LastPersistError"/> 只写不清，
    /// 成功之后仍留着上一次的陈旧异常。当前 MainForm 只在 <c>saved == false</c> 分支读它，
    /// 所以没有用户可见影响；但任何「只看 LastPersistError 不看返回值」的新代码都会误报。
    /// </summary>
    [Fact]
    public void SaveTemplate_AfterFailureThenSuccess_SucceedsButKeepsStaleError()
    {
        using var ws = new TempWorkspace();
        var directory = Path.Combine(ws.Root, "sub");
        var path = Path.Combine(directory, "templates.json");

        // 先用一个「文件」占住目录路径 → EnsureDirectory 必然失败
        File.WriteAllText(directory, "not a directory");
        var manager = new TemplateManager(path);

        Assert.False(manager.SaveTemplate("X", "-x"));
        var staleError = manager.LastPersistError;
        Assert.NotNull(staleError);

        // 移走阻塞文件后重新保存：这次必须真的成功并落盘
        File.Delete(directory);
        Assert.True(manager.SaveTemplate("X", "-x2"));
        Assert.Equal("-x2", new TemplateManager(path).GetTemplate("X"));

        // 特征化：成功路径不清空陈旧错误（低危，UI 只在失败分支读取）
        Assert.Same(staleError, manager.LastPersistError);
    }

    /// <summary>正常路径回归：保存 → 立即落盘 → 新实例能读回 → 无 .tmp 残留。</summary>
    [Fact]
    public void SaveTemplate_NormalPath_PersistsImmediatelyAndLeavesNoTempFile()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "normal.json");
        var manager = new TemplateManager(path);

        Assert.True(manager.SaveTemplate("正常", "-a\r\n--b"));
        var first = File.ReadAllText(path, Encoding.UTF8);

        // 覆盖同一模板也要成功
        Assert.True(manager.SaveTemplate("正常", "-c"));
        var reloaded = new TemplateManager(path);

        Assert.Equal("-c", reloaded.GetTemplate("正常"));
        Assert.NotEqual(first, File.ReadAllText(path, Encoding.UTF8));
        Assert.False(File.Exists(path + ".tmp"), "成功路径不应留下 .tmp");
    }

    /// <summary>空模板名没有「静默成功」的余地：必须抛参数异常，且不能污染内存状态。</summary>
    [Fact]
    public void SaveTemplate_EmptyName_ThrowsAndDoesNotMutateMemory()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "empty-name.json");
        var manager = new TemplateManager(path);
        var before = manager.Count;

        Assert.Throws<ArgumentException>(() => manager.SaveTemplate("   ", "-a"));
        Assert.Throws<ArgumentNullException>(() => manager.SaveTemplate("ok", null!));
        Assert.Equal(before, manager.Count);
        Assert.False(File.Exists(path));
    }

    /// <summary>内置模板首次落盘（成功路径）仍应成功，且失败时只记日志不抛异常。</summary>
    [Fact]
    public void EnsureDefaults_WritablePath_WritesFileAndDoesNotThrow()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "defaults.json");
        var manager = new TemplateManager(path);

        // 模板文件不存在时 Reload 已把内置模板放进内存，因此「内存新增数」是 0，
        // 但由于 !Exists，EnsureDefaults 仍会落盘一次（这才是本用例要验证的行为）。
        var added = manager.EnsureDefaults();

        Assert.Equal(0, added);
        Assert.True(File.Exists(path), "模板文件缺失时 EnsureDefaults 必须把它写到磁盘");
        Assert.Null(manager.LastPersistError);
        Assert.NotNull(manager.GetTemplate(TemplateManager.WebProjectTemplateName));
        Assert.NotNull(manager.GetTemplate(TemplateManager.PythonProjectTemplateName));
        Assert.False(File.Exists(path + ".tmp"), "成功路径不应留下 .tmp");

        // 落盘内容可被新实例读回（两个内置模板都在）
        var reloaded = new TemplateManager(path);
        Assert.Equal(2, reloaded.Count);
    }

    // ==================================================================
    // P1-18：完整性检查必须三态，且 Unknown 必须带原因
    // ==================================================================

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(0x0000, "Restricted")]
    [InlineData(0x1000, "Restricted")]
    [InlineData(0x1FFF, "Restricted")] // 中完整性边界下方
    [InlineData(0x2000, "Normal")]
    [InlineData(0x2001, "Normal")]     // 中完整性边界上方
    [InlineData(0x3000, "Normal")]
    [InlineData(0x4000, "Normal")]
    [InlineData(-1, "Restricted")]     // 异常负值必须 fail-safe（当作受限），不能 fail-open
    public void ProcessIntegrity_Classify_MapsRidBoundaries(int? rid, string expected)
    {
        var (state, description, failureReason) = InvokeClassify(rid);

        Assert.Equal(expected, state);

        if (expected == "Unknown")
        {
            Assert.False(string.IsNullOrWhiteSpace(failureReason), "Unknown 必须带 failureReason");
        }
        else
        {
            Assert.Null(failureReason);
            Assert.False(string.IsNullOrWhiteSpace(description), $"{state} 必须带可读描述");
        }
    }

    /// <summary>
    /// 本机真实返回值（沙箱里预期为 Restricted/Low）：无论落在哪一态，都必须自洽且可重复。
    /// 这条用例的价值在于：证明 <c>Check</c> 在本机真的能跑通（而不是永远 Unknown 蒙混）。
    /// </summary>
    [Fact]
    public void ProcessIntegrity_Check_IsSelfConsistentAndStable()
    {
        var type = LoadUsbBackupAssembly()
            .GetType("WinToolBox.Tools.UsbBackup.ProcessIntegrity", throwOnError: true)!;
        var check = type.GetMethod("Check", BindingFlags.Public | BindingFlags.Static)!;

        string Run(out string description, out string? reason)
        {
            var args = new object?[] { null, null };
            var state = check.Invoke(null, args)!.ToString()!;
            description = (string)args[0]!;
            reason = args[1] as string;
            return state;
        }

        var first = Run(out var description1, out var reason1);
        var second = Run(out var description2, out var reason2);

        Assert.Contains(first, new[] { "Normal", "Restricted", "Unknown" });
        Assert.Equal(first, second);
        Assert.Equal(description1, description2);

        if (first == "Unknown")
        {
            Assert.False(string.IsNullOrWhiteSpace(reason1), "Unknown 必须给用户一个原因");
            Assert.False(string.IsNullOrWhiteSpace(reason2));
        }
        else
        {
            Assert.True(string.IsNullOrWhiteSpace(reason1), "非 Unknown 不应带 failureReason");
            Assert.False(string.IsNullOrWhiteSpace(description1));
        }

        // 兼容重载只在 Restricted 时为 true，Unknown 时不得假装「正常」
        var isRestricted = type.GetMethod("IsRestricted", BindingFlags.Public | BindingFlags.Static)!;
        var restrictedArgs = new object?[] { null };
        var restricted = (bool)isRestricted.Invoke(null, restrictedArgs)!;
        Assert.Equal(first == "Restricted", restricted);
    }

    // ==================================================================
    // P1-2：托盘 UI 同步上下文（能 headless 验证的部分）
    // ==================================================================

    /// <summary>空同步上下文：作为「构造时捕获到的 UI 上下文」的替身，不执行任何回调。</summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
        }
    }

    /// <summary>
    /// <see cref="Notifier"/> 在**构造时**捕获 <c>SynchronizationContext.Current</c>：这是切回 UI 线程的唯一依据。
    /// 直接读私有字段验证（不触发 WinForms 类型加载，见类型注释里的环境限制）。
    /// </summary>
    [Fact]
    public void Notifier_CapturesSynchronizationContextAtConstruction()
    {
        var original = SynchronizationContext.Current;
        try
        {
            var fake = new RecordingSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(fake);

            // 注意：不能 Dispose（Dispose 的方法体会引用 NotifyIcon → 触发 WinForms 类型加载，
            // 而会话内运行器没有 WindowsDesktop 框架）。本用例只验证「构造时捕获」。
            var notifier = new Notifier();
            var field = typeof(Notifier).GetField("_uiContext", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field is not null, "Notifier 内部字段 _uiContext 不存在（实现已变更，请更新本用例）");

            Assert.Same(fake, field!.GetValue(notifier));

            SynchronizationContext.SetSynchronizationContext(null);

            // 上下文被清掉后，同一个 Notifier 仍然持有构造时捕获的实例（不会被后续线程状态影响）
            Assert.Same(fake, field.GetValue(notifier));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    /// <summary>源码守卫：<see cref="Notifier"/> 必须「上下文不同就 <c>Post</c>」，不能直接在当前线程执行。</summary>
    [Fact]
    public void NotifierSource_PostsToCapturedContextWhenCurrentDiffers()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "WinToolBox.Core", "Notifier.cs");
        Assert.True(File.Exists(path), $"找不到 {path}");

        var source = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("context != SynchronizationContext.Current", source, StringComparison.Ordinal);
        Assert.Contains("context.Post(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 为什么必须在 <c>Program.Main</c> 里显式安装上下文：<see cref="Task.Run(Action)"/>、
    /// 新建线程、线程池线程上的 <c>SynchronizationContext.Current</c> 都是 null，
    /// 所以「构造时捕获」如果发生在这些线程上，捕获到的就是 null，切回 UI 线程的能力直接消失。
    /// </summary>
    /// <remarks>
    /// 关键陷阱：**不能用 <c>Task.Wait()</c> / <c>GetResult()</c> 观察后台任务**——它们会把任务
    /// 内联到等待线程上执行，于是任务「看起来」继承了上下文（本用例初版就被这一点误导过）。
    /// 这里改用手动事件等待，保证委托真的跑在另一个线程上。
    /// </remarks>
    [Fact]
    public void SynchronizationContext_DoesNotReachBackgroundThreads()
    {
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RecordingSynchronizationContext());

            SynchronizationContext? onTask = new RecordingSynchronizationContext();
            var taskDone = new ManualResetEventSlim(false);
            Task.Run(() =>
            {
                onTask = SynchronizationContext.Current;
                taskDone.Set();
            });
            Assert.True(taskDone.Wait(TimeSpan.FromSeconds(10)), "后台任务未在 10 秒内执行");
            Assert.Null(onTask);

            SynchronizationContext? onThread = new RecordingSynchronizationContext();
            var thread = new Thread(() => onThread = SynchronizationContext.Current);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(onThread);

            SynchronizationContext? onPool = new RecordingSynchronizationContext();
            var poolDone = new ManualResetEventSlim(false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                onPool = SynchronizationContext.Current;
                poolDone.Set();
            });
            Assert.True(poolDone.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(onPool);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    /// <summary>
    /// 语句顺序守卫：<c>Program.Main</c> 必须在创建 <c>TrayApplicationContext</c> **之前**安装同步上下文，
    /// 否则构造时捕获到的 <c>Current</c> 恒为 null（P1-2 的根因）。
    /// </summary>
    [Fact]
    public void ProgramMain_InstallsSynchronizationContextBeforeCreatingTrayContext()
    {
        var programPath = Path.Combine(FindRepoRoot(), "src", "Tools", "UsbBackup", "Program.cs");
        Assert.True(File.Exists(programPath), $"找不到 {programPath}");

        var source = File.ReadAllText(programPath, Encoding.UTF8);
        var install = source.IndexOf("new WindowsFormsSynchronizationContext()", StringComparison.Ordinal);
        var tray = source.IndexOf("new TrayApplicationContext(", StringComparison.Ordinal);

        Assert.True(install >= 0, "Program.cs 未安装 WindowsFormsSynchronizationContext");
        Assert.True(tray > install, "TrayApplicationContext 必须在安装同步上下文之后才构造");
    }
}
