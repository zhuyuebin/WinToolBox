namespace WinToolBox.Core;

/// <summary>
/// 目录复制器：流式复制 + 增量复制（按“修改时间 + 大小”跳过未修改文件）。
/// 安全约定：只读源目录、只写目标目录；默认不删除目标中的任何文件；绝不执行源中的任何程序。
/// </summary>
public sealed class FileCopier
{
    /// <summary>默认流式缓冲区大小（1 MiB），用于大文件复制。</summary>
    public const int DefaultBufferSize = 1024 * 1024;

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
    /// true（默认）：仅在“源文件更新或大小不同”时覆盖，否则跳过。
    /// false：只要目标存在就跳过（只补新增文件）。
    /// </summary>
    public bool OverwriteExistingFiles { get; set; } = true;

    /// <summary>复制后是否把目标文件的最后修改时间对齐源文件（增量复制依赖它）。</summary>
    public bool PreserveTimestamps { get; set; } = true;

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

        // 1) 预扫描：收集需要复制的文件，得到总数/总字节数，用于进度显示
        var files = CollectFiles(source, rules, _logger);
        var totalBytes = files.Sum(static f => f.Length);

        AppPaths.EnsureDirectory(target);

        var processed = 0;

        // 2) 逐个文件复制
        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
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

                var decision = Decide(file, destinationPath);
                switch (decision)
                {
                    case CopyDecision.SkipUnchanged:
                        result.SkippedFiles++;
                        break;

                    case CopyDecision.SkipExisting:
                        result.SkippedFiles++;
                        break;

                    case CopyDecision.Copy:
                        StreamCopy(file.FullPath, destinationPath, cancellationToken);
                        result.CopiedFiles++;
                        result.CopiedBytes += file.Length;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                result.FailedFiles++;
                var message = $"{file.RelativePath}：{ex.Message}";
                result.Errors.Add(message);
                _logger?.Warn($"复制失败 {file.FullPath}", ex);
            }

            processed++;
            progressCallback?.Invoke(new CopyProgress
            {
                CurrentFile = file.RelativePath,
                TotalFiles = files.Count,
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

    private CopyDecision Decide(FileEntry file, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return CopyDecision.Copy;
        }

        if (!OverwriteExistingFiles)
        {
            return CopyDecision.SkipExisting;
        }

        var sourceInfo = new FileInfo(file.FullPath);
        var targetInfo = new FileInfo(destinationPath);

        // 增量判定：大小相同且目标不比源旧 => 认为未修改，跳过
        if (sourceInfo.Length == targetInfo.Length &&
            targetInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
        {
            return CopyDecision.SkipUnchanged;
        }

        return CopyDecision.Copy;
    }

    /// <summary>流式复制：固定缓冲区循环读写，支持大文件，不使用 File.Copy 以便控制进度与时间戳。</summary>
    private void StreamCopy(string sourceFile, string destinationPath, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Max(1, BufferSize)];

        using (var sourceStream = new FileStream(
                   sourceFile,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite,
                   buffer.Length,
                   FileOptions.SequentialScan))
        using (var destinationStream = new FileStream(
                   destinationPath,
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
        }

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

    /// <summary>广度优先遍历源目录，应用排除规则，返回待复制文件列表（按相对路径排序，保证结果稳定）。</summary>
    private static List<FileEntry> CollectFiles(string sourceRoot, ExcludeRules rules, Logger? logger)
    {
        var results = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> subDirectories;
            IEnumerable<string> files;

            try
            {
                subDirectories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception ex)
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

                results.Add(new FileEntry(file, Path.GetRelativePath(sourceRoot, file), length));
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

        results.Sort(static (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>判断 candidate 是否等于 root 或位于 root 之内（用于阻止自我递归复制）。</summary>
    private static bool IsSameOrChildPath(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar;

        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private enum CopyDecision
    {
        Copy,
        SkipUnchanged,
        SkipExisting
    }

    private readonly record struct FileEntry(string FullPath, string RelativePath, long Length);
}
