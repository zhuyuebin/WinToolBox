using System.Text;

namespace WinToolBox.Core.Services;

/// <summary>
/// FolderCreator 的核心业务逻辑（无 UI 依赖）：按相对路径批量创建文件夹、
/// 为空目录创建占位文件、以及多根目录批量创建。
/// </summary>
/// <remarks>
/// <para>单个文件夹创建失败只记入结果，不中断整体流程；多根目录模式下某个根目录失败也不影响其它根目录。</para>
/// <para>占位文件只在「真正为空」的目录（既没有文件也没有子目录）里创建；已存在则跳过且不报错。</para>
/// </remarks>
public sealed class FolderCreatorService
{
    /// <summary>默认占位文件名。</summary>
    public const string DefaultPlaceholderName = ".gitkeep";

    private readonly Logger? _logger;

    /// <summary>创建服务实例。<paramref name="logger"/> 可为 null（仅不写日志）。</summary>
    public FolderCreatorService(Logger? logger = null) => _logger = logger;

    /// <summary>
    /// 在单个根目录下批量创建文件夹（父级自动递归创建，已存在则跳过）。
    /// </summary>
    /// <param name="rootDirectory">根目录；不存在时会被自动创建。</param>
    /// <param name="relativePaths">相对根目录的路径列表（空白项会被忽略）。</param>
    /// <param name="createPlaceholder">是否为空目录创建占位文件。</param>
    /// <param name="placeholderName">占位文件名，默认 <c>.gitkeep</c>。</param>
    /// <param name="progress">进度回调（在调用线程上同步触发）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public FolderCreateResult CreateFolders(
        string rootDirectory,
        IEnumerable<string> relativePaths,
        bool createPlaceholder = false,
        string placeholderName = DefaultPlaceholderName,
        IProgress<FolderCreateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(relativePaths);
        cancellationToken.ThrowIfCancellationRequested();

        var root = ResolveRoot(rootDirectory);
        var paths = relativePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .ToList();

        var placeholder = ResolvePlaceholderName(placeholderName);

        // 根目录本身不存在时一并创建，避免后续每一条都失败
        TryCreateRoot(root);

        var items = new List<FolderCreateItem>(paths.Count);
        var processed = 0;

        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(root, relativePath);
            FolderCreateItem item;

            try
            {
                var existed = Directory.Exists(fullPath);
                if (!existed)
                {
                    Directory.CreateDirectory(fullPath);
                }

                item = new FolderCreateItem
                {
                    RelativePath = relativePath,
                    FullPath = fullPath,
                    Status = existed ? FolderCreateStatus.Existed : FolderCreateStatus.Created,
                    Message = existed ? "已存在，跳过" : null
                };
            }
            catch (Exception ex)
            {
                item = new FolderCreateItem
                {
                    RelativePath = relativePath,
                    FullPath = fullPath,
                    Status = FolderCreateStatus.Failed,
                    Message = DescribeError(ex)
                };

                _logger?.Warn($"创建文件夹失败：{fullPath}", ex);
            }

            items.Add(item);

            processed++;
            progress?.Report(new FolderCreateProgress
            {
                Total = paths.Count,
                Processed = processed,
                CurrentPath = relativePath
            });
        }

        // 占位文件放在「全部目录创建完成之后」再判断：
        // 这样父目录一旦有了子目录就不再是空目录，只有真正的空目录（叶子）才会生成占位文件。
        if (createPlaceholder)
        {
            ApplyPlaceholders(items, placeholder, cancellationToken);
        }

        var result = new FolderCreateResult
        {
            RootDirectory = root,
            Items = items
        };

        _logger?.Info($"文件夹创建完成：根目录 {root}；{result.Summary}");

        return result;
    }

    /// <summary>
    /// 在多个根目录下批量创建同一套文件夹结构。
    /// </summary>
    /// <remarks>
    /// 会先去重（忽略大小写）并剔除空行 / 非法路径；单个根目录失败只记入结果，不中断其它根目录。
    /// </remarks>
    /// <param name="rootDirectories">根目录列表（每行一个）。</param>
    /// <param name="relativePaths">相对路径列表。</param>
    /// <param name="createPlaceholder">是否为空目录创建占位文件。</param>
    /// <param name="placeholderName">占位文件名，默认 <c>.gitkeep</c>。</param>
    /// <param name="progress">进度回调（含「当前处理第 X / 共 Y 个」）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public MultiRootCreateResult CreateFoldersInMultipleRoots(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> relativePaths,
        bool createPlaceholder = false,
        string placeholderName = DefaultPlaceholderName,
        IProgress<MultiRootCreateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootDirectories);
        ArgumentNullException.ThrowIfNull(relativePaths);
        cancellationToken.ThrowIfCancellationRequested();

        var paths = relativePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .ToList();

        var roots = new List<string>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in rootDirectories)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                skipped.Add("（空行）");
                continue;
            }

            if (!TryNormalizeRoot(entry, out var fullPath, out var reason))
            {
                skipped.Add($"{entry.Trim()}（{reason}）");
                _logger?.Warn($"跳过多根目录中的无效路径：{entry.Trim()}（{reason}）");
                continue;
            }

            if (!seen.Add(fullPath))
            {
                skipped.Add($"{fullPath}（重复，已忽略）");
                continue;
            }

            roots.Add(fullPath);
        }

        var results = new List<FolderCreateResult>(roots.Count);

        for (var index = 0; index < roots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = roots[index];
            var bridge = progress is null
                ? null
                : new MultiRootProgressBridge(progress, root, index + 1, roots.Count);

            try
            {
                results.Add(CreateFolders(
                    root,
                    paths,
                    createPlaceholder,
                    placeholderName,
                    bridge,
                    cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error($"根目录处理失败：{root}", ex);
                results.Add(new FolderCreateResult
                {
                    RootDirectory = root,
                    Items = Array.Empty<FolderCreateItem>(),
                    Error = DescribeError(ex)
                });
            }
        }

        var multiResult = new MultiRootCreateResult
        {
            Results = results,
            SkippedRoots = skipped
        };

        _logger?.Info(multiResult.Summary);
        return multiResult;
    }

    /// <summary>报告里某一段超过该条数时改用 &lt;details&gt; 折叠，保证 Markdown 可读性。</summary>
    public const int ReportFoldThreshold = 100;

    /// <summary>
    /// 把检查结果导出为 Markdown 报告（含时间、目标目录、检查模式、统计与详细列表）。
    /// </summary>
    /// <param name="filePath">目标文件路径（.md）；目录不存在时会自动创建。</param>
    /// <param name="report">检查报告数据。</param>
    /// <returns>写入的完整路径。</returns>
    public string ExportCheckReport(string filePath, FolderCheckReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(report);

        var fullPath = Path.GetFullPath(filePath);
        var markdown = BuildCheckReport(report);

        AppPaths.EnsureDirectory(Path.GetDirectoryName(fullPath));

        // 带 BOM 的 UTF-8：Windows 记事本与各类 Markdown 编辑器都能正确识别中文
        File.WriteAllText(fullPath, markdown, new UTF8Encoding(true));

        _logger?.Info($"检查报告已导出：{fullPath}（{report.Entries.Count} 条明细）");
        return fullPath;
    }

    /// <summary>生成检查报告的 Markdown 文本（不写文件，便于测试与预览）。</summary>
    public static string BuildCheckReport(FolderCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();

        builder.AppendLine("# 目录一致性检查报告");
        builder.AppendLine();
        builder.AppendLine("| 项目 | 内容 |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| 检查时间 | {report.CheckedAt:yyyy-MM-dd HH:mm:ss} |");
        builder.AppendLine($"| 目标目录 | {EscapeInline(report.RootDirectory)} |");
        builder.AppendLine($"| 检查模式 | {EscapeInline(report.ModeText)} |");
        builder.AppendLine($"| 检查结论 | {(report.IsConsistent ? "完全一致" : "存在差异")} |");
        builder.AppendLine();

        builder.AppendLine("## 统计");
        builder.AppendLine();
        builder.AppendLine("| 结果 | 数量 |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| 匹配 | {report.MatchedCount} |");
        builder.AppendLine($"| 缺失 | {report.MissingCount} |");
        builder.AppendLine($"| 多余 | {report.ExtraCount} |");
        builder.AppendLine($"| 合计 | {report.Entries.Count} |");
        builder.AppendLine();

        AppendSection(builder, "缺失的文件夹", report.MissingCount, report, FolderCheckEntryKind.Missing);
        AppendSection(builder, "多余的文件夹", report.ExtraCount, report, FolderCheckEntryKind.Extra);
        AppendSection(builder, "匹配的文件夹", report.MatchedCount, report, FolderCheckEntryKind.Matched);

        return builder.ToString();
    }

    /// <summary>追加一个分类小节：超过 <see cref="ReportFoldThreshold"/> 条时用 &lt;details&gt; 折叠。</summary>
    private static void AppendSection(
        StringBuilder builder,
        string title,
        int count,
        FolderCheckReport report,
        FolderCheckEntryKind kind)
    {
        builder.AppendLine($"## {title}（{count}）");
        builder.AppendLine();

        if (count == 0)
        {
            builder.AppendLine("无。");
            builder.AppendLine();
            return;
        }

        var entries = report.Entries.Where(entry => entry.Kind == kind).ToList();
        var fold = count > ReportFoldThreshold;

        if (fold)
        {
            builder.AppendLine("<details>");
            builder.AppendLine($"<summary>展开查看 {count} 项</summary>");
            builder.AppendLine();
        }

        foreach (var entry in entries)
        {
            builder.AppendLine($"- {EscapeInline(entry.RelativePath)}");
        }

        if (fold)
        {
            builder.AppendLine();
            builder.AppendLine("</details>");
        }

        builder.AppendLine();
    }

    /// <summary>转义路径中的 Markdown 特殊字符（反引号、竖线、换行），避免破坏报告结构。</summary>
    public static string EscapeInline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("|", "\\|")
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ');
    }

    /// <summary>
    /// 在全部目录创建完成后统一判断并创建占位文件（只处理真正的空目录）。
    /// </summary>
    private void ApplyPlaceholders(
        List<FolderCreateItem> items,
        string placeholderName,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[index];
            if (item.Status == FolderCreateStatus.Failed || !Directory.Exists(item.FullPath))
            {
                continue;
            }

            if (!IsDirectoryEmpty(item.FullPath))
            {
                continue;
            }

            var (created, path) = TryCreatePlaceholder(item.FullPath, placeholderName);
            if (!created && path is null)
            {
                continue;
            }

            items[index] = new FolderCreateItem
            {
                RelativePath = item.RelativePath,
                FullPath = item.FullPath,
                Status = item.Status,
                Message = item.Message,
                PlaceholderCreated = created,
                PlaceholderPath = path
            };
        }
    }

    /// <summary>判断目录是否「真正为空」（既没有文件，也没有子目录）。</summary>
    public static bool IsDirectoryEmpty(string directory)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到内容时按「非空」处理，避免往无法确认的目录里写占位文件
            return false;
        }
    }

    /// <summary>规范化根目录列表：去空白、去重（忽略大小写）；非法路径会被剔除。</summary>
    public static IReadOnlyList<string> NormalizeRoots(IEnumerable<string>? roots)
    {
        var result = new List<string>();

        if (roots is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in roots)
        {
            if (TryNormalizeRoot(entry, out var fullPath, out _) && seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }

    /// <summary>校验并规范化单个根目录路径。</summary>
    public static bool TryNormalizeRoot(string? root, out string normalized, out string? reason)
    {
        normalized = string.Empty;
        reason = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            reason = "路径为空";
            return false;
        }

        var trimmed = root.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            reason = "包含非法字符";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(trimmed);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"路径格式无效：{ex.Message}";
            return false;
        }
    }

    /// <summary>把异常翻译成友好的中文提示。</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "权限不足，无法创建该文件夹。",
        PathTooLongException => "路径过长，超出系统限制。",
        DirectoryNotFoundException => "上级目录不存在或路径无效。",
        ArgumentException => "路径中包含非法字符或格式不正确。",
        NotSupportedException => "路径格式不受支持（例如包含冒号）。",
        IOException io => $"IO 错误：{io.Message}",
        _ => ex.Message
    };

    /// <summary>规范化并校验根目录路径，非法时抛 <see cref="ArgumentException"/>。</summary>
    private static string ResolveRoot(string rootDirectory)
    {
        try
        {
            return Path.GetFullPath(rootDirectory);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"根目录路径无效：{ex.Message}", nameof(rootDirectory), ex);
        }
    }

    private static string ResolvePlaceholderName(string? placeholderName)
        => string.IsNullOrWhiteSpace(placeholderName) ? DefaultPlaceholderName : placeholderName.Trim();

    private void TryCreateRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                _logger?.Info($"已创建根目录：{root}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"创建根目录失败：{root}", ex);
        }
    }

    /// <summary>为空目录创建占位文件；已存在时返回 (false, 现有路径) 且不报错。</summary>
    private (bool Created, string? Path) TryCreatePlaceholder(string directory, string placeholderName)
    {
        try
        {
            var path = Path.Combine(directory, placeholderName);

            if (File.Exists(path))
            {
                return (false, path);
            }

            // 空文件即可满足 Git 跟踪空目录的需求
            File.WriteAllBytes(path, Array.Empty<byte>());
            _logger?.Info($"已创建占位文件：{path}");
            return (true, path);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"创建占位文件失败：{directory}", ex);
            return (false, null);
        }
    }

    /// <summary>把单根进度转成多根进度（同步转发，不额外切换线程）。</summary>
    private sealed class MultiRootProgressBridge : IProgress<FolderCreateProgress>
    {
        private readonly IProgress<MultiRootCreateProgress> _inner;
        private readonly string _root;
        private readonly int _rootIndex;
        private readonly int _rootTotal;

        public MultiRootProgressBridge(
            IProgress<MultiRootCreateProgress> inner,
            string root,
            int rootIndex,
            int rootTotal)
        {
            _inner = inner;
            _root = root;
            _rootIndex = rootIndex;
            _rootTotal = rootTotal;
        }

        public void Report(FolderCreateProgress value)
            => _inner.Report(new MultiRootCreateProgress
            {
                RootIndex = _rootIndex,
                RootTotal = _rootTotal,
                CurrentRoot = _root,
                Total = value.Total,
                Processed = value.Processed,
                CurrentPath = value.CurrentPath
            });
    }
}
