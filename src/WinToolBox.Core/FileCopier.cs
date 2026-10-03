using System.Security.Cryptography;

namespace WinToolBox.Core;

/// <summary>
/// 目录复制器：流式复制 + 增量复制。
/// <para>增量判定不再只看“大小 + 修改时间”：大小相同后还会做内容校验，
/// 避免“内容变了但大小与时间戳没变（甚至时间戳更旧）”的文件被永久静默跳过。</para>
/// 安全约定：只读源目录、只写目标目录；默认不删除目标中的任何文件；绝不执行源中的任何程序。
/// </summary>
public sealed class FileCopier
{
    /// <summary>默认流式缓冲区大小（1 MiB），用于大文件复制。</summary>
    public const int DefaultBufferSize = 1024 * 1024;

    /// <summary>大文件阈值（16 MiB）：超过该大小的文件改用“大小 + mtime + 首尾抽样哈希”折中判定。</summary>
    public const long LargeFileThresholdBytes = 16L * 1024 * 1024;

    /// <summary>抽样哈希取样窗口大小（首 / 尾各 64 KiB）。</summary>
    public const int SampleWindowBytes = 64 * 1024;

    private readonly Logger? _logger;

    /// <summary>创建复制器。</summary>
    public FileCopier(Logger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>流式复制缓冲区大小（字节）。</summary>
    public int BufferSize { get; set; } = DefaultBufferSize;

    /// <summary>
    /// 目标已存在同名文件时是否允许覆盖。
    /// true（默认）：仅在“大小不同、时间戳更新或内容不同”时覆盖，否则跳过。
    /// false：只要目标存在就跳过（只补新增文件）。
    /// </summary>
    public bool OverwriteExistingFiles { get; set; } = true;

    /// <summary>复制后是否把目标文件的最后修改时间对齐源文件（增量复制依赖它）。</summary>
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>
    /// 连续失败多少个文件后提前中止本轮复制（默认 20；&lt;= 0 表示不中止）。
    /// <para>连续失败通常意味着「盘拔了 / 目标写满 / 权限全无」，继续跑只会把时间浪费在必然失败的 I/O 上。</para>
    /// <para>中止时 <see cref="CopyResult.AbortedByConsecutiveFailures"/> 为 true，与用户主动取消区分开。</para>
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 20;

    /// <summary>
    /// 严格内容校验（默认 true，对应 <c>BackupConfig.StrictContentVerification</c>）。
    /// <para>true：大小相同的小文件逐个做 SHA256 全量校验，判为“内容未变”的文件计入
    /// <see cref="CopyResult.VerifiedFiles"/>（本工程用“已校验且相同”这一含义）。</para>
    /// <para>false：只比大小 + 时间戳，跳过的文件计入 <see cref="CopyResult.UnchangedAssumed"/>，
    /// 摘要会明确标注“未做内容校验”。</para>
    /// </summary>
    public bool StrictContentVerification { get; set; } = true;

    /// <summary>
    /// 覆盖目标文件【之前】的回调，参数为（源文件完整路径、目标文件完整路径、源文件长度）。
    /// <para>用于在旧版本被覆盖前先归档（UsbBackup 的 <c>history/{yyyy-MM-dd}/</c> 语义）。</para>
    /// <para>回调抛出的异常会让本次文件复制记为失败，从而避免“旧版本已丢、新版本没写入”的静默数据丢失。</para>
    /// </summary>
    public Action<string, string, long>? OnBeforeOverwrite { get; set; }

    /// <summary>
    /// 每个文件复制【判定完成】后的回调（覆盖前触发）：参数为（源完整路径、目标完整路径、源相对路径、是否已判定需要复制）。
    /// <para><c>willCopy == false</c> 表示内容未变、本次跳过，调用方不应归档旧版本。</para>
    /// <para>抛出异常会让本次文件复制记为失败。</para>
    /// </summary>
    public Action<string, string, string, bool>? OnFileDecision { get; set; }

    /// <summary>
    /// 单个文件【实际复制失败】时触发（参数为该文件相对源目录的路径）。
    /// <para>调用方据此区分「判定为需要复制」与「真的复制成功了」——
    /// 例如「旧版本已被归档、但新版本没写进去」需要单独归类，不能被算成一次成功的更新。</para>
    /// </summary>
    public Action<string>? OnCopyFailed { get; set; }

    /// <summary>
    /// 预扫描进度回调：惰性枚举每发现一个文件触发一次（累计发现数量）。
    /// <para>用于解决“复制开始前长时间零进度”的问题：调用方可以据此上报“已发现 N 个文件”。</para>
    /// <para>回调抛出的异常会被忽略，绝不影响复制本身。</para>
    /// </summary>
    public Action<int>? OnScanProgress { get; set; }

    /// <summary>
    /// 增量复制目录。
    /// </summary>
    /// <param name="sourceDirectory">源目录（U 盘根目录）。</param>
    /// <param name="targetDirectory">目标目录（本次备份目录，不存在时自动创建）。</param>
    /// <param name="progressCallback">进度回调，可为 null。</param>
    /// <param name="excludeRules">排除规则，默认使用 <see cref="ExcludeRules.CreateDefault"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public CopyResult CopyDirectory(
        string sourceDirectory,
        string targetDirectory,
        Action<CopyProgress>? progressCallback = null,
        ExcludeRules? excludeRules = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("源目录不能为空", nameof(sourceDirectory));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("目标目录不能为空", nameof(targetDirectory));
        }

        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(targetDirectory);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"源目录不存在：{source}");
        }

        if (IsSameOrChildPath(target, source))
        {
            throw new ArgumentException($"目标目录不能位于源目录内部：{target}", nameof(targetDirectory));
        }

        if (IsSameOrChildPath(source, target))
        {
            throw new ArgumentException($"源目录不能位于目标目录内部：{source}", nameof(sourceDirectory));
        }

        var rules = excludeRules ?? ExcludeRules.CreateDefault();
        var result = new CopyResult { SourceDirectory = source, TargetDirectory = target };

        // 1) 轻量预扫描：只统计「文件数 + 总字节数」，不保存任何路径，用于进度显示。
        //    真正待复制清单不在这里物化 —— 见下面的惰性枚举。
        var (estimatedTotalFiles, totalBytes) = CountFiles(source, rules, _logger);

        AppPaths.EnsureDirectory(target);

        var processed = 0;
        var discovered = 0;

        // 当前「连续失败」计数：失败累加，任何一次成功（含跳过）清零。
        var consecutiveFailures = 0;

        // 2) 惰性枚举 + 逐个文件复制：任何时刻内存里都只有一个待处理条目。
        foreach (var file in CollectFiles(source, rules, _logger))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            discovered++;

            // 发现即上报，避免“复制开始前长时间零进度”
            if (OnScanProgress is not null)
            {
                try
                {
                    OnScanProgress(discovered);
                }
                catch
                {
                    // 进度上报失败绝不能影响复制
                }
            }

            var destinationPath = Path.Combine(target, file.RelativePath);

            try
            {
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                    result.CreatedDirectories++;
                }

                var decision = Decide(file, destinationPath, cancellationToken);

                // 无论复制还是跳过都要通知调用方，否则调用方无法区分
                // “旧版本该归档”和“内容其实没变、不该归档”。
                OnFileDecision?.Invoke(file.FullPath, destinationPath, file.RelativePath, decision.Action == CopyDecision.Copy);

                switch (decision.Action)
                {
                    case CopyDecision.SkipUnchanged:
                        result.SkippedFiles++;
                        if (decision.ContentVerified)
                        {
                            result.VerifiedFiles++;
                        }
                        else
                        {
                            result.UnchangedAssumed++;
                        }

                        break;

                    case CopyDecision.SkipExisting:
                        result.SkippedFiles++;
                        break;

                    case CopyDecision.Copy:
                        // 覆盖前触发归档钩子（保留旧版 OnBeforeOverwrite 语义，便于单独使用）
                        if (decision.TargetExists && OnBeforeOverwrite is not null)
                        {
                            OnBeforeOverwrite(file.FullPath, destinationPath, file.Length);
                        }

                        try
                        {
                            StreamCopy(file.FullPath, destinationPath, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // 让调用方知道「这个文件的新版本没写成功」（旧版本可能已经归档走了）
                            OnCopyFailed?.Invoke(file.RelativePath);
                            throw;
                        }

                        result.CopiedFiles++;
                        result.CopiedBytes += file.Length;
                        break;
                }

                // 走到这里说明这一步成功（复制或跳过）：连续失败计数清零。
                // 注意必须放在 try 内部 —— 放在 catch 之后的话，每个文件都会执行一次，
                // 计数器永远回到 0，早停就永远不会触发。
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                result.FailedFiles++;
                consecutiveFailures++;
                var message = $"{file.RelativePath}：{ex.Message}";
                result.Errors.Add(message);
                _logger?.Warn($"复制失败 {file.FullPath}", ex);

                // 连续失败早停：必须放在 catch 里 —— 真正的 I/O 失败发生在 StreamCopy 内部，只有这里能看到。
                if (MaxConsecutiveFailures > 0 && consecutiveFailures >= MaxConsecutiveFailures)
                {
                    result.AbortedByConsecutiveFailures = true;
                    _logger?.Error(
                        $"连续失败达到 {MaxConsecutiveFailures} 个，已提前中止本轮复制" +
                        $"（已处理 {processed} 个文件）。");
                    break;
                }
            }

            processed++;
            progressCallback?.Invoke(new CopyProgress
            {
                CurrentFile = file.RelativePath,
                TotalFiles = Math.Max(estimatedTotalFiles, discovered),
                ProcessedFiles = processed,
                CopiedFiles = result.CopiedFiles,
                SkippedFiles = result.SkippedFiles,
                FailedFiles = result.FailedFiles,
                CopiedBytes = result.CopiedBytes,
                TotalBytes = totalBytes
            });
        }

        _logger?.Info(
            $"复制完成：{source} -> {target}；新增/更新 {result.CopiedFiles}，跳过 {result.SkippedFiles}，" +
            $"失败 {result.FailedFiles}，目录 {result.CreatedDirectories}，字节 {result.CopiedBytes}");

        return result;
    }

    /// <summary>只复制一个文件（保留目录结构由调用方负责），用于零散补拷。</summary>
    public void CopyFile(string sourceFile, string targetFile, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFile);

        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException("源文件不存在", sourceFile);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetFile));
        AppPaths.EnsureDirectory(directory);
        StreamCopy(sourceFile, targetFile, cancellationToken);
    }

    /// <summary>
    /// 增量判定：先比大小，大小不同一律重拷；大小相同时再比修改时间与内容。
    /// <para>时间戳比较使用严格的 <c>&gt;</c>：源与目标同刻的文件也会重拷一次，
    /// 因为同刻无法区分“已经同步”和“内容被改写后又把时间戳改回去了”。</para>
    /// </summary>
    private CopyVerdict Decide(FileEntry file, string destinationPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationPath))
        {
            return CopyVerdict.Copy(targetExists: false);
        }

        if (!OverwriteExistingFiles)
        {
            return CopyVerdict.SkipExisting();
        }

        var sourceInfo = new FileInfo(file.FullPath);
        var targetInfo = new FileInfo(destinationPath);

        // 1) 大小不同 => 一定需要复制
        if (sourceInfo.Length != targetInfo.Length)
        {
            return CopyVerdict.Copy(targetExists: true);
        }

        // 2) 大小相同：严格内容校验
        if (StrictContentVerification)
        {
            // 2a) 小文件：SHA256 全量比对
            if (sourceInfo.Length <= LargeFileThresholdBytes)
            {
                return HashEngine.AreEqual(file.FullPath, destinationPath, HashAlgorithmKind.SHA256, cancellationToken)
                    ? CopyVerdict.SkipVerified()
                    : CopyVerdict.Copy(targetExists: true);
            }

            // 2b) 大文件：大小 + mtime + 首尾各 64 KiB 抽样哈希
            //     mtime 严格更新 => 直接判为已更新，不必读盘；
            //     mtime 相同或更旧 => 抽样比对内容，抽样不同即复制；
            //     抽样相同 => 折中接受（无法证明内容相同，如实计入 UnchangedAssumed）。
            if (sourceInfo.LastWriteTimeUtc > targetInfo.LastWriteTimeUtc)
            {
                return CopyVerdict.Copy(targetExists: true);
            }

            return AreSampledContentEqual(file.FullPath, destinationPath, sourceInfo.Length, cancellationToken)
                ? CopyVerdict.SkipAssumed()
                : CopyVerdict.Copy(targetExists: true);
        }

        // 3) 非严格模式：只比大小 + 时间戳，跳过的文件如实标注“未做内容校验”
        return targetInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc
            ? CopyVerdict.SkipAssumed()
            : CopyVerdict.Copy(targetExists: true);
    }

    /// <summary>
    /// 大文件内容折中比对：文件很小时退化为全量，否则比首尾各 <see cref="SampleWindowBytes"/> 的 SHA256。
    /// </summary>
    private static bool AreSampledContentEqual(string pathA, string pathB, long length, CancellationToken cancellationToken)
    {
        if (length <= (long)SampleWindowBytes * 2)
        {
            return HashEngine.AreEqual(pathA, pathB, HashAlgorithmKind.SHA256, cancellationToken);
        }

        return string.Equals(
                   ComputeHeadTailHash(pathA, length, cancellationToken),
                   ComputeHeadTailHash(pathB, length, cancellationToken),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>计算“首 64 KiB + 尾 64 KiB”的 SHA256（十六进制小写）。</summary>
    private static string ComputeHeadTailHash(string path, long length, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            SampleWindowBytes,
            FileOptions.RandomAccess);

        var buffer = new byte[SampleWindowBytes];
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var headRead = ReadFully(stream, buffer, SampleWindowBytes);
        incremental.AppendData(buffer, 0, headRead);

        stream.Seek(-SampleWindowBytes, SeekOrigin.End);
        var tailRead = ReadFully(stream, buffer, SampleWindowBytes);
        incremental.AppendData(buffer, 0, tailRead);

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>尽力读满 <paramref name="count"/> 字节（文件末尾可能不足）。</summary>
    private static int ReadFully(FileStream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// 流式复制：固定缓冲区循环读写，支持大文件，不使用 File.Copy 以便控制进度与时间戳。
    /// <para>先写同目录下的 <c>{目标名}.tmp</c>，复制完成后再原子替换目标文件，
    /// 这样中途失败（含进程被强制结束）不会留下截断的目标文件。</para>
    /// </summary>
    private void StreamCopy(string sourceFile, string destinationPath, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Max(1, BufferSize)];
        var tempPath = destinationPath + ".tmp";

        try
        {
            using (var sourceStream = new FileStream(
                       sourceFile,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite,
                       buffer.Length,
                       FileOptions.SequentialScan))
            using (var destinationStream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       buffer.Length,
                       FileOptions.SequentialScan))
            {
                int read;
                while ((read = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destinationStream.Write(buffer, 0, read);
                }

                destinationStream.Flush(flushToDisk: true);
            }

            // 复制已完成：原子替换目标文件（.tmp 与目标同目录，因此是同卷 move）
            File.Move(tempPath, destinationPath, overwrite: true);

            if (PreserveTimestamps)
            {
                try
                {
                    File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourceFile));
                }
                catch (Exception ex)
                {
                    // 时间戳对齐失败不影响文件内容，下次会重拷但不会损坏数据
                    _logger?.Warn($"设置目标文件时间失败：{destinationPath}", ex);
                }
            }
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>清理未完成的临时文件（失败不影响原始异常）。</summary>
    private void TryDeleteTempFile(string tempPath)
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
            _logger?.Warn($"清理临时文件失败：{tempPath}", ex);
        }
    }

    /// <summary>
    /// 惰性广度优先遍历源目录，应用排除规则，逐个产出待复制文件。
    /// <para>与旧实现的差别：不再把整盘清单物化成一个 List，任何时刻内存里只有一个待处理目录 / 文件，
    /// 几十万文件的 U 盘也不会因为“先建全量清单”而吃掉大量内存、或让进度长时间停在 0。</para>
    /// <para>目录联接 / 符号链接（<see cref="FileAttributes.ReparsePoint"/>）会被跳过，
    /// 避免把链接目标的内容也复制进来、或陷入循环。</para>
    /// </summary>
    private static IEnumerable<FileEntry> CollectFiles(string sourceRoot, ExcludeRules rules, Logger? logger)
    {
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            // 目录联接 / 符号链接：跳过，绝不递归进去
            FileAttributes currentAttributes;
            try
            {
                currentAttributes = new DirectoryInfo(current).Attributes;
            }
            catch (Exception ex)
            {
                logger?.Warn($"读取目录属性失败，已跳过：{current}", ex);
                continue;
            }

            if ((currentAttributes & FileAttributes.ReparsePoint) != 0)
            {
                logger?.Warn($"跳过目录联接/符号链接，不复制其内容：{current}");
                continue;
            }

            // P1-3：必须立即执行枚举（GetXxx 而非 EnumerateXxx），否则异常会推迟到
            // foreach 才抛出——那时已经不在 try 里，一个无权限目录会让整个备份 0 文件。
            string[] subDirectories;
            string[] files;
            try
            {
                subDirectories = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or PathTooLongException)
            {
                logger?.Warn($"读取目录失败，已跳过：{current}", ex);
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (rules.IsExcludedFile(name))
                {
                    continue;
                }

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception ex)
                {
                    logger?.Warn($"读取文件信息失败，已跳过：{file}", ex);
                    continue;
                }

                yield return new FileEntry(file, Path.GetRelativePath(sourceRoot, file), length);
            }

            foreach (var directory in subDirectories)
            {
                var name = Path.GetFileName(directory);
                if (rules.IsExcludedDirectory(name))
                {
                    logger?.Info($"命中排除规则，跳过目录：{directory}");
                    continue;
                }

                pending.Push(directory);
            }
        }
    }

    /// <summary>
    /// 轻量预扫描：统计「会参与复制」的文件数量与总字节数，不保存任何路径。
    /// <para>复用同一套枚举与排除规则，保证与实际复制口径一致；内存占用与文件数无关。</para>
    /// </summary>
    private static (int FileCount, long TotalBytes) CountFiles(string sourceRoot, ExcludeRules rules, Logger? logger)
    {
        var count = 0;
        var bytes = 0L;

        foreach (var entry in CollectFiles(sourceRoot, rules, logger))
        {
            count++;
            bytes += entry.Length;
        }

        return (count, bytes);
    }

    /// <summary>
    /// 判断 candidate 是否等于 root、或位于 root 之内（用于阻止自我递归复制）。
    /// <para>实现要点：先把两侧都补上一个目录分隔符再比较，这样
    /// <c>D:\foo</c> 与 <c>D:\foo\</c> 等价，而 <c>D:\foobar</c> 不会被误判成 <c>D:\foo</c> 的子路径。</para>
    /// </summary>
    private static bool IsSameOrChildPath(string candidate, string root)
        => NormalizeForComparison(candidate)
            .StartsWith(NormalizeForComparison(root), StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉尾部分隔符、统一补一个目录分隔符（内部辅助）。</summary>
    private static string NormalizeForComparison(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
           + Path.DirectorySeparatorChar;

    private enum CopyDecision
    {
        Copy,
        SkipUnchanged,
        SkipExisting
    }

    /// <summary>一次增量判定的结论。</summary>
    private readonly record struct CopyVerdict(CopyDecision Action, bool TargetExists, bool ContentVerified)
    {
        /// <summary>需要复制（<paramref name="targetExists"/> 表示会覆盖已有文件，覆盖前需归档旧版本）。</summary>
        public static CopyVerdict Copy(bool targetExists) => new(CopyDecision.Copy, targetExists, false);

        /// <summary>跳过：已经过 SHA256 全量校验，确认内容一致。</summary>
        public static CopyVerdict SkipVerified() => new(CopyDecision.SkipUnchanged, false, true);

        /// <summary>跳过：仅凭大小 / 时间戳 / 抽样哈希推断未变，未做全量内容校验。</summary>
        public static CopyVerdict SkipAssumed() => new(CopyDecision.SkipUnchanged, false, false);

        /// <summary>跳过：目标已存在且不允许覆盖。</summary>
        public static CopyVerdict SkipExisting() => new(CopyDecision.SkipExisting, false, false);
    }

    private readonly record struct FileEntry(string FullPath, string RelativePath, long Length);
}
