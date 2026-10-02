using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 空文件夹清理服务：扫描「自身及所有子目录中都没有任何文件」的目录，
/// 删除时默认走回收站，并支持进度回调与取消。
/// </summary>
/// <remarks>
/// <para>「空」的定义：目录树下不存在任何文件；只有空子目录的目录同样算空。</para>
/// <para>符号链接 / 目录联接（ReparsePoint）会被跳过，避免死循环与误删外部内容。</para>
/// <para>扫描结果按深度从深到浅排序，删除时先删子目录，父目录不会重复计入。</para>
/// </remarks>
public sealed class EmptyFolderCleanerService
{
    private readonly Logger? _logger;

    /// <summary>创建清理服务。</summary>
    public EmptyFolderCleanerService(Logger? logger = null) => _logger = logger;

    /// <summary>
    /// 判断目录是否为空（自身及所有子目录中都没有文件）。
    /// 目录不存在或没有访问权限时返回 false（保守策略：不确定就不当成空目录）。
    /// </summary>
    public static bool IsEmptyDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    return false;
                }

                if (!IsEmptyDirectory(entry))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 扫描根目录下的空目录。<paramref name="includeSubDirectories"/> 为 false 时只检查直接子目录。
    /// </summary>
    public EmptyFolderScanResult Scan(
        string rootDirectory,
        bool includeSubDirectories = true,
        IProgress<EmptyFolderScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return new EmptyFolderScanResult { Error = "根目录不能为空。" };
        }

        if (!Directory.Exists(rootDirectory))
        {
            return new EmptyFolderScanResult { RootDirectory = rootDirectory, Error = "目录不存在：" + rootDirectory };
        }

        var found = new List<EmptyFolderItem>();
        var scanned = 0;

        try
        {
            var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar);
            ScanDirectory(root, root, includeSubDirectories, found, progress, ref scanned, cancellationToken);

            _logger?.Info($"空目录扫描完成：{root}，发现 {found.Count} 个空目录，共扫描 {scanned} 个目录。");
            return new EmptyFolderScanResult { RootDirectory = root, Items = found };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error($"空目录扫描失败：{rootDirectory}", ex);
            return new EmptyFolderScanResult { RootDirectory = rootDirectory, Items = found, Error = ex.Message };
        }
    }

    /// <summary>
    /// 删除指定的空目录。<paramref name="useRecycleBin"/> 为 true（默认）时移入回收站。
    /// </summary>
    public EmptyFolderDeleteResult Delete(
        IEnumerable<string> directories,
        bool useRecycleBin = true,
        IProgress<EmptyFolderDeleteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var targets = directories
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(CountDepth)
            .ToList();

        var items = new List<EmptyFolderDeleteItem>(targets.Count);
        var processed = 0;

        foreach (var directory in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!Directory.Exists(directory))
                {
                    // 已被父目录一起删除，视为成功
                    items.Add(new EmptyFolderDeleteItem { Path = directory, Success = true });
                }
                else
                {
                    SafeDelete.DeleteDirectory(directory, useRecycleBin);
                    items.Add(new EmptyFolderDeleteItem { Path = directory, Success = true });
                    _logger?.Info($"已删除空目录：{directory}（回收站={useRecycleBin}）");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"删除空目录失败：{directory}", ex);
                items.Add(new EmptyFolderDeleteItem { Path = directory, Success = false, Error = ex.Message });
            }

            processed++;
            progress?.Report(new EmptyFolderDeleteProgress
            {
                Percent = targets.Count == 0 ? 100 : processed * 100d / targets.Count,
                Processed = processed,
                Total = targets.Count,
                CurrentPath = directory
            });
        }

        var result = new EmptyFolderDeleteResult { Items = items, UsedRecycleBin = useRecycleBin };
        _logger?.Info("空目录清理结束：" + result.Summary);
        return result;
    }

    /// <summary>递归扫描：返回「本目录树中是否存在文件」，并把空的子目录收集起来（后序，天然「深的在前」）。</summary>
    private bool ScanDirectory(
        string directory,
        string root,
        bool recurse,
        List<EmptyFolderItem> found,
        IProgress<EmptyFolderScanProgress>? progress,
        ref int scanned,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasFile = false;
        scanned++;

        progress?.Report(new EmptyFolderScanProgress
        {
            ScannedDirectories = scanned,
            FoundCount = found.Count,
            CurrentPath = directory
        });

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"读取属性失败，按「非空」处理：{entry}", ex);
                hasFile = true;
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                hasFile = true;
                continue;
            }

            if (!recurse)
            {
                // 非递归模式：只检查直接子目录，但其「空」仍需看整棵子树，
                // 否则「只含子目录的目录」会被误判为空而误删。
                if (IsEmptyDirectory(entry))
                {
                    found.Add(CreateItem(entry, root));
                }

                continue;
            }

            if (ScanDirectory(entry, root, recurse: true, found, progress, ref scanned, cancellationToken))
            {
                hasFile = true;
            }
            else
            {
                found.Add(CreateItem(entry, root));
            }
        }

        return hasFile;
    }

    /// <summary>按相对路径生成条目（深度 = 相对路径的层数）。</summary>
    private static EmptyFolderItem CreateItem(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var depth = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Length;

        return new EmptyFolderItem
        {
            FullPath = fullPath,
            RelativePath = relative,
            Depth = depth
        };
    }

    /// <summary>路径深度（用于删除时「深的先删」）。</summary>
    private static int CountDepth(string path)
        => path.Count(static ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
}
