using System.Security.Cryptography;
using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// HashEngine 单元测试。
/// 覆盖：空文件的已知哈希（MD5 / SHA1 / SHA256）、相同与不同内容的判定、
/// 大文件流式哈希与一次性哈希的一致性、十六进制小写格式与长度、
/// 进度回调（总量、末次字节数、最终百分比）、预先取消、文件不存在、
/// AreEqual 的「先比大小再比哈希」短路行为、算法名称映射以及异步接口与同步结果一致。
/// 所有文件都创建在各自的临时目录中，不依赖真实磁盘上的固定路径。
/// </summary>
public sealed class HashEngineTests
{
    // ------------------------------------------------------------------
    // 测试数据与辅助方法
    // ------------------------------------------------------------------

    /// <summary>大文件测试用大小（300 KB，超过 128 KB 默认缓冲区，确保走多轮流式读取）。</summary>
    private const int LargeFileSizeInBytes = 300 * 1024;

    /// <summary>同步收集进度回调的实现（避免 <see cref="Progress{T}"/> 依赖同步上下文导致漏记）。</summary>
    private sealed class ProgressRecorder : IProgress<HashProgress>
    {
        /// <summary>按回调顺序记录的进度快照。</summary>
        public List<HashProgress> Reports { get; } = new();

        /// <summary>记录一次进度。</summary>
        public void Report(HashProgress value) => Reports.Add(value);
    }

    /// <summary>把手写的一次性哈希结果转成与 HashEngine 相同的十六进制小写字符串。</summary>
    private static string ToLowerCaseHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>在临时工作区根目录下创建两个内容相同的文件，返回它们的绝对路径。</summary>
    private static (string First, string Second) CreateSameContentPair(TempWorkspace ws)
    {
        var first = ws.CreateFile("pair-a.bin", "same-content-for-hash", null, ws.Root);
        var second = ws.CreateFile("pair-b.bin", "same-content-for-hash", null, ws.Root);
        return (first, second);
    }

    // ------------------------------------------------------------------
    // 已知哈希与内容比较
    // ------------------------------------------------------------------

    /// <summary>空文件返回对应算法的标准已知哈希值。</summary>
    [Theory]
    [InlineData(HashAlgorithmKind.MD5, "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData(HashAlgorithmKind.SHA1, "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData(HashAlgorithmKind.SHA256, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void ComputeHash_ForEmptyFile_ReturnsKnownHash(HashAlgorithmKind algorithm, string expected)
    {
        using var ws = new TempWorkspace();
        var empty = ws.CreateFile("empty.bin", Array.Empty<byte>());

        Assert.Equal(0, new FileInfo(empty).Length);
        Assert.Equal(expected, HashEngine.ComputeHash(empty, algorithm));
    }

    /// <summary>内容完全相同的两个文件得到相同哈希。</summary>
    [Fact]
    public void ComputeHash_WithSameContent_ReturnsSameHash()
    {
        using var ws = new TempWorkspace();
        var (first, second) = CreateSameContentPair(ws);

        Assert.Equal(
            HashEngine.ComputeHash(first, HashAlgorithmKind.SHA256),
            HashEngine.ComputeHash(second, HashAlgorithmKind.SHA256));
    }

    /// <summary>大小相同但内容不同的文件得到不同哈希（证明比较的是内容而不是长度）。</summary>
    [Fact]
    public void ComputeHash_WithDifferentContent_ReturnsDifferentHash()
    {
        using var ws = new TempWorkspace();
        var first = ws.CreateFile("diff-a.bin", "aaaa", null, ws.Root);
        var second = ws.CreateFile("diff-b.bin", "bbbb", null, ws.Root);

        Assert.Equal(new FileInfo(first).Length, new FileInfo(second).Length);
        Assert.NotEqual(
            HashEngine.ComputeHash(first, HashAlgorithmKind.SHA256),
            HashEngine.ComputeHash(second, HashAlgorithmKind.SHA256));
    }

    /// <summary>同一个文件用三种算法算出的哈希互不相同。</summary>
    [Fact]
    public void ComputeHash_WithThreeAlgorithms_ReturnsThreeDistinctHashes()
    {
        using var ws = new TempWorkspace();
        var path = ws.CreateFile("algorithms.bin", "wintoolbox-hash-engine", null, ws.Root);

        var md5 = HashEngine.ComputeHash(path, HashAlgorithmKind.MD5);
        var sha1 = HashEngine.ComputeHash(path, HashAlgorithmKind.SHA1);
        var sha256 = HashEngine.ComputeHash(path, HashAlgorithmKind.SHA256);

        Assert.NotEqual(md5, sha1);
        Assert.NotEqual(md5, sha256);
        Assert.NotEqual(sha1, sha256);
    }

    /// <summary>不指定算法时默认使用 SHA256。</summary>
    [Fact]
    public void ComputeHash_WithoutAlgorithm_UsesSha256ByDefault()
    {
        using var ws = new TempWorkspace();
        var path = ws.CreateFile("default.bin", "default-algorithm", null, ws.Root);

        Assert.Equal(
            HashEngine.ComputeHash(path, HashAlgorithmKind.SHA256),
            HashEngine.ComputeHash(path));
    }

    // ------------------------------------------------------------------
    // 大文件与格式
    // ------------------------------------------------------------------

    /// <summary>300 KB 二进制文件的流式哈希与一次性哈希、与 TempWorkspace 的 SHA256 完全一致。</summary>
    [Fact]
    public void ComputeHash_ForLargeFile_MatchesOneShotHash()
    {
        using var ws = new TempWorkspace();
        var path = ws.CreateRandomBinaryFile("large.bin", LargeFileSizeInBytes);

        Assert.True(
            new FileInfo(path).Length > HashEngine.DefaultBufferSize,
            "测试文件应大于默认缓冲区，才能验证多轮流式读取");

        using var sha256 = SHA256.Create();
        var expected = ToLowerCaseHex(sha256.ComputeHash(File.ReadAllBytes(path)));

        var streamed = HashEngine.ComputeHash(path, HashAlgorithmKind.SHA256);

        Assert.Equal(expected, streamed);
        Assert.Equal(TempWorkspace.ComputeFileSha256(path), streamed);
    }

    /// <summary>返回值是十六进制小写字符串，长度符合各算法要求。</summary>
    [Theory]
    [InlineData(HashAlgorithmKind.MD5, 32)]
    [InlineData(HashAlgorithmKind.SHA1, 40)]
    [InlineData(HashAlgorithmKind.SHA256, 64)]
    public void ComputeHash_ReturnsLowerCaseHexWithExpectedLength(HashAlgorithmKind algorithm, int expectedLength)
    {
        using var ws = new TempWorkspace();
        var path = ws.CreateRandomBinaryFile("hex.bin", 4096);

        var hash = HashEngine.ComputeHash(path, algorithm);

        Assert.Equal(expectedLength, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    // ------------------------------------------------------------------
    // 进度、取消与异常
    // ------------------------------------------------------------------

    /// <summary>进度回调至少触发一次，总量等于文件大小，末次已读字节等于总量且百分比为 100。</summary>
    [Fact]
    public void ComputeHash_WithProgressReporter_ReportsFullProgress()
    {
        using var ws = new TempWorkspace();
        var path = ws.CreateRandomBinaryFile("progress.bin", LargeFileSizeInBytes);
        var size = new FileInfo(path).Length;
        var recorder = new ProgressRecorder();

        var hash = HashEngine.ComputeHash(path, HashAlgorithmKind.SHA256, recorder);

        Assert.False(string.IsNullOrWhiteSpace(hash));
        Assert.NotEmpty(recorder.Reports);
        Assert.All(recorder.Reports, report => Assert.Equal(size, report.TotalBytes));
        Assert.All(recorder.Reports, report => Assert.Equal(Path.GetFullPath(path), report.FilePath));

        var last = recorder.Reports[^1];
        Assert.Equal(size, last.ProcessedBytes);
        Assert.Equal(100d, last.Percent, 3);
        Assert.Equal(0L, recorder.Reports[0].ProcessedBytes);
    }

    /// <summary>传入已取消的令牌时抛出 OperationCanceledException。</summary>
    [Fact]
    public void ComputeHash_WhenTokenAlreadyCanceled_ThrowsOperationCanceledException()
    {
        using var ws = new TempWorkspace();
        var path = ws.CreateFile("cancel.bin", "cancel-me-please", null, ws.Root);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => HashEngine.ComputeHash(path, HashAlgorithmKind.SHA256, null, cts.Token));
    }

    /// <summary>文件不存在时抛出 FileNotFoundException 或 DirectoryNotFoundException。</summary>
    [Fact]
    public void ComputeHash_WhenFileMissing_ThrowsFileOrDirectoryNotFound()
    {
        using var ws = new TempWorkspace();
        var missing = Path.Combine(ws.Root, "no-such-file.bin");

        Assert.False(File.Exists(missing));

        var exception = RecordException(() => HashEngine.ComputeHash(missing));

        Assert.NotNull(exception);
        Assert.True(
            exception is FileNotFoundException or DirectoryNotFoundException,
            $"应抛出 FileNotFoundException 或 DirectoryNotFoundException，实际是 {exception.GetType().Name}");
    }

    /// <summary>路径为空白时抛出参数异常。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ComputeHash_WhenPathIsBlank_ThrowsArgumentException(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => HashEngine.ComputeHash(path));
    }

    // ------------------------------------------------------------------
    // AreEqual
    // ------------------------------------------------------------------

    /// <summary>内容相同的两个文件判定为相等。</summary>
    [Fact]
    public void AreEqual_WithSameContent_ReturnsTrue()
    {
        using var ws = new TempWorkspace();
        var (first, second) = CreateSameContentPair(ws);

        Assert.True(HashEngine.AreEqual(first, second));
    }

    /// <summary>大小不同的两个文件判定为不相等，且不抛出异常（先比大小，直接短路）。</summary>
    [Fact]
    public void AreEqual_WithDifferentSize_ReturnsFalseWithoutThrowing()
    {
        using var ws = new TempWorkspace();
        var small = ws.CreateFile("small.bin", "abc", null, ws.Root);
        var large = ws.CreateFile("large.bin", "abcdefghij", null, ws.Root);

        var exception = RecordException(() => HashEngine.AreEqual(small, large));

        Assert.Null(exception);
        Assert.False(HashEngine.AreEqual(small, large));
    }

    /// <summary>大小与内容相同、仅修改时间不同时仍判定为相等（AreEqual 只看内容）。</summary>
    [Fact]
    public void AreEqual_WithSameContentButDifferentWriteTime_ReturnsTrue()
    {
        using var ws = new TempWorkspace();
        var first = ws.CreateFile("time-a.bin", "time-independent", DateTime.UtcNow.AddHours(-5), ws.Root);
        var second = ws.CreateFile("time-b.bin", "time-independent", DateTime.UtcNow.AddMinutes(-1), ws.Root);

        Assert.NotEqual(File.GetLastWriteTimeUtc(first), File.GetLastWriteTimeUtc(second));
        Assert.True(HashEngine.AreEqual(first, second));
    }

    /// <summary>其中一个文件不存在时返回 false，而不是抛异常。</summary>
    [Fact]
    public void AreEqual_WhenOneFileMissing_ReturnsFalse()
    {
        using var ws = new TempWorkspace();
        var existing = ws.CreateFile("existing.bin", "existing-content", null, ws.Root);
        var missing = Path.Combine(ws.Root, "missing.bin");

        Assert.False(HashEngine.AreEqual(existing, missing));
        Assert.False(HashEngine.AreEqual(missing, existing));
    }

    /// <summary>两个文件都不存在时返回 false。</summary>
    [Fact]
    public void AreEqual_WhenBothFilesMissing_ReturnsFalse()
    {
        using var ws = new TempWorkspace();

        Assert.False(HashEngine.AreEqual(
            Path.Combine(ws.Root, "missing-a.bin"),
            Path.Combine(ws.Root, "missing-b.bin")));
    }

    // ------------------------------------------------------------------
    // 算法名称映射与异步接口
    // ------------------------------------------------------------------

    /// <summary>GetAlgorithmName 返回用于显示的算法名称。</summary>
    [Theory]
    [InlineData(HashAlgorithmKind.MD5, "MD5")]
    [InlineData(HashAlgorithmKind.SHA1, "SHA1")]
    [InlineData(HashAlgorithmKind.SHA256, "SHA256")]
    public void GetAlgorithmName_ReturnsDisplayName(HashAlgorithmKind algorithm, string expected)
    {
        Assert.Equal(expected, HashEngine.GetAlgorithmName(algorithm));
    }

    /// <summary>ToHashAlgorithmName 正确映射到 .NET 的 HashAlgorithmName。</summary>
    [Theory]
    [InlineData(HashAlgorithmKind.MD5, "MD5")]
    [InlineData(HashAlgorithmKind.SHA1, "SHA1")]
    [InlineData(HashAlgorithmKind.SHA256, "SHA256")]
    public void ToHashAlgorithmName_MapsToDotNetName(HashAlgorithmKind algorithm, string expected)
    {
        Assert.Equal(expected, HashEngine.ToHashAlgorithmName(algorithm).Name);
    }

    /// <summary>异步接口的结果与同步接口完全一致。</summary>
    [Fact]
    public async Task ComputeHashAsync_ReturnsSameHashAsSync()
    {
        using var ws = new TempWorkspace();
        var path = ws.CreateRandomBinaryFile("async.bin", LargeFileSizeInBytes);

        var sync = HashEngine.ComputeHash(path, HashAlgorithmKind.SHA256);
        var asyncHash = await HashEngine.ComputeHashAsync(path, HashAlgorithmKind.SHA256);

        Assert.Equal(sync, asyncHash);
        Assert.Equal(TempWorkspace.ComputeFileSha256(path), asyncHash);
    }

    /// <summary>执行动作并捕获其抛出的异常；没有异常时返回 null（本地实现，避免依赖不同 xUnit 版本的助手 API 差异）。</summary>
    private static Exception? RecordException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
