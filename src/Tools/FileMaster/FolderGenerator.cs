using WinToolBox.Core;
using WinToolBox.Core.Services;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster;

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

    /// <summary>本次是否为该目录创建了占位文件。</summary>
    public bool PlaceholderCreated { get; init; }

    /// <summary>占位文件完整路径（未创建时为 null）。</summary>
    public string? PlaceholderPath { get; init; }
}

/// <summary>批量创建文件夹的结果汇总。</summary>
public sealed class FolderGenerationResult
{
    /// <summary>根目录。</summary>
    public string RootDirectory { get; init; } = string.Empty;

    /// <summary>逐条明细，顺序与规则一致。</summary>
    public IReadOnlyList<FolderItemResult> Items { get; init; } = Array.Empty<FolderItemResult>();

    /// <summary>整个根目录处理失败时的原因。</summary>
    public string? Error { get; init; }

    /// <summary>新建数量。</summary>
    public int CreatedCount => Items.Count(static i => i.Status == FolderItemStatus.Created);

    /// <summary>已存在（跳过）数量。</summary>
    public int ExistedCount => Items.Count(static i => i.Status == FolderItemStatus.Existed);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(static i => i.Status == FolderItemStatus.Failed);

    /// <summary>本次创建的占位文件数量。</summary>
    public int PlaceholderCount => Items.Count(static i => i.PlaceholderCreated);

    /// <summary>是否全部成功。</summary>
    public bool Success => Error is null && FailedCount == 0;

    /// <summary>中文摘要。</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
            {
                return $"创建失败：{Error}";
            }

            var text = $"创建完成：新建 {CreatedCount} 个，跳过 {ExistedCount} 个已存在，失败 {FailedCount} 个。";
            return PlaceholderCount > 0 ? text + $" 另创建占位文件 {PlaceholderCount} 个。" : text;
        }
    }
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
/// 文件夹生成器：规则模型与 <see cref="FolderCreatorService"/> 之间的适配层。
/// 真正的创建逻辑（含占位文件、多根目录）都在 WinToolBox.Core 中，本类只做规则映射与结果转换。
/// </summary>
public sealed class FolderGenerator
{
    private readonly Logger? _logger;
    private readonly FolderCreatorService _service;

    /// <summary>创建生成器。</summary>
    public FolderGenerator(Logger? logger = null)
    {
        _logger = logger;
        _service = new FolderCreatorService(logger);
    }

    /// <summary>
    /// 在后台线程批量创建文件夹（不阻塞 UI）。
    /// </summary>
    /// <param name="rootDirectory">根目录。</param>
    /// <param name="rules">规则列表。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <param name="createPlaceholder">是否为空目录创建占位文件。</param>
    /// <param name="placeholderName">占位文件名，默认 <c>.gitkeep</c>。</param>
    public Task<FolderGenerationResult> GenerateAsync(
        string rootDirectory,
        IReadOnlyList<FolderRule> rules,
        IProgress<FolderProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool createPlaceholder = false,
        string placeholderName = FolderCreatorService.DefaultPlaceholderName)
        => Task.Run(
            () => Generate(rootDirectory, rules, progress, cancellationToken, createPlaceholder, placeholderName),
            cancellationToken);

    /// <summary>
    /// 批量创建文件夹。参数非法时抛 <see cref="ArgumentException"/>；
    /// 单个文件夹创建失败只记入结果，不中断整体流程。
    /// </summary>
    public FolderGenerationResult Generate(
        string rootDirectory,
        IReadOnlyList<FolderRule> rules,
        IProgress<FolderProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool createPlaceholder = false,
        string placeholderName = FolderCreatorService.DefaultPlaceholderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(rules);

        var paths = new List<string>(rules.Count);
        paths.AddRange(rules.Select(static rule => rule.RelativePath));

        var core = _service.CreateFolders(
            rootDirectory,
            paths,
            createPlaceholder,
            placeholderName,
            progress is null ? null : new ProgressBridge(progress),
            cancellationToken);

        return ToGenerationResult(core);
    }

    /// <summary>把 Core 的创建结果转换成界面使用的规则结果（多根目录模式也会用到）。</summary>
    public static FolderGenerationResult ToGenerationResult(FolderCreateResult core)
    {
        ArgumentNullException.ThrowIfNull(core);

        return new FolderGenerationResult
        {
            RootDirectory = core.RootDirectory,
            Error = core.Error,
            Items = core.Items
                .Select(static item => new FolderItemResult
                {
                    RelativePath = item.RelativePath,
                    FullPath = item.FullPath,
                    Status = item.Status switch
                    {
                        FolderCreateStatus.Created => FolderItemStatus.Created,
                        FolderCreateStatus.Existed => FolderItemStatus.Existed,
                        _ => FolderItemStatus.Failed
                    },
                    Message = item.Message,
                    PlaceholderCreated = item.PlaceholderCreated,
                    PlaceholderPath = item.PlaceholderPath
                })
                .ToList()
        };
    }

    /// <summary>把 Core 的进度同步转发成界面进度（不额外切换线程）。</summary>
    private sealed class ProgressBridge : IProgress<FolderCreateProgress>
    {
        private readonly IProgress<FolderProgress> _inner;

        public ProgressBridge(IProgress<FolderProgress> inner) => _inner = inner;

        public void Report(FolderCreateProgress value)
            => _inner.Report(new FolderProgress
            {
                Total = value.Total,
                Processed = value.Processed,
                CurrentPath = value.CurrentPath
            });
    }
}
