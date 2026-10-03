using System.Globalization;
using WinToolBox.Core;

namespace WinToolBox.Tools.FileMaster.Models;

/// <summary>重复文件查找选项。</summary>
public sealed class DuplicateFinderOptions
{
    /// <summary>要扫描的目录（可多个）。</summary>
    public IReadOnlyList<string> Directories { get; init; } = Array.Empty<string>();

    /// <summary>是否递归子目录。</summary>
    public bool IncludeSubDirectories { get; init; } = true;

    /// <summary>小于该字节数的文件不参与比较（默认 1 字节，即不忽略任何非空文件）。</summary>
    public long MinFileSize { get; init; } = 1;

    /// <summary>哈希算法。</summary>
    public HashAlgorithmKind Algorithm { get; init; } = HashAlgorithmKind.SHA256;

    /// <summary>需要忽略的文件 / 目录名（分号或换行分隔，不区分大小写）。</summary>
    public string? ExcludeNames { get; init; }
}

/// <summary>一个重复文件组（内容完全相同的多个文件）。</summary>
public sealed class DuplicateGroup
{
    /// <summary>内容哈希。</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>单个文件大小。</summary>
    public long FileSize { get; init; }

    /// <summary>组内文件（按修改时间升序，第一个默认为「保留」的原始文件）。</summary>
    public IReadOnlyList<DuplicateFileItem> Files { get; init; } = Array.Empty<DuplicateFileItem>();

    /// <summary>建议保留的文件路径（修改时间最早的一个）。</summary>
    public string SuggestedKeepPath => Files.Count == 0 ? string.Empty : Files[0].Path;

    /// <summary>可回收空间（组内除保留文件外的总大小）。</summary>
    public long WastedBytes => FileSize * Math.Max(0, Files.Count - 1);
}

/// <summary>重复组中的一个文件。</summary>
public sealed class DuplicateFileItem
{
    /// <summary>完整路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>文件大小。</summary>
    public long Size { get; init; }

    /// <summary>修改时间。</summary>
    public DateTime LastWriteTime { get; init; }
}

/// <summary>重复文件扫描进度。</summary>
public sealed class DuplicateScanProgress
{
    /// <summary>当前阶段说明（按大小分组 / 计算哈希 / 完成）。</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>当前文件。</summary>
    public string CurrentPath { get; init; } = string.Empty;

    /// <summary>完成百分比（0-100）。</summary>
    public double Percent => Total <= 0 ? 0 : Processed * 100d / Total;
}

/// <summary>重复文件扫描结果。</summary>
public sealed class DuplicateScanResult
{
    /// <summary>重复组。</summary>
    public IReadOnlyList<DuplicateGroup> Groups { get; init; } = Array.Empty<DuplicateGroup>();

    /// <summary>参与比较的文件总数。</summary>
    public int ScannedFileCount { get; init; }

    /// <summary>计算过哈希的文件数。</summary>
    public int HashedFileCount { get; init; }

    /// <summary>
    /// 被自动合并掉的扫描目录（保序）：与其它目录等价的写法（大小写 / 尾分隔符 / 相对路径不同），
    /// 或已被另一个递归扫描的父目录完整包含，因此不再单独扫描。
    /// 展开这些目录会让同一批文件被扫两次、被当成「互为副本」而虚增重复组。
    /// </summary>
    public IReadOnlyList<string> MergedDirectories { get; init; } = Array.Empty<string>();

    /// <summary>错误信息（正常为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>重复文件总个数（不含每组保留的那个）。</summary>
    public int DuplicateFileCount => Groups.Sum(static group => Math.Max(0, group.Files.Count - 1));

    /// <summary>可回收空间（字节）。</summary>
    public long WastedBytes => Groups.Sum(static group => group.WastedBytes);

    /// <summary>可回收空间的友好显示。</summary>
    public string WastedText => FormatSize(WastedBytes);

    /// <summary>一句话摘要。</summary>
    public string Summary => Error is not null
        ? "查找失败：" + Error
        : $"扫描 {ScannedFileCount} 个文件，发现 {Groups.Count} 组重复（可删除 {DuplicateFileCount} 个，可回收 {WastedText}）。" +
          (MergedDirectories.Count == 0
              ? string.Empty
              : $"已自动合并 {MergedDirectories.Count} 个被包含的扫描目录，未重复统计。");

    /// <summary>把字节数格式化为 B / KB / MB / GB。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return (bytes / 1024d / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / 1024d / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }
}

/// <summary>重复文件删除进度。</summary>
public sealed class DuplicateDeleteProgress
{
    /// <summary>完成百分比（0-100）。</summary>
    public double Percent { get; init; }

    /// <summary>已处理数量。</summary>
    public int Processed { get; init; }

    /// <summary>总数。</summary>
    public int Total { get; init; }

    /// <summary>当前文件。</summary>
    public string CurrentPath { get; init; } = string.Empty;
}

/// <summary>重复文件删除结果。</summary>
public sealed class DuplicateDeleteResult
{
    /// <summary>成功删除的文件。</summary>
    public IReadOnlyList<string> DeletedPaths { get; init; } = Array.Empty<string>();

    /// <summary>删除失败的文件与原因。</summary>
    public IReadOnlyList<(string Path, string Error)> FailedItems { get; init; } =
        Array.Empty<(string, string)>();

    /// <summary>回收的空间（字节）。</summary>
    public long FreedBytes { get; init; }

    /// <summary>是否走回收站。</summary>
    public bool UsedRecycleBin { get; init; } = true;

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedItems.Count == 0;

    /// <summary>一句话摘要。</summary>
    public string Summary =>
        $"删除完成：成功 {DeletedPaths.Count} 个（释放 {DuplicateScanResult.FormatSize(FreedBytes)}）" +
        $"，失败 {FailedItems.Count} 个" +
        (UsedRecycleBin ? "，已放入回收站。" : "，已永久删除。");
}
