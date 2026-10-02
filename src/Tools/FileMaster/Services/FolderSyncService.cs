using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 文件夹同步 / 镜像服务：以源目录为准，把差异整理成「先删除、后创建目录、再复制」的计划，
/// 计划可先预览、二次确认后再执行；删除默认走回收站。
/// </summary>
/// <remarks>
/// 三种模式：
/// <list type="bullet">
/// <item><see cref="SyncMode.CopyOnly"/>：只复制新增 / 更新的文件，不动目标中的多余内容。</item>
/// <item><see cref="SyncMode.OneWaySync"/>：复制新增 / 更新，并删除目标中源目录不存在的内容。</item>
/// <item><see cref="SyncMode.Mirror"/>：目标完全等于源（在单向同步基础上强制重写所有文件）。</item>
/// </list>
/// 安全检查：源目录与目标目录不能相同，也不能互相包含。
/// </remarks>
public sealed class FolderSyncService
{
    private readonly Logger? _logger;

    /// <summary>创建同步服务。</summary>
    public FolderSyncService(Logger? logger = null) => _logger = logger;

    /// <summary>生成同步计划（不修改任何文件）。</summary>
    public SyncPlan BuildPlan(FolderSyncOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.SourceDirectory) || !Directory.Exists(options.SourceDirectory))
        {
            return new SyncPlan { Error = "源目录不存在：" + options.SourceDirectory };
        }

        if (string.IsNullOrWhiteSpace(options.TargetDirectory))
        {
            return new SyncPlan { Error = "目标目录不能为空。" };
        }

        var source = Path.GetFullPath(options.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(options.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar);

        var safetyError = ValidateDirectories(source, target);
        if (safetyError is not null)
        {
            return new SyncPlan { Error = safetyError };
        }

        var searchOption = options.IncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var excludeSet = new HashSet<string>(
            FolderDiffService.ParseExcludes(options.ExcludeNames),
            StringComparer.OrdinalIgnoreCase);

        Dictionary<string, FileInfo> sourceFiles;
        Dictionary<string, FileInfo> targetFiles;
        List<string> sourceDirectories;
        List<string> targetDirectories;

        try
        {
            sourceFiles = EnumerateFiles(source, searchOption, excludeSet);
            targetFiles = Directory.Exists(target) ? EnumerateFiles(target, searchOption, excludeSet) : new(StringComparer.OrdinalIgnoreCase);

            sourceDirectories = EnumerateDirectories(source, searchOption, excludeSet);
            targetDirectories = Directory.Exists(target) ? EnumerateDirectories(target, searchOption, excludeSet) : new List<string>();
        }
        catch (Exception ex)
        {
            _logger?.Error("枚举目录失败。", ex);
            return new SyncPlan { Error = ex.Message };
        }

        var items = new List<SyncPlanItem>();

        // ---------- 1. 删除：目标中源目录不存在的内容（非 CopyOnly 模式） ----------
        if (options.Mode != SyncMode.CopyOnly)
        {
            var targetSet = new HashSet<string>(targetDirectories, StringComparer.OrdinalIgnoreCase);

            foreach (var (relative, info) in targetFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sourceFiles.ContainsKey(relative))
                {
                    continue;
                }

                items.Add(new SyncPlanItem
                {
                    Kind = SyncActionKind.DeleteFile,
                    RelativePath = relative,
                    TargetPath = info.FullName,
                    Size = info.Length,
                    Reason = "目标中多余的文件"
                });
            }

            // 目录按深度从深到浅删除，避免父目录先被删掉
            foreach (var relative in targetDirectories
                         .Where(relative => !IsDirectoryInSource(relative, sourceFiles, sourceDirectories))
                         .OrderByDescending(CountDepth))
            {
                cancellationToken.ThrowIfCancellationRequested();

                items.Add(new SyncPlanItem
                {
                    Kind = SyncActionKind.DeleteDirectory,
                    RelativePath = relative,
                    TargetPath = Path.Combine(target, relative),
                    Reason = "目标中多余的目录"
                });
            }

            _ = targetSet;
        }

        // ---------- 2. 创建目录 ----------
        foreach (var relative in sourceDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!targetDirectories.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new SyncPlanItem
                {
                    Kind = SyncActionKind.CreateDirectory,
                    RelativePath = relative,
                    TargetPath = Path.Combine(target, relative),
                    Reason = "目标中缺少的目录"
                });
            }
        }

        // ---------- 3. 复制 / 更新文件 ----------
        foreach (var (relative, info) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!targetFiles.TryGetValue(relative, out var existing))
            {
                items.Add(new SyncPlanItem
                {
                    Kind = SyncActionKind.CopyFile,
                    RelativePath = relative,
                    SourcePath = info.FullName,
                    TargetPath = Path.Combine(target, relative),
                    Size = info.Length,
                    Reason = "目标中不存在的文件"
                });
                continue;
            }

            var needsUpdate = options.Mode == SyncMode.Mirror
                              || IsDifferent(info, existing, options.CompareByHash, cancellationToken);

            if (needsUpdate)
            {
                items.Add(new SyncPlanItem
                {
                    Kind = SyncActionKind.UpdateFile,
                    RelativePath = relative,
                    SourcePath = info.FullName,
                    TargetPath = Path.Combine(target, relative),
                    Size = info.Length,
                    Reason = options.Mode == SyncMode.Mirror ? "镜像模式：强制重写" : "内容或时间不同"
                });
            }
        }

        var plan = new SyncPlan { Items = items };
        _logger?.Info("同步计划：" + plan.Summary);
        return plan;
    }

    /// <summary>执行同步计划。</summary>
    public SyncResult Apply(
        SyncPlan plan,
        FolderSyncOptions options,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        var results = new List<SyncResultItem>(plan.Items.Count);
        var copiedBytes = 0L;
        var processed = 0;

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                switch (item.Kind)
                {
                    case SyncActionKind.CreateDirectory:
                        Directory.CreateDirectory(item.TargetPath);
                        break;

                    case SyncActionKind.DeleteFile:
                        if (File.Exists(item.TargetPath))
                        {
                            SafeDelete.DeleteFile(item.TargetPath, options.UseRecycleBin);
                        }

                        break;

                    case SyncActionKind.DeleteDirectory:
                        if (Directory.Exists(item.TargetPath))
                        {
                            SafeDelete.DeleteDirectory(item.TargetPath, options.UseRecycleBin);
                        }

                        break;

                    case SyncActionKind.CopyFile:
                    case SyncActionKind.UpdateFile:
                    {
                        var directory = Path.GetDirectoryName(item.TargetPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        File.Copy(item.SourcePath, item.TargetPath, overwrite: true);
                        copiedBytes += item.Size;
                        break;
                    }
                }

                results.Add(new SyncResultItem
                {
                    Kind = item.Kind,
                    RelativePath = item.RelativePath,
                    Success = true
                });

                _logger?.Info($"同步 {DescribeAction(item.Kind)}：{item.RelativePath}");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"同步失败（{DescribeAction(item.Kind)}）：{item.RelativePath}", ex);
                results.Add(new SyncResultItem
                {
                    Kind = item.Kind,
                    RelativePath = item.RelativePath,
                    Success = false,
                    Error = ex.Message
                });
            }

            processed++;
            progress?.Report(new SyncProgress
            {
                Percent = plan.Items.Count == 0 ? 100 : processed * 100d / plan.Items.Count,
                Processed = processed,
                Total = plan.Items.Count,
                CurrentAction = $"{DescribeAction(item.Kind)}：{item.RelativePath}"
            });
        }

        var result = new SyncResult { Items = results, CopiedBytes = copiedBytes };
        _logger?.Info(result.Summary);
        return result;
    }

    /// <summary>动作的中文说明。</summary>
    public static string DescribeAction(SyncActionKind kind) => kind switch
    {
        SyncActionKind.CreateDirectory => "创建目录",
        SyncActionKind.CopyFile => "复制文件",
        SyncActionKind.UpdateFile => "更新文件",
        SyncActionKind.DeleteFile => "删除多余文件",
        SyncActionKind.DeleteDirectory => "删除多余目录",
        _ => kind.ToString()
    };

    /// <summary>安全性校验：源与目标不能相同 / 互相包含。</summary>
    public static string? ValidateDirectories(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return "源目录与目标目录不能是同一个目录。";
        }

        if (IsSubPathOf(target, source))
        {
            return "目标目录不能位于源目录内部（会递归复制自身）。";
        }

        if (IsSubPathOf(source, target))
        {
            return "源目录不能位于目标目录内部（删除操作可能误删源文件）。";
        }

        return null;
    }

    /// <summary><paramref name="path"/> 是否位于 <paramref name="parent"/> 内部。</summary>
    private static bool IsSubPathOf(string path, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断文件是否需要更新（大小不同或内容不同）。</summary>
    private bool IsDifferent(FileInfo source, FileInfo target, bool compareByHash, CancellationToken cancellationToken)
    {
        if (source.Length != target.Length)
        {
            return true;
        }

        if (compareByHash)
        {
            return !HashEngine.AreEqual(source.FullName, target.FullName, HashAlgorithmKind.SHA256, cancellationToken);
        }

        return source.LastWriteTimeUtc != target.LastWriteTimeUtc;
    }

    /// <summary>源目录中是否存在该相对目录（或其下的文件）。</summary>
    private static bool IsDirectoryInSource(
        string relativeDirectory,
        Dictionary<string, FileInfo> sourceFiles,
        List<string> sourceDirectories)
    {
        if (sourceDirectories.Contains(relativeDirectory, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = relativeDirectory + Path.DirectorySeparatorChar;
        return sourceFiles.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>枚举文件（相对路径 -&gt; FileInfo）。</summary>
    private static Dictionary<string, FileInfo> EnumerateFiles(
        string root,
        SearchOption searchOption,
        HashSet<string> excludes)
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(root, "*", searchOption))
        {
            var relative = Path.GetRelativePath(root, file);
            if (IsExcluded(relative, excludes))
            {
                continue;
            }

            result[relative] = new FileInfo(file);
        }

        return result;
    }

    /// <summary>枚举目录（相对路径列表）。</summary>
    private static List<string> EnumerateDirectories(
        string root,
        SearchOption searchOption,
        HashSet<string> excludes)
    {
        var result = new List<string>();

        foreach (var directory in Directory.EnumerateDirectories(root, "*", searchOption))
        {
            var relative = Path.GetRelativePath(root, directory);
            if (IsExcluded(relative, excludes))
            {
                continue;
            }

            result.Add(relative);
        }

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

        return segments.Length > 0 && segments.Any(excludes.Contains);
    }

    /// <summary>路径深度（用于「深的目录先删除」）。</summary>
    private static int CountDepth(string relativePath)
        => relativePath.Count(static ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
}
