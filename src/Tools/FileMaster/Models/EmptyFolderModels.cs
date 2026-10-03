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

    /// <summary>
    /// 是否被跳过（没有删除）。
    /// <para>最典型的情况：扫描之后有人在目录里放了文件，删除前重新判空发现它已经不再为空，
    /// 此时保守地跳过而不是删掉里面的文件。</para>
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>被跳过时的原因。</summary>
    public string? SkipReason { get; init; }

    /// <summary>真正被删除（或被父目录一并删除）的条目。</summary>
    public bool Deleted => Success && !Skipped;
}

/// <summary>删除结果。</summary>
public sealed class EmptyFolderDeleteResult
{
    /// <summary>明细。</summary>
    public IReadOnlyList<EmptyFolderDeleteItem> Items { get; init; } = Array.Empty<EmptyFolderDeleteItem>();

    /// <summary>是否走回收站。</summary>
    public bool UsedRecycleBin { get; init; } = true;

    /// <summary>成功删除数量（不含被跳过的条目）。</summary>
    public int DeletedCount => Items.Count(static item => item.Deleted);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static item => !item.Success);

    /// <summary>因「删除前复查发现已不再为空」而跳过的数量。</summary>
    public int SkippedCount => Items.Count(static item => item.Skipped);

    /// <summary>是否没有失败（被跳过不算失败，但会在摘要中单独列出）。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary =>
        $"清理完成：成功 {DeletedCount} 个{(UsedRecycleBin ? "（已放入回收站）" : "（已永久删除）")}，失败 {FailedCount} 个" +
        (SkippedCount > 0 ? $"，跳过 {SkippedCount} 个（删除前复查发现已不再为空）。" : "。");
}
