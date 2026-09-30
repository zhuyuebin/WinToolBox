namespace WinToolBox.Core;

/// <summary>
/// U 盘（可移动存储设备）信息。
/// 唯一标识不使用盘符（盘符会变），而是“卷序列号 + 卷标 + 容量”组合。
/// </summary>
public sealed class UsbDeviceInfo
{
    /// <summary>根路径，例如 E:\ 。</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>盘符，例如 E: 。</summary>
    public string DriveLetter => RootPath.TrimEnd('\\', '/');

    /// <summary>卷标（可能为空）。</summary>
    public string VolumeLabel { get; init; } = string.Empty;

    /// <summary>文件系统，例如 FAT32 / exFAT / NTFS。</summary>
    public string FileSystem { get; init; } = string.Empty;

    /// <summary>总容量（字节）。</summary>
    public long TotalSize { get; init; }

    /// <summary>剩余空间（字节）。</summary>
    public long FreeSpace { get; init; }

    /// <summary>卷序列号（8 位十六进制大写字符串）。</summary>
    public string VolumeSerialNumber { get; init; } = string.Empty;

    /// <summary>唯一标识：卷标_序列号_容量。</summary>
    public string UniqueId => BackupRules.BuildUniqueId(VolumeLabel, VolumeSerialNumber, TotalSize);

    /// <summary>用于界面/日志显示的友好名称。</summary>
    public string DisplayName =>
        $"{(string.IsNullOrWhiteSpace(VolumeLabel) ? "未命名U盘" : VolumeLabel)} ({DriveLetter}) " +
        $"{CopyResult.FormatSize(TotalSize)}，可用 {CopyResult.FormatSize(FreeSpace)}";

    /// <inheritdoc />
    public override string ToString() => $"{DisplayName} 序列号={VolumeSerialNumber}";
}
