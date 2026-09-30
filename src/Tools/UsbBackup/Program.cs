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

        var logger = Logger.Instance;

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

    /// <summary>启动托盘程序（单实例保护 + 全局异常兜底）。</summary>
    private static int RunTrayApplication(Logger logger)
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            logger.Warn("检测到已有 UsbBackup 实例在运行，本次启动已取消。");
            MessageBox.Show(
                "WinToolBox U盘备份 已经在运行，请查看系统托盘图标。",
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        AttachGlobalExceptionHandlers(logger);

        logger.Info("UsbBackup 托盘程序启动。");

        try
        {
            Application.Run(new TrayApplicationContext());
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

            var target = BackupRules.BuildTargetDirectory(backupRoot, device, DateTime.Now);
            report.AppendLine($"源目录　：{sourceRoot}");
            report.AppendLine($"目标目录：{target}");
            report.AppendLine($"设备标识：{device.UniqueId}");
            report.AppendLine();

            var copier = new FileCopier(logger);

            // 1) 首次复制：应复制 2 个文件（readme.txt 与 sub\data.csv）
            var first = copier.CopyDirectory(sourceRoot, target, null, BackupRules.CreateDefaultExcludeRules());
            Check(report, ref pass, ref fail, "首次复制复制了 2 个文件", first.CopiedFiles == 2, $"实际 {first.CopiedFiles}");
            Check(report, ref pass, ref fail, "首次复制没有失败文件", first.FailedFiles == 0, $"失败 {first.FailedFiles}");
            Check(report, ref pass, ref fail, "目标文件 readme.txt 已生成", File.Exists(Path.Combine(target, "readme.txt")), "文件不存在");
            Check(report, ref pass, ref fail, "目标文件 sub\\data.csv 已生成", File.Exists(Path.Combine(target, "sub", "data.csv")), "文件不存在");
            Check(report, ref pass, ref fail, "排除规则生效：.tmp 未被复制", !File.Exists(Path.Combine(target, "skip.tmp")), "被复制了");
            Check(report, ref pass, ref fail, "排除规则生效：autorun.inf 未被复制", !File.Exists(Path.Combine(target, "autorun.inf")), "被复制了");

            // 2) 第二次复制：全部内容未变化，应全部跳过
            var second = copier.CopyDirectory(sourceRoot, target, null, BackupRules.CreateDefaultExcludeRules());
            Check(report, ref pass, ref fail, "第二次复制全部跳过（CopiedFiles = 0）", second.CopiedFiles == 0, $"实际 {second.CopiedFiles}");
            Check(report, ref pass, ref fail, "第二次复制跳过 2 个文件", second.SkippedFiles == 2, $"实际 {second.SkippedFiles}");

            // 3) 修改一个源文件后再复制：只应复制 1 个（大小变化，增量判定必然生效）
            var modifiedPath = Path.Combine(sourceRoot, "readme.txt");
            File.WriteAllText(modifiedPath, "内容已更新，长度也变化了，用于验证增量复制。", new UTF8Encoding(false));

            var third = copier.CopyDirectory(sourceRoot, target, null, BackupRules.CreateDefaultExcludeRules());
            Check(report, ref pass, ref fail, "源文件修改后只复制 1 个文件", third.CopiedFiles == 1, $"实际 {third.CopiedFiles}");
            Check(report, ref pass, ref fail, "修改后的内容已同步到目标", File.ReadAllText(Path.Combine(target, "readme.txt")) == File.ReadAllText(modifiedPath), "内容不一致");

            // 4) 配置往返：写入临时 config.json 后再读回
            var configFile = Path.Combine(tempRoot, "config", "config.json");
            var configManager = new ConfigManager(configFile, logger);
            var config = BackupConfig.CreateDefault();
            config.BackupTargetDirectory = backupRoot;
            config.AutoBackupEnabled = false;
            config.ExcludedExtensions.Add(".bak");
            config.ExcludedExtensions.Add("TEMP");
            configManager.Save(config);

            var loaded = configManager.Load();
            Check(report, ref pass, ref fail, "配置已写入磁盘", File.Exists(configFile), "配置文件不存在");
            Check(report, ref pass, ref fail, "配置往返：目标目录一致", string.Equals(loaded.BackupTargetDirectory, backupRoot, StringComparison.OrdinalIgnoreCase), $"实际 {loaded.BackupTargetDirectory}");
            Check(report, ref pass, ref fail, "配置往返：自动备份开关一致", !loaded.AutoBackupEnabled, $"实际 {loaded.AutoBackupEnabled}");
            Check(report, ref pass, ref fail, "配置往返：自定义后缀已保留", loaded.ExcludedExtensions.Contains(".bak"), "缺少 .bak");
            Check(report, ref pass, ref fail, "配置往返：后缀已规范化为小写", loaded.ExcludedExtensions.Contains(".temp"), "缺少 .temp");

            // 5) U 盘枚举不应抛异常（自检机通常没有 U 盘，只验证调用安全）
            var detector = new UsbDetector(logger);
            var drives = detector.GetRemovableDrives();
            report.AppendLine($"[信息] 当前可移动磁盘数量：{drives.Count}（自检不要求存在 U 盘）");
            Check(report, ref pass, ref fail, "可移动磁盘枚举未抛异常", true, string.Empty);

            // 6) 只读安全校验：源目录中的文件数量未被改变（程序绝不写入源）
            Check(report, ref pass, ref fail, "源目录文件数量保持不变（4 个）", Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories).Length == 4, "源目录被意外修改");

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
