using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 文件夹差异比对服务：遍历左右两个目录，比较文件大小、修改时间（可选 SHA256 哈希），
/// 返回「相同 / 仅左侧 / 仅右侧 / 内容不同」四类差异列表。
/// </summary>
/// <remarks>
/// 比对方式：
/// <list type="bullet">
/// <item><see cref="DiffCompareMode.SizeAndTime"/>：大小 + 修改时间（默认，最快）。</item>
/// <item><see cref="DiffCompareMode.SizeOnly"/>：只比大小。</item>
/// <item><see cref="DiffCompareMode.Hash"/>：大小相同再比 SHA256（最准确，最慢）。</item>
/// </list>
/// </remarks>
public sealed class FolderDiffService
{
    private readonly Logger? _logger;

    /// <summary>创建比对服务。</summary>
    public FolderDiffService(Logger? logger = null) => _logger = logger;

    /// <summary>执行比对。</summary>
    public FolderDiffResult Compare(
        FolderDiffOptions options,
        IProgress<FolderDiffProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.LeftDirectory) || !Directory.Exists(options.LeftDirectory))
        {
            return new FolderDiffResult { Error = "左侧目录不存在：" + options.LeftDirectory };
        }

        if (string.IsNullOrWhiteSpace(options.RightDirectory) || !Directory.Exists(options.RightDirectory))
        {
            return new FolderDiffResult { Error = "右侧目录不存在：" + options.RightDirectory };
        }

        var excludes = ParseExcludes(options.ExcludeNames);
        var searchOption = options.IncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        Dictionary<string, string> leftFiles;
        Dictionary<string, string> rightFiles;

        try
        {
            leftFiles = EnumerateFiles(options.LeftDirectory, searchOption, excludes);
            rightFiles = EnumerateFiles(options.RightDirectory, searchOption, excludes);
        }
        catch (Exception ex)
        {
            _logger?.Error("遍历目录失败。", ex);
            return new FolderDiffResult { Error = ex.Message };
        }

        var allPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in leftFiles.Keys)
        {
            allPaths.Add(path);
        }

        foreach (var path in rightFiles.Keys)
        {
            allPaths.Add(path);
        }

        var items = new List<FolderDiffItem>(allPaths.Count);
        var compared = 0;

        foreach (var relativePath in allPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            compared++;
            progress?.Report(new FolderDiffProgress { ComparedCount = compared, CurrentPath = relativePath });

            leftFiles.TryGetValue(relativePath, out var leftPath);
            rightFiles.TryGetValue(relativePath, out var rightPath);

            if (leftPath is null)
            {
                var rightInfo = new FileInfo(rightPath!);
                items.Add(new FolderDiffItem
                {
                    RelativePath = relativePath,
                    Status = DiffStatus.RightOnly,
                    RightPath = rightPath,
                    RightSize = rightInfo.Length,
                    RightModified = rightInfo.LastWriteTime,
                    Message = "仅右侧存在"
                });
                continue;
            }

            if (rightPath is null)
            {
                var leftInfo = new FileInfo(leftPath);
                items.Add(new FolderDiffItem
                {
                    RelativePath = relativePath,
                    Status = DiffStatus.LeftOnly,
                    LeftPath = leftPath,
                    LeftSize = leftInfo.Length,
                    LeftModified = leftInfo.LastWriteTime,
                    Message = "仅左侧存在"
                });
                continue;
            }

            var left = new FileInfo(leftPath);
            var right = new FileInfo(rightPath);

            var status = CompareFiles(left, right, options, cancellationToken, out var message);

            items.Add(new FolderDiffItem
            {
                RelativePath = relativePath,
                Status = status,
                LeftPath = leftPath,
                RightPath = rightPath,
                LeftSize = left.Length,
                RightSize = right.Length,
                LeftModified = left.LastWriteTime,
                RightModified = right.LastWriteTime,
                Message = message
            });
        }

        var result = new FolderDiffResult { Items = items };
        _logger?.Info("文件夹比对：" + result.Summary);
        return result;
    }

    /// <summary>比较两个文件，返回差异状态与说明。</summary>
    private DiffStatus CompareFiles(
        FileInfo left,
        FileInfo right,
        FolderDiffOptions options,
        CancellationToken cancellationToken,
        out string message)
    {
        if (left.Length != right.Length)
        {
            message = $"大小不同（{left.Length} / {right.Length} 字节）";
            return DiffStatus.Different;
        }

        switch (options.CompareMode)
        {
            case DiffCompareMode.SizeOnly:
                message = "大小相同";
                return DiffStatus.Same;

            case DiffCompareMode.Hash:
            {
                var leftHash = HashEngine.ComputeHash(left.FullName, HashAlgorithmKind.SHA256, null, cancellationToken);
                var rightHash = HashEngine.ComputeHash(right.FullName, HashAlgorithmKind.SHA256, null, cancellationToken);

                if (string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase))
                {
                    message = "内容相同（SHA256 一致）";
                    return DiffStatus.Same;
                }

                message = "内容不同（SHA256 不一致）";
                return DiffStatus.Different;
            }

            default:
            {
                var tolerance = TimeSpan.FromSeconds(Math.Max(0, options.TimeToleranceSeconds));
                var delta = (left.LastWriteTime - right.LastWriteTime).Duration();

                if (delta <= tolerance)
                {
                    message = "大小与修改时间一致";
                    return DiffStatus.Same;
                }

                message = $"修改时间不同（相差 {delta.TotalSeconds:0} 秒）";
                return DiffStatus.Different;
            }
        }
    }

    /// <summary>枚举目录中的文件，返回「相对路径 -&gt; 完整路径」（忽略大小写去重）。</summary>
    private static Dictionary<string, string> EnumerateFiles(
        string root,
        SearchOption searchOption,
        IReadOnlyCollection<string> excludes)
    {
        var fullRoot = Path.GetFullPath(root);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 忽略名单按「不区分大小写」匹配（Windows 文件名不区分大小写）
        var excludeSet = new HashSet<string>(excludes, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", searchOption))
        {
            var relative = Path.GetRelativePath(fullRoot, file);

            if (IsExcluded(relative, excludeSet))
            {
                continue;
            }

            result[relative] = file;
        }

        return result;
    }

    /// <summary>相对路径中任一层级命中忽略名时跳过。</summary>
    private static bool IsExcluded(string relativePath, IReadOnlyCollection<string> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var fileName = segments.Length > 0 ? segments[^1] : relativePath;

        return excludes.Contains(fileName) || segments.Any(excludes.Contains);
    }

    /// <summary>解析忽略名单（分号 / 逗号 / 换行分隔）。</summary>
    public static IReadOnlyList<string> ParseExcludes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把比对结果导出为文本报告。</summary>
    public static string BuildReport(FolderDiffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("状态\t相对路径\t左侧大小\t右侧大小\t说明");

        foreach (var item in result.Items)
        {
            builder.Append(StatusText(item.Status)).Append('\t')
                .Append(item.RelativePath).Append('\t')
                .Append(item.LeftSize?.ToString() ?? "-").Append('\t')
                .Append(item.RightSize?.ToString() ?? "-").Append('\t')
                .Append(item.Message).AppendLine();
        }

        builder.AppendLine().AppendLine(result.Summary);
        return builder.ToString();
    }

    /// <summary>状态对应的中文说明。</summary>
    public static string StatusText(DiffStatus status) => status switch
    {
        DiffStatus.Same => "相同",
        DiffStatus.LeftOnly => "仅左侧",
        DiffStatus.RightOnly => "仅右侧",
        DiffStatus.Different => "内容不同",
        _ => status.ToString()
    };
}
