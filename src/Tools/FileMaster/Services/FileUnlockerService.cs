using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 文件占用查询与解锁服务：使用 Windows Restart Manager（<c>rmsession.dll</c>）查询
/// 正在占用某个文件 / 目录的进程，并提供结束进程的能力。
/// </summary>
/// <remarks>
/// <para>实现方式：<c>RmStartSession</c> → <c>RmRegisterResources</c> → <c>RmGetList</c> → <c>RmEndSession</c>。</para>
/// <para>目录查询会自动展开目录内的文件（上限 <see cref="MaxFilesPerQuery"/> 个），因为 Restart Manager 只接受文件。</para>
/// <para>不涉及任何注册表操作；结束时对关键系统进程做保护，拒绝结束。</para>
/// </remarks>
public sealed class FileUnlockerService
{
    /// <summary>单次查询最多注册的文件数（目录查询时）。</summary>
    public const int MaxFilesPerQuery = 256;

    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;

    /// <summary>禁止结束的系统关键进程名（小写、不含扩展名）。</summary>
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "memory compression"
    };

    private readonly Logger? _logger;

    /// <summary>创建解锁服务。</summary>
    public FileUnlockerService(Logger? logger = null) => _logger = logger;

    /// <summary>
    /// 查询占用指定路径（文件或目录）的进程。
    /// </summary>
    public FileLockQueryResult FindLockingProcesses(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FileLockQueryResult { Path = path ?? string.Empty, Error = "路径不能为空。" };
        }

        var fullPath = SafeGetFullPath(path);
        var isDirectory = Directory.Exists(fullPath);
        var isFile = File.Exists(fullPath);

        if (!isDirectory && !isFile)
        {
            return new FileLockQueryResult { Path = fullPath, PathExists = false };
        }

        var files = isDirectory ? ExpandDirectoryFiles(fullPath) : new List<string> { fullPath };

        if (files.Count == 0)
        {
            return new FileLockQueryResult
            {
                Path = fullPath,
                PathExists = true,
                IsDirectory = true,
                ScannedFileCount = 0
            };
        }

        try
        {
            var processes = QueryRestartManager(files, out var error);

            if (error is not null)
            {
                return new FileLockQueryResult
                {
                    Path = fullPath,
                    PathExists = true,
                    IsDirectory = isDirectory,
                    ScannedFileCount = files.Count,
                    Processes = processes,
                    Error = error
                };
            }

            _logger?.Info($"文件占用查询：{fullPath}，文件 {files.Count} 个，占用进程 {processes.Count} 个。");

            return new FileLockQueryResult
            {
                Path = fullPath,
                PathExists = true,
                IsDirectory = isDirectory,
                ScannedFileCount = files.Count,
                Processes = processes
            };
        }
        catch (Exception ex)
        {
            _logger?.Error($"文件占用查询失败：{fullPath}", ex);
            return new FileLockQueryResult
            {
                Path = fullPath,
                PathExists = true,
                IsDirectory = isDirectory,
                ScannedFileCount = files.Count,
                Error = ex.Message
            };
        }
    }

    /// <summary>查询一组文件各自被哪些进程占用（去重合并）。</summary>
    public IReadOnlyList<FileLockProcess> FindLockingProcessesForFiles(IEnumerable<string> files)
    {
        var list = files
            .Where(static file => !string.IsNullOrWhiteSpace(file))
            .Select(SafeGetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
        {
            return Array.Empty<FileLockProcess>();
        }

        return QueryRestartManager(list, out _);
    }

    /// <summary>
    /// 结束指定进程。关键系统进程会被拒绝（不抛异常，返回失败结果）。
    /// </summary>
    public ProcessTerminateResult TerminateProcess(int processId)
    {
        var name = GetProcessName(processId);

        if (IsCriticalProcessName(name) || processId <= 4)
        {
            _logger?.Warn($"拒绝结束关键系统进程：{name}({processId})");
            return new ProcessTerminateResult
            {
                ProcessId = processId,
                ProcessName = name,
                Success = false,
                Rejected = true,
                Error = "该进程属于关键系统进程，为避免系统不稳定已拒绝结束。"
            };
        }

        try
        {
            using var process = Process.GetProcessById(processId);

            if (process.HasExited)
            {
                return new ProcessTerminateResult { ProcessId = processId, ProcessName = name, Success = true };
            }

            process.Kill();
            process.WaitForExit(5000);

            _logger?.Info($"已结束进程：{name}({processId})");
            return new ProcessTerminateResult { ProcessId = processId, ProcessName = name, Success = true };
        }
        catch (ArgumentException)
        {
            // 进程已经退出
            return new ProcessTerminateResult { ProcessId = processId, ProcessName = name, Success = true };
        }
        catch (Exception ex)
        {
            _logger?.Warn($"结束进程失败：{name}({processId})", ex);
            return new ProcessTerminateResult
            {
                ProcessId = processId,
                ProcessName = name,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>批量结束进程（带进度与取消）。不传进程集合时会先查询占用。</summary>
    public UnlockResult TerminateProcesses(
        IEnumerable<FileLockProcess> processes,
        IProgress<UnlockProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var targets = processes
            .GroupBy(static process => process.ProcessId)
            .Select(static group => group.First())
            .ToList();

        var results = new List<ProcessTerminateResult>(targets.Count);
        var processed = 0;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(TerminateProcess(target.ProcessId));

            processed++;
            progress?.Report(new UnlockProgress
            {
                Percent = targets.Count == 0 ? 100 : processed * 100d / targets.Count,
                Processed = processed,
                Total = targets.Count,
                CurrentProcess = target.DisplayName
            });
        }

        var result = new UnlockResult { Items = results };
        _logger?.Info(result.Summary);
        return result;
    }

    /// <summary>是否是受保护的关键系统进程名。</summary>
    public static bool IsCriticalProcessName(string? processName)
        => !string.IsNullOrWhiteSpace(processName) && ProtectedProcessNames.Contains(processName.Trim());

    /// <summary>应用类型的中文说明。</summary>
    public static string DescribeAppType(LockingAppType type) => type switch
    {
        LockingAppType.MainWindow => "桌面应用",
        LockingAppType.OtherWindow => "后台窗口",
        LockingAppType.Service => "Windows 服务",
        LockingAppType.Explorer => "资源管理器",
        LockingAppType.Console => "控制台程序",
        LockingAppType.Critical => "关键系统进程",
        _ => "未知类型"
    };

    // ---------------------------------------------------------------- 内部实现

    /// <summary>目录查询：展开目录内的文件（上限 <see cref="MaxFilesPerQuery"/> 个）。</summary>
    private List<string> ExpandDirectoryFiles(string directory)
    {
        var files = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                files.Add(file);

                if (files.Count >= MaxFilesPerQuery)
                {
                    _logger?.Warn($"目录内文件过多，仅检查前 {MaxFilesPerQuery} 个文件：{directory}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"展开目录文件失败：{directory}", ex);
        }

        return files;
    }

    /// <summary>调用 Restart Manager 查询占用进程。</summary>
    private List<FileLockProcess> QueryRestartManager(IReadOnlyList<string> files, out string? error)
    {
        error = null;
        var result = new List<FileLockProcess>();

        var sessionKey = new StringBuilder(256);
        var startResult = RmStartSession(out var sessionHandle, 0, sessionKey);

        if (startResult != ErrorSuccess)
        {
            error = $"RmStartSession 失败（错误码 {startResult}）。";
            return result;
        }

        try
        {
            var fileArray = files.ToArray();
            var registerResult = RmRegisterResources(
                sessionHandle,
                (uint)fileArray.Length,
                fileArray,
                0,
                null,
                0,
                null);

            if (registerResult != ErrorSuccess)
            {
                error = $"RmRegisterResources 失败（错误码 {registerResult}）。";
                return result;
            }

            uint rebootReasons = 0;
            uint needed = 0;
            uint count = 0;
            RM_PROCESS_INFO[]? infos = null;
            int getListResult;

            // 先问需要多少条，再按需分配（期间进程列表可能变化，最多重试几次）
            for (var attempt = 0; attempt < 4; attempt++)
            {
                getListResult = RmGetList(sessionHandle, out needed, ref count, infos, ref rebootReasons);

                if (getListResult == ErrorSuccess)
                {
                    break;
                }

                if (getListResult != ErrorMoreData)
                {
                    error = $"RmGetList 失败（错误码 {getListResult}）。";
                    return result;
                }

                count = needed;
                infos = new RM_PROCESS_INFO[count];
            }

            if (infos is null || count == 0)
            {
                return result;
            }

            foreach (var info in infos)
            {
                if (info.Process.dwProcessId <= 0)
                {
                    continue;
                }

                var processName = GetProcessName(info.Process.dwProcessId);

                result.Add(new FileLockProcess
                {
                    ProcessId = info.Process.dwProcessId,
                    ProcessName = processName,
                    AppName = info.strAppName ?? string.Empty,
                    ServiceName = info.strServiceShortName ?? string.Empty,
                    AppType = (LockingAppType)info.ApplicationType,
                    Restartable = info.bRestartable
                });
            }

            return result
                .GroupBy(static process => process.ProcessId)
                .Select(static group => group.First())
                .OrderBy(static process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    /// <summary>取进程名（进程已退出时返回空字符串）。</summary>
    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>安全取完整路径（路径非法时返回原字符串）。</summary>
    private static string SafeGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    // 注意：strSessionKey 是「输出」缓冲区（CCH_RM_SESSION_KEY + 1 个 WCHAR），
    // 必须用 StringBuilder 之类的可写缓冲区，传 string 会得到 ERROR_WRITE_FAULT(29)。
    [DllImport("rstrtmgr.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    /// <summary>把 Restart Manager 返回的应用名压缩成便于显示的一行。</summary>
    public static string ShortenAppName(string? appName, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return string.Empty;
        }

        var text = appName.Trim();
        return text.Length <= maxLength ? text : "…" + text[^(maxLength - 1)..];
    }

    /// <summary>生成占用信息的文本报告。</summary>
    public static string BuildReport(FileLockQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.AppendLine("路径：" + result.Path);
        builder.AppendLine("是否目录：" + (result.IsDirectory ? "是" : "否"));
        builder.AppendLine("检查文件数：" + result.ScannedFileCount);
        builder.AppendLine(result.Summary);
        builder.AppendLine();

        foreach (var process in result.Processes)
        {
            builder.Append("- ").Append(process.DisplayName)
                .Append("（PID ").Append(process.ProcessId).Append("，").Append(process.AppTypeText)
                .Append(process.Restartable ? "，可重启" : string.Empty)
                .Append("）");
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
