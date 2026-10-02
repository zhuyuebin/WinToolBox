namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>时间戳处理对象。</summary>
public enum TimestampTarget
{
    /// <summary>只处理文件。</summary>
    Files,

    /// <summary>只处理文件夹。</summary>
    Folders,

    /// <summary>文件与文件夹都处理。</summary>
    Both
}

/// <summary>时间戳修改模式。</summary>
public enum TimestampMode
{
    /// <summary>把「修改时间」改成与「创建时间」一致。</summary>
    LastWriteEqualsCreation,

    /// <summary>把选定字段统一改成指定时间。</summary>
    SetFixedTime
}

/// <summary>要修改的时间字段（可组合）。</summary>
[Flags]
public enum TimestampField
{
    /// <summary>不修改任何字段。</summary>
    None = 0,

    /// <summary>创建时间。</summary>
    CreationTime = 1,

    /// <summary>修改时间。</summary>
    LastWriteTime = 2,

    /// <summary>访问时间。</summary>
    LastAccessTime = 4,

    /// <summary>创建时间 + 修改时间（默认）。</summary>
    CreationAndLastWrite = CreationTime | LastWriteTime
}

/// <summary>时间戳修改规则。</summary>
public sealed class TimestampOptions
{
    /// <summary>处理对象。</summary>
    public TimestampTarget Target { get; init; } = TimestampTarget.Files;

    /// <summary>是否递归处理子目录。</summary>
    public bool IncludeSubDirectories { get; init; } = true;

    /// <summary>修改模式。</summary>
    public TimestampMode Mode { get; init; } = TimestampMode.LastWriteEqualsCreation;

    /// <summary>「统一改成指定时间」模式下使用的时间。</summary>
    public DateTime FixedTime { get; init; } = DateTime.Now;

    /// <summary>「统一改成指定时间」模式下要修改的字段。</summary>
    public TimestampField Fields { get; init; } = TimestampField.CreationAndLastWrite;
}

/// <summary>一条时间戳预览。</summary>
public sealed class TimestampPreviewItem
{
    /// <summary>完整路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>是否是目录。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>当前创建时间。</summary>
    public DateTime CurrentCreationTime { get; init; }

    /// <summary>当前修改时间。</summary>
    public DateTime CurrentLastWriteTime { get; init; }

    /// <summary>当前访问时间。</summary>
    public DateTime CurrentLastAccessTime { get; init; }

    /// <summary>计划写入的创建时间（null 表示不修改）。</summary>
    public DateTime? NewCreationTime { get; init; }

    /// <summary>计划写入的修改时间（null 表示不修改）。</summary>
    public DateTime? NewLastWriteTime { get; init; }

    /// <summary>计划写入的访问时间（null 表示不修改）。</summary>
    public DateTime? NewLastAccessTime { get; init; }

    /// <summary>是否有变化。</summary>
    public bool WillChange =>
        (NewCreationTime is { } c && c != CurrentCreationTime) ||
        (NewLastWriteTime is { } w && w != CurrentLastWriteTime) ||
        (NewLastAccessTime is { } a && a != CurrentLastAccessTime);
}

/// <summary>时间戳预览结果。</summary>
public sealed class TimestampPreviewResult
{
    /// <summary>预览条目。</summary>
    public IReadOnlyList<TimestampPreviewItem> Items { get; init; } = Array.Empty<TimestampPreviewItem>();

    /// <summary>错误信息（正常为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>需要修改的数量。</summary>
    public int ChangeCount => Items.Count(static item => item.WillChange);

    /// <summary>一句话摘要。</summary>
    public string Summary => Error is not null
        ? "预览失败：" + Error
        : $"共 {Items.Count} 项，其中 {ChangeCount} 项需要修改。";
}

/// <summary>时间戳执行进度。</summary>
public sealed class TimestampProgress
{
    /// <summary>完成百分比（0-100）。</summary>
    public double Percent { get; init; }

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>当前处理的路径。</summary>
    public string CurrentPath { get; init; } = string.Empty;
}

/// <summary>单条时间戳修改结果。</summary>
public sealed class TimestampResultItem
{
    /// <summary>路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }
}

/// <summary>时间戳修改结果。</summary>
public sealed class TimestampResult
{
    /// <summary>明细。</summary>
    public IReadOnlyList<TimestampResultItem> Items { get; init; } = Array.Empty<TimestampResultItem>();

    /// <summary>成功数量。</summary>
    public int SucceededCount => Items.Count(static item => item.Success);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static item => !item.Success);

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary => $"时间戳修改完成：成功 {SucceededCount} 项，失败 {FailedCount} 项。";
}
