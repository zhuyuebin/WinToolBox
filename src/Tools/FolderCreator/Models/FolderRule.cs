namespace WinToolBox.Tools.FolderCreator.Models;

/// <summary>
/// 一条文件夹规则：来自规则文本中某个以 <c>-</c> 开头的行。
/// </summary>
public sealed class FolderRule
{
    /// <summary>父级缺失时自动补齐所用的目录名。</summary>
    public const string AutoCreatedName = "未命名目录";

    /// <summary>目录名（不含路径分隔符）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>层级深度，从 1 开始（1 个 <c>-</c> 为第 1 级）。</summary>
    public int Depth { get; init; }

    /// <summary>规则文本中的行号（从 1 开始）。</summary>
    public int LineNumber { get; init; }

    /// <summary>原始行内容。</summary>
    public string RawLine { get; init; } = string.Empty;

    /// <summary>相对根目录的路径，例如 <c>一级目录\二级目录</c>。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>是否为“父级自动补齐”生成的目录。</summary>
    public bool IsAutoCreated { get; init; }

    /// <inheritdoc />
    public override string ToString() => RelativePath;
}

/// <summary>
/// 规则解析结果。解析失败（如非法字符）时 <see cref="Success"/> 为 false，
/// 具体原因在 <see cref="Errors"/> 中（含行号，可直接展示给用户）。
/// </summary>
public sealed class RuleParseResult
{
    /// <summary>是否全部解析成功（没有错误）。</summary>
    public bool Success { get; init; }

    /// <summary>解析出的规则，按文档顺序排列（父级一定在子级之前），包含自动补齐的父级。</summary>
    public IReadOnlyList<FolderRule> Rules { get; init; } = Array.Empty<FolderRule>();

    /// <summary>错误信息（含行号）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>警告信息（含行号），例如自动补齐父级、重复规则。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>中文摘要，适合直接显示在状态栏。</summary>
    public string Summary
    {
        get
        {
            if (Errors.Count > 0)
            {
                return $"解析失败：{Errors.Count} 处错误，已识别 {Rules.Count} 条规则。";
            }

            if (Rules.Count == 0)
            {
                return "没有解析到任何规则（请用行首的 - 表示层级）。";
            }

            var autoCreated = Rules.Count(static r => r.IsAutoCreated);
            return autoCreated > 0
                ? $"解析成功：共 {Rules.Count} 条规则（其中 {autoCreated} 条为自动补齐的父级）。"
                : $"解析成功：共 {Rules.Count} 条规则。";
        }
    }
}
