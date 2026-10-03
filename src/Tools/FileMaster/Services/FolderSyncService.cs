using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 文件夹同步 / 镜像服务：以源目录为准，把差异整理成「删除 + 创建目录 + 复制」的计划，
/// 计划可先预览、二次确认后再执行；删除默认走回收站。
/// </summary>
/// <remarks>
/// <para>计划列表按「删除 -&gt; 新建目录 -&gt; 复制」展示，但 <see cref="Apply"/> 的执行顺序是
/// <b>先复制、后删除</b>：只有复制阶段全部成功后才执行删除。
/// 这样即使「复制失败」，目标也不会比同步前更少（删除是唯一会让目标变少的动作）。</para>
/// <para>三种模式：
/// <list type="bullet">
/// <item><see cref="SyncMode.CopyOnly"/>：只复制新增 / 更新的文件，不动目标中的多余内容。</item>
/// <item><see cref="SyncMode.OneWaySync"/>：复制新增 / 更新，并删除目标中源目录不存在的内容。</item>
/// <item><see cref="SyncMode.Mirror"/>：目标完全等于源（在单向同步基础上强制重写所有文件）。</item>
/// </list></para>
/// <para>安全检查：源目录与目标目录不能相同，也不能互相包含；
/// 目录联接 / 符号链接（重解析点）不会被递归进去。</para>
/// </remarks>
public sealed class FolderSyncService
{
    /// <summary>「因含被忽略条目而退化为逐项删除」的计划项原因（P1-14）。</summary>
    public const string DegradedDeleteReason = "目录内含被忽略条目，退化为逐项删除";

    /// <summary>「因复制失败已跳过删除」的结果项说明（P1-13）。</summary>
    public const string SkippedDeleteMessage =
        "因复制失败已跳过删除：为避免目标内容比同步前更少，本次未执行该删除动作，请修复复制失败后重新同步。";

    /// <summary>原子复制使用的临时文件扩展名（不会与用户文件同名冲突）。</summary>
    internal const string TempExtension = ".wxtmp";

    /// <summary>形如 <c>{文件名}.{guid}.wxtmp</c> 的暂存文件：该扩展名只属于本工具。</summary>
    private static readonly string TempSearchPattern = "*" + TempExtension;

    /// <summary>陈旧暂存文件的清理阈值（超过该年龄才认为是中断残留）。</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    /// <summary>
    /// 各目录上次清理陈旧暂存文件的时间（按目录节流，避免每次复制都扫同一个目录）。
    /// <para>按目录而不是全进程记时：不同目标目录各自独立，测试与并发使用都不会互相干扰。</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastStaleSweepByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

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
            var extraFiles = new List<(string Relative, FileInfo Info)>();
            var extraDirectories = new List<string>();

            // P1-14：被忽略条目在枚举阶段就被过滤掉了，因此「目标中多余的目录」判定看不到它们。
            // 这些相对路径用于把「因含被忽略条目而退化为逐项删除」的计划项标注清楚。
            var degradedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (relative, info) in targetFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!sourceFiles.ContainsKey(relative))
                {
                    extraFiles.Add((relative, info));
                }
            }

            // 目录按深度从深到浅删除，避免父目录先被删掉
            foreach (var relative in targetDirectories
                         .Where(relative => !IsDirectoryInSource(relative, sourceFiles, sourceDirectories))
                         .OrderByDescending(CountDepth))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 忽略名单为空时与旧行为完全一致（不做任何子树复核，零额外开销）
                if (excludeSet.Count == 0)
                {
                    extraDirectories.Add(relative);
                    continue;
                }

                // P1-14：用【未过滤的真实枚举】复核子树，而不是依赖已过滤的枚举结果。
                // 子树里只要有被忽略条目，就放弃整目录递归删除，退化为逐项删除
                //（未被忽略的文件已经在上面的 extraFiles 里，逐项删除不会碰忽略内容）。
                var degraded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!CollectDegradedSubtree(target, relative, excludeSet, degraded))
                {
                    extraDirectories.Add(relative);
                    continue;
                }

                _logger?.Info($"目标目录「{relative}」内含被忽略条目，放弃整目录删除，改为逐项删除。");
                degradedPaths.UnionWith(degraded);
            }

            foreach (var (relative, info) in extraFiles)
            {
                items.Add(new SyncPlanItem
                {
                    Kind = SyncActionKind.DeleteFile,
                    RelativePath = relative,
                    TargetPath = info.FullName,
                    Size = info.Length,
                    Reason = degradedPaths.Contains(relative) ? DegradedDeleteReason : "目标中多余的文件"
                });
            }

            foreach (var relative in extraDirectories)
            {
                items.Add(new SyncPlanItem
                {
                    Kind = SyncActionKind.DeleteDirectory,
                    RelativePath = relative,
                    TargetPath = Path.Combine(target, relative),
                    Reason = degradedPaths.Contains(relative) ? DegradedDeleteReason : "目标中多余的目录"
                });
            }
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
    /// <remarks>等价于 <see cref="ApplyWithReport"/>，只是不返回「跳过删除」的附加信息。</remarks>
    public SyncResult Apply(
        SyncPlan plan,
        FolderSyncOptions options,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ApplyWithReport(plan, options, progress, cancellationToken).Result;

    /// <summary>
    /// 执行同步计划，并返回「因复制失败跳过了多少删除项」等附加信息。
    /// </summary>
    /// <remarks>
    /// <para>执行分两个阶段，与计划列表的展示顺序无关：</para>
    /// <list type="number">
    /// <item>阶段一：<see cref="SyncActionKind.CreateDirectory"/> / <see cref="SyncActionKind.CopyFile"/> /
    /// <see cref="SyncActionKind.UpdateFile"/> —— 只会让目标内容变多；</item>
    /// <item>阶段二：<see cref="SyncActionKind.DeleteFile"/> / <see cref="SyncActionKind.DeleteDirectory"/> ——
    /// 只有在阶段一<b>全部成功</b>后才执行。</item>
    /// </list>
    /// <para>原因（P1-13）：删除是唯一会让目标变少的动作。旧实现按计划顺序执行（删除排在复制之前），
    /// 一旦「删除成功、复制失败」，目标就会比同步前更少 —— 那是真实的数据丢失。
    /// 现在复制阶段只要有一项失败，就整体跳过删除阶段，被跳过的删除项
    /// <see cref="SyncResultItem.Success"/> 为 false、<see cref="SyncResultItem.Error"/> 写明原因，
    /// 便于 UI 提示「因复制失败已跳过删除」。</para>
    /// <para><see cref="SyncResult.Items"/> 与 <see cref="SyncPlan.Items"/> 一一对应、顺序一致。</para>
    /// </remarks>
    public FolderSyncApplyReport ApplyWithReport(
        SyncPlan plan,
        FolderSyncOptions options,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        var planItems = plan.Items;
        var results = new SyncResultItem?[planItems.Count];
        var copiedBytes = 0L;
        var processed = 0;
        var copyFailed = false;

        // ---------- 阶段一：创建目录 + 复制 / 更新文件（只增不减） ----------
        for (var index = 0; index < planItems.Count; index++)
        {
            var item = planItems[index];
            if (IsDeleteKind(item.Kind))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var resultItem = ExecuteItem(item, options, ref copiedBytes);
            results[index] = resultItem;
            copyFailed |= !resultItem.Success;

            processed++;
            ReportProgress(progress, planItems.Count, processed, item);
        }

        // ---------- 阶段二：删除（复制阶段有任何失败时整体跳过） ----------
        var skippedDeletes = 0;

        for (var index = 0; index < planItems.Count; index++)
        {
            var item = planItems[index];
            if (!IsDeleteKind(item.Kind))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (copyFailed)
            {
                // 删除会让目标比同步前更少：复制已经失败的情况下，宁可不删。
                // 标记为「跳过」而不是「失败」—— 这是有意的保护动作，不该在界面上显示成错误。
                results[index] = new SyncResultItem
                {
                    Kind = item.Kind,
                    RelativePath = item.RelativePath,
                    Success = false,
                    Skipped = true,
                    Error = SkippedDeleteMessage
                };

                skippedDeletes++;
                _logger?.Warn($"因复制失败已跳过删除：{item.RelativePath}");
            }
            else
            {
                results[index] = ExecuteItem(item, options, ref copiedBytes);
            }

            processed++;
            ReportProgress(progress, planItems.Count, processed, item);
        }

        var resultItems = new SyncResultItem[planItems.Count];
        for (var index = 0; index < planItems.Count; index++)
        {
            resultItems[index] = results[index] ?? new SyncResultItem
            {
                Kind = planItems[index].Kind,
                RelativePath = planItems[index].RelativePath,
                Success = false,
                Error = "未执行"
            };
        }

        var result = new SyncResult { Items = resultItems, CopiedBytes = copiedBytes };
        var report = new FolderSyncApplyReport
        {
            Result = result,
            SkippedDeleteCount = skippedDeletes,
            CopyPhaseFailed = copyFailed,
            Note = skippedDeletes > 0
                ? $"复制阶段存在失败，已跳过删除 {skippedDeletes} 项：目标内容不会比同步前更少，请修复复制失败后重新同步。"
                : null
        };

        _logger?.Info(report.Summary);
        return report;
    }

    /// <summary>删除类动作（需要放到复制阶段之后执行）。</summary>
    private static bool IsDeleteKind(SyncActionKind kind)
        => kind is SyncActionKind.DeleteFile or SyncActionKind.DeleteDirectory;

    /// <summary>执行单个计划项；异常一律转成失败结果，不向外抛。</summary>
    private SyncResultItem ExecuteItem(SyncPlanItem item, FolderSyncOptions options, ref long copiedBytes)
    {
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
                        if ((new DirectoryInfo(item.TargetPath).Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            // 目录联接 / 符号链接：只删链接本身（非递归）。删链接【不会】影响其目标数据。
                            Directory.Delete(item.TargetPath, recursive: false);
                        }
                        else
                        {
                            // 递归删除前先摘掉它内部的目录联接 / 符号链接：
                            // Directory.Delete(recursive: true) 遇到联接会先删链接再抛「访问被拒绝」，
                            // 结果是父目录残留 + 整个同步报一个假失败。逐个删链接只影响链接本身，不动其目标数据。
                            RemoveReparsePointChildren(item.TargetPath, _logger);

                            SafeDelete.DeleteDirectory(item.TargetPath, options.UseRecycleBin);
                        }
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

                    CopyAtomically(item.SourcePath, item.TargetPath, _logger);
                    copiedBytes += item.Size;
                    break;
                }
            }

            _logger?.Info($"同步 {DescribeAction(item.Kind)}：{item.RelativePath}");

            return new SyncResultItem
            {
                Kind = item.Kind,
                RelativePath = item.RelativePath,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            // 取消不是「这一项失败」，交给调用方按取消处理
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"同步失败（{DescribeAction(item.Kind)}）：{item.RelativePath}", ex);
            return new SyncResultItem
            {
                Kind = item.Kind,
                RelativePath = item.RelativePath,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>上报进度（分母始终是计划项总数）。</summary>
    private static void ReportProgress(IProgress<SyncProgress>? progress, int total, int processed, SyncPlanItem item)
    {
        progress?.Report(new SyncProgress
        {
            Percent = total == 0 ? 100 : processed * 100d / total,
            Processed = processed,
            Total = total,
            CurrentAction = $"{DescribeAction(item.Kind)}：{item.RelativePath}"
        });
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
        var normalizedSource = NormalizeForComparison(source);
        var normalizedTarget = NormalizeForComparison(target);

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return "源目录与目标目录不能是同一个目录。";
        }

        if (IsSubPathOf(normalizedTarget, normalizedSource))
        {
            return "目标目录不能位于源目录内部（会递归复制自身）。";
        }

        if (IsSubPathOf(normalizedSource, normalizedTarget))
        {
            return "源目录不能位于目标目录内部（删除操作可能误删源文件）。";
        }

        return null;
    }

    /// <summary>
    /// 比较用归一化：统一分隔符 + 去掉尾部分隔符。
    /// <para>这样 <c>D:\foo\</c>、<c>D:/foo</c> 都被视为 <c>D:\foo</c>：
    /// 既不会把「同一个目录」误判成子路径，也不会漏判真正的包含关系。</para>
    /// </summary>
    private static string NormalizeForComparison(string path)
        => path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// <paramref name="path"/> 是否位于 <paramref name="parent"/> 内部（两者完全相同时返回 false）。
    /// <para>两侧都先去尾分隔符、再补一个分隔符后做前缀比较，因此
    /// <c>D:\foobar</c> 不会被误判为 <c>D:\foo</c> 的子路径，
    /// 带尾分隔符的 <c>D:\foo\</c> 也不会因为「重复补分隔符」而失配。</para>
    /// </summary>
    private static bool IsSubPathOf(string path, string parent)
    {
        var normalizedParent = NormalizeForComparison(parent);
        var normalizedPath = NormalizeForComparison(path);

        if (normalizedParent.Length == 0
            || string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 原子复制：先写同目录下的唯一暂存文件，复制成功后再原子替换目标文件。
    /// </summary>
    /// <remarks>
    /// 不能直接用 <c>File.Copy(src, dst, overwrite: true)</c>：它**先截断目标**再写内容，
    /// 因此复制中途失败（源读错误 / U 盘拔出 / 目标写满 / 源被加字节范围锁）会把目标里
    /// 原有的完整旧版本变成一个「长度对、内容全 0」的坏文件 —— 旧版本彻底丢失。
    /// <para>用暂存文件 + 原子替换后，失败时旧版本逐字节保持不动，只是本次复制失败。</para>
    /// <para>暂存文件名带随机后缀且使用本工具专属扩展名（<see cref="TempExtension"/>），
    /// 因此**绝不会**与目标目录里的用户文件同名、更不会去删用户文件；
    /// 中断残留由 <see cref="SweepStaleTempFiles"/> 按年龄清理。</para>
    /// </remarks>
    internal static void CopyAtomically(string sourcePath, string targetPath, Logger? logger)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            SweepStaleTempFiles(directory, logger);
        }

        var tempPath = BuildTempPath(targetPath);

        try
        {
            File.Copy(sourcePath, tempPath, overwrite: false);

            // 暂存文件与目标同目录 => 同卷 => 原子替换
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            // 失败时清掉自己这个半截暂存文件；目标文件保持原样（这才是「不丢旧版本」的关键）
            TryDeleteTempFile(tempPath, logger);
            throw;
        }
    }

    /// <summary>生成唯一暂存文件路径：<c>{目标名}.{随机}.wxtmp</c>（同目录、同卷）。</summary>
    private static string BuildTempPath(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        var fileName = Path.GetFileName(targetPath);
        var name = $"{fileName}.{Guid.NewGuid():N}{TempExtension}";

        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>
    /// 清理超过 <see cref="StaleTempAge"/> 的陈旧暂存文件（上次被强杀留下的）。
    /// </summary>
    /// <remarks>
    /// 只按「本工具专属扩展名 + 足够老的修改时间」筛选，因此不会误删用户文件、也不会
    /// 影响正在并发执行的另一次复制。清理失败一律忽略：它只是卫生工作，不影响本次复制结论。
    /// <para>进程级节流：同一小时内最多扫一次，避免每次复制都遍历目录。</para>
    /// </remarks>
    private static void SweepStaleTempFiles(string directory, Logger? logger)
    {
        var now = DateTime.UtcNow;

        // 按目录节流：同一目录一小时内只扫一次
        if (LastStaleSweepByDirectory.TryGetValue(directory, out var last)
            && now - last < StaleTempAge)
        {
            return;
        }

        LastStaleSweepByDirectory[directory] = now;

        try
        {
            foreach (var stale in Directory.EnumerateFiles(directory, TempSearchPattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    // 只清理【本工具生成的】暂存文件：名字形如 {目标名}.{32位十六进制}.wxtmp。
                    // 仅按扩展名删会误伤用户恰好同扩展名的老旧文件，这里再核对一次文件名形态。
                    if (!IsOwnTempFileName(Path.GetFileName(stale)))
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) < StaleTempAge)
                    {
                        continue;
                    }

                    File.Delete(stale);
                    logger?.Info($"已清理陈旧暂存文件：{stale}");
                }
                catch (Exception ex)
                {
                    // 例如文件正被其它写入者独占：跳过即可，绝不影响本次复制
                    logger?.Warn($"清理陈旧暂存文件失败：{stale}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Warn($"扫描陈旧暂存文件失败：{directory}", ex);
        }
    }

    /// <summary>
    /// 判定文件名是否是本工具生成的暂存文件：<c>{任意名}.{32 位十六进制 GUID}.wxtmp</c>。
    /// </summary>
    private static bool IsOwnTempFileName(string fileName)
    {
        if (!fileName.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 去掉扩展名后，最后一段必须正好是 32 位十六进制（Guid.NewGuid():N）
        var withoutExtension = fileName[..^TempExtension.Length];
        var separator = withoutExtension.LastIndexOf('.');

        if (separator < 1 || withoutExtension.Length - separator - 1 != 32)
        {
            return false;
        }

        var token = withoutExtension.AsSpan(separator + 1);

        foreach (var ch in token)
        {
            var isHex = (ch >= '0' && ch <= '9')
                        || (ch >= 'a' && ch <= 'f')
                        || (ch >= 'A' && ch <= 'F');

            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 递归删除目录【之前】，先把目录树里的目录联接 / 符号链接逐个摘掉（只删链接本身）。
    /// </summary>
    /// <remarks>
    /// <c>Directory.Delete(path, recursive: true)</c> 遇到联接时会先删掉链接、再抛
    /// 「对路径 xxx 的访问被拒绝」，于是父目录残留、本轮同步无谓地报一个失败。
    /// <para>删链接只影响链接本身，<b>不会</b>触碰它指向的数据；摘掉之后再递归删父目录就一路顺畅。</para>
    /// <para>逐个失败不影响整体：真正的错误会由随后的递归删除抛出。</para>
    /// </remarks>
    private static void RemoveReparsePointChildren(string directory, Logger? logger)
    {
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch (Exception ex)
            {
                logger?.Warn($"枚举目录失败，跳过其联接清理：{current}", ex);
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex)
                {
                    logger?.Warn($"读取属性失败，跳过：{entry}", ex);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    try
                    {
                        // 只删链接本身：recursive:false 保证不会顺着链接删到目标数据
                        Directory.Delete(entry, recursive: false);
                        logger?.Info($"已摘除目录联接/符号链接（只删链接，不影响其目标数据）：{entry}");
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn($"删除目录联接失败：{entry}", ex);
                    }

                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    /// <summary>清理未完成的临时文件（失败不影响原始异常）。</summary>
    private static void TryDeleteTempFile(string tempPath, Logger? logger)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            logger?.Warn($"清理同步临时文件失败：{tempPath}", ex);
        }
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
    private Dictionary<string, FileInfo> EnumerateFiles(
        string root,
        SearchOption searchOption,
        HashSet<string> excludes)
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        EnumerateTree(root, searchOption, excludes, result, directories: null);
        return result;
    }

    /// <summary>枚举目录（相对路径列表）。</summary>
    private List<string> EnumerateDirectories(
        string root,
        SearchOption searchOption,
        HashSet<string> excludes)
    {
        var result = new List<string>();
        EnumerateTree(root, searchOption, excludes, files: null, result);
        return result;
    }

    /// <summary>
    /// 手写递归遍历（P1-1）：不使用 <c>SearchOption.AllDirectories</c>。
    /// <para>原因：<c>Directory.EnumerateFiles/EnumerateDirectories(root, "*", AllDirectories)</c>
    /// 会跟随目录联接 / 符号链接递归进去，把链接目标（可能在源目录之外、甚至形成环）的内容
    /// 当成源内容复制 / 删除。这里改为显式栈遍历，遇到
    /// <see cref="FileAttributes.ReparsePoint"/> 的目录就跳过并记日志。</para>
    /// <para>忽略名单命中（相对路径任一层级命中）的条目整棵子树跳过；
    /// 相对路径语义（相对 <paramref name="root"/>）与旧实现完全一致。</para>
    /// </summary>
    private void EnumerateTree(
        string root,
        SearchOption searchOption,
        HashSet<string> excludes,
        Dictionary<string, FileInfo>? files,
        List<string>? directories)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var entry in Directory.GetFileSystemEntries(current))
            {
                var relative = Path.GetRelativePath(root, entry);
                if (IsExcluded(relative, excludes))
                {
                    continue;
                }

                var attributes = File.GetAttributes(entry);

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    if (files is not null)
                    {
                        files[relative] = new FileInfo(entry);
                    }

                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    _logger?.Warn($"跳过目录联接 / 符号链接，不递归其内容：{relative}");
                    continue;
                }

                directories?.Add(relative);

                if (searchOption == SearchOption.AllDirectories)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    /// <summary>
    /// P1-14：用【未经过滤的真实枚举】复核「目标中多余」的目录子树里是否存在被忽略条目。
    /// </summary>
    /// <param name="root">目标目录根（用于计算相对路径）。</param>
    /// <param name="relativeDirectory">待复核的相对目录。</param>
    /// <param name="excludes">忽略名单。</param>
    /// <param name="degradedPaths">输出：子树中【未被忽略】的相对路径（文件与目录），用于逐项删除与标注原因。</param>
    /// <returns>子树内是否存在被忽略条目（或读不到的内容）。</returns>
    /// <remarks>
    /// 返回 true 时调用方必须放弃整目录递归删除，改为逐项删除：
    /// 否则 <c>.git</c> 之类的忽略内容会被 <c>Directory.Delete(recursive: true)</c> 一并删掉。
    /// 不跟随重解析点；读不到的子目录按「含不可删除内容」保守处理（宁可少删，不可误删）。
    /// </remarks>
    private static bool CollectDegradedSubtree(
        string root,
        string relativeDirectory,
        HashSet<string> excludes,
        HashSet<string> degradedPaths)
    {
        degradedPaths.Add(relativeDirectory);

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(Path.Combine(root, relativeDirectory));
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or PathTooLongException)
        {
            // 读不到就当作有不可删除的内容：放弃整目录删除，绝不冒「连忽略内容一起删」的险
            return true;
        }

        var foundIgnored = false;

        foreach (var entry in entries)
        {
            var relative = Path.GetRelativePath(root, entry);

            if (IsExcluded(relative, excludes))
            {
                foundIgnored = true;
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or FileNotFoundException
                    or DirectoryNotFoundException or PathTooLongException)
            {
                // 属性读不到：同样保守处理
                foundIgnored = true;
                continue;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                degradedPaths.Add(relative);
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 目录联接 / 符号链接：不递归进去（其内容不由本目录负责），
                // 但它本身必须列入逐项删除清单 —— 否则整目录递归删除会先删掉链接再抛
                // 「访问被拒绝」，导致父目录残留 + 本轮报一个失败。
                degradedPaths.Add(relative);
                continue;
            }

            degradedPaths.Add(relative);
            if (CollectDegradedSubtree(root, relative, excludes, degradedPaths))
            {
                foundIgnored = true;
            }
        }

        return foundIgnored;
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

/// <summary>
/// 同步执行报告：在 <see cref="SyncResult"/> 之外，额外说明「复制失败导致删除被跳过」的情况。
/// </summary>
/// <remarks>
/// 由 <see cref="FolderSyncService.ApplyWithReport"/> 返回；<see cref="FolderSyncService.Apply"/> 只返回
/// <see cref="Result"/>。被跳过的删除项在 <see cref="SyncResult.Items"/> 中同样可见
/// （<see cref="SyncResultItem.Success"/> 为 false，<see cref="SyncResultItem.Error"/> 为
/// <see cref="FolderSyncService.SkippedDeleteMessage"/>）。
/// </remarks>
public sealed class FolderSyncApplyReport
{
    /// <summary>逐项执行结果（顺序与同步计划一致）。</summary>
    public SyncResult Result { get; init; } = new();

    /// <summary>因复制阶段失败而被跳过的删除项数量。</summary>
    public int SkippedDeleteCount { get; init; }

    /// <summary>复制阶段是否出现过失败。</summary>
    public bool CopyPhaseFailed { get; init; }

    /// <summary>是否存在被跳过的删除项。</summary>
    public bool DeletesSkipped => SkippedDeleteCount > 0;

    /// <summary>附加说明（没有跳过时为 null）。</summary>
    public string? Note { get; init; }

    /// <summary>一句话摘要。</summary>
    public string Summary => DeletesSkipped
        ? $"{Result.Summary}（因复制失败已跳过删除 {SkippedDeleteCount} 项）"
        : Result.Summary;
}
