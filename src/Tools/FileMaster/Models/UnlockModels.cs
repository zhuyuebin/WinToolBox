using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>占用文件的应用程序类型（对应 Restart Manager 的 RM_APP_TYPE）。</summary>
public enum LockingAppType
{
    /// <summary>未知类型。</summary>
    Unknown = 0,

    /// <summary>有可见主窗口的应用。</summary>
    MainWindow = 1,

    /// <summary>有窗口但无主窗口的应用。</summary>
    OtherWindow = 2,

    /// <summary>Windows 服务。</summary>
    Service = 3,

    /// <summary>Windows 资源管理器。</summary>
    Explorer = 4,

    /// <summary>控制台程序（cmd / powershell 等）。</summary>
    Console = 5,

    /// <summary>关键系统进程（不可结束）。</summary>
    Critical = 1000
}

/// <summary>一个占用文件的进程。</summary>
public sealed class FileLockProcess
{
    /// <summary>进程 ID。</summary>
    public int ProcessId { get; init; }

    /// <summary>进程名（例如 <c>notepad</c>，可能为空）。</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Restart Manager 给出的应用名（通常是完整路径）。</summary>
    public string AppName { get; init; } = string.Empty;

    /// <summary>服务短名（仅服务类型有值）。</summary>
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>应用类型。</summary>
    public LockingAppType AppType { get; init; }

    /// <summary>是否可以由 Restart Manager 重启。</summary>
    public bool Restartable { get; init; }

    /// <summary>是否允许结束该进程（关键系统进程不允许）。</summary>
    public bool CanTerminate => AppType != LockingAppType.Critical && ProcessId > 4 && !FileUnlockerService.IsCriticalProcessName(ProcessName);

    /// <summary>界面显示用的名称。</summary>
    public string DisplayName => ProcessName.Length > 0
        ? ProcessName + ".exe"
        : (AppName.Length > 0 ? AppName : "（未知进程）");

    /// <summary>类型的中文说明。</summary>
    public string AppTypeText => AppType switch
    {
        LockingAppType.MainWindow => "桌面应用",
        LockingAppType.OtherWindow => "后台窗口",
        LockingAppType.Service => "Windows 服务",
        LockingAppType.Explorer => "资源管理器",
        LockingAppType.Console => "控制台程序",
        LockingAppType.Critical => "关键系统进程",
        _ => "未知类型"
    };
}

/// <summary>一次「谁占用了这个文件」查询的结果。</summary>
public sealed class FileLockQueryResult
{
    /// <summary>被查询的路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>路径是否存在。</summary>
    public bool PathExists { get; init; }

    /// <summary>占用该路径的进程（已按进程 ID 去重）。</summary>
    public IReadOnlyList<FileLockProcess> Processes { get; init; } = Array.Empty<FileLockProcess>();

    /// <summary>查询是否成功（Restart Manager 调用失败时记录原因）。</summary>
    public string? Error { get; init; }

    /// <summary>是否为目录查询。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>参与查询的文件数量（目录查询时是目录内文件数）。</summary>
    public int ScannedFileCount { get; init; }

    /// <summary>占用进程数量。</summary>
    public int LockCount => Processes.Count;

    /// <summary>一句话摘要。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
            {
                return "查询失败：" + Error;
            }

            if (!PathExists)
            {
                return "路径不存在：" + Path;
            }

            return LockCount == 0
                ? "没有被任何进程占用。"
                : $"被 {LockCount} 个进程占用：" + string.Join("、", Processes.Select(static p => $"{p.DisplayName}({p.ProcessId})"));
        }
    }
}

/// <summary>结束单个进程的结果。</summary>
public sealed class ProcessTerminateResult
{
    /// <summary>进程 ID。</summary>
    public int ProcessId { get; init; }

    /// <summary>进程名（用于提示）。</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>是否因为「关键系统进程」被主动拒绝。</summary>
    public bool Rejected { get; init; }
}

/// <summary>结束多个进程的进度。</summary>
public sealed class UnlockProgress
{
    /// <summary>完成百分比（0-100）。</summary>
    public double Percent { get; init; }

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>当前进程名称。</summary>
    public string CurrentProcess { get; init; } = string.Empty;
}

/// <summary>结束多个进程的结果。</summary>
public sealed class UnlockResult
{
    /// <summary>明细。</summary>
    public IReadOnlyList<ProcessTerminateResult> Items { get; init; } = Array.Empty<ProcessTerminateResult>();

    /// <summary>成功数量。</summary>
    public int SucceededCount => Items.Count(static item => item.Success);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static item => !item.Success);

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary => $"解锁完成：成功结束 {SucceededCount} 个进程，失败 {FailedCount} 个。";
}
