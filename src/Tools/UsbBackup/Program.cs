using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using WinToolBox.Core;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// UsbBackup 托盘程序入口。
/// 支持三个入口分支：--version 打印版本、--selftest 本机冒烟自检、默认启动托盘程序。
/// </summary>
internal static class Program
{
    /// <summary>单实例互斥体名称，避免同一台机器同时运行多个托盘实例。</summary>
    private const string SingleInstanceMutexName = "WinToolBox.UsbBackup.SingleInstance";

    /// <summary>命令行：打印版本号。</summary>
    private const string VersionSwitch = "--version";

    /// <summary>命令行：本机冒烟自检。</summary>
    private const string SelfTestSwitch = "--selftest";

    /// <summary>ATTACH_PARENT_PROCESS：附加到父进程控制台（WinExe 默认没有控制台）。</summary>
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>程序入口。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码：0 成功，1 失败。</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        // 高 DPI、默认字体等 WinForms 全局初始化（由 SDK 生成）
        ApplicationConfiguration.Initialize();

        // P1-2：必须在创建 TrayApplicationContext / Notifier **之前**装好 UI 同步上下文。
        // 它们都在构造函数里读 SynchronizationContext.Current 并在后台任务完成后用它切回 UI 线程；
        // 而此时消息循环还没跑（Application.Run 尚未调用），Current 是 null ——
        // 结果是备份完成后的气泡回调直接在后台线程执行，通知时有时无。
        WindowsFormsSynchronizationContext.AutoInstall = true;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        // P1-4：写工具专属日志目录（%LocalAppData%\WinToolBox\logs\UsbBackup\），
        // 避免与 FileMaster 混在同一个文件里，也避免两边的保留期清理互相干扰。
        var logger = Logger.ForTool("UsbBackup");

        if (HasSwitch(args, VersionSwitch))
        {
            PrintVersion();
            return 0;
        }

        if (HasSwitch(args, SelfTestSwitch))
        {
            var reportPath = GetSelfTestReportPath(args);
            return RunSelfTest(reportPath, logger);
        }

        return RunTrayApplication(logger);
    }

    /// <summary>
    /// 日志写入失败时给出明确提示（P1-4）。
    /// </summary>
    /// <remarks>
    /// 日志失败本身不影响备份功能，但会让排障无从下手：用户以为「日志里没有错误 = 没问题」。
    /// 这里只在**确实写入失败**时提示，并给出日志目录与常见原因。
    /// </remarks>
    private static void WarnIfLogWriteFailed(Logger logger)
    {
        if (!logger.HasWriteFailure)
        {
            return;
        }

        try
        {
            MessageBox.Show(
                $"本次运行有 {logger.FailedWriteCount} 条日志未能写入磁盘。" + Environment.NewLine + Environment.NewLine +
                $"日志目录：{logger.LogDirectory}" + Environment.NewLine +
                (logger.LastError is { } error ? "最近一次原因：" + error.Message : string.Empty) +
                Environment.NewLine + Environment.NewLine +
                "备份功能不受影响，但出问题时将没有日志可查。常见原因：磁盘空间不足、目录被安全软件拦截、目录权限不足。" +
                Environment.NewLine + "可尝试在「设置」里清理磁盘空间，或以你自己的账户重新启动本程序。",
                "WinToolBox - U盘备份（日志写入失败）",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            // 连提示框都弹不出来时，只能记入日志（可能同样失败，但不影响启动）
            logger.Warn("显示日志写入失败提示时出错。", ex);
        }
    }

    /// <summary>
    /// 受限上下文（低完整性）下，系统会静默拒绝托盘图标注册，用户只会看到“进程在运行但托盘里没有图标”。
    /// 这里提前把原因和解决办法讲清楚，避免被误判成程序没启动。
    /// </summary>
    private static void WarnIfRestrictedContext(Logger logger)
    {
        var result = ProcessIntegrity.Check(out var integrity, out var failureReason);

        if (result == IntegrityCheckResult.Unknown)
        {
            // P1-18：读不到完整性级别时不能当成「正常」蒙混过去（fail-open），
            // 而是明确告诉用户「无法确定」，并给出托盘图标不显示时的排查方向。
            logger.Warn($"无法确定进程完整性级别：{failureReason}");
            try
            {
                MessageBox.Show(
                    "无法确定当前进程的完整性级别（原因：" + failureReason + "）。" + Environment.NewLine + Environment.NewLine +
                    "这本身不影响备份功能，但如果托盘图标没有显示，请依次检查：" + Environment.NewLine +
                    "  1) 任务管理器里 UsbBackup.exe 是否在运行（在运行就说明程序已启动）；" + Environment.NewLine +
                    "  2) 图标是否被 Windows 11 收进了任务栏的 “^” 折叠区；" + Environment.NewLine +
                    "  3) 到「设置 → 个性化 → 任务栏 → 其他系统托盘图标」把 UsbBackup 设为显示；" + Environment.NewLine +
                    "  4) 若以上都正常仍看不到图标，请改为在资源管理器里双击 UsbBackup.exe 启动。",
                    "WinToolBox - U盘备份",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                logger.Warn("显示完整性级别未知提示失败。", ex);
            }

            return;
        }

        if (result != IntegrityCheckResult.Restricted)
        {
            return;
        }

        logger.Warn($"检测到受限运行上下文（{integrity}），托盘图标可能无法显示。");

        try
        {
            MessageBox.Show(
                "检测到本程序运行在受限/沙箱环境中（完整性级别：" + integrity + "）。\n\n" +
                "Windows 会拒绝受限进程注册托盘图标，因此你很可能看不到托盘图标（但进程确实在运行）。\n\n" +
                "请改用下面任一方式启动：\n" +
                "  1) 在 Windows 资源管理器里双击 UsbBackup.exe；\n" +
                "  2) 在你自己的 Windows 终端（不是沙箱/受限终端）里运行。\n\n" +
                "如果之前已经在受限终端里启动过，请先在任务管理器中结束所有 UsbBackup.exe，再重新启动。",
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            logger.Warn("显示受限环境提示失败。", ex);
        }
    }

    /// <summary>启动托盘程序（单实例保护 + 全局异常兜底）。</summary>
    private static int RunTrayApplication(Logger logger)
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            logger.Warn("检测到已有 UsbBackup 实例在运行，本次启动已取消。");
            MessageBox.Show(
                "WinToolBox U盘备份 已经在运行，请查看通知区域（任务栏右下角，图标可能收在 “^” 折叠区里）。\n\n" +
                "如果通知区域里也找不到图标，说明之前那个实例没能注册托盘图标（例如是在沙箱/受限终端里启动的）。\n" +
                "请在任务管理器中结束所有 UsbBackup.exe，然后改用“在资源管理器里双击 UsbBackup.exe”重新启动。",
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        AttachGlobalExceptionHandlers(logger);

        WarnIfRestrictedContext(logger);

        logger.Info("UsbBackup 托盘程序启动。");

        // P1-4：日志写不出去必须让用户知道 —— 否则出问题时「日志里什么都没有」，
        // 用户会以为是程序没问题，而不是日志根本没落盘。
        WarnIfLogWriteFailed(logger);

        try
        {
            Application.Run(new TrayApplicationContext(logger));
            logger.Info("UsbBackup 托盘程序已退出。");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("托盘程序发生未处理异常，已退出。", ex);
            return 1;
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 互斥体未被本线程持有（例如启动阶段就失败），忽略即可
            }
        }
    }

    /// <summary>挂接全局未处理异常，保证异常被写入日志而不是静默崩溃。</summary>
    private static void AttachGlobalExceptionHandlers(Logger logger)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            logger.Error("UI 线程发生未处理异常。", e.Exception);

            try
            {
                MessageBox.Show(
                    "程序发生了一个未预期的错误，详情已写入日志。" + Environment.NewLine + e.Exception.Message,
                    "WinToolBox - U盘备份",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception notifyEx)
            {
                logger.Warn("显示未处理异常提示框失败。", notifyEx);
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Error("非 UI 线程发生未处理异常。", e.ExceptionObject as Exception);
    }

    /// <summary>输出程序版本；先附加到父进程控制台，保证从终端启动时可见。</summary>
    private static void PrintVersion()
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch (Exception)
        {
            // 没有父控制台（例如从资源管理器双击启动）时静默忽略
        }

        var assembly = Assembly.GetEntryAssembly();

        var informational = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var name = assembly?.GetName().Name ?? "UsbBackup";
        var version = assembly?.GetName().Version?.ToString() ?? "未知";

        var text = string.IsNullOrWhiteSpace(informational)
            ? $"{name} {version}"
            : $"{name} {informational}";

        try
        {
            Console.WriteLine($"{AppPaths.ProductName} - U盘备份 {text}");
        }
        catch (Exception)
        {
            // WinExe 没有控制台时忽略输出失败
        }
    }

    /// <summary>判断命令行中是否包含指定开关（忽略大小写）。</summary>
    private static bool HasSwitch(string[] args, string name)
        => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>自检报告输出路径：命令行第二个参数优先，否则用 %TEMP%\UsbBackup-selftest.txt。</summary>
    private static string GetSelfTestReportPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], SelfTestSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return Path.GetFullPath(args[i + 1]);
            }

            break;
        }

        return Path.Combine(Path.GetTempPath(), "UsbBackup-selftest.txt");
    }

    /// <summary>
    /// 本机冒烟自检：在临时目录中完整跑一遍“新建 → 增量跳过 → 增量更新 → 配置往返”流程，
    /// 不依赖真实 U 盘，用于验证 exe 可以正常运行。
    /// </summary>
    private static int RunSelfTest(string reportPath, Logger logger)
    {
        var report = new StringBuilder();
        var pass = 0;
        var fail = 0;

        report.AppendLine("========================================");
        report.AppendLine("WinToolBox - U盘备份 本机冒烟自检报告");
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"程序版本：{GetInformationalVersion()}");
        report.AppendLine($"日志目录：{logger.LogDirectory}");
        report.AppendLine($"运行目录：{AppContext.BaseDirectory}");
        report.AppendLine("========================================");

        string? tempRoot = null;

        try
        {
            logger.Info("开始执行 --selftest 本机冒烟自检。");

            tempRoot = Path.Combine(Path.GetTempPath(), "UsbBackup-selftest-" + Guid.NewGuid().ToString("N"));
            var sourceRoot = Path.Combine(tempRoot, "source");
            var backupRoot = Path.Combine(tempRoot, "backup");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "sub"));

            File.WriteAllText(Path.Combine(sourceRoot, "readme.txt"), "这是自检生成的测试文件。", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(sourceRoot, "sub", "data.csv"), "id,value" + Environment.NewLine + "1,100", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(sourceRoot, "skip.tmp"), "默认排除后缀 .tmp，不应被复制。", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(sourceRoot, "autorun.inf"), "默认排除文件名，不应被复制。", new UTF8Encoding(false));

            // 模拟一个 U 盘设备（不需要真实硬件）
            var device = new UsbDeviceInfo
            {
                RootPath = sourceRoot + Path.DirectorySeparatorChar,
                VolumeLabel = "SELFTEST",
                FileSystem = "NTFS",
                VolumeSerialNumber = "ABCD1234",
                TotalSize = 8L * 1024 * 1024 * 1024,
                FreeSpace = 4L * 1024 * 1024 * 1024
            };

            var target = BackupRules.BuildCurrentDirectory(backupRoot, device);
            var historyRoot = BackupRules.BuildHistoryRootDirectory(backupRoot, device);
            report.AppendLine($"源目录　　：{sourceRoot}");
            report.AppendLine($"current　 ：{target}");
            report.AppendLine($"history　 ：{historyRoot}");
            report.AppendLine($"设备标识　：{device.UniqueId}");
            report.AppendLine();

            // 用镜像服务（产品语义：固定目录 + 真增量 + 软删除历史）执行自检
            var mirror = new BackupMirrorService(logger);
            var rules = BackupRules.CreateDefaultExcludeRules();

            MirrorResult RunMirror() => mirror.Mirror(
                sourceDirectory: sourceRoot,
                backupRootDirectory: backupRoot,
                device: device,
                excludeRules: rules,
                historyRetentionDays: BackupConfig.DefaultHistoryRetentionDays,
                strictContentVerification: true);

            // 1) 首次镜像：应复制 2 个文件（readme.txt 与 sub\data.csv）
            var firstRun = RunMirror();
            var first = firstRun.Copy;
            Check(report, ref pass, ref fail, "首次备份复制了 2 个文件", first.CopiedFiles == 2, $"实际 {first.CopiedFiles}");
            Check(report, ref pass, ref fail, "首次备份没有失败文件", firstRun.FailedFiles == 0, $"失败 {firstRun.FailedFiles}");
            Check(report, ref pass, ref fail, "current\\readme.txt 已生成", File.Exists(Path.Combine(target, "readme.txt")), "文件不存在");
            Check(report, ref pass, ref fail, "current\\sub\\data.csv 已生成", File.Exists(Path.Combine(target, "sub", "data.csv")), "文件不存在");
            Check(report, ref pass, ref fail, "排除规则生效：.tmp 未被复制", !File.Exists(Path.Combine(target, "skip.tmp")), "被复制了");
            Check(report, ref pass, ref fail, "排除规则生效：autorun.inf 未被复制", !File.Exists(Path.Combine(target, "autorun.inf")), "被复制了");
            Check(report, ref pass, ref fail, "目标目录不含日期（固定 current 目录）",
                !target.Contains(DateTime.Now.ToString("yyyy-MM-dd")), target);
            Check(report, ref pass, ref fail, "manifest.json 已生成", File.Exists(firstRun.ManifestPath), firstRun.ManifestPath);

            // 2) 第二次镜像：全部内容未变化，应全部跳过且不产生 history
            var secondRun = RunMirror();
            Check(report, ref pass, ref fail, "第二次备份全部跳过（CopiedFiles = 0）", secondRun.Copy.CopiedFiles == 0, $"实际 {secondRun.Copy.CopiedFiles}");
            Check(report, ref pass, ref fail, "第二次备份跳过 2 个文件", secondRun.Copy.SkippedFiles == 2, $"实际 {secondRun.Copy.SkippedFiles}");
            Check(report, ref pass, ref fail, "内容未变时不产生历史版本", secondRun.ArchivedFiles == 0, $"实际 {secondRun.ArchivedFiles}");

            // 3) 修改一个源文件后再镜像：新版本写入 current，旧版本进入 history
            var modifiedPath = Path.Combine(sourceRoot, "readme.txt");
            var originalContent = File.ReadAllText(modifiedPath);
            File.WriteAllText(modifiedPath, "内容已更新，长度也变化了，用于验证增量备份 + 版本历史。", new UTF8Encoding(false));

            var thirdRun = RunMirror();
            Check(report, ref pass, ref fail, "源文件修改后只复制 1 个文件", thirdRun.Copy.CopiedFiles == 1, $"实际 {thirdRun.Copy.CopiedFiles}");
            Check(report, ref pass, ref fail, "修改后的内容已同步到 current",
                File.ReadAllText(Path.Combine(target, "readme.txt")) == File.ReadAllText(modifiedPath), "内容不一致");

            var historyToday = BackupRules.BuildHistoryDirectory(backupRoot, device, DateTime.Now);
            var archivedReadme = Path.Combine(historyToday, "readme.txt");
            Check(report, ref pass, ref fail, "旧版本被移入 history", File.Exists(archivedReadme), archivedReadme);
            Check(report, ref pass, ref fail, "history 中保留的是改写前的旧内容",
                File.Exists(archivedReadme) && File.ReadAllText(archivedReadme) == originalContent, "旧版本内容不符");

            // 4) 删除 U 盘里的文件：current 中的副本移入 history，而不是消失
            var deletedSource = Path.Combine(sourceRoot, "sub", "data.csv");
            var deletedContent = File.ReadAllText(deletedSource);
            File.Delete(deletedSource);

            var fourthRun = RunMirror();
            var currentDataCsv = Path.Combine(target, "sub", "data.csv");
            var archivedDataCsv = Path.Combine(historyToday, "sub", "data.csv");
            Check(report, ref pass, ref fail, "U 盘删除后 current 中不再有该文件", !File.Exists(currentDataCsv), currentDataCsv);
            Check(report, ref pass, ref fail, "U 盘删除后副本被移入 history（软删除）", File.Exists(archivedDataCsv), archivedDataCsv);
            Check(report, ref pass, ref fail, "history 中保留被删除文件的原始内容",
                File.Exists(archivedDataCsv) && File.ReadAllText(archivedDataCsv) == deletedContent, "内容不符");
            Check(report, ref pass, ref fail, "manifest 记录了删除并转入历史 1 个",
                BackupMirrorService.ReadManifest(fourthRun.ManifestPath)?.Comparison.DeletedFiles == 1,
                "manifest 对比结果不符");

            // 4) 配置往返：写入临时 config.json 后再读回
            var configFile = Path.Combine(tempRoot, "config", "config.json");
            var configManager = new ConfigManager(configFile, logger);
            var config = BackupConfig.CreateDefault();
            config.BackupTargetDirectory = backupRoot;
            config.ExcludedExtensions.Add(".bak");
            config.ExcludedExtensions.Add("TEMP");
            configManager.Save(config);

            var loaded = configManager.Load();
            Check(report, ref pass, ref fail, "配置已写入磁盘", File.Exists(configFile), "配置文件不存在");
            Check(report, ref pass, ref fail, "配置往返：目标目录一致", string.Equals(loaded.BackupTargetDirectory, backupRoot, StringComparison.OrdinalIgnoreCase), $"实际 {loaded.BackupTargetDirectory}");
            Check(report, ref pass, ref fail, "配置往返：自定义后缀已保留", loaded.ExcludedExtensions.Contains(".bak"), "缺少 .bak");
            Check(report, ref pass, ref fail, "配置往返：后缀已规范化为小写", loaded.ExcludedExtensions.Contains(".temp"), "缺少 .temp");

            // 5) U 盘枚举不应抛异常（自检机通常没有 U 盘，只验证调用安全）
            var detector = new UsbDetector(logger);
            var drives = detector.GetRemovableDrives();
            report.AppendLine($"[信息] 当前可移动磁盘数量：{drives.Count}（自检不要求存在 U 盘）");
            Check(report, ref pass, ref fail, "可移动磁盘枚举未抛异常", true, string.Empty);

            // 6) 只读安全校验：源目录中的文件数量未被改变（程序绝不写入源）
            //    自检过程中我们在步骤 4) 故意删掉了 1 个源文件（模拟 U 盘上误删），
            //    因此期望值 = 初始 4 个 - 1 = 3 个：既验证“没有多写”，也验证“删除确实生效”。
            var sourceFileCount = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories).Length;
            Check(report, ref pass, ref fail, "源目录文件数量保持不变（4 - 1 个已删除 = 3 个）", sourceFileCount == 3, $"实际 {sourceFileCount} 个");

            logger.Info($"自检完成：通过 {pass} 项，失败 {fail} 项。");
        }
        catch (Exception ex)
        {
            fail++;
            report.AppendLine($"[异常] 自检过程中发生未处理异常：{ex}");
            logger.Error("自检过程中发生未处理异常。", ex);
        }
        finally
        {
            if (tempRoot is not null)
            {
                try
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, true);
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine($"[提示] 清理临时目录失败（不影响结论）：{ex.Message}");
                }
            }
        }

        report.AppendLine();
        report.AppendLine("----------------------------------------");
        report.AppendLine($"自检结论：{(fail == 0 ? "全部通过" : "存在失败项")}（通过 {pass} 项，失败 {fail} 项）");
        report.AppendLine($"退出码：{(fail == 0 ? 0 : 1)}");
        report.AppendLine("----------------------------------------");

        var text = report.ToString();

        try
        {
            var directory = Path.GetDirectoryName(reportPath);
            AppPaths.EnsureDirectory(directory);
            File.WriteAllText(reportPath, text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            logger.Error($"写入自检报告失败：{reportPath}", ex);
            fail++;
        }

        try
        {
            AttachConsole(AttachParentProcess);
            Console.WriteLine(text);
            Console.WriteLine($"报告文件：{reportPath}");
        }
        catch (Exception)
        {
            // 没有控制台时忽略
        }

        // 退出码：0 表示全部通过，1 表示存在失败项
        return fail == 0 ? 0 : 1;
    }

    /// <summary>记录一条自检结论。</summary>
    private static void Check(StringBuilder report, ref int pass, ref int fail, string title, bool condition, string detail)
    {
        if (condition)
        {
            pass++;
            report.AppendLine($"[通过] {title}");
        }
        else
        {
            fail++;
            report.AppendLine($"[失败] {title}（{detail}）");
        }
    }

    /// <summary>取程序信息版本号（失败时返回程序集版本）。</summary>
    private static string GetInformationalVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                   .InformationalVersion
               ?? assembly?.GetName().Version?.ToString()
               ?? "未知";
    }

    /// <summary>
    /// 把当前进程附加到父进程控制台，使 WinExe 从终端启动时能够输出文本。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
}
