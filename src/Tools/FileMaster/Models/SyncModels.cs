namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>文件夹同步模式。</summary>
public enum SyncMode
{
    /// <summary>单向复制：把源目录中新增 / 更新的文件复制到目标，不删除目标中多余的内容。</summary>
    CopyOnly,

    /// <summary>单向同步：复制新增 / 更新，并删除目标中源目录不存在的内容。</summary>
    OneWaySync,

    /// <summary>镜像：目标完全等于源（复制 + 删除 + 强制重写全部文件）。</summary>
    Mirror
}

/// <summary>同步计划中的动作类型。</summary>
public enum SyncActionKind
{
    /// <summary>创建目录。</summary>
    CreateDirectory,

    /// <summary>复制新文件。</summary>
    CopyFile,

    /// <summary>覆盖更新文件。</summary>
    UpdateFile,

    /// <summary>删除多余文件。</summary>
    DeleteFile,

    /// <summary>删除多余目录。</summary>
    DeleteDirectory
}

/// <summary>文件夹同步选项。</summary>
public sealed class FolderSyncOptions
{
    /// <summary>源目录。</summary>
    public string SourceDirectory { get; init; } = string.Empty;

    /// <summary>目标目录。</summary>
    public string TargetDirectory { get; init; } = string.Empty;

    /// <summary>同步模式。</summary>
    public SyncMode Mode { get; init; } = SyncMode.OneWaySync;

    /// <summary>是否递归子目录。</summary>
    public bool IncludeSubDirectories { get; init; } = true;

    /// <summary>是否用哈希（SHA256）比较内容，而不是「大小 + 修改时间」。</summary>
    public bool CompareByHash { get; init; }

    /// <summary>删除操作是否走回收站（默认 true）。</summary>
    public bool UseRecycleBin { get; init; } = true;

    /// <summary>需要忽略的文件 / 目录名（分号或换行分隔）。</summary>
    public string? ExcludeNames { get; init; }
}

/// <summary>同步计划中的一条动作。</summary>
public sealed class SyncPlanItem
{
    /// <summary>动作类型。</summary>
    public SyncActionKind Kind { get; init; }

    /// <summary>相对路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>源路径（删除动作时为空）。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>目标路径。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>文件大小（目录或删除动作可能为 0）。</summary>
    public long Size { get; init; }

    /// <summary>执行原因说明。</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>同步计划。</summary>
public sealed class SyncPlan
{
    /// <summary>动作列表（删除在前、复制在后）。</summary>
    public IReadOnlyList<SyncPlanItem> Items { get; init; } = Array.Empty<SyncPlanItem>();

    /// <summary>错误信息（正常为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>要复制的文件数（新增 + 更新）。</summary>
    public int CopyCount => Items.Count(static item =>
        item.Kind is SyncActionKind.CopyFile or SyncActionKind.UpdateFile);

    /// <summary>要创建的目录数。</summary>
    public int CreateDirectoryCount => Items.Count(static item => item.Kind == SyncActionKind.CreateDirectory);

    /// <summary>要删除的文件数。</summary>
    public int DeleteFileCount => Items.Count(static item => item.Kind == SyncActionKind.DeleteFile);

    /// <summary>要删除的目录数。</summary>
    public int DeleteDirectoryCount => Items.Count(static item => item.Kind == SyncActionKind.DeleteDirectory);

    /// <summary>要复制的总字节数。</summary>
    public long CopyBytes => Items
        .Where(static item => item.Kind is SyncActionKind.CopyFile or SyncActionKind.UpdateFile)
        .Sum(static item => item.Size);

    /// <summary>是否有任何动作。</summary>
    public bool HasChanges => Items.Count > 0;

    /// <summary>一句话摘要。</summary>
    public string Summary => Error is not null
        ? "生成同步计划失败：" + Error
        : $"计划：复制 {CopyCount} 个文件（{DuplicateScanResult.FormatSize(CopyBytes)}），" +
          $"新建目录 {CreateDirectoryCount} 个，删除文件 {DeleteFileCount} 个，删除目录 {DeleteDirectoryCount} 个。";
}

/// <summary>同步进度。</summary>
public sealed class SyncProgress
{
    /// <summary>完成百分比（0-100）。</summary>
    public double Percent { get; init; }

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>当前动作说明。</summary>
    public string CurrentAction { get; init; } = string.Empty;
}

/// <summary>单条同步执行结果。</summary>
public sealed class SyncResultItem
{
    /// <summary>动作类型。</summary>
    public SyncActionKind Kind { get; init; }

    /// <summary>相对路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 是否被【有意跳过】（没有执行，也不是失败）。
    /// <para>最典型的情况：复制阶段出现失败时，为免目标比同步前更少而整体跳过删除。</para>
    /// <para>跳过的项不计入 <see cref="SyncResult.FailedCount"/>，否则界面会把「保护性跳过」显示成失败。</para>
    /// </summary>
    public bool Skipped { get; init; }
}

/// <summary>同步执行结果。</summary>
public sealed class SyncResult
{
    /// <summary>明细。</summary>
    public IReadOnlyList<SyncResultItem> Items { get; init; } = Array.Empty<SyncResultItem>();

    /// <summary>复制的字节数。</summary>
    public long CopiedBytes { get; init; }

    /// <summary>删除的文件数。</summary>
    public int DeletedFileCount => Items.Count(static item =>
        item.Success && item.Kind == SyncActionKind.DeleteFile);

    /// <summary>成功数量。</summary>
    public int SucceededCount => Items.Count(static item => item.Success);

    /// <summary>被有意跳过（例如复制失败时保护性跳过删除）的数量。</summary>
    public int SkippedCount => Items.Count(static item => item.Skipped);

    /// <summary>失败数量（不含被有意跳过的项）。</summary>
    public int FailedCount => Items.Count(static item => !item.Success && !item.Skipped);

    /// <summary>是否没有失败项（被跳过的项不算失败，但会在摘要里单独说明）。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary =>
        $"同步完成：成功 {SucceededCount} 项（复制 {DuplicateScanResult.FormatSize(CopiedBytes)}，" +
        $"删除文件 {DeletedFileCount} 个），失败 {FailedCount} 项" +
        (SkippedCount > 0 ? $"，跳过 {SkippedCount} 项（因复制失败已跳过删除）。" : "。");
}
