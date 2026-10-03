using System.Diagnostics.CodeAnalysis;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 路径安全辅助：判断包含关系、把「相对子目录」安全地解析到某个根目录之内。
/// </summary>
/// <remarks>
/// 这些判断反复出现在「自动分类」「同步」「重复查找」等地方，且都是**安全边界**，
/// 因此集中到一处，避免某个调用点又退回裸 <c>StartsWith</c> 或直接 <c>Path.Combine</c>。
/// <para>核心原则：比较前先把两侧都「去尾分隔符、再补一个分隔符」，这样</para>
/// <list type="bullet">
/// <item><c>D:\foo</c> 与 <c>D:\foo\</c> 被视为同一路径；</item>
/// <item><c>D:\foobar</c> 不会被误判成 <c>D:\foo</c> 的子路径。</item>
/// </list>
/// </remarks>
internal static class PathSafety
{
    /// <summary>把路径规范化成可直接做前缀比较的形式（绝对路径 + 去尾分隔符 + 补一个分隔符）。</summary>
    private static string Normalize(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

    /// <summary>两个路径是否指向同一个位置（忽略大小写与尾分隔符差异）。</summary>
    public static bool IsSamePath(string pathA, string pathB)
        => string.Equals(
            Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="candidate"/> 是否**严格位于** <paramref name="root"/> 之内。
    /// </summary>
    /// <remarks>
    /// 用补分隔符前缀比较而不是裸 <c>StartsWith</c>：后者会把
    /// <c>D:\database</c> 误判成 <c>D:\data</c> 的子目录。
    /// </remarks>
    public static bool IsChildPath(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return Normalize(candidate).StartsWith(Normalize(root), StringComparison.OrdinalIgnoreCase)
                   && !IsSamePath(candidate, root);
        }
        catch (Exception)
        {
            // 路径非法时保守返回 false（由调用方另行报错）
            return false;
        }
    }

    /// <summary><paramref name="candidate"/> 是否等于 <paramref name="root"/> 或位于其内。</summary>
    public static bool IsSameOrChildPath(string candidate, string root)
        => IsSamePath(candidate, root) || IsChildPath(candidate, root);

    /// <summary>
    /// 把「相对子目录」解析成 <paramref name="root"/> 内的绝对路径。
    /// </summary>
    /// <param name="root">目标根目录（允许尚不存在）。</param>
    /// <param name="relativeSubDirectory">用户提供的相对子目录，例如 <c>图片/2026</c>。</param>
    /// <param name="resolved">成功时为根目录之内的绝对路径。</param>
    /// <returns>解析并校验通过返回 true；越界（<c>..</c>、绝对路径、盘符等）返回 false。</returns>
    /// <remarks>
    /// 这是防「写到目标目录之外」的关键：完全信任输入就等同于允许
    /// <c>..\..\Windows\Temp</c> 或 <c>D:\elsewhere</c> 把文件写到别处。
    /// </remarks>
    public static bool TryResolveInside(
        string root,
        string relativeSubDirectory,
        [NotNullWhen(true)] out string? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            var combined = Path.Combine(rootFull, relativeSubDirectory ?? string.Empty);
            var combinedFull = Path.GetFullPath(combined);

            // GetFullPath 会解析掉 .. 与 .，因此越界会体现为「不在根目录之内」
            if (!IsSameOrChildPath(combinedFull, rootFull))
            {
                return false;
            }

            // 仅靠 GetFullPath 还不够：Win32 会在打开路径时**去掉每个路径段末尾的空格与点**，
            // 于是 `target\.. \outside`（段名「.. 」）在字符串上看似在 target 之内，
            // 实际却被解析成 `target\..\outside` —— 一个真实的越界写。
            // 必须在字符串层面就把这类段拒掉。
            if (HasWin32AmbiguousSegment(combinedFull, rootFull))
            {
                return false;
            }

            resolved = combinedFull;
            return true;
        }
        catch (Exception)
        {
            // 非法字符、超长路径等：一律判为不可用
            return false;
        }
    }

    /// <summary>
    /// 判断「根目录之下」的相对段里是否存在会被 Win32 归一化改变含义的段。
    /// </summary>
    /// <remarks>
    /// 规则：把每个段末尾的空格与点去掉后，
    /// <list type="bullet">
    /// <item>结果为空（如 <c>"..."</c>、<c>"   "</c>）→ 该段会被 Win32 整个丢掉，路径含义改变；</item>
    /// <item>结果是 <c>.</c> 或 <c>..</c>（如 <c>".. "</c>、<c>".. ."</c>、<c>". "</c>）→
    /// 会在打开时变成上层目录引用，<b>直接越界</b>。</item>
    /// </list>
    /// 这两种情况一律拒绝，宁可让用户改个目录名，也不能把文件写到目标之外。
    /// </remarks>
    private static bool HasWin32AmbiguousSegment(string candidateFull, string rootFull)
    {
        var relative = Path.GetRelativePath(rootFull, candidateFull);

        if (relative.Length == 0 || relative == ".")
        {
            return false;
        }

        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var trimmed = segment.TrimEnd(' ', '.');

            // 整段只有空格/点 → Win32 会把它丢掉，路径层级与字符串不一致
            if (trimmed.Length == 0)
            {
                return true;
            }

            // 「.. 」「.. .」「. 」这类会被归一化成目录引用
            if (trimmed is "." or "..")
            {
                return true;
            }
        }

        return false;
    }
}
