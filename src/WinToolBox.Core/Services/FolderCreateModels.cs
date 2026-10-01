namespace WinToolBox.Core.Services;

/// <summary>单个文件夹的处理结果。</summary>
public enum FolderCreateStatus
{
    /// <summary>本次新建。</summary>
    Created,

    /// <summary>已经存在，跳过。</summary>
    Existed,

    /// <summary>创建失败（权限不足、路径过长等）。</summary>
    Failed
}

/// <summary>单个文件夹的处理明细。</summary>
public sealed class FolderCreateItem
{
    /// <summary>相对根目录的路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>完整路径。</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>处理结果。</summary>
    public FolderCreateStatus Status { get; init; }

    /// <summary>补充说明（失败原因等）。</summary>
    public string? Message { get; init; }

    /// <summary>本次是否创建了占位文件。</summary>
    public bool PlaceholderCreated { get; init; }

    /// <summary>占位文件完整路径（未创建时为 null；已存在时也会给出路径）。</summary>
    public string? PlaceholderPath { get; init; }
}

/// <summary>单个根目录的批量创建结果。</summary>
public sealed class FolderCreateResult
{
    /// <summary>根目录（已规范化为完整路径）。</summary>
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>逐条明细，顺序与传入的相对路径一致。</summary>
    public IReadOnlyList<FolderCreateItem> Items { get; init; } = Array.Empty<FolderCreateItem>();

    /// <summary>整个根目录处理失败时的原因（例如路径非法、无法创建根目录）。</summary>
    public string? Error { get; init; }

    /// <summary>新建数量。</summary>
    public int CreatedCount => Items.Count(static item => item.Status == FolderCreateStatus.Created);

    /// <summary>已存在（跳过）数量。</summary>
    public int ExistedCount => Items.Count(static item => item.Status == FolderCreateStatus.Existed);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static item => item.Status == FolderCreateStatus.Failed);

    /// <summary>本次创建的占位文件数量。</summary>
    public int PlaceholderCount => Items.Count(static item => item.PlaceholderCreated);

    /// <summary>是否全部成功（根目录处理没有失败，且没有失败项）。</summary>
    public bool Success => Error is null && FailedCount == 0;

    /// <summary>中文摘要。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
            {
                return $"创建失败：{Error}";
            }

            var text = $"创建完成：新建 {CreatedCount} 个，跳过 {ExistedCount} 个已存在，失败 {FailedCount} 个。";
            return PlaceholderCount > 0 ? text + $" 另创建占位文件 {PlaceholderCount} 个。" : text;
        }
    }
}

/// <summary>单个根目录内的创建进度快照。</summary>
public sealed class FolderCreateProgress
{
    /// <summary>总任务数。</summary>
    public int Total { get; init; }

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>当前处理的相对路径。</summary>
    public string CurrentPath { get; init; } = string.Empty;

    /// <summary>完成百分比（0-100）。</summary>
    public double Percent => Total <= 0 ? 100d : Math.Round(Processed * 100d / Total, 1);
}

/// <summary>多根目录创建进度快照。</summary>
public sealed class MultiRootCreateProgress
{
    /// <summary>当前是第几个根目录（从 1 开始）。</summary>
    public int RootIndex { get; init; }

    /// <summary>根目录总数。</summary>
    public int RootTotal { get; init; }

    /// <summary>当前根目录。</summary>
    public string CurrentRoot { get; init; } = string.Empty;

    /// <summary>当前根目录内的规则总数。</summary>
    public int Total { get; init; }

    /// <summary>当前根目录内已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>当前处理的相对路径。</summary>
    public string CurrentPath { get; init; } = string.Empty;

    /// <summary>「当前处理第 X / 共 Y 个」提示文本。</summary>
    public string RootText => $"当前处理第 {RootIndex} / 共 {RootTotal} 个";

    /// <summary>整体完成百分比（0-100，按根目录数平均）。</summary>
    public double Percent
    {
        get
        {
            if (RootTotal <= 0)
            {
                return 100d;
            }

            var inner = Total <= 0 ? 1d : Math.Min(1d, Processed / (double)Total);
            return Math.Round((RootIndex - 1 + inner) * 100d / RootTotal, 1);
        }
    }
}

/// <summary>多根目录批量创建的结果汇总。</summary>
public sealed class MultiRootCreateResult
{
    /// <summary>各根目录的处理结果（顺序与去重后的根目录顺序一致）。</summary>
    public IReadOnlyList<FolderCreateResult> Results { get; init; } = Array.Empty<FolderCreateResult>();

    /// <summary>被跳过的根目录条目及原因（空行、路径非法、重复等）。</summary>
    public IReadOnlyList<string> SkippedRoots { get; init; } = Array.Empty<string>();

    /// <summary>实际处理的根目录数量。</summary>
    public int RootCount => Results.Count;

    /// <summary>新建数量合计。</summary>
    public int TotalCreated => Results.Sum(static result => result.CreatedCount);

    /// <summary>已存在（跳过）数量合计。</summary>
    public int TotalExisted => Results.Sum(static result => result.ExistedCount);

    /// <summary>失败数量合计。</summary>
    public int TotalFailed => Results.Sum(static result => result.FailedCount + (result.Error is null ? 0 : 1));

    /// <summary>创建的占位文件数量合计。</summary>
    public int TotalPlaceholders => Results.Sum(static result => result.PlaceholderCount);

    /// <summary>是否全部成功。</summary>
    public bool Success => Results.Count > 0 && Results.All(static result => result.Success);

    /// <summary>中文摘要。</summary>
    public string Summary
    {
        get
        {
            var text = $"多根目录创建完成：处理 {RootCount} 个根目录，新建 {TotalCreated} 个，跳过 {TotalExisted} 个已存在，失败 {TotalFailed} 个。";

            if (TotalPlaceholders > 0)
            {
                text += $" 另创建占位文件 {TotalPlaceholders} 个。";
            }

            return SkippedRoots.Count > 0 ? text + $" 已忽略 {SkippedRoots.Count} 个无效或重复路径。" : text;
        }
    }
}
