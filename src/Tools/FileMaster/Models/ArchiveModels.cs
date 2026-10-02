namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>自动分类规则类型。</summary>
public enum ArchiveRuleKind
{
    /// <summary>按扩展名归类。</summary>
    Extension,

    /// <summary>按日期归类（修改时间）。</summary>
    Date,

    /// <summary>按首字母归类。</summary>
    FirstLetter,

    /// <summary>兜底规则：前面规则都不匹配时使用。</summary>
    Fallback
}

/// <summary>移动 / 复制方式。</summary>
public enum ArchiveAction
{
    /// <summary>移动（源文件消失）。</summary>
    Move,

    /// <summary>复制（保留源文件）。</summary>
    Copy
}

/// <summary>一条自动分类规则。</summary>
public sealed class ArchiveRule
{
    /// <summary>规则类型。</summary>
    public ArchiveRuleKind Kind { get; init; } = ArchiveRuleKind.Extension;

    /// <summary>扩展名列表（仅 <see cref="ArchiveRuleKind.Extension"/> 使用），例如 <c>.jpg;.png</c>。</summary>
    public string Extensions { get; init; } = string.Empty;

    /// <summary>日期格式（仅 <see cref="ArchiveRuleKind.Date"/> 使用），例如 <c>yyyy-MM</c>。</summary>
    public string DateFormat { get; init; } = "yyyy-MM";

    /// <summary>目标子目录（相对目标根目录）；为空时按规则自动生成。</summary>
    public string TargetSubDirectory { get; init; } = string.Empty;

    /// <summary>规则原文（用于界面显示与错误提示）。</summary>
    public string SourceText { get; init; } = string.Empty;

    /// <summary>规则是否可用（解析出错时记录错误信息）。</summary>
    public string? Error { get; init; }
}

/// <summary>自动分类选项。</summary>
public sealed class ArchiveOptions
{
    /// <summary>源目录。</summary>
    public string SourceDirectory { get; init; } = string.Empty;

    /// <summary>目标目录。</summary>
    public string TargetDirectory { get; init; } = string.Empty;

    /// <summary>是否包含子目录中的文件。</summary>
    public bool IncludeSubDirectories { get; init; }

    /// <summary>移动还是复制。</summary>
    public ArchiveAction Action { get; init; } = ArchiveAction.Move;

    /// <summary>目标已存在同名文件时是否覆盖。</summary>
    public bool Overwrite { get; init; }

    /// <summary>移动后是否清理源目录中的空文件夹。</summary>
    public bool CleanEmptyFolders { get; init; }

    /// <summary>规则列表（按顺序匹配，<see cref="ArchiveRuleKind.Fallback"/> 最后匹配）。</summary>
    public IReadOnlyList<ArchiveRule> Rules { get; init; } = Array.Empty<ArchiveRule>();
}

/// <summary>规则解析结果。</summary>
public sealed class ArchiveRuleParseResult
{
    /// <summary>解析出的规则（按输入顺序）。</summary>
    public IReadOnlyList<ArchiveRule> Rules { get; init; } = Array.Empty<ArchiveRule>();

    /// <summary>错误列表（含行号）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>是否解析成功。</summary>
    public bool Success => Errors.Count == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary => Errors.Count > 0
        ? $"规则解析失败：{Errors.Count} 处错误。"
        : $"共 {Rules.Count} 条规则。";
}

/// <summary>一条分类预览。</summary>
public sealed class ArchivePlanItem
{
    /// <summary>源文件完整路径。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>目标文件完整路径。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>命中的规则类型。</summary>
    public ArchiveRuleKind RuleKind { get; init; }

    /// <summary>命中的规则描述。</summary>
    public string RuleDescription { get; init; } = string.Empty;

    /// <summary>是否可执行（目标已存在且未允许覆盖时为 false）。</summary>
    public bool CanApply { get; init; } = true;

    /// <summary>跳过 / 冲突原因。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>分类预览结果。</summary>
public sealed class ArchivePlan
{
    /// <summary>预览条目。</summary>
    public IReadOnlyList<ArchivePlanItem> Items { get; init; } = Array.Empty<ArchivePlanItem>();

    /// <summary>错误信息（正常为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>可执行数量。</summary>
    public int ReadyCount => Items.Count(static item => item.CanApply);

    /// <summary>跳过数量。</summary>
    public int SkippedCount => Items.Count(static item => !item.CanApply);

    /// <summary>一句话摘要。</summary>
    public string Summary => Error is not null
        ? "生成预览失败：" + Error
        : $"共 {Items.Count} 个文件：可执行 {ReadyCount}，跳过 {SkippedCount}。";
}

/// <summary>分类执行进度。</summary>
public sealed class ArchiveProgress
{
    /// <summary>完成百分比（0-100）。</summary>
    public double Percent { get; init; }

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>当前处理的文件。</summary>
    public string CurrentPath { get; init; } = string.Empty;
}

/// <summary>单条执行结果。</summary>
public sealed class ArchiveResultItem
{
    /// <summary>源路径。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>目标路径。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }
}

/// <summary>分类执行结果。</summary>
public sealed class ArchiveResult
{
    /// <summary>明细。</summary>
    public IReadOnlyList<ArchiveResultItem> Items { get; init; } = Array.Empty<ArchiveResultItem>();

    /// <summary>清理掉的空目录数量。</summary>
    public int RemovedEmptyFolderCount { get; init; }

    /// <summary>成功数量。</summary>
    public int SucceededCount => Items.Count(static item => item.Success);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static item => !item.Success);

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary =>
        $"分类完成：成功 {SucceededCount} 个文件，失败 {FailedCount} 个" +
        (RemovedEmptyFolderCount > 0 ? $"，清理空目录 {RemovedEmptyFolderCount} 个。" : "。");
}
