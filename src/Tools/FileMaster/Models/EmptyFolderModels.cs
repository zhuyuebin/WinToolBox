namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>一个「空文件夹」条目（其自身及所有子目录中都没有任何文件）。</summary>
public sealed class EmptyFolderItem
{
    /// <summary>完整路径。</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>相对扫描根目录的路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>相对根目录的层级（根的直接子目录为 1）。</summary>
    public int Depth { get; init; }
}

/// <summary>空目录扫描进度。</summary>
public sealed class EmptyFolderScanProgress
{
    /// <summary>已扫描目录数。</summary>
    public int ScannedDirectories { get; init; }

    /// <summary>已发现空目录数。</summary>
    public int FoundCount { get; init; }

    /// <summary>当前扫描的目录。</summary>
    public string CurrentPath { get; init; } = string.Empty;
}

/// <summary>空目录扫描结果。</summary>
public sealed class EmptyFolderScanResult
{
    /// <summary>扫描根目录。</summary>
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>发现的空目录（已按「深的在前」排序，便于直接删除）。</summary>
    public IReadOnlyList<EmptyFolderItem> Items { get; init; } = Array.Empty<EmptyFolderItem>();

    /// <summary>扫描过程中的错误（正常为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>发现数量。</summary>
    public int TotalCount => Items.Count;

    /// <summary>一句话摘要。</summary>
    public string Summary => Error is not null
        ? "扫描失败：" + Error
        : $"共发现 {TotalCount} 个空文件夹。";
}

/// <summary>删除进度。</summary>
public sealed class EmptyFolderDeleteProgress
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

/// <summary>单条删除结果。</summary>
public sealed class EmptyFolderDeleteItem
{
    /// <summary>目录路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>是否成功（目录已不存在也算成功）。</summary>
    public bool Success { get; init; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }
}

/// <summary>删除结果。</summary>
public sealed class EmptyFolderDeleteResult
{
    /// <summary>明细。</summary>
    public IReadOnlyList<EmptyFolderDeleteItem> Items { get; init; } = Array.Empty<EmptyFolderDeleteItem>();

    /// <summary>是否走回收站。</summary>
    public bool UsedRecycleBin { get; init; } = true;

    /// <summary>成功数量。</summary>
    public int DeletedCount => Items.Count(static item => item.Success);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static item => !item.Success);

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary =>
        $"清理完成：成功 {DeletedCount} 个{(UsedRecycleBin ? "（已放入回收站）" : "（已永久删除）")}，失败 {FailedCount} 个。";
}
