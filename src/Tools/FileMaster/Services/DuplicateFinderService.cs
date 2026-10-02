using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 重复文件查找服务：先按大小分组（大小不同的文件不可能重复），
/// 再对同大小的候选文件计算哈希（默认 SHA256），最终按内容哈希分组。
/// </summary>
/// <remarks>
/// <para>两阶段策略可以最大限度减少读盘与哈希计算量。</para>
/// <para>删除默认走回收站，调用方必须先预览并由用户勾选，再执行删除。</para>
/// </remarks>
public sealed class DuplicateFinderService
{
    private readonly Logger? _logger;

    /// <summary>创建重复文件查找服务。</summary>
    public DuplicateFinderService(Logger? logger = null) => _logger = logger;

    /// <summary>扫描目录并返回重复文件分组。</summary>
    public DuplicateScanResult Find(
        DuplicateFinderOptions options,
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var directories = options.Directories
            .Where(static directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories.Count == 0)
        {
            return new DuplicateScanResult { Error = "没有可扫描的目录（请确认目录存在）。" };
        }

        var searchOption = options.IncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var excludeSet = new HashSet<string>(
            FolderDiffService.ParseExcludes(options.ExcludeNames),
            StringComparer.OrdinalIgnoreCase);

        // ---------- 1. 收集文件并按大小分组 ----------
        var candidates = new List<FileInfo>();

        try
        {
            foreach (var directory in directories)
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", searchOption))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(directory, file);
                    if (IsExcluded(relative, excludeSet))
                    {
                        continue;
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(file);
                        if (!info.Exists || info.Length < options.MinFileSize)
                        {
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn($"读取文件信息失败，已跳过：{file}", ex);
                        continue;
                    }

                    candidates.Add(info);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error("扫描目录失败。", ex);
            return new DuplicateScanResult { Error = ex.Message };
        }

        var sizeGroups = candidates
            .GroupBy(static info => info.Length)
            .Where(static group => group.Count() > 1)
            .ToList();

        var toHash = sizeGroups.SelectMany(static group => group).ToList();

        _logger?.Info(
            $"重复文件查找：共 {candidates.Count} 个文件，其中 {toHash.Count} 个大小相同需要计算哈希。");

        // ---------- 2. 计算哈希并按 (大小, 哈希) 分组 ----------
        var hashed = new List<(FileInfo Info, string Hash)>();
        var processed = 0;

        foreach (var info in toHash)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var hash = HashEngine.ComputeHash(info.FullName, options.Algorithm);
                hashed.Add((info, hash));
            }
            catch (Exception ex)
            {
                _logger?.Warn($"计算哈希失败，已跳过：{info.FullName}", ex);
            }

            processed++;
            progress?.Report(new DuplicateScanProgress
            {
                Phase = "计算哈希",
                Processed = processed,
                Total = toHash.Count,
                CurrentPath = info.FullName
            });
        }

        var groups = hashed
            .GroupBy(static pair => (pair.Info.Length, pair.Hash))
            .Where(static group => group.Count() > 1)
            .Select(group => new DuplicateGroup
            {
                Hash = group.Key.Hash,
                FileSize = group.Key.Length,
                Files = group
                    .Select(static pair => new DuplicateFileItem
                    {
                        Path = pair.Info.FullName,
                        Size = pair.Info.Length,
                        LastWriteTime = pair.Info.LastWriteTime
                    })
                    .OrderBy(static item => item.LastWriteTime)
                    .ThenBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderByDescending(static group => group.WastedBytes)
            .ToList();

        var result = new DuplicateScanResult
        {
            Groups = groups,
            ScannedFileCount = candidates.Count,
            HashedFileCount = hashed.Count
        };

        _logger?.Info(result.Summary);
        return result;
    }

    /// <summary>删除指定的重复文件（默认走回收站）。</summary>
    public DuplicateDeleteResult Delete(
        IEnumerable<string> paths,
        bool useRecycleBin = true,
        IProgress<DuplicateDeleteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var targets = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deleted = new List<string>();
        var failed = new List<(string Path, string Error)>();
        var freedBytes = 0L;
        var processed = 0;

        foreach (var path in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(path))
                {
                    // 已经不在了：视为成功，避免用户困惑
                    deleted.Add(path);
                }
                else
                {
                    var size = new FileInfo(path).Length;
                    SafeDelete.DeleteFile(path, useRecycleBin);
                    deleted.Add(path);
                    freedBytes += size;
                    _logger?.Info($"已删除重复文件：{path}（回收站={useRecycleBin}）");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"删除重复文件失败：{path}", ex);
                failed.Add((path, ex.Message));
            }

            processed++;
            progress?.Report(new DuplicateDeleteProgress
            {
                Percent = targets.Count == 0 ? 100 : processed * 100d / targets.Count,
                Processed = processed,
                Total = targets.Count,
                CurrentPath = path
            });
        }

        var result = new DuplicateDeleteResult
        {
            DeletedPaths = deleted,
            FailedItems = failed,
            FreedBytes = freedBytes,
            UsedRecycleBin = useRecycleBin
        };

        _logger?.Info(result.Summary);
        return result;
    }

    /// <summary>相对路径中任一层级命中忽略名时跳过。</summary>
    private static bool IsExcluded(string relativePath, HashSet<string> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }

        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        return excludes.Contains(segments[^1]) || segments.Any(excludes.Contains);
    }
}
