using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using WinToolBox.Core;
using WinToolBox.Core.Services;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster;

/// <summary>
/// FileMaster 程序入口。
/// 支持命令行：<c>--version</c> 输出版本，<c>--selftest [报告路径]</c> 执行本机冒烟自检（退出码 0 表示通过）。
/// </summary>
internal static class Program
{
    private const int AttachParentProcess = -1;

    /// <summary>自检与示例规则共用的示例文本。</summary>
    private const string SampleRules = """
        # 这是一个注释行，以 # 开头将被忽略
        根目录名称

        -一级目录
        --二级目录
        ---三级目录
        -一级目录2
        --二级目录2
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (HasSwitch(args, "--version"))
        {
            AttachConsole(AttachParentProcess);
            Console.WriteLine($"FileMaster {GetVersion()}");
            return 0;
        }

        if (HasSwitch(args, "--selftest"))
        {
            var reportPath = GetSwitchValue(args, "--selftest")
                             ?? Path.Combine(Path.GetTempPath(), "FileMaster-selftest.txt");
            return RunSelfTest(reportPath);
        }

        var logger = Logger.Instance;

        // 一次性迁移：把旧版 %AppData%\WinToolBox\FolderCreator\templates.json 复制到 FileMaster 目录
        TemplateManager.MigrateLegacyTemplates(logger);

        Application.ThreadException += (_, e) => HandleFatal(logger, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleFatal(logger, e.ExceptionObject as Exception);

        try
        {
            // 窗口程序没有托盘图标，因此不注入 INotifier，结果统一显示在状态栏。
            Application.Run(new MainForm(logger, null));
            return 0;
        }
        catch (Exception ex)
        {
            HandleFatal(logger, ex);
            return 1;
        }
    }

    /// <summary>取当前程序版本号。</summary>
    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? "0.1.0"
            : informational;
    }

    private static bool HasSwitch(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>取形如 <c>--selftest D:\report.txt</c> 的取值，没有则返回 null。</summary>
    private static string? GetSwitchValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>全局异常处理：写日志并给出友好提示，避免静默崩溃。</summary>
    private static void HandleFatal(Logger logger, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        logger.Error("FileMaster 发生未处理异常。", exception);

        try
        {
            MessageBox.Show(
                $"程序发生错误：{exception.Message}\n\n详细信息已写入日志：{logger.LogDirectory}",
                "FileMaster",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 提示失败时不再抛出，避免二次崩溃
        }
    }

    /// <summary>
    /// 本机冒烟自检：在临时目录里跑一遍“解析规则 → 生成目录 → 重复生成（应全部跳过）→ 严格/宽松检查”，
    /// 不依赖任何真实数据，用于验证程序可正常运行。
    /// </summary>
    private static int RunSelfTest(string reportPath)
    {
        var report = new StringBuilder();
        var passed = 0;
        var failed = 0;
        var workRoot = Path.Combine(Path.GetTempPath(), "FileMaster-selftest-" + Guid.NewGuid().ToString("N"));

        void Check(string name, bool ok, string? detail = null)
        {
            if (ok)
            {
                passed++;
                report.AppendLine($"[通过] {name}");
            }
            else
            {
                failed++;
                report.AppendLine($"[失败] {name}{(string.IsNullOrEmpty(detail) ? string.Empty : "：" + detail)}");
            }
        }

        report.AppendLine("========================================");
        report.AppendLine("FileMaster 本机冒烟自检报告");
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"程序版本：{GetVersion()}");
        report.AppendLine($"日志目录：{Logger.Instance.LogDirectory}");
        report.AppendLine($"工作目录：{workRoot}");
        report.AppendLine("========================================");

        try
        {
            var parser = new RuleParser();
            var generator = new FolderGenerator();
            var checker = new FolderChecker();

            // ---------- 1. 规则解析 ----------
            var parsed = parser.Parse(SampleRules);
            Check("示例规则解析成功且共 5 条", parsed.Success && parsed.Rules.Count == 5, parsed.Summary);
            Check("层级深度解析正确（1/2/3）",
                parsed.Rules.Count >= 3 && parsed.Rules[0].Depth == 1 && parsed.Rules[1].Depth == 2 && parsed.Rules[2].Depth == 3);
            Check("无 '-' 的提示行被忽略", parsed.Rules.All(r => r.Name != "根目录名称"));

            var invalid = parser.Parse("-非法:名称");
            Check("非法字符被识别并报错", !invalid.Success && invalid.Errors.Count > 0,
                invalid.Errors.FirstOrDefault());

            var autoFilled = parser.Parse("---三级目录");
            Check("缺失父级被自动补齐（3 条规则、前 2 条为补齐）",
                autoFilled.Rules.Count == 3 &&
                autoFilled.Rules[0].IsAutoCreated &&
                autoFilled.Rules[1].IsAutoCreated &&
                !autoFilled.Rules[2].IsAutoCreated,
                string.Join(" / ", autoFilled.Rules.Select(r => r.RelativePath)));

            // ---------- 2. 文件夹生成 ----------
            var first = generator.Generate(workRoot, parsed.Rules);
            Check("首次生成：新建 5 个、无失败", first.CreatedCount == 5 && first.FailedCount == 0, first.Summary);
            Check("目录结构真实存在",
                parsed.Rules.All(r => Directory.Exists(Path.Combine(workRoot, r.RelativePath))));

            var progressCount = 0;
            var lastPercent = 0d;
            var progress = new SynchronousProgress(p =>
            {
                progressCount++;
                lastPercent = p.Percent;
            });

            var second = generator.Generate(workRoot, parsed.Rules, progress);
            Check("重复生成：全部跳过、不报错", second.CreatedCount == 0 && second.ExistedCount == 5 && second.Success,
                second.Summary);
            Check("进度回调覆盖全部规则且最终为 100%", progressCount == 5 && Math.Abs(lastPercent - 100d) < 0.001,
                $"回调 {progressCount} 次，最终 {lastPercent}%");

            // ---------- 3. 目录检查 ----------
            var strictOk = checker.Check(workRoot, parsed.Rules, FolderCheckMode.Strict);
            Check("严格模式：结构一致时判定为一致",
                strictOk.IsConsistent && strictOk.MissingCount == 0 && strictOk.ExtraCount == 0, strictOk.Summary);

            Directory.CreateDirectory(Path.Combine(workRoot, "多余目录"));
            Directory.Delete(Path.Combine(workRoot, "一级目录2"), recursive: true);

            var strictBad = checker.Check(workRoot, parsed.Rules, FolderCheckMode.Strict);
            Check("严格模式：同时识别缺失与多余",
                strictBad.MissingCount >= 1 && strictBad.ExtraCount >= 1 && !strictBad.IsConsistent, strictBad.Summary);

            var looseBad = checker.Check(workRoot, parsed.Rules, FolderCheckMode.Loose);
            Check("宽松模式：只报缺失、忽略多余",
                looseBad.MissingCount >= 1 && looseBad.ExtraCount == 0 && !looseBad.IsConsistent, looseBad.Summary);

            var missingRoot = checker.Check(Path.Combine(workRoot, "不存在的目录"), parsed.Rules);
            Check("根目录不存在时给出友好错误而不抛异常", missingRoot.Error is not null && !missingRoot.IsConsistent);
        }
        catch (Exception ex)
        {
            failed++;
            report.AppendLine($"[失败] 自检过程抛出异常：{ex}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                {
                    Directory.Delete(workRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"[提示] 清理临时目录失败：{ex.Message}");
            }
        }

        var exitCode = failed == 0 ? 0 : 1;
        report.AppendLine();
        report.AppendLine("----------------------------------------");
        report.AppendLine($"自检结论：{(failed == 0 ? "全部通过" : "存在失败项")}（通过 {passed} 项，失败 {failed} 项）");
        report.AppendLine($"退出码：{exitCode}");
        report.AppendLine("----------------------------------------");

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 报告写不出来不影响退出码，但尽量留下痕迹
            Logger.Instance.Warn($"写入自检报告失败：{reportPath}", ex);
        }

        return exitCode;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// 同步进度接收器：自检流程没有消息循环，用不了会异步派发的 <see cref="Progress{T}"/>，
    /// 否则回调时序不确定（计数可能还没累加就被断言）。
    /// </summary>
    private sealed class SynchronousProgress : IProgress<FolderProgress>
    {
        private readonly Action<FolderProgress> _onReport;

        public SynchronousProgress(Action<FolderProgress> onReport)
        {
            _onReport = onReport;
        }

        public void Report(FolderProgress value) => _onReport(value);
    }
}
