namespace WinToolBox.Core.Services;

/// <summary>
/// 目录结构反向工具：从现有目录生成「短横线缩进」规则文本，或导出标准 tree 格式文本。
/// </summary>
/// <remarks>
/// <para>默认排除 <c>.git</c>、<c>node_modules</c>、<c>bin</c>、<c>obj</c>、<c>.vs</c>。</para>
/// <para>每一层级都按名称排序（序数、忽略大小写），保证同一目录多次生成结果完全一致。</para>
/// <para>不跟随符号链接 / 交接点，避免目录环导致无限递归。</para>
/// </remarks>
public sealed class RuleGenerator
{
    /// <summary>
    /// 最大层级，与 FolderCreator 的 <c>RuleParser.MaxDepth</c> 保持一致，
    /// 保证生成的规则一定能被解析（该一致性由 <c>FolderCreator.Tests</c> 中的契约测试守护）。
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>默认排除的目录 / 文件名。</summary>
    public static readonly IReadOnlyList<string> DefaultExcludes = new[]
    {
        ".git", "node_modules", "bin", "obj", ".vs"
    };

    /// <summary>规则行与树行的换行符（与界面文本框一致）。</summary>
    private const string LineBreak = "\r\n";

    private readonly Logger? _logger;

    /// <summary>创建实例。<paramref name="logger"/> 可为 null（仅不写日志）。</summary>
    public RuleGenerator(Logger? logger = null) => _logger = logger;

    /// <summary>
    /// 递归扫描 <paramref name="rootPath"/>，生成「短横线缩进」规则文本（如 <c>-一级目录</c>、<c>--二级目录</c>）。
    /// </summary>
    /// <param name="rootPath">目标根目录（其自身不会出现在结果里）。</param>
    /// <param name="excludes">要排除的目录 / 文件名；为 null 时使用 <see cref="DefaultExcludes"/>。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>规则文本；根目录下没有可用的子目录时返回空字符串。</returns>
    public string GenerateRuleFromDirectory(
        string rootPath,
        IEnumerable<string>? excludes = null,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateRoot(rootPath);
        var excludeSet = NormalizeExcludes(excludes);

        var lines = new List<string>();
        WalkDirectories(root, depth: 1, excludeSet, lines, cancellationToken);

        _logger?.Info($"反向生成规则完成：{root}，共 {lines.Count} 个目录");
        return string.Join(LineBreak, lines);
    }

    /// <summary>
    /// 递归扫描 <paramref name="rootPath"/>，生成标准 <c>tree</c> 格式文本。
    /// </summary>
    /// <param name="rootPath">目标根目录（第一行输出该路径）。</param>
    /// <param name="excludes">要排除的目录 / 文件名；为 null 时使用 <see cref="DefaultExcludes"/>。</param>
    /// <param name="includeFiles">是否同时列出文件（默认 false，等价于 <c>tree</c> 不带 <c>/F</c> 的行为）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public string GenerateTreeText(
        string rootPath,
        IEnumerable<string>? excludes = null,
        bool includeFiles = false,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateRoot(rootPath);
        var excludeSet = NormalizeExcludes(excludes);

        var builder = new System.Text.StringBuilder();
        builder.Append(root).Append(LineBreak);

        var directoryCount = 0;
        var fileCount = 0;

        WalkTree(
            root,
            prefix: string.Empty,
            depth: 1,
            excludeSet,
            includeFiles,
            builder,
            ref directoryCount,
            ref fileCount,
            cancellationToken);

        builder.Append(LineBreak)
            .Append(includeFiles ? $"{directoryCount} 个目录，{fileCount} 个文件" : $"{directoryCount} 个目录");

        _logger?.Info($"导出目录树完成：{root}，目录 {directoryCount} 个，文件 {fileCount} 个");
        return builder.ToString();
    }

    /// <summary>把排除项规范化为不区分大小写的集合；传入 null 时使用默认排除项。</summary>
    public static IReadOnlyCollection<string> NormalizeExcludes(IEnumerable<string>? excludes)
    {
        var source = excludes ?? DefaultExcludes;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                set.Add(item.Trim());
            }
        }

        return set;
    }

    /// <summary>校验根目录：为空抛 <see cref="ArgumentException"/>，不存在抛 <see cref="DirectoryNotFoundException"/>。</summary>
    private static string ValidateRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("根目录不能为空", nameof(rootPath));
        }

        var full = Path.GetFullPath(rootPath.Trim());
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"目录不存在：{full}");
        }

        return full;
    }

    /// <summary>深度优先遍历目录，输出「-」规则行（父级一定先于子级）。</summary>
    private void WalkDirectories(
        string directory,
        int depth,
        IReadOnlyCollection<string> excludes,
        List<string> lines,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth > MaxDepth)
        {
            _logger?.Warn($"目录层级超过上限 {MaxDepth} 级，已跳过更深的内容：{directory}");
            return;
        }

        foreach (var child in EnumerateDirectories(directory, excludes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            lines.Add(new string('-', depth) + Path.GetFileName(child));
            WalkDirectories(child, depth + 1, excludes, lines, cancellationToken);
        }
    }

    /// <summary>深度优先遍历目录，输出 tree 文本。</summary>
    private void WalkTree(
        string directory,
        string prefix,
        int depth,
        IReadOnlyCollection<string> excludes,
        bool includeFiles,
        System.Text.StringBuilder builder,
        ref int directoryCount,
        ref int fileCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth > MaxDepth)
        {
            _logger?.Warn($"目录层级超过上限 {MaxDepth} 级，已跳过更深的内容：{directory}");
            return;
        }

        var entries = new List<(string Name, string FullPath, bool IsDirectory)>();

        foreach (var child in EnumerateDirectories(directory, excludes))
        {
            entries.Add((Path.GetFileName(child), child, true));
        }

        if (includeFiles)
        {
            foreach (var file in EnumerateFiles(directory, excludes))
            {
                entries.Add((Path.GetFileName(file), file, false));
            }
        }

        // 目录与文件放在一起按名称排序，与 tree /F 的展示顺序一致
        entries.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[index];
            var isLast = index == entries.Count - 1;

            builder.Append(prefix)
                .Append(isLast ? "└─" : "├─")
                .Append(entry.Name)
                .Append(LineBreak);

            if (!entry.IsDirectory)
            {
                fileCount++;
                continue;
            }

            directoryCount++;
            WalkTree(
                entry.FullPath,
                prefix + (isLast ? "   " : "│  "),
                depth + 1,
                excludes,
                includeFiles,
                builder,
                ref directoryCount,
                ref fileCount,
                cancellationToken);
        }
    }

    /// <summary>枚举子目录：过滤排除项与符号链接，按名称排序；无权访问的目录会被跳过并记录日志。</summary>
    private IEnumerable<string> EnumerateDirectories(string directory, IReadOnlyCollection<string> excludes)
    {
        string[] children;

        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger?.Warn($"跳过无法访问的目录：{directory}", ex);
            yield break;
        }

        foreach (var child in SortByName(children))
        {
            if (IsExcluded(child, excludes))
            {
                continue;
            }

            if (IsReparsePoint(child))
            {
                _logger?.Warn($"跳过符号链接 / 交接点目录：{child}");
                continue;
            }

            yield return child;
        }
    }

    /// <summary>枚举文件：过滤排除项，按名称排序；无权访问时跳过。</summary>
    private IEnumerable<string> EnumerateFiles(string directory, IReadOnlyCollection<string> excludes)
    {
        string[] children;

        try
        {
            children = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger?.Warn($"跳过无法访问的目录：{directory}", ex);
            yield break;
        }

        foreach (var child in SortByName(children))
        {
            if (IsExcluded(child, excludes))
            {
                continue;
            }

            yield return child;
        }
    }

    private static string[] SortByName(string[] paths)
    {
        var sorted = (string[])paths.Clone();
        Array.Sort(sorted, static (left, right) =>
            string.Compare(Path.GetFileName(left), Path.GetFileName(right), StringComparison.OrdinalIgnoreCase));
        return sorted;
    }

    private static bool IsExcluded(string path, IReadOnlyCollection<string> excludes)
        => excludes.Contains(Path.GetFileName(path));

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到属性时按普通目录处理，后续遍历会用同样的方式容错
            return false;
        }
    }
}
