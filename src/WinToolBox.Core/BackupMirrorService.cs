using System.Text;
using System.Text.Json;

namespace WinToolBox.Core;

/// <summary>一次「镜像备份」的结果汇总。</summary>
public sealed class MirrorResult
{
    /// <summary>备份根目录（设备目录）。</summary>
    public string DeviceRootDirectory { get; init; } = string.Empty;

    /// <summary>U 盘最新副本目录（current）。</summary>
    public string CurrentDirectory { get; init; } = string.Empty;

    /// <summary>历史版本根目录（history）。</summary>
    public string HistoryRootDirectory { get; init; } = string.Empty;

    /// <summary>文件复制统计（在 Mirror 内部绑定为真实的 CopyResult）。</summary>
    public CopyResult Copy { get; set; } = new();

    /// <summary>被移入 history 的文件记录。</summary>
    public IList<HistoryMoveRecord> HistoryMoves { get; } = new List<HistoryMoveRecord>();

    /// <summary>被成功移入 history 的文件数。</summary>
    public int ArchivedFiles => HistoryMoves.Count;

    /// <summary>
    /// 复制失败 + 「U 盘已删除」阶段归档失败。
    /// <para><see cref="ArchiveFailures"/> 单独列出「归档失败」这一细分类别用于诊断，不重复累加。</para>
    /// </summary>
    public int FailedFiles => Copy.FailedFiles + DeletionArchiveFailures;

    /// <summary>归档（移入 history）失败数。</summary>
    public int ArchiveFailures { get; set; }

    /// <summary>
    /// 「U 盘已删除」的文件在归档失败后确实已不在 current 中的数量（正常应为 0）。
    /// <para>大于 0 表示真的丢了内容，必须在结果里显著提示。</para>
    /// </summary>
    public int DeletedFilesKnownLost { get; set; }

    /// <summary>
    /// 「U 盘已删除」阶段归档失败的个数。
    /// <para>这部分失败【不在】<see cref="CopyResult.FailedFiles"/> 里（它发生在复制循环之外），
    /// 因此要单独计入 <see cref="FailedFiles"/>。</para>
    /// <para>反之，复制阶段归档失败已经让该文件在复制循环里记为失败，绝不能重复计数。</para>
    /// </summary>
    public int DeletionArchiveFailures { get; set; }

    /// <summary>是否因「连续失败达到阈值」而中止（区别于用户主动取消）。</summary>
    public bool AbortedByFailureThreshold { get; set; }

    /// <summary>是否被取消。</summary>
    public bool Cancelled { get; set; }

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedFiles == 0 && !Cancelled;

    /// <summary>本次备份完成后的 current 文件总数。</summary>
    public int CurrentFileCount { get; set; }

    /// <summary>本次备份完成后的 current 总字节数。</summary>
    public long CurrentTotalBytes { get; set; }

    /// <summary>被清理掉的过期 history 目录名。</summary>
    public IList<string> PurgedHistoryFolders { get; } = new List<string>();

    /// <summary>被清理掉的「上次复制中断留下的 .tmp 暂存残骸」数量。</summary>
    public int CleanupRemovedFiles { get; set; }

    /// <summary>写入的清单路径（未写入时为空）。</summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>归档失败明细。</summary>
    public IList<string> ArchiveErrors { get; } = new List<string>();

    /// <summary>本轮复制失败的文件（源相对路径）。</summary>
    /// <remarks>
    /// 用于事后把「旧版本已归档、但新版本没写进去」的文件从「更新」重新归类为「失败 + 待恢复」，
    /// 否则用户会看到一个既「更新 1」又「失败 1」的矛盾结论。
    /// </remarks>
    public IList<string> FailedRelativePaths { get; } = new List<string>();

    /// <summary>检查某个相对路径的文件是否已归档（测试辅助）。</summary>
    public bool IsArchived(string relativePath)
        => HistoryMoves.Any(m => string.Equals(m.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// UsbBackup 的镜像备份实现，对应产品语义「U 盘的增量镜像 + 30 天版本历史」。
/// <para>目录结构：<c>{目标目录}\{卷标}_{序列号}\{ current\ | history\{yyyy-MM-dd}\ | manifest.json }</c></para>
/// <list type="number">
/// <item>U 盘新增的文件 → 复制到 <c>current</c>。</item>
/// <item>U 盘修改过的文件 → 新版本写入 <c>current</c>，<b>旧版本先移入</b> <c>history\{今天}</c>。</item>
/// <item>U 盘删除的文件 → <c>current</c> 中的副本<b>移入</b> <c>history\{今天}</c>（软删除，绝不直接删）。</item>
/// <item>超过保留期的 <c>history\{yyyy-MM-dd}</c> 目录自动清理。</item>
/// <item>每次备份更新 <c>manifest.json</c>。</item>
/// </list>
/// <para>安全约定：只读源（U 盘）、只写备份目录；绝不删除 current 中的文件内容（只移动）；绝不执行 U 盘中的任何程序。</para>
/// </summary>
public sealed class BackupMirrorService
{
    /// <summary>连续失败多少个子文件后中止备份（避免对着坏盘空转几小时）。</summary>
    public const int ConsecutiveFailureAbortThreshold = 20;

    /// <summary>失败明细最多保留多少条（其余只计数）。</summary>
    public const int MaxFailureDetails = 50;

    /// <summary>
    /// Windows 经典路径长度上限（<c>MAX_PATH</c>）。归档名变长时用它判断是否需要走「短路径兜底」。
    /// </summary>
    public const int MaxClassicPathLength = 260;

    /// <summary>
    /// 长路径兜底使用的子目录前缀：冲突版本放到 <c>history\{日期}\h{n}\</c> 下
    /// （丢掉原相对目录 + 文件名压成哈希），确保兜底路径真的比唯一名短。
    /// </summary>
    public const string ShortFallbackFolderName = "h";

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Logger? _logger;

    /// <summary>创建镜像服务。</summary>
    public BackupMirrorService(Logger? logger = null) => _logger = logger;

    /// <summary>
    /// 执行一次镜像备份。
    /// </summary>
    /// <param name="sourceDirectory">U 盘根目录（只读）。</param>
    /// <param name="backupRootDirectory">用户配置的备份目标目录。</param>
    /// <param name="device">设备信息（决定设备子目录名）。</param>
    /// <param name="excludeRules">排除规则。</param>
    /// <param name="historyRetentionDays">history 保留天数；0 表示永久保留。</param>
    /// <param name="strictContentVerification">是否做严格内容校验。</param>
    /// <param name="now">当前时间（测试可注入）。</param>
    /// <param name="progressCallback">复制进度回调。</param>
    /// <param name="scanProgress">预扫描进度回调（累计发现文件数）。</param>
    /// <param name="maxConsecutiveFailures">连续失败多少个文件后提前中止（默认 <see cref="ConsecutiveFailureAbortThreshold"/>；0 表示不中止）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public MirrorResult Mirror(
        string sourceDirectory,
        string backupRootDirectory,
        UsbDeviceInfo device,
        ExcludeRules excludeRules,
        int historyRetentionDays,
        bool strictContentVerification = true,
        DateTime? now = null,
        Action<CopyProgress>? progressCallback = null,
        Action<int>? scanProgress = null,
        int maxConsecutiveFailures = ConsecutiveFailureAbortThreshold,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRootDirectory);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(excludeRules);

        var timestamp = now ?? DateTime.Now;
        var source = Path.GetFullPath(sourceDirectory);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"源目录不存在：{source}");
        }

        var deviceRoot = BackupRules.BuildDeviceRootDirectory(backupRootDirectory, device);
        var current = BackupRules.BuildCurrentDirectory(backupRootDirectory, device);
        var historyRoot = BackupRules.BuildHistoryRootDirectory(backupRootDirectory, device);
        var todayHistory = BackupRules.BuildHistoryDirectory(backupRootDirectory, device, timestamp);

        AppPaths.EnsureDirectory(current);
        AppPaths.EnsureDirectory(historyRoot);

        // 连续失败早停由 FileCopier 内部实现（真正的 I/O 失败只有它能观测到）。
        var result = new MirrorResult
        {
            DeviceRootDirectory = deviceRoot,
            CurrentDirectory = current,
            HistoryRootDirectory = historyRoot
        };

        // 复制前记录 current 中已存在的相对路径集合：
        // 用来精确区分「U 盘新增」与「覆盖已有文件」，并决定旧版本是否需要先归档。
        var existingBefore = SnapshotRelativePaths(current);

        var archivedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var copier = new FileCopier(_logger)
        {
            OverwriteExistingFiles = true,
            PreserveTimestamps = true,
            StrictContentVerification = strictContentVerification,
            MaxConsecutiveFailures = maxConsecutiveFailures
        };

        copier.OnFileDecision = (_, _, relativePath, willCopy) =>
        {
            // 内容未变：不归档，直接放行（这也是“只改时间戳不该产生历史版本”的保证）
            if (!willCopy || !existingBefore.Contains(relativePath))
            {
                return;
            }

            // 旧版本先移入 history，再让 FileCopier 写入新版本。
            // ArchiveOne 抛异常会让该文件记为复制失败 —— 正确：宁可失败也不能「旧版本已丢、新版本没写」。
            ArchiveOne(current, todayHistory, relativePath, HistoryReason.OverwrittenByNewerVersion, result);
            archivedRelativePaths.Add(relativePath);
        };

        // 预扫描进度：解决“复制开始前长时间零进度”
        copier.OnScanProgress = scanProgress;

        // 记录「新版本没写成功」的文件，稍后把它们的归档重新归类为「待恢复」
        copier.OnCopyFailed = relativePath => result.FailedRelativePaths.Add(relativePath);

        var copy = copier.CopyDirectory(source, current, progressCallback, excludeRules, cancellationToken);

        result.Copy = copy;

        // 失败明细限流（保留前 N 条 + 总数）
        TrimFailureDetails(copy);

        if (copy.AbortedByConsecutiveFailures)
        {
            result.AbortedByFailureThreshold = true;
            _logger?.Warn(
                $"连续失败达到 {maxConsecutiveFailures} 个，已提前中止本轮备份" +
                $"（已处理 {copy.CopiedFiles + copy.SkippedFiles + copy.FailedFiles} 个文件）。");

            // 不再继续归档/清理/统计/写清单：结论交给调用方提示用户
            return result;
        }

        if (copy.Cancelled)
        {
            result.Cancelled = true;
            return result;
        }

        // 归档了旧版本、但新版本没写成功的文件：从「更新」改判为「U 盘已删除（内容已保全在 history）」，
        // 避免用户看到「更新 1 + 失败 1」这种自相矛盾的结论。
        // 返回值是「被改判的个数」，BuildComparison 必须据此扣减，否则同一文件会同时进「更新」和「删除」。
        var reclassified = ReclassifyArchivedButNotCopied(result);

        // ---- U 盘已删除的文件：current 中的副本移入 history（软删除）----
        MoveDeletedToHistory(source, current, todayHistory, excludeRules, result, cancellationToken);

        // ---- 清理「上一次复制被中断」留下的 .tmp 暂存残骸 ----
        CleanupOrphanedStagingFiles(source, current, excludeRules, result);

        // 归档失败数 = 明细条数（ArchiveErrors 是唯一事实来源，避免两处计数互相覆盖）
        result.ArchiveFailures = result.ArchiveErrors.Count;

        // ---- 清理过期 history ----
        PurgeExpiredHistory(historyRoot, historyRetentionDays, timestamp, result);

        // ---- 统计 current 现状 ----
        var (fileCount, totalBytes) = MeasureDirectory(current);
        result.CurrentFileCount = fileCount;
        result.CurrentTotalBytes = totalBytes;

        var comparison = BuildComparison(copy, result, archivedRelativePaths.Count, reclassified);

        // ---- 写入 manifest.json ----
        var manifestPath = WriteManifest(
            backupRootDirectory,
            device,
            deviceRoot,
            timestamp,
            historyRetentionDays,
            result,
            comparison);

        result.ManifestPath = manifestPath;

        var historyPolicy = historyRetentionDays <= 0 ? "永久" : historyRetentionDays + " 天";
        _logger?.Info(
            $"镜像备份完成：{source} -> {current}；{comparison.Summary}" +
            $"转入历史 {result.ArchivedFiles} 个（旧版本 {archivedRelativePaths.Count}，已删除 {comparison.DeletedFiles}），" +
            $"history 保留 {historyPolicy}，current 共 {fileCount} 个文件 / {CopyResult.FormatSize(totalBytes)}。");

        return result;
    }

    /// <summary>连续失败达到阈值时是否应中止（供调用方判断，纯函数便于测试）。</summary>
    public static bool ShouldAbortOnFailures(int consecutiveFailures)
        => consecutiveFailures >= ConsecutiveFailureAbortThreshold;

    /// <summary>失败明细限流：保留前 <see cref="MaxFailureDetails"/> 条，其余折叠成一行计数。</summary>
    public static void TrimFailureDetails(CopyResult copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        if (copy.Errors.Count <= MaxFailureDetails)
        {
            return;
        }

        var total = copy.Errors.Count;
        var kept = copy.Errors.Take(MaxFailureDetails).ToList();
        kept.Add($"……其余 {total - MaxFailureDetails} 条失败明细已省略（共 {total} 条）。");

        copy.Errors.Clear();
        foreach (var line in kept)
        {
            copy.Errors.Add(line);
        }
    }

    /// <summary>读取清单；文件缺失或损坏时返回 null（不抛异常）。</summary>
    public static BackupManifest? ReadManifest(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<BackupManifest>(json, ManifestSerializerOptions);
        }
        catch
        {
            // 清单损坏不该影响备份本身
            return null;
        }
    }

    /// <summary>统计目录下的文件数与总字节数（不保存路径，内存占用恒定）。</summary>
    public static (int FileCount, long TotalBytes) MeasureDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        var count = 0;
        var bytes = 0L;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
            catch
            {
                // 单个文件读不到就跳过统计，不影响备份结论
            }
        }

        return (count, bytes);
    }

    /// <summary>计算 history 目录当前占用字节数（尽力而为）。</summary>
    public static long MeasureHistoryBytes(string historyRootDirectory)
        => Directory.Exists(historyRootDirectory)
            ? MeasureDirectory(historyRootDirectory).TotalBytes
            : 0L;

    // ------------------------------------------------------------------ 内部

    /// <summary>把 current 中相对路径集合快照出来（只存字符串，比完整文件清单轻得多）。</summary>
    private static HashSet<string> SnapshotRelativePaths(string directory)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(directory))
        {
            return set;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            set.Add(Path.GetRelativePath(directory, file));
        }

        return set;
    }

    /// <summary>把一个文件从 current 移入 history{日期}，保留原相对路径。</summary>
    private void ArchiveOne(
        string currentDirectory,
        string historyDirectory,
        string relativePath,
        HistoryReason reason,
        MirrorResult result)
    {
        var sourcePath = Path.Combine(currentDirectory, relativePath);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var targetPath = Path.Combine(historyDirectory, relativePath);
        var resolvedTarget = ResolveArchiveTarget(historyDirectory, relativePath, targetPath);

        long length;
        try
        {
            length = new FileInfo(sourcePath).Length;
        }
        catch
        {
            length = 0;
        }

        try
        {
            var effectiveTargetDirectory = Path.GetDirectoryName(resolvedTarget);
            if (!string.IsNullOrEmpty(effectiveTargetDirectory))
            {
                // 必须放在 try 内：目录创建失败也是「归档失败」，要让调用方看得见
                Directory.CreateDirectory(effectiveTargetDirectory);
            }

            MoveFile(sourcePath, resolvedTarget);

            long archivedLength;
            try
            {
                archivedLength = new FileInfo(resolvedTarget).Length;
            }
            catch
            {
                archivedLength = length;
            }

            result.HistoryMoves.Add(new HistoryMoveRecord
            {
                RelativePath = relativePath,
                Reason = reason,
                ArchivedTo = resolvedTarget,
                Length = archivedLength
            });

            _logger?.Info(
                $"已保留旧版本：{relativePath} -> {resolvedTarget}（" +
                (reason == HistoryReason.DeletedOnSource ? "U盘已删除" : "被新版本覆盖") + "）");
        }
        catch (Exception ex)
        {
            // 这里不再往 ArchiveErrors 里加明细：复制阶段由 FileCopier 统一记录失败明细
            // （同一个文件只应出现一次，否则失败数会翻倍、清单里出现重复条目）。
            _logger?.Warn($"保留旧版本失败：{sourcePath}", ex);
            throw;
        }
    }

    /// <summary>
    /// 决定归档目标路径。
    /// <para>同名路径已被占用时不能覆盖（每一版都要留），因此改用唯一名字；</para>
    /// <para>若唯一名字会让路径超过 Windows 经典长度上限（长路径盘上很常见），
    /// 则退化为 <c>history\{日期}\_c{序号}\{哈希}{扩展名}</c> —— 丢掉原相对目录并压缩文件名，
    /// 确保兜底路径【真的比唯一名短】，否则这个分支等于没兜底。</para>
    /// </summary>
    private static string ResolveArchiveTarget(string historyDirectory, string relativePath, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return targetPath;
        }

        var uniquePath = BuildUniqueArchivePath(historyDirectory, relativePath);
        if (uniquePath.Length <= MaxClassicPathLength)
        {
            return uniquePath;
        }

        var fallback = BuildShortArchivePath(historyDirectory, relativePath);

        // 兜底必须真的更短才有意义；否则原样返回唯一名（宁可它失败，也不要一个"更长的兜底"）
        return fallback.Length < uniquePath.Length ? fallback : uniquePath;
    }

    /// <summary>
    /// 为同日冲突的归档文件生成唯一路径，例如
    /// <c>history\2026-10-03\report@143005.txt</c>、同秒再冲突时加 <c>@143005-2</c>。
    /// <para>名字保持可读，便于用户直接在 history 里找到对应文件。</para>
    /// </summary>
    private static string BuildUniqueArchivePath(string historyDirectory, string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var extension = Path.GetExtension(relativePath);

        var targetDirectory = string.IsNullOrEmpty(directory)
            ? historyDirectory
            : Path.Combine(historyDirectory, directory);

        var stamp = DateTime.Now.ToString("HHmmss");

        var candidate = Path.Combine(targetDirectory, $"{fileName}@{stamp}{extension}");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(targetDirectory, $"{fileName}@{stamp}-{suffix}{extension}");
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// 长路径兜底：逐级缩短归档路径，确保兜底【真的比唯一名短】。
    /// <list type="number">
    /// <item><c>history\{日期}\h{n}\{哈希}{扩展名}</c> —— 丢掉原相对目录、文件名压成哈希；</item>
    /// <item><c>history\{日期}\{哈希}{扩展名}</c> —— 再去掉一层目录；</item>
    /// <item><c>history\{哈希}{扩展名}</c> —— 连日期目录也去掉（该文件不再参与按天清理，
    /// 需要用户自行在 history 里找到它；这是「宁可换个地方放，也不能丢」的最后手段）。</item>
    /// </list>
    /// </summary>
    private static string BuildShortArchivePath(string historyDirectory, string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        var digest = ComputeShortDigest(relativePath);
        var historyRoot = Path.GetDirectoryName(historyDirectory) ?? historyDirectory;

        var candidates = new List<string>();

        for (var index = 1; index <= 99; index++)
        {
            candidates.Add(Path.Combine(historyDirectory, $"{ShortFallbackFolderName}{index}", digest + extension));
        }

        candidates.Add(Path.Combine(historyDirectory, digest + extension));
        candidates.Add(Path.Combine(historyRoot, digest + extension));

        foreach (var candidate in candidates)
        {
            if (candidate.Length < MaxClassicPathLength && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 连最短的候选都放不下：返回最短的那个（由调用方比较后决定，失败方向仍然是安全的：
        // 归档失败时文件会留在 current，不会被删掉）。
        return candidates[^1];
    }

    /// <summary>把相对路径压成 8 位十六进制摘要，用于长路径兜底的文件名。</summary>
    private static string ComputeShortDigest(string relativePath)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(relativePath.ToLowerInvariant()));

        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    /// <summary>移动文件；跨卷（File.Move 失败）时退化为「复制 + 删除」，复制成功前绝不动源文件。</summary>
    private static void MoveFile(string sourcePath, string targetPath)
    {
        try
        {
            File.Move(sourcePath, targetPath);
        }
        catch (IOException)
        {
            // 跨卷 move 会抛 IOException：先复制，确认成功后再删源
            File.Copy(sourcePath, targetPath, overwrite: false);
            File.Delete(sourcePath);
        }
    }

    /// <summary>把 current 中「U 盘已不存在」的文件移入 history（软删除，绝不直接删）。</summary>
    private void MoveDeletedToHistory(
        string sourceDirectory,
        string currentDirectory,
        string historyDirectory,
        ExcludeRules excludeRules,
        MirrorResult result,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(currentDirectory))
        {
            return;
        }

        foreach (var currentFile in Directory.EnumerateFiles(currentDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(currentDirectory, currentFile);
            var name = Path.GetFileName(currentFile);

            // 被排除规则排除的文件本来就不参与备份，不属于「U 盘已删除」
            if (excludeRules.IsExcludedFile(name))
            {
                continue;
            }

            if (File.Exists(Path.Combine(sourceDirectory, relativePath)))
            {
                continue;
            }

            try
            {
                ArchiveOne(currentDirectory, historyDirectory, relativePath, HistoryReason.DeletedOnSource, result);
            }
            catch (Exception ex)
            {
                // 归档失败必须让用户看得见：记入明细，并说明后果与文件现状。
                var stillThere = File.Exists(currentFile);
                result.ArchiveErrors.Add($"{relativePath}（U盘已删除，归档失败）：{ex.Message}");
                result.DeletionArchiveFailures++;
                result.DeletedFilesKnownLost += stillThere ? 0 : 1;

                _logger?.Error(
                    $"无法把「U 盘已删除」的文件移入 history：{relativePath}。" +
                    (stillThere
                        ? "该文件仍保留在 current 中，下次备份会重试。"
                        : "该文件已不在 current 中！"),
                    ex);
            }
        }
    }

    /// <summary>构造与上次备份的对比结果。</summary>
    /// <remarks>
    /// 「更新」= 复制前 current 已有该相对路径、且文件真的被成功更新（每份旧版本都已归档到 history）。
    /// 「新增」= 复制成功且复制前 current 里没有该相对路径。被改判为「已删除」的文件不再计入这两者。
    /// </remarks>
    private static BackupComparison BuildComparison(
        CopyResult copy,
        MirrorResult result,
        int archivedOverwrites,
        int reclassified)
    {
        var deleted = result.HistoryMoves.Count(m => m.Reason == HistoryReason.DeletedOnSource);
        var effectiveUpdated = Math.Max(0, archivedOverwrites - reclassified);

        return new BackupComparison
        {
            NewFiles = Math.Max(0, copy.CopiedFiles - effectiveUpdated),
            UpdatedFiles = effectiveUpdated,
            DeletedFiles = deleted,
            UnchangedFiles = copy.SkippedFiles,
            FailedFiles = copy.FailedFiles + result.ArchiveFailures
        };
    }

    /// <summary>
    /// 把「旧版本已归档、但新版本没写成功」的文件从 <see cref="HistoryReason.OverwrittenByNewerVersion"/>
    /// 改判为 <see cref="HistoryReason.DeletedOnSource"/>。
    /// </summary>
    /// <returns>被改判的文件个数。调用方必须据此从「更新」计数里扣减，
    /// 否则同一个文件会同时出现在「更新」和「删除」里，并挤掉真正的新增文件。</returns>
    /// <remarks>
    /// 事实就是如此：current 里已经没有这个文件了，而它的最后一份内容安全地躺在 history 里。
    /// </remarks>
    private int ReclassifyArchivedButNotCopied(MirrorResult result)
    {
        if (result.FailedRelativePaths.Count == 0 || result.HistoryMoves.Count == 0)
        {
            return 0;
        }

        var failed = new HashSet<string>(result.FailedRelativePaths, StringComparer.OrdinalIgnoreCase);
        var reclassified = 0;

        for (var i = 0; i < result.HistoryMoves.Count; i++)
        {
            var move = result.HistoryMoves[i];
            if (move.Reason != HistoryReason.OverwrittenByNewerVersion || !failed.Contains(move.RelativePath))
            {
                continue;
            }

            result.HistoryMoves[i] = new HistoryMoveRecord
            {
                RelativePath = move.RelativePath,
                Reason = HistoryReason.DeletedOnSource,
                ArchivedTo = move.ArchivedTo,
                Length = move.Length
            };
            reclassified++;

            _logger?.Warn(
                $"旧版本已保存到 history，但新版本写入失败：{move.RelativePath}。" +
                $"current 中已无该文件，可从 {move.ArchivedTo} 恢复。");
        }

        return reclassified;
    }

    /// <summary>
    /// 清理上一次复制被强制中断时留下的 <c>{名字}.tmp</c> 暂存残骸。
    /// <para>这些文件既不在 U 盘上，也不属于备份内容；如果不管，它们会永久留在 current 里污染备份。
    /// 只有当「对应源文件在本轮复制后确实存在」时才删除，避免误删用户自己的同名文件。</para>
    /// </summary>
    private void CleanupOrphanedStagingFiles(
        string sourceDirectory,
        string currentDirectory,
        ExcludeRules excludeRules,
        MirrorResult result)
    {
        if (!Directory.Exists(currentDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(currentDirectory, "*.tmp", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(currentDirectory, file);

            // 只处理「由本程序产生」的 .tmp：它一定对应一个最终文件名
            if (!relativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var finalRelativePath = relativePath[..^4];

            // 若该名字本身就是被排除的后缀（例如源里真有 a.tmp），不要碰
            if (excludeRules.IsExcludedFile(Path.GetFileName(finalRelativePath)))
            {
                continue;
            }

            // 只有当「去掉 .tmp 后的正式文件」在源里存在时才认定它是本程序的残骸
            if (!File.Exists(Path.Combine(sourceDirectory, finalRelativePath)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                result.CleanupRemovedFiles++;
                _logger?.Info($"已清理上次复制中断留下的暂存文件：{file}");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"清理暂存文件失败：{file}", ex);
            }
        }
    }

    /// <summary>清理超过保留期的 history{yyyy-MM-dd} 目录。</summary>
    private void PurgeExpiredHistory(
        string historyRootDirectory,
        int retentionDays,
        DateTime now,
        MirrorResult result)
    {
        if (retentionDays <= 0 || !Directory.Exists(historyRootDirectory))
        {
            return;
        }

        var cutoff = now.Date.AddDays(-retentionDays);

        foreach (var directory in Directory.GetDirectories(historyRootDirectory))
        {
            var folderName = Path.GetFileName(directory);
            var folderDate = BackupRules.TryParseHistoryFolderName(folderName);

            // 非日期目录（用户自己放的）不动
            if (folderDate is null)
            {
                continue;
            }

            // 严格早于截止日才清理：保留期内的（含当天）一律保留
            if (folderDate.Value.Date >= cutoff)
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                result.PurgedHistoryFolders.Add(folderName);
                _logger?.Info($"已清理过期历史版本目录：{directory}（保留 {retentionDays} 天）");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"清理过期历史版本目录失败：{directory}", ex);
            }
        }
    }

    /// <summary>构造与上次备份的对比结果。</summary>
    private static BackupComparison BuildComparison(
        BackupManifest? previous,
        HashSet<string> existingBefore,
        CopyResult copy,
        MirrorResult result,
        int currentFileCount)
    {
        var deleted = result.HistoryMoves.Count(m => m.Reason == HistoryReason.DeletedOnSource);
        var archivedOverwrites = result.HistoryMoves.Count(m => m.Reason == HistoryReason.OverwrittenByNewerVersion);

        return new BackupComparison
        {
            // 「新增」= 复制成功且复制前 current 里没有的文件
            NewFiles = Math.Max(0, copy.CopiedFiles - archivedOverwrites),
            UpdatedFiles = archivedOverwrites,
            DeletedFiles = deleted,
            UnchangedFiles = copy.SkippedFiles,
            FailedFiles = copy.FailedFiles + result.ArchiveErrors.Count
        };
    }

    /// <summary>写入 manifest.json（先写临时文件再替换）。</summary>
    private string WriteManifest(
        string backupRootDirectory,
        UsbDeviceInfo device,
        string deviceRootDirectory,
        DateTime timestamp,
        int retentionDays,
        MirrorResult result,
        BackupComparison comparison)
    {
        var manifestPath = BackupRules.BuildManifestPath(backupRootDirectory, device);

        var manifest = new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,
            LastBackupTime = timestamp,
            DeviceUniqueId = device.UniqueId,
            DeviceDisplayName = device.DisplayName,
            DeviceRootDirectory = deviceRootDirectory,
            TotalFiles = result.CurrentFileCount,
            TotalBytes = result.CurrentTotalBytes,
            HistoryRetentionDays = retentionDays,
            HistoryKeptForever = retentionDays <= 0,
            HistoryBytes = MeasureHistoryBytes(result.HistoryRootDirectory),
            PurgedHistoryFolders = result.PurgedHistoryFolders.ToList(),
            Comparison = comparison,
            HistoryMoves = result.HistoryMoves.ToList()
        };

        try
        {
            var json = JsonSerializer.Serialize(manifest, ManifestSerializerOptions);
            var tempPath = manifestPath + ".tmp";

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, manifestPath, overwrite: true);

            _logger?.Info($"已更新备份清单：{manifestPath}");
            return manifestPath;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"写入备份清单失败：{manifestPath}", ex);
            return string.Empty;
        }
    }
}
