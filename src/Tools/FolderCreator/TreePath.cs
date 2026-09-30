namespace WinToolBox.Tools.FolderCreator;

/// <summary>
/// 结果树的节点键工具。
/// </summary>
/// <remarks>
/// 结果树把「从根到本级的完整相对路径」存进 <see cref="System.Windows.Forms.TreeNode.Name"/>，
/// 用于在同一父级下唯一定位节点。拼接时**必须把当前层级名一起拼进去**：
/// 否则同一父级下的多个子节点会得到相同的键，后一个节点会复用并覆盖前一个节点
/// —— 曾经因此出现「严格模式下缺失项被多余项覆盖、界面上看不到缺失」的缺陷。
/// </remarks>
public static class TreePath
{
    /// <summary>相对路径分隔符（与规则解析保持一致）。</summary>
    public const char Separator = '\\';

    /// <summary>拼接父级键与本级目录名；父级键为空时直接返回本级目录名。</summary>
    public static string Combine(string? parentPath, string segment)
        => string.IsNullOrEmpty(parentPath) ? segment : parentPath + Separator + segment;

    /// <summary>
    /// 把一条相对路径展开成逐级节点键（父级在前）。
    /// 例如 <c>一级\二级</c> → <c>["一级", "一级\二级"]</c>。
    /// </summary>
    public static IReadOnlyList<string> KeysFor(string? relativePath)
    {
        var keys = new List<string>();

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return keys;
        }

        var segments = relativePath.Split(
            new[] { Separator, '/' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var current = string.Empty;
        foreach (var segment in segments)
        {
            current = Combine(current, segment);
            keys.Add(current);
        }

        return keys;
    }
}
