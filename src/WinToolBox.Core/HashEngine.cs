using System.Security.Cryptography;

namespace WinToolBox.Core;

/// <summary>支持的哈希算法。</summary>
public enum HashAlgorithmKind
{
    /// <summary>MD5（快，仅用于去重等非安全场景）。</summary>
    MD5,

    /// <summary>SHA1（兼容旧系统）。</summary>
    SHA1,

    /// <summary>SHA256（默认，推荐）。</summary>
    SHA256
}

/// <summary>哈希计算进度。</summary>
public sealed class HashProgress
{
    /// <summary>当前文件完整路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>已读取字节数。</summary>
    public long ProcessedBytes { get; init; }

    /// <summary>文件总字节数。</summary>
    public long TotalBytes { get; init; }

    /// <summary>完成百分比（0-100）。</summary>
    public double Percent => TotalBytes <= 0 ? 100 : ProcessedBytes * 100d / TotalBytes;
}

/// <summary>
/// 哈希引擎：流式读取大文件（默认 128 KB 缓冲，内存占用恒定），支持 MD5 / SHA1 / SHA256，
/// 带进度回调与取消。
/// </summary>
/// <remarks>
/// <para>返回值为十六进制小写字符串（例如 <c>e3b0c44298fc1c14...</c>）。</para>
/// <para>读取时使用 <see cref="FileShare.ReadWrite"/>，不会因为其它进程只读打开而失败。</para>
/// </remarks>
public static class HashEngine
{
    /// <summary>默认缓冲区大小（128 KB）。</summary>
    public const int DefaultBufferSize = 1024 * 128;

    /// <summary>算法对应的名称（用于显示与 <see cref="HashAlgorithmName"/>）。</summary>
    public static string GetAlgorithmName(HashAlgorithmKind algorithm) => algorithm switch
    {
        HashAlgorithmKind.MD5 => "MD5",
        HashAlgorithmKind.SHA1 => "SHA1",
        _ => "SHA256"
    };

    /// <summary>转换为 .NET 的算法名称。</summary>
    public static HashAlgorithmName ToHashAlgorithmName(HashAlgorithmKind algorithm) => algorithm switch
    {
        HashAlgorithmKind.MD5 => HashAlgorithmName.MD5,
        HashAlgorithmKind.SHA1 => HashAlgorithmName.SHA1,
        _ => HashAlgorithmName.SHA256
    };

    /// <summary>
    /// 同步计算文件哈希（流式读取，不会把整个文件读进内存）。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="algorithm">哈希算法。</param>
    /// <param name="progress">进度回调（可空；按缓冲区粒度上报）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>十六进制小写哈希字符串。</returns>
    public static string ComputeHash(
        string filePath,
        HashAlgorithmKind algorithm = HashAlgorithmKind.SHA256,
        IProgress<HashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var totalBytes = new FileInfo(fullPath).Length;

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            DefaultBufferSize,
            FileOptions.SequentialScan);

        using var incremental = IncrementalHash.CreateHash(ToHashAlgorithmName(algorithm));

        var buffer = new byte[DefaultBufferSize];
        var processed = 0L;

        progress?.Report(new HashProgress { FilePath = fullPath, ProcessedBytes = 0, TotalBytes = totalBytes });

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            incremental.AppendData(buffer, 0, read);
            processed += read;

            progress?.Report(new HashProgress
            {
                FilePath = fullPath,
                ProcessedBytes = processed,
                TotalBytes = totalBytes
            });
        }

        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>异步计算文件哈希（真正的异步流式读取，适合在 UI 后台任务中使用）。</summary>
    public static async Task<string> ComputeHashAsync(
        string filePath,
        HashAlgorithmKind algorithm = HashAlgorithmKind.SHA256,
        IProgress<HashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var totalBytes = new FileInfo(fullPath).Length;

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var incremental = IncrementalHash.CreateHash(ToHashAlgorithmName(algorithm));

        var buffer = new byte[DefaultBufferSize];
        var processed = 0L;

        progress?.Report(new HashProgress { FilePath = fullPath, ProcessedBytes = 0, TotalBytes = totalBytes });

        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            incremental.AppendData(buffer, 0, read);
            processed += read;

            progress?.Report(new HashProgress
            {
                FilePath = fullPath,
                ProcessedBytes = processed,
                TotalBytes = totalBytes
            });
        }

        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// 比较两个文件内容是否相同：先比大小，再比哈希（大小不同直接返回 false，避免无谓读盘）。
    /// </summary>
    public static bool AreEqual(
        string pathA,
        string pathB,
        HashAlgorithmKind algorithm = HashAlgorithmKind.SHA256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathA);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathB);

        var infoA = new FileInfo(pathA);
        var infoB = new FileInfo(pathB);

        if (!infoA.Exists || !infoB.Exists)
        {
            return false;
        }

        if (infoA.Length != infoB.Length)
        {
            return false;
        }

        var hashA = ComputeHash(pathA, algorithm, null, cancellationToken);
        var hashB = ComputeHash(pathB, algorithm, null, cancellationToken);
        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
    }
}
