using WinToolBox.Core;
using WinToolBox.Core.Services;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster;

/// <summary>检查模式。</summary>
public enum FolderCheckMode
{
    /// <summary>严格模式：目标目录中多出的文件夹也要报出来。</summary>
    Strict,

    /// <summary>宽松模式：只检查规则要求的文件夹是否存在，忽略多出的文件夹。</summary>
    Loose
}

/// <summary>单个文件夹的检查状态。</summary>
public enum FolderCheckStatus
{
    /// <summary>规则有，目标也有。</summary>
    Matched,

    /// <summary>规则有，目标没有。</summary>
    Missing,

    /// <summary>目标有，规则没有（仅严格模式）。</summary>
    Extra
}

/// <summary>单条检查结果。</summary>
public sealed class FolderCheckItem
{
    /// <summary>相对根目录的路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>层级深度。</summary>
    public int Depth { get; init; }

    /// <summary>检查状态。</summary>
    public FolderCheckStatus Status { get; init; }

    /// <summary>界面显示文本，例如 <c>[缺失] 一级目录\二级目录</c>。</summary>
    public string DisplayText => $"[{StatusText}] {RelativePath}";

    /// <summary>状态的中文名称。</summary>
    public string StatusText => Status switch
    {
        FolderCheckStatus.Matched => "匹配",
        FolderCheckStatus.Missing => "缺失",
        FolderCheckStatus.Extra => "多余",
        _ => "未知"
    };
}

/// <summary>目录一致性检查结果。</summary>
public sealed class FolderCheckResult
{
    /// <summary>被检查的根目录。</summary>
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>使用的检查模式。</summary>
    public FolderCheckMode Mode { get; init; }

    /// <summary>检查明细（按深度升序，同深度按名称排序）。</summary>
    public IReadOnlyList<FolderCheckItem> Items { get; init; } = Array.Empty<FolderCheckItem>();

    /// <summary>匹配数量。</summary>
    public int MatchedCount => Items.Count(static i => i.Status == FolderCheckStatus.Matched);

    /// <summary>缺失数量。</summary>
    public int MissingCount => Items.Count(static i => i.Status == FolderCheckStatus.Missing);

    /// <summary>多余数量。</summary>
    public int ExtraCount => Items.Count(static i => i.Status == FolderCheckStatus.Extra);

    /// <summary>是否完全一致（无缺失；宽松模式下不关心多余）。</summary>
    public bool IsConsistent => Error is null && MissingCount == 0 &&
                                (Mode == FolderCheckMode.Loose || ExtraCount == 0);

    /// <summary>根目录不存在/不可访问时的友好错误。</summary>
    public string? Error { get; init; }

    /// <summary>中文摘要。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
            {
                return $"检查失败：{Error}";
            }

            var modeText = Mode == FolderCheckMode.Strict ? "严格模式" : "宽松模式";
            var conclusion = IsConsistent ? "目录结构与规则一致。" : "目录结构与规则不一致。";
            return $"检查完成（{modeText}）：匹配 {MatchedCount}，缺失 {MissingCount}，多余 {ExtraCount}。{conclusion}";
        }
    }
}

/// <summary>
/// 目录一致性检查器：把“规则要求的目录”与“目标目录的实际结构”做比对。
/// </summary>
public sealed class FolderChecker
{
    private readonly Logger? _logger;

    /// <summary>创建检查器。</summary>
    public FolderChecker(Logger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 检查目标根目录是否符合规则。
    /// 根目录不存在时不会抛异常，而是返回带 <see cref="FolderCheckResult.Error"/> 的结果。
    /// </summary>
    /// <param name="rootDirectory">目标根目录。</param>
    /// <param name="rules">规则列表。</param>
    /// <param name="mode">检查模式（严格模式会额外报告「多余」）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public FolderCheckResult Check(
        string rootDirectory,
        IReadOnlyList<FolderRule> rules,
        FolderCheckMode mode = FolderCheckMode.Strict,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(rules);
        cancellationToken.ThrowIfCancellationRequested();

        string root;
        try
        {
            root = Path.GetFullPath(rootDirectory);
        }
        catch (Exception ex)
        {
            return new FolderCheckResult
            {
                RootDirectory = rootDirectory,
                Mode = mode,
                Error = $"目标根目录路径无效：{ex.Message}"
            };
        }

        if (!Directory.Exists(root))
        {
            return new FolderCheckResult
            {
                RootDirectory = root,
                Mode = mode,
                Error = $"目标根目录不存在：{root}"
            };
        }

        var items = new List<FolderCheckItem>();

        // 规则侧：去重后逐条判断是否存在
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!expected.Add(rule.RelativePath))
            {
                continue;
            }

            var fullPath = Path.Combine(root, rule.RelativePath);
            var exists = SafeDirectoryExists(fullPath);

            items.Add(new FolderCheckItem
            {
                RelativePath = rule.RelativePath,
                Depth = Math.Max(1, rule.Depth),
                Status = exists ? FolderCheckStatus.Matched : FolderCheckStatus.Missing
            });
        }

        // 目标侧：严格模式下把规则里没有的目录标记为“多余”
        if (mode == FolderCheckMode.Strict)
        {
            foreach (var relative in EnumerateRelativeDirectories(root, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (expected.Contains(relative))
                {
                    continue;
                }

                items.Add(new FolderCheckItem
                {
                    RelativePath = relative,
                    Depth = CountDepth(relative),
                    Status = FolderCheckStatus.Extra
                });
            }
        }

        var ordered = items
            .OrderBy(static i => i.Depth)
            .ThenBy(static i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new FolderCheckResult
        {
            RootDirectory = root,
            Mode = mode,
            Items = ordered
        };

        _logger?.Info($"目录检查完成：{root}；{result.Summary}");

        return result;
    }

    /// <summary>
    /// 把检查结果转换成可导出的报告数据（Markdown 报告由
    /// <see cref="FolderCreatorService.ExportCheckReport"/> 生成）。
    /// </summary>
    /// <param name="result">检查结果。</param>
    /// <param name="checkedAt">检查时间；为空时取当前时间。</param>
    public static FolderCheckReport ToReport(FolderCheckResult result, DateTimeOffset? checkedAt = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new FolderCheckReport
        {
            RootDirectory = result.RootDirectory,
            ModeText = result.Mode == FolderCheckMode.Strict ? "严格模式" : "宽松模式",
            CheckedAt = checkedAt ?? DateTimeOffset.Now,
            Entries = result.Items
                .Select(static item => new FolderCheckEntry
                {
                    RelativePath = item.RelativePath,
                    Kind = item.Status switch
                    {
                        FolderCheckStatus.Matched => FolderCheckEntryKind.Matched,
                        FolderCheckStatus.Missing => FolderCheckEntryKind.Missing,
                        _ => FolderCheckEntryKind.Extra
                    }
                })
                .ToList()
        };
    }

    /// <summary>递归枚举目标根目录下的所有子目录（相对路径），跳过无法访问的目录与重解析点。</summary>
    private static List<string> EnumerateRelativeDirectories(string root, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Pop();

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                // 权限不足或目录已消失：跳过，不影响其它目录的检查
                continue;
            }

            foreach (var child in children)
            {
                try
                {
                    var attributes = File.GetAttributes(child);
                    results.Add(Path.GetRelativePath(root, child));

                    // 不进入符号链接/联接点，避免循环
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
                catch (Exception)
                {
                    // 单个目录读取失败时忽略
                }
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>相对路径的层级深度（按分隔符切分）。</summary>
    private static int CountDepth(string relativePath)
        => relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
