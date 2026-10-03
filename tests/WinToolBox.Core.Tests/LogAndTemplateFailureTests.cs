using System.Text;
using WinToolBox.Core;
using WinToolBox.Core.Services;

namespace WinToolBox.Core.Tests;

/// <summary>
/// 阶段五回归测试：日志与模板的「失败可见性」。
/// <list type="number">
/// <item><b>P1-4</b>：两个工具不再共用一个日志文件；写入用 <c>FileShare.ReadWrite</c> 并可并发写；
/// 失败时重试而不是静默丢行，且 <c>FailedWriteCount</c> 如实暴露。</item>
/// <item><b>P1-5</b>：模板保存/删除失败必须把失败传回调用方，而不是假装成功。</item>
/// </list>
/// </summary>
public sealed class LogAndTemplateFailureTests
{
    // ------------------------------------------------------------------
    // P1-4：按工具分目录
    // ------------------------------------------------------------------

    [Fact]
    public void LogDirectoryFor_ReturnsToolSpecificSubDirectory()
    {
        var usbBackup = AppPaths.LogDirectoryFor("UsbBackup");
        var fileMaster = AppPaths.LogDirectoryFor("FileMaster");

        Assert.NotEqual(usbBackup, fileMaster);
        Assert.Equal(Path.Combine(AppPaths.LogDirectory, "UsbBackup"), usbBackup);
        Assert.Equal(Path.Combine(AppPaths.LogDirectory, "FileMaster"), fileMaster);

        // 两个工具的日志目录必须在同一个父目录下（便于「打开日志目录」）
        Assert.Equal(
            Path.GetDirectoryName(usbBackup),
            Path.GetDirectoryName(fileMaster));
    }

    [Fact]
    public void Logger_ForTool_WritesIntoThatToolsDirectory()
    {
        using var ws = new TempWorkspace();

        var usbLogger = Logger.ForTool("UsbBackup");
        var fileLogger = Logger.ForTool("FileMaster");

        Assert.EndsWith("UsbBackup", usbLogger.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("FileMaster", fileLogger.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UsbBackup", usbLogger.ToolName);
        Assert.Equal("FileMaster", fileLogger.ToolName);

        // 传入空工具名时退回共享目录（兼容旧行为）
        var shared = Logger.ForTool(null);
        Assert.Equal(AppPaths.LogDirectory, shared.LogDirectory);
        Assert.Null(shared.ToolName);
    }

    [Fact]
    public void LogDirectoryFor_SanitizesToolName()
    {
        var directory = AppPaths.LogDirectoryFor("My/Tool:Name");

        // 非法字符被替换，且不会逃出 logs 目录
        Assert.DoesNotContain("/", Path.GetFileName(directory));
        Assert.DoesNotContain(":", Path.GetFileName(directory));
        Assert.StartsWith(
            Path.GetFullPath(AppPaths.LogDirectory),
            Path.GetFullPath(directory),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Logger_TwoInstances_WritingSameFile_DoNotLoseLines()
    {
        using var ws = new TempWorkspace();
        var logDirectory = Path.Combine(ws.Root, "logs");
        Directory.CreateDirectory(logDirectory);

        var loggerA = new Logger(logDirectory, Logger.DefaultRetentionDays, "toolA");
        var loggerB = new Logger(logDirectory, Logger.DefaultRetentionDays, "toolB");

        const int perLogger = 60;

        // 两个实例并发写同一个当天文件：旧实现用 FileShare.Read，会互相抢锁丢行
        var tasks = new[]
        {
            Task.Run(() => { for (var i = 0; i < perLogger; i++) loggerA.Info($"A-{i}"); }),
            Task.Run(() => { for (var i = 0; i < perLogger; i++) loggerB.Info($"B-{i}"); })
        };

        Task.WaitAll(tasks);

        Assert.False(loggerA.HasWriteFailure, "A 不应有写入失败");
        Assert.False(loggerB.HasWriteFailure, "B 不应有写入失败");

        var lines = File.ReadAllLines(loggerA.CurrentLogFilePath, Encoding.UTF8);
        Assert.Equal(perLogger * 2, lines.Length);

        for (var i = 0; i < perLogger; i++)
        {
            Assert.Contains(lines, line => line.Contains($"A-{i}"));
            Assert.Contains(lines, line => line.Contains($"B-{i}"));
        }
    }

    [Fact]
    public void Logger_WhenWriteFails_ExposesFailureCountInsteadOfSilentLoss()
    {
        using var ws = new TempWorkspace();

        // 用一个「文件」占住日志目录路径：EnsureDirectory 必然失败
        var blocker = Path.Combine(ws.Root, "logs-as-file");
        File.WriteAllText(blocker, "not a directory");

        var logger = new Logger(blocker, Logger.DefaultRetentionDays, "tool");

        logger.Info("第一条");
        logger.Info("第二条");

        Assert.True(logger.HasWriteFailure, "写入失败必须能被调用方察觉");
        Assert.Equal(2, logger.FailedWriteCount);
        Assert.NotNull(logger.LastError);
    }

    [Fact]
    public void Logger_AppendsInsteadOfTruncating()
    {
        using var ws = new TempWorkspace();
        var logDirectory = Path.Combine(ws.Root, "logs");

        var logger = new Logger(logDirectory, Logger.DefaultRetentionDays, "tool");
        logger.Info("第一行");
        logger.Info("第二行");

        var lines = File.ReadAllLines(logger.CurrentLogFilePath, Encoding.UTF8);
        Assert.Equal(2, lines.Length);
        Assert.Contains("第一行", lines[0]);
        Assert.Contains("第二行", lines[1]);
    }

    // ------------------------------------------------------------------
    // P1-5：模板保存失败必须传回调用方
    // ------------------------------------------------------------------

    [Fact]
    public void SaveTemplate_WhenDiskWriteFails_ReturnsFalse()
    {
        using var ws = new TempWorkspace();

        // 用一个「文件」占住模板所在目录的路径 → 写盘必然失败
        var blocker = Path.Combine(ws.Root, "templates-as-file");
        File.WriteAllText(blocker, "not a directory");

        var manager = new TemplateManager(Path.Combine(blocker, "templates.json"));

        var saved = manager.SaveTemplate("我的模板", "-a");

        Assert.False(saved, "写盘失败时必须返回 false，不能假装保存成功");
        Assert.NotNull(manager.LastPersistError);

        // 内存里仍然可用（不因为写盘失败就丢数据），但重启后会丢
        Assert.Equal("-a", manager.GetTemplate("我的模板"));
    }

    [Fact]
    public void SaveTemplate_WhenSuccessful_ReturnsTrueAndPersists()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "templates.json");
        var manager = new TemplateManager(path);

        Assert.True(manager.SaveTemplate("模板X", "-x"));
        Assert.Null(manager.LastPersistError);

        var reloaded = new TemplateManager(path);
        Assert.Equal("-x", reloaded.GetTemplate("模板X"));
    }

    [Fact]
    public void DeleteTemplate_WhenDiskWriteFails_ReturnsFalse()
    {
        using var ws = new TempWorkspace();
        var directory = Path.Combine(ws.Root, "templates-dir");
        var path = Path.Combine(directory, "templates.json");

        var manager = new TemplateManager(path);
        Assert.True(manager.SaveTemplate("待删除", "-a"));

        // 用「文件占住目录路径」的方式让后续写盘失败：把目录换成同名文件
        File.Delete(path);
        Directory.Delete(directory, recursive: true);
        File.WriteAllText(directory, "blocker");

        var deleted = manager.DeleteTemplate("待删除");

        Assert.False(deleted, "无法落盘时不能报告删除成功");
        Assert.NotNull(manager.LastPersistError);
    }

    [Fact]
    public void EnsureDefaults_WhenDiskWriteFails_DoesNotThrow()
    {
        using var ws = new TempWorkspace();
        var blocker = Path.Combine(ws.Root, "blocker-file");
        File.WriteAllText(blocker, "not a directory");

        var manager = new TemplateManager(Path.Combine(blocker, "templates.json"));

        // 写盘失败也不能抛异常拖垮启动（这是本用例的核心）
        var added = manager.EnsureDefaults();

        Assert.True(added >= 0);
        Assert.True(manager.Count > 0, "内置模板应可用（内存中）");

        // 内存里仍能拿到内置模板
        Assert.NotNull(manager.GetTemplate(TemplateManager.WebProjectTemplateName));
    }

    [Fact]
    public void EnsureDefaults_WhenTemplateFileMissingAndWriteFails_ReportsPersistError()
    {
        using var ws = new TempWorkspace();
        var blocker = Path.Combine(ws.Root, "blocker-file");
        File.WriteAllText(blocker, "not a directory");

        var path = Path.Combine(blocker, "templates.json");
        var manager = new TemplateManager(path);

        // 模板文件不存在 → EnsureDefaults 一定会尝试落盘一次；此时写盘必然失败
        Assert.False(File.Exists(path));

        manager.EnsureDefaults();

        Assert.NotNull(manager.LastPersistError);
    }

    // ------------------------------------------------------------------
    // P1-18：完整性检查必须是三态，且读不到时不能伪装成「正常」
    // ------------------------------------------------------------------

    /// <summary>
    /// 通过反射驱动 UsbBackup 里的 internal <c>ProcessIntegrity</c>
    /// （测试项目没有引用 UsbBackup 项目，因此用已构建的程序集）。
    /// </summary>
    private static Type LoadProcessIntegrityType()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var candidates = new[]
        {
            Path.Combine(repoRoot, "src", "Tools", "UsbBackup", "bin", "Release", "net8.0-windows", "win-x64", "UsbBackup.dll"),
            Path.Combine(repoRoot, "src", "Tools", "UsbBackup", "bin", "Release", "net8.0-windows", "UsbBackup.dll")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        Assert.True(path is not null, "找不到 UsbBackup.dll，请先执行 dotnet build WinToolBox.sln -c Release");

        var assembly = System.Reflection.Assembly.LoadFrom(path!);
        return assembly.GetType("WinToolBox.Tools.UsbBackup.ProcessIntegrity", throwOnError: true)!;
    }

    [Fact]
    public void ProcessIntegrity_HasThreeStateResult_IncludingUnknown()
    {
        var type = LoadProcessIntegrityType();
        var resultType = type.Assembly.GetType("WinToolBox.Tools.UsbBackup.IntegrityCheckResult", throwOnError: true)!;

        var names = Enum.GetNames(resultType);

        Assert.Contains("Normal", names);
        Assert.Contains("Restricted", names);
        Assert.Contains("Unknown", names);
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(0x0000, "Restricted")]
    [InlineData(0x1000, "Restricted")]
    [InlineData(0x2000, "Normal")]
    [InlineData(0x3000, "Normal")]
    [InlineData(0x4000, "Normal")]
    public void ProcessIntegrity_Classify_MapsRidToExpectedState(int? rid, string expected)
    {
        var type = LoadProcessIntegrityType();
        var classify = type.GetMethod("Classify", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

        var args = new object?[] { rid, null, null };
        var result = classify.Invoke(null, args);

        Assert.Equal(expected, result!.ToString());

        // 读不到时必须给出原因（否则用户拿不到任何线索）
        if (expected == "Unknown")
        {
            Assert.False(string.IsNullOrWhiteSpace(args[2] as string), "Unknown 必须带 failureReason");
        }
    }

    [Fact]
    public void ProcessIntegrity_Check_OnThisProcess_ReturnsAConsistentState()
    {
        var type = LoadProcessIntegrityType();
        var check = type.GetMethod("Check", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

        var args = new object?[] { null, null };
        var result = check.Invoke(null, args);

        var state = result!.ToString();
        Assert.Contains(state, new[] { "Normal", "Restricted", "Unknown" });

        // Normal / Restricted 必须给出可读描述；Unknown 必须给出原因
        if (state == "Unknown")
        {
            Assert.False(string.IsNullOrWhiteSpace(args[1] as string));
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(args[0] as string), $"{state} 必须带描述");
        }
    }
}
