using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 重复文件查找服务：先按大小分组（大小不同的文件不可能重复），
/// 再对同大小的候选文件计算哈希（默认 SHA256），最终按内容哈希分组。
/// </summary>
/// <remarks>
/// <para>两阶段策略可以最大限度减少读盘与哈希计算量。</para>
/// <para>扫描目录先做归一化：合并等价写法与「被父目录完整包含」的子目录，并按规范化完整路径对候选文件去重，
/// 否则父目录 + 子目录会把同一文件扫成「互为副本」的重复组（见 <see cref="NormalizeDirectories"/>）。</para>
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

        // 目录归一化：统一写法、去重，并在递归扫描时丢弃被父目录完整覆盖的子目录。
        // 否则「D:\data + D:\data\sub」会把同一批文件扫两遍，同一个文件会被当成「互为副本的重复文件」。
        var directories = NormalizeDirectories(
            options.Directories,
            options.IncludeSubDirectories,
            out var mergedDirectories);

        if (directories.Count == 0)
        {
            return new DuplicateScanResult { Error = "没有可扫描的目录（请确认目录存在）。" };
        }

        if (mergedDirectories.Count > 0)
        {
            _logger?.Info($"重复文件查找：已自动合并 {mergedDirectories.Count} 个扫描目录（等价的写法或被父目录包含），不再重复扫描。");
        }

        var searchOption = options.IncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var excludeSet = new HashSet<string>(
            FolderDiffService.ParseExcludes(options.ExcludeNames),
            StringComparer.OrdinalIgnoreCase);

        // ---------- 1. 收集文件并按大小分组 ----------
        var candidates = new List<FileInfo>();

        // 同一物理文件还可能通过不同写法的目录被枚举到两次（大小写 / 尾分隔符 / 相对路径），
        // 这里统一按「规范化完整路径」去重，保证它只被统计与哈希一次。
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateFileSkips = 0;

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

                    if (!seenFiles.Add(NormalizeFilePath(file)))
                    {
                        duplicateFileSkips++;
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

        if (duplicateFileSkips > 0)
        {
            _logger?.Info($"重复文件查找：有 {duplicateFileSkips} 个文件被重复枚举，已按规范化路径去重。");
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
            HashedFileCount = hashed.Count,
            MergedDirectories = mergedDirectories
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

    // ---------------------------------------------------------------- 目录与文件路径归一化

    /// <summary>
    /// 归一化扫描目录：丢弃空白目录与不存在的目录，合并写法不同但等价的目录（大小写、尾分隔符、相对路径），
    /// 并在递归扫描时丢弃被其它目录完整包含的子目录。
    /// </summary>
    /// <remarks>
    /// <para>为什么必须合并：父目录与子目录同时参与扫描时，同一个文件会被枚举两次，
    /// 于是被当成「互为副本的两个重复文件」——用户点「全选重复项」后可能删掉唯一的那份原件，
    /// 而「可回收空间」也是虚的。</para>
    /// <para>为什么 <paramref name="includeSubDirectories"/> 为 false 时不能合并父子目录：
    /// 只扫顶层时父目录并不会扫到子目录里的文件，把子目录合并掉会直接漏扫，
    /// 因此该模式下只做「等价写法去重」，父子目录各自保留、各扫各的顶层。</para>
    /// </remarks>
    /// <param name="directories">用户提供的扫描目录（可含空白项与不存在的路径）。</param>
    /// <param name="includeSubDirectories">是否递归子目录。</param>
    /// <param name="mergedDirectories">输出：被自动合并掉的目录（保序、去重）。</param>
    /// <returns>实际参与扫描的目录（保序、去重）。</returns>
    public static IReadOnlyList<string> NormalizeDirectories(
        IEnumerable<string> directories,
        bool includeSubDirectories,
        out IReadOnlyList<string> mergedDirectories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var kept = new List<string>();
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMerged(string directory)
        {
            if (mergedSeen.Add(directory))
            {
                merged.Add(directory);
            }
        }

        // 1) 统一写法并去重：不存在的目录按原有语义直接忽略（调用方据此判断「没有可扫描的目录」）
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = TrimTrailingSeparator(Path.GetFullPath(directory));
            }
            catch (Exception)
            {
                // 非法路径（非法字符等）视同不存在，保持过滤语义一致
                continue;
            }

            if (!seen.Add(fullPath))
            {
                AddMerged(fullPath);
                continue;
            }

            kept.Add(fullPath);
        }

        if (!includeSubDirectories || kept.Count < 2)
        {
            mergedDirectories = merged;
            return kept;
        }

        // 2) 递归扫描时：若某个目录是另一个目录的严格子路径，则丢弃它（父目录已经完整覆盖它）。
        //    这里与输入顺序无关：无论父目录写在前还是写在后，子目录都会被合并掉。
        var effective = new List<string>();

        foreach (var directory in kept)
        {
            // 显式排除「自己」：即使将来有人改坏 IsProperSubPathOf 的相等语义，
            // 也不会再出现「目录把自己合并掉、kept 变空」的故障。
            if (kept.Any(other => !string.Equals(other, directory, StringComparison.OrdinalIgnoreCase)
                                  && IsProperSubPathOf(directory, other)))
            {
                AddMerged(directory);
            }
            else
            {
                effective.Add(directory);
            }
        }

        mergedDirectories = merged;
        return effective;
    }

    /// <summary>
    /// 判断 <paramref name="path"/> 是否与 <paramref name="parent"/> 相同或位于其下（大小写不敏感，忽略尾分隔符）。
    /// 供界面提示「该目录会被自动合并」使用。
    /// </summary>
    /// <param name="path">待判断的路径。</param>
    /// <param name="parent">可能的父路径。</param>
    public static bool IsSameOrSubPathOf(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        try
        {
            var normalizedPath = TrimTrailingSeparator(Path.GetFullPath(path));
            var normalizedParent = TrimTrailingSeparator(Path.GetFullPath(parent));

            return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
                || IsProperSubPathOf(normalizedPath, normalizedParent);
        }
        catch (Exception)
        {
            // 非法路径无法判断包含关系，按「不包含」处理，由调用方各自的校验给出提示
            return false;
        }
    }

    /// <summary>
    /// 判断 <paramref name="path"/> 是否是 <paramref name="parent"/> 的<b>严格</b>子路径（大小写不敏感）。
    /// </summary>
    /// <remarks>
    /// 必须「补一个目录分隔符之后再做前缀比较」，不能直接对裸路径用 <c>StartsWith</c>：
    /// 否则 <c>D:\data</c> 会把 <c>D:\database</c> 错误地当成自己的子目录吞掉。
    /// <para><b>相等一律返回 false</b>（这正是 "proper" 的含义）：像 <c>D:\</c> 这类路径根本身
    /// 自带尾分隔符，若不先判相等，<c>IsProperSubPathOf("D:\", "D:\")</c> 会返回 true，
    /// 于是「驱动器根」会把自己吞掉，最终 kept 为空、整个扫描被跳过。</para>
    /// <para>两个参数都必须是已去尾分隔符的完整路径（只有驱动器根之类的路径根会保留尾分隔符）。</para>
    /// </remarks>
    private static bool IsProperSubPathOf(string path, string parent)
    {
        // 先去尾分隔符再比相等，避免 "D:\" 与 "D:" 被当成不同路径
        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(trimmedPath, trimmedParent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把文件路径规范化为「完整路径 + 去尾随分隔符」，用于判断两个路径是否指向同一个目录项。</summary>
    /// <remarks>
    /// 这里只用路径做去重，没有用 <c>File.OpenHandle</c> + <c>GetFileInformationByHandle</c> 的
    /// 「卷序列号 + 文件索引」来识别同一份数据，原因有三：
    /// 1) 同一路径的大小写、尾分隔符、相对写法已经覆盖本机可稳定复现的全部等价形式；
    /// 2) 硬链接在目录枚举里本来就是两个目录项，按文件索引合并会把它们当成一个文件，
    ///    反而让用户看不到重复（或删掉其中一个路径后误判可回收空间）；
    /// 3) 该 P/Invoke 在沙箱 / 非 NTFS 卷上无法稳定验证，宁可保持行为可预测。
    /// 已知局限：硬链接与 8.3 短名仍会被当成两个不同文件。
    /// </remarks>
    private static string NormalizeFilePath(string path)
    {
        try
        {
            return TrimTrailingSeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            // 取不到完整路径时退回原字符串，至少保证同一次扫描内结果稳定
            return path;
        }
    }

    /// <summary>去掉路径末尾的目录分隔符（路径根本身除外，例如 <c>D:\</c> 保持原样）。</summary>
    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && root.Length >= path.Length)
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
