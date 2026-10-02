namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>批量重命名的处理对象。</summary>
public enum RenameTarget
{
    /// <summary>只处理文件。</summary>
    Files,

    /// <summary>只处理文件夹。</summary>
    Folders,

    /// <summary>文件与文件夹都处理。</summary>
    Both
}

/// <summary>序号 / 日期的插入位置。</summary>
public enum RenameAffixPosition
{
    /// <summary>加在原名称前面。</summary>
    Prefix,

    /// <summary>加在原名称后面（扩展名之前）。</summary>
    Suffix
}

/// <summary>日期取值来源。</summary>
public enum RenameDateSource
{
    /// <summary>创建时间。</summary>
    CreationTime,

    /// <summary>修改时间。</summary>
    LastWriteTime
}

/// <summary>重命名条目类型。</summary>
public enum RenameItemKind
{
    /// <summary>文件。</summary>
    File,

    /// <summary>文件夹。</summary>
    Folder
}

/// <summary>重命名预览条目的状态。</summary>
public enum RenameItemStatus
{
    /// <summary>可以重命名。</summary>
    Ready,

    /// <summary>名称没有变化，跳过。</summary>
    Unchanged,

    /// <summary>目标名称已被占用（冲突）。</summary>
    Conflict,

    /// <summary>规则生成的名称非法。</summary>
    Invalid
}

/// <summary>
/// 批量重命名规则。所有规则按固定顺序执行：
/// 删除字符 → 查找替换 → 前缀 → 后缀 → 序号 → 日期 → 统一扩展名 → 清理非法字符。
/// </summary>
public sealed class RenameOptions
{
    /// <summary>处理对象：文件 / 文件夹 / 两者。</summary>
    public RenameTarget Target { get; init; } = RenameTarget.Files;

    /// <summary>是否递归处理子目录。</summary>
    public bool IncludeSubDirectories { get; init; }

    /// <summary>要删除的字符集合（逐个字符删除，例如 <c>_- </c>）。</summary>
    public string? RemoveChars { get; init; }

    /// <summary>查找内容（<see cref="UseRegex"/> 为 true 时是正则表达式）。</summary>
    public string? FindText { get; init; }

    /// <summary>替换内容（正则模式下支持 <c>$1</c> 分组引用）。</summary>
    public string? ReplaceText { get; init; }

    /// <summary>查找是否使用正则表达式。</summary>
    public bool UseRegex { get; init; }

    /// <summary>查找是否区分大小写（仅文本模式生效）。</summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>加在原名称前面的文本。</summary>
    public string? Prefix { get; init; }

    /// <summary>加在原名称后面（扩展名之前）的文本。</summary>
    public string? Suffix { get; init; }

    /// <summary>序号起始值；为 null 表示不添加序号。</summary>
    public int? SequenceStart { get; init; }

    /// <summary>序号位数（不足补 0）。</summary>
    public int SequenceDigits { get; init; } = 3;

    /// <summary>序号与名称之间的分隔符。</summary>
    public string SequenceSeparator { get; init; } = "_";

    /// <summary>序号插入位置。</summary>
    public RenameAffixPosition SequencePosition { get; init; } = RenameAffixPosition.Suffix;

    /// <summary>日期格式（例如 <c>yyyyMMdd</c>）；为 null 表示不添加日期。</summary>
    public string? DateFormat { get; init; }

    /// <summary>日期取值来源。</summary>
    public RenameDateSource DateSource { get; init; } = RenameDateSource.LastWriteTime;

    /// <summary>日期插入位置。</summary>
    public RenameAffixPosition DatePosition { get; init; } = RenameAffixPosition.Suffix;

    /// <summary>是否保护扩展名（文件名主体参与重命名，扩展名保持不变）。</summary>
    public bool KeepExtension { get; init; } = true;

    /// <summary>统一替换成的新扩展名（例如 <c>.txt</c>）；为 null 表示不改扩展名。</summary>
    public string? NewExtension { get; init; }

    /// <summary>名称排序方式：true 表示按创建时间排序，false 表示按名称排序（决定序号顺序）。</summary>
    public bool OrderByCreationTime { get; init; }
}

/// <summary>一条重命名预览。</summary>
public sealed class RenamePlanItem
{
    /// <summary>原完整路径。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>原名称（文件名或目录名）。</summary>
    public string OriginalName { get; init; } = string.Empty;

    /// <summary>规则生成的新名称。</summary>
    public string NewName { get; init; } = string.Empty;

    /// <summary>新完整路径。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>条目类型。</summary>
    public RenameItemKind Kind { get; init; }

    /// <summary>预览状态。</summary>
    public RenameItemStatus Status { get; init; }

    /// <summary>补充说明（冲突原因等）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>是否参与实际重命名。</summary>
    public bool CanApply => Status == RenameItemStatus.Ready;
}

/// <summary>重命名预览结果。</summary>
public sealed class RenamePlan
{
    /// <summary>预览条目（与输入顺序一致）。</summary>
    public IReadOnlyList<RenamePlanItem> Items { get; init; } = Array.Empty<RenamePlanItem>();

    /// <summary>可执行条数。</summary>
    public int ReadyCount => Items.Count(static item => item.Status == RenameItemStatus.Ready);

    /// <summary>名称未变化条数。</summary>
    public int UnchangedCount => Items.Count(static item => item.Status == RenameItemStatus.Unchanged);

    /// <summary>冲突条数。</summary>
    public int ConflictCount => Items.Count(static item => item.Status == RenameItemStatus.Conflict);

    /// <summary>非法条数。</summary>
    public int InvalidCount => Items.Count(static item => item.Status == RenameItemStatus.Invalid);

    /// <summary>扫描过程中的错误（正常为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>一句话摘要。</summary>
    public string Summary =>
        Error is not null
            ? $"扫描失败：{Error}"
            : $"共 {Items.Count} 项：可重命名 {ReadyCount}，未变化 {UnchangedCount}，冲突 {ConflictCount}，非法 {InvalidCount}";
}

/// <summary>重命名执行进度。</summary>
public sealed class RenameProgress
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

/// <summary>一条重命名执行结果。</summary>
public sealed class RenameResultItem
{
    /// <summary>原路径。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>新路径。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }
}

/// <summary>重命名执行结果。</summary>
public sealed class RenameResult
{
    /// <summary>明细。</summary>
    public IReadOnlyList<RenameResultItem> Items { get; init; } = Array.Empty<RenameResultItem>();

    /// <summary>成功数量。</summary>
    public int SucceededCount => Items.Count(static item => item.Success);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static item => !item.Success);

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary => $"重命名完成：成功 {SucceededCount} 项，失败 {FailedCount} 项";
}
