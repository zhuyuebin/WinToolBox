using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 时间戳批量修改服务：支持「修改时间 = 创建时间」与「统一改成指定时间」两种模式，
/// 可作用于文件 / 文件夹 / 两者，支持递归、进度回调与取消。
/// </summary>
/// <remarks>
/// 执行顺序固定为「先文件、后目录，目录按深度从深到浅」：
/// 写入子项会刷新父目录的修改时间，因此父目录必须最后写。
/// </remarks>
public sealed class TimestampService
{
    private readonly Logger? _logger;

    /// <summary>创建时间戳服务。</summary>
    public TimestampService(Logger? logger = null) => _logger = logger;

    /// <summary>校验规则；返回错误说明，规则合法时返回 null。</summary>
    public static string? Validate(TimestampOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Mode == TimestampMode.SetFixedTime && options.Fields == TimestampField.None)
        {
            return "请至少选择一个要修改的时间字段。";
        }

        return null;
    }

    /// <summary>
    /// 纯函数：按规则计算某个条目最终要写入的时间。返回 null 表示该字段不修改。
    /// </summary>
    /// <param name="options">规则。</param>
    /// <param name="creationTime">当前创建时间。</param>
    /// <param name="lastWriteTime">当前修改时间。</param>
    /// <param name="lastAccessTime">当前访问时间。</param>
    public static (DateTime? Creation, DateTime? LastWrite, DateTime? LastAccess) ResolveNewTimes(
        TimestampOptions options,
        DateTime creationTime,
        DateTime lastWriteTime,
        DateTime lastAccessTime)
    {
        ArgumentNullException.ThrowIfNull(options);

        switch (options.Mode)
        {
            case TimestampMode.LastWriteEqualsCreation:
                // 把修改时间改成与创建时间一致
                return (null, creationTime, null);

            case TimestampMode.SetFixedTime:
                return (
                    options.Fields.HasFlag(TimestampField.CreationTime) ? options.FixedTime : null,
                    options.Fields.HasFlag(TimestampField.LastWriteTime) ? options.FixedTime : null,
                    options.Fields.HasFlag(TimestampField.LastAccessTime) ? options.FixedTime : null);

            default:
                return (null, null, null);
        }
    }

    /// <summary>扫描目录并生成时间戳预览。</summary>
    public TimestampPreviewResult BuildPreviewFromDirectory(
        string directory,
        TimestampOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return new TimestampPreviewResult { Error = "目录不能为空。" };
        }

        if (!Directory.Exists(directory))
        {
            return new TimestampPreviewResult { Error = "目录不存在：" + directory };
        }

        var paths = new List<string>();
        var searchOption = options.IncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            if (options.Target is TimestampTarget.Files or TimestampTarget.Both)
            {
                paths.AddRange(Directory.EnumerateFiles(directory, "*", searchOption));
            }

            if (options.Target is TimestampTarget.Folders or TimestampTarget.Both)
            {
                paths.AddRange(Directory.EnumerateDirectories(directory, "*", searchOption));
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"扫描目录失败：{directory}", ex);
            return new TimestampPreviewResult { Error = ex.Message };
        }

        return BuildPreview(paths, options, cancellationToken);
    }

    /// <summary>按显式路径列表生成时间戳预览。</summary>
    public TimestampPreviewResult BuildPreview(
        IEnumerable<string> paths,
        TimestampOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        var ruleError = Validate(options);
        if (ruleError is not null)
        {
            return new TimestampPreviewResult { Error = ruleError };
        }

        var items = new List<TimestampPreviewItem>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    if (options.Target is TimestampTarget.Folders)
                    {
                        continue;
                    }

                    items.Add(CreateItem(path, isDirectory: false, options));
                }
                else if (Directory.Exists(path))
                {
                    if (options.Target is TimestampTarget.Files)
                    {
                        continue;
                    }

                    items.Add(CreateItem(path, isDirectory: true, options));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"读取时间戳失败，已跳过：{path}", ex);
            }
        }

        var result = new TimestampPreviewResult { Items = items };
        _logger?.Info($"时间戳预览完成：{result.Summary}");
        return result;
    }

    /// <summary>执行时间戳修改（只处理预览中标记为需要变化的条目）。</summary>
    public TimestampResult Apply(
        IEnumerable<TimestampPreviewItem> items,
        IProgress<TimestampProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 先文件后目录，目录按深度从深到浅：避免「写子项刷新父目录时间」
        var ordered = items
            .Where(static item => item.WillChange)
            .OrderBy(static item => item.IsDirectory ? 1 : 0)
            .ThenByDescending(static item => CountDepth(item.Path))
            .ToList();

        var results = new List<TimestampResultItem>(ordered.Count);
        var processed = 0;

        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.Path))
                    {
                        throw new DirectoryNotFoundException("目录不存在：" + item.Path);
                    }

                    ApplyTimes(
                        item.Path,
                        item,
                        Directory.SetCreationTime,
                        Directory.SetLastWriteTime,
                        Directory.SetLastAccessTime);
                }
                else
                {
                    if (!File.Exists(item.Path))
                    {
                        throw new FileNotFoundException("文件不存在：" + item.Path);
                    }

                    ApplyTimes(
                        item.Path,
                        item,
                        File.SetCreationTime,
                        File.SetLastWriteTime,
                        File.SetLastAccessTime);
                }

                results.Add(new TimestampResultItem { Path = item.Path, Success = true });
                _logger?.Info($"已修改时间戳：{item.Path}");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"修改时间戳失败：{item.Path}", ex);
                results.Add(new TimestampResultItem { Path = item.Path, Success = false, Error = ex.Message });
            }

            processed++;
            progress?.Report(new TimestampProgress
            {
                Percent = ordered.Count == 0 ? 100 : processed * 100d / ordered.Count,
                Processed = processed,
                Total = ordered.Count,
                CurrentPath = item.Path
            });
        }

        var result = new TimestampResult { Items = results };
        _logger?.Info(result.Summary);
        return result;
    }

    /// <summary>把计划时间写入磁盘。</summary>
    private static void ApplyTimes(
        string path,
        TimestampPreviewItem item,
        Action<string, DateTime> setCreationTime,
        Action<string, DateTime> setLastWriteTime,
        Action<string, DateTime> setLastAccessTime)
    {
        if (item.NewCreationTime is { } creation)
        {
            setCreationTime(path, creation);
        }

        if (item.NewLastWriteTime is { } lastWrite)
        {
            setLastWriteTime(path, lastWrite);
        }

        if (item.NewLastAccessTime is { } lastAccess)
        {
            setLastAccessTime(path, lastAccess);
        }
    }

    /// <summary>读取当前时间戳并计算目标时间。</summary>
    private static TimestampPreviewItem CreateItem(string path, bool isDirectory, TimestampOptions options)
    {
        var creation = isDirectory ? Directory.GetCreationTime(path) : File.GetCreationTime(path);
        var lastWrite = isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
        var lastAccess = isDirectory ? Directory.GetLastAccessTime(path) : File.GetLastAccessTime(path);

        var (newCreation, newLastWrite, newLastAccess) =
            ResolveNewTimes(options, creation, lastWrite, lastAccess);

        return new TimestampPreviewItem
        {
            Path = path,
            IsDirectory = isDirectory,
            CurrentCreationTime = creation,
            CurrentLastWriteTime = lastWrite,
            CurrentLastAccessTime = lastAccess,
            NewCreationTime = newCreation,
            NewLastWriteTime = newLastWrite,
            NewLastAccessTime = newLastAccess
        };
    }

    /// <summary>路径深度（用于目录「深的先写」）。</summary>
    private static int CountDepth(string path)
        => path.Count(static ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
}
