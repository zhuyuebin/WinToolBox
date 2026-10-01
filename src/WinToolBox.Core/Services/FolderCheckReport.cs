namespace WinToolBox.Core.Services;

/// <summary>检查条目类型。</summary>
public enum FolderCheckEntryKind
{
    /// <summary>匹配（规则要求且实际存在）。</summary>
    Matched,

    /// <summary>缺失（规则要求但实际不存在）。</summary>
    Missing,

    /// <summary>多余（实际存在但规则未要求）。</summary>
    Extra
}

/// <summary>单条检查结果。</summary>
public sealed class FolderCheckEntry
{
    /// <summary>相对根目录的路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>结果类型。</summary>
    public FolderCheckEntryKind Kind { get; init; }
}

/// <summary>
/// 检查报告数据（无 UI 依赖）：由工具层的检查结果转换而来，交给
/// <see cref="FolderCreatorService.ExportCheckReport"/> 写成 Markdown。
/// </summary>
public sealed class FolderCheckReport
{
    /// <summary>被检查的根目录。</summary>
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>检查模式文本（例如「严格模式」「宽松模式」）。</summary>
    public string ModeText { get; init; } = string.Empty;

    /// <summary>检查时间。</summary>
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>逐条检查结果。</summary>
    public IReadOnlyList<FolderCheckEntry> Entries { get; init; } = Array.Empty<FolderCheckEntry>();

    /// <summary>匹配数量。</summary>
    public int MatchedCount => Entries.Count(static entry => entry.Kind == FolderCheckEntryKind.Matched);

    /// <summary>缺失数量。</summary>
    public int MissingCount => Entries.Count(static entry => entry.Kind == FolderCheckEntryKind.Missing);

    /// <summary>多余数量。</summary>
    public int ExtraCount => Entries.Count(static entry => entry.Kind == FolderCheckEntryKind.Extra);

    /// <summary>是否完全一致（没有缺失、也没有多余）。</summary>
    public bool IsConsistent => MissingCount == 0 && ExtraCount == 0;
}
