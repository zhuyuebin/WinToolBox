using System.Text;

namespace WinToolBox.Core.Tests;

/// <summary>
/// 测试用临时工作区：每次实例化都创建一个独立目录，保证测试之间互不干扰、可并行。
/// 目录结构：{系统临时目录}\WinToolBoxTests\{GUID}\
/// <list type="bullet">
/// <item><see cref="Root"/>：根目录（可自由放置其他子目录）。</item>
/// <item><see cref="SourceDir"/>：模拟 U 盘的“源目录”。</item>
/// <item><see cref="TargetDir"/>：模拟备份的“目标目录”。</item>
/// </list>
/// 典型用法：<c>using var ws = new TempWorkspace();</c>
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>创建独立临时目录（源目录、目标目录此时尚未真正建立，由测试/复制逻辑按需创建）。</summary>
    public TempWorkspace()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "WinToolBoxTests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);

        SourceDir = Path.Combine(Root, "source");
        TargetDir = Path.Combine(Root, "target");
    }

    /// <summary>本次测试独占的根目录。</summary>
    public string Root { get; }

    /// <summary>源目录（模拟 U 盘）。</summary>
    public string SourceDir { get; }

    /// <summary>目标目录（模拟备份目录）。</summary>
    public string TargetDir { get; }

    /// <summary>在源目录下创建文本文件，返回文件完整路径。</summary>
    public string CreateFile(string relativePath, string content, DateTime? lastWriteTimeUtc = null)
        => CreateFile(relativePath, content, lastWriteTimeUtc, SourceDir);

    /// <summary>在指定根目录下创建文本文件，返回文件完整路径。</summary>
    public string CreateFile(string relativePath, string content, DateTime? lastWriteTimeUtc, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(content);
        return CreateFile(
            relativePath,
            Encoding.UTF8.GetBytes(content),
            lastWriteTimeUtc,
            baseDirectory);
    }

    /// <summary>在源目录下创建二进制文件，返回文件完整路径。</summary>
    public string CreateFile(string relativePath, byte[] content, DateTime? lastWriteTimeUtc = null)
        => CreateFile(relativePath, content, lastWriteTimeUtc, SourceDir);

    /// <summary>在指定根目录下创建二进制文件，返回文件完整路径。</summary>
    public string CreateFile(string relativePath, byte[] content, DateTime? lastWriteTimeUtc, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = ResolvePath(baseDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(fullPath, content);

        // 显式设置时间戳，让增量复制的判定完全可控
        File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc ?? DateTime.UtcNow.AddMinutes(-10));
        return fullPath;
    }

    /// <summary>在源目录下生成 <paramref name="sizeInBytes"/> 字节的确定性伪随机内容并写成文件（用于大文件流式复制校验）。</summary>
    public string CreateRandomBinaryFile(string relativePath, int sizeInBytes, int seed = 20260101)
    {
        if (sizeInBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
        }

        var data = new byte[sizeInBytes];
        new Random(seed).NextBytes(data);

        // 大文件时间戳统一放到过去，避免“复制后目标时间 &gt;= 源时间”造成判定歧义
        return CreateFile(relativePath, data, DateTime.UtcNow.AddHours(-1));
    }

    /// <summary>在源目录下创建目录（文件系统里的目录，用于目录结构/排除目录场景）。</summary>
    public string CreateDirectory(string relativePath) => CreateDirectory(relativePath, SourceDir);

    /// <summary>在指定根目录下创建目录。</summary>
    public string CreateDirectory(string relativePath, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var fullPath = ResolvePath(baseDirectory, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    /// <summary>读取文件全部字节（用于内容一致性断言）。</summary>
    public static byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <summary>计算文件的 SHA256（十六进制小写），用于大文件内容一致性校验（逐块读取，不全量载入内存）。</summary>
    public static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>读取文本文件（用于日志/配置断言）。</summary>
    public static string ReadAllText(string path) => File.ReadAllText(path);

    /// <summary>尽力删除整个临时目录：失败不抛异常，先清除只读属性再删。</summary>
    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            ClearReadOnlyAttributes(Root);

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // 测试清理失败不应让测试失败（文件可能仍被占用）
            }
        }
        catch
        {
            // 整体兜底：Dispose 永不抛异常
        }
    }

    private string ResolvePath(string baseDirectory, string relativePath)
        => Path.IsPathFullyQualified(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(baseDirectory, relativePath));

    private static void ClearReadOnlyAttributes(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // 单个文件处理失败不影响其它文件
                }
            }
        }
        catch
        {
            // 遍历失败时仍尝试直接删除
        }
    }
}
