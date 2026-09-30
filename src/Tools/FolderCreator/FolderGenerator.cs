using WinToolBox.Core;
using WinToolBox.Tools.FolderCreator.Models;

namespace WinToolBox.Tools.FolderCreator;

/// <summary>单个文件夹的处理结果。</summary>
public enum FolderItemStatus
{
    /// <summary>本次新建。</summary>
    Created,

    /// <summary>已经存在，跳过。</summary>
    Existed,

    /// <summary>创建失败（权限不足、路径过长等）。</summary>
    Failed
}

/// <summary>单个文件夹的处理明细。</summary>
public sealed class FolderItemResult
{
    /// <summary>相对根目录的路径。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>完整路径。</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>处理结果。</summary>
    public FolderItemStatus Status { get; init; }

    /// <summary>补充说明（失败原因等）。</summary>
    public string? Message { get; init; }
}

/// <summary>批量创建文件夹的结果汇总。</summary>
public sealed class FolderGenerationResult
{
    /// <summary>根目录。</summary>
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>逐条明细，顺序与规则一致。</summary>
    public IReadOnlyList<FolderItemResult> Items { get; init; } = Array.Empty<FolderItemResult>();

    /// <summary>新建数量。</summary>
    public int CreatedCount => Items.Count(static i => i.Status == FolderItemStatus.Created);

    /// <summary>已存在（跳过）数量。</summary>
    public int ExistedCount => Items.Count(static i => i.Status == FolderItemStatus.Existed);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static i => i.Status == FolderItemStatus.Failed);

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedCount == 0;

    /// <summary>中文摘要。</summary>
    public string Summary =>
        $"创建完成：新建 {CreatedCount} 个，跳过 {ExistedCount} 个已存在，失败 {FailedCount} 个。";
}

/// <summary>创建进度快照。</summary>
public sealed class FolderProgress
{
    /// <summary>总任务数。</summary>
    public int Total { get; init; }

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>当前处理的相对路径。</summary>
    public string CurrentPath { get; init; } = string.Empty;

    /// <summary>完成百分比（0-100）。</summary>
    public double Percent => Total <= 0 ? 100d : Math.Round(Processed * 100d / Total, 1);
}

/// <summary>
/// 文件夹生成器：按规则在根目录下批量创建文件夹（父级自动递归创建），已存在则跳过。
/// </summary>
public sealed class FolderGenerator
{
    private readonly Logger? _logger;

    /// <summary>创建生成器。</summary>
    public FolderGenerator(Logger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 在后台线程批量创建文件夹（不阻塞 UI）。
    /// </summary>
    public Task<FolderGenerationResult> GenerateAsync(
        string rootDirectory,
        IReadOnlyList<FolderRule> rules,
        IProgress<FolderProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Generate(rootDirectory, rules, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// 批量创建文件夹。参数非法时抛 <see cref="ArgumentException"/>；
    /// 单个文件夹创建失败只记入结果，不中断整体流程。
    /// </summary>
    public FolderGenerationResult Generate(
        string rootDirectory,
        IReadOnlyList<FolderRule> rules,
        IProgress<FolderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(rules);
        cancellationToken.ThrowIfCancellationRequested();

        var root = ResolveRoot(rootDirectory);
        var items = new List<FolderItemResult>(rules.Count);

        // 根目录本身不存在时一并创建，避免后续每一条都失败
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

        var total = rules.Count;
        var processed = 0;

        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(root, rule.RelativePath);
            FolderItemResult item;

            try
            {
                if (Directory.Exists(fullPath))
                {
                    item = new FolderItemResult
                    {
                        RelativePath = rule.RelativePath,
                        FullPath = fullPath,
                        Status = FolderItemStatus.Existed,
                        Message = "已存在，跳过"
                    };
                }
                else
                {
                    Directory.CreateDirectory(fullPath);
                    item = new FolderItemResult
                    {
                        RelativePath = rule.RelativePath,
                        FullPath = fullPath,
                        Status = FolderItemStatus.Created
                    };
                }
            }
            catch (Exception ex)
            {
                item = new FolderItemResult
                {
                    RelativePath = rule.RelativePath,
                    FullPath = fullPath,
                    Status = FolderItemStatus.Failed,
                    Message = DescribeError(ex)
                };

                _logger?.Warn($"创建文件夹失败：{fullPath}", ex);
            }

            items.Add(item);

            processed++;
            progress?.Report(new FolderProgress
            {
                Total = total,
                Processed = processed,
                CurrentPath = rule.RelativePath
            });
        }

        var result = new FolderGenerationResult
        {
            RootDirectory = root,
            Items = items
        };

        _logger?.Info($"文件夹创建完成：根目录 {root}；{result.Summary}");

        return result;
    }

    /// <summary>把异常翻译成友好的中文提示。</summary>
    private static string DescribeError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "权限不足，无法创建该文件夹。",
        PathTooLongException => "路径过长，超出系统限制。",
        DirectoryNotFoundException => "上级目录不存在或路径无效。",
        ArgumentException => "路径中包含非法字符或格式不正确。",
        NotSupportedException => "路径格式不受支持（例如包含冒号）。",
        IOException io => $"IO 错误：{io.Message}",
        _ => ex.Message
    };

    /// <summary>规范化并校验根目录路径。</summary>
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
}
