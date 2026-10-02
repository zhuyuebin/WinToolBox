namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>差异比对方式。</summary>
public enum DiffCompareMode
{
    /// <summary>比较大小与修改时间（推荐，速度快）。</summary>
    SizeAndTime,

    /// <summary>只比较文件大小。</summary>
    SizeOnly,

    /// <summary>比较 SHA256 哈希（最准确，慢）。</summary>
    Hash
}

/// <summary>一个路径在两个目录中的差异状态。</summary>
public enum DiffStatus
{
    /// <summary>两侧都有且内容一致。</summary>
    Same,

    /// <summary>只有左侧存在。</summary>
    LeftOnly,

    /// <summary>只有右侧存在。</summary>
    RightOnly,

    /// <summary>两侧都有但内容不同。</summary>
    Different
}

/// <summary>差异比对选项。</summary>
public sealed class FolderDiffOptions
{
    /// <summary>左侧目录。</summary>
    public string LeftDirectory { get; init; } = string.Empty;

    /// <summary>右侧目录。</summary>
    public string RightDirectory { get; init; } = string.Empty;

    /// <summary>是否递归子目录。</summary>
    public bool IncludeSubDirectories { get; init; } = true;

    /// <summary>比对方式。</summary>
    public DiffCompareMode CompareMode { get; init; } = DiffCompareMode.SizeAndTime;

    /// <summary>修改时间允许的误差（秒）；两侧差值在误差内视为相同。</summary>
    public int TimeToleranceSeconds { get; init; } = 2;

    /// <summary>需要忽略的文件 / 目录名（分号分隔，不区分大小写）。</summary>
    public string? ExcludeNames { get; init; }
}

/// <summary>一条差异条目。</summary>
public sealed class FolderDiffItem
{
    /// <summary>相对路径（两侧一致的部分）。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>差异状态。</summary>
    public DiffStatus Status { get; init; }

    /// <summary>左侧完整路径（不存在时为 null）。</summary>
    public string? LeftPath { get; init; }

    /// <summary>右侧完整路径（不存在时为 null）。</summary>
    public string? RightPath { get; init; }

    /// <summary>左侧大小（字节，不存在时为 null）。</summary>
    public long? LeftSize { get; init; }

    /// <summary>右侧大小（字节，不存在时为 null）。</summary>
    public long? RightSize { get; init; }

    /// <summary>左侧修改时间。</summary>
    public DateTime? LeftModified { get; init; }

    /// <summary>右侧修改时间。</summary>
    public DateTime? RightModified { get; init; }

    /// <summary>是否是目录。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>差异说明。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>差异比对进度。</summary>
public sealed class FolderDiffProgress
{
    /// <summary>已比较的条目数。</summary>
    public int ComparedCount { get; init; }

    /// <summary>当前处理的相对路径。</summary>
    public string CurrentPath { get; init; } = string.Empty;
}

/// <summary>差异比对结果。</summary>
public sealed class FolderDiffResult
{
    /// <summary>差异条目。</summary>
    public IReadOnlyList<FolderDiffItem> Items { get; init; } = Array.Empty<FolderDiffItem>();

    /// <summary>错误信息（正常为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>相同数量。</summary>
    public int SameCount => Items.Count(static item => item.Status == DiffStatus.Same);

    /// <summary>仅左侧数量。</summary>
    public int LeftOnlyCount => Items.Count(static item => item.Status == DiffStatus.LeftOnly);

    /// <summary>仅右侧数量。</summary>
    public int RightOnlyCount => Items.Count(static item => item.Status == DiffStatus.RightOnly);

    /// <summary>内容不同数量。</summary>
    public int DifferentCount => Items.Count(static item => item.Status == DiffStatus.Different);

    /// <summary>是否完全一致。</summary>
    public bool IsIdentical => Error is null && LeftOnlyCount == 0 && RightOnlyCount == 0 && DifferentCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary => Error is not null
        ? "比对失败：" + Error
        : $"共 {Items.Count} 项：相同 {SameCount}，仅左侧 {LeftOnlyCount}，仅右侧 {RightOnlyCount}，内容不同 {DifferentCount}。";
}
