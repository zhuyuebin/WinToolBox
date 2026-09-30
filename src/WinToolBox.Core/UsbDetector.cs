using System.Runtime.InteropServices;
using System.Text;

namespace WinToolBox.Core;

/// <summary>
/// 获取当前插入的 USB 存储设备信息：卷标、总容量、剩余空间、卷序列号。
/// 唯一标识使用“卷序列号 + 卷标 + 容量”，不使用盘符。
/// </summary>
public sealed class UsbDetector
{
    private readonly Logger? _logger;

    /// <summary>创建检测器。</summary>
    public UsbDetector(Logger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>返回当前所有已就绪的可移动存储设备（U 盘）。</summary>
    public IReadOnlyList<UsbDeviceInfo> GetRemovableDrives()
    {
        var devices = new List<UsbDeviceInfo>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            _logger?.Warn("枚举驱动器失败", ex);
            return devices;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady)
                {
                    continue;
                }

                var device = CreateDeviceInfo(drive);
                if (device is not null)
                {
                    devices.Add(device);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"读取驱动器信息失败：{SafeRoot(drive)}", ex);
            }
        }

        return devices
            .OrderBy(static d => d.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按唯一标识查找设备，未插入时返回 null。</summary>
    public UsbDeviceInfo? FindByUniqueId(string? uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            return null;
        }

        return GetRemovableDrives()
            .FirstOrDefault(d => string.Equals(d.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按根路径（盘符）查找设备，未插入时返回 null。</summary>
    public UsbDeviceInfo? FindByRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var normalized = rootPath!.TrimEnd('\\', '/');
        return GetRemovableDrives()
            .FirstOrDefault(d => string.Equals(d.RootPath.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读取卷信息（卷标、文件系统、序列号、容量）。</summary>
    public static bool TryGetVolumeInfo(
        string rootPath,
        out string volumeLabel,
        out string fileSystem,
        out string serialNumber,
        out long totalSize,
        out long freeSpace)
    {
        volumeLabel = string.Empty;
        fileSystem = string.Empty;
        serialNumber = string.Empty;
        totalSize = 0;
        freeSpace = 0;

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        var root = rootPath.TrimEnd('\\', '/') + "\\";
        var labelBuffer = new StringBuilder(261);
        var fileSystemBuffer = new StringBuilder(261);

        var ok = GetVolumeInformationW(
            root,
            labelBuffer,
            labelBuffer.Capacity,
            out var serial,
            out _,
            out _,
            fileSystemBuffer,
            fileSystemBuffer.Capacity);

        if (!ok)
        {
            return false;
        }

        volumeLabel = labelBuffer.ToString().Trim();
        fileSystem = fileSystemBuffer.ToString().Trim();
        serialNumber = serial.ToString("X8");

        if (GetDiskFreeSpaceExW(root, out _, out var total, out var free))
        {
            totalSize = (long)total;
            freeSpace = (long)free;
        }

        return true;
    }

    private UsbDeviceInfo? CreateDeviceInfo(DriveInfo drive)
    {
        var root = drive.Name;

        long totalSize = 0;
        long freeSpace = 0;
        try
        {
            totalSize = drive.TotalSize;
            freeSpace = drive.TotalFreeSpace;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"读取容量失败：{root}", ex);
        }

        var label = string.Empty;
        var fileSystem = string.Empty;
        var serial = string.Empty;

        if (TryGetVolumeInfo(root, out var volumeLabel, out var fs, out var serialNumber, out var apiTotal, out var apiFree))
        {
            label = volumeLabel;
            fileSystem = fs;
            serial = serialNumber;

            if (apiTotal > 0)
            {
                totalSize = apiTotal;
            }

            if (apiFree > 0)
            {
                freeSpace = apiFree;
            }
        }
        else
        {
            // 卷信息 API 失败时退回 DriveInfo（没有序列号，唯一性会退化）
            try
            {
                label = drive.VolumeLabel;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"读取卷标失败：{root}", ex);
            }

            try
            {
                fileSystem = drive.DriveFormat;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"读取文件系统失败：{root}", ex);
            }

            _logger?.Warn($"无法读取卷序列号，唯一标识将缺少序列号：{root}");
        }

        return new UsbDeviceInfo
        {
            RootPath = root,
            VolumeLabel = label,
            FileSystem = fileSystem,
            VolumeSerialNumber = serial,
            TotalSize = totalSize,
            FreeSpace = freeSpace
        };
    }

    private static string SafeRoot(DriveInfo drive)
    {
        try
        {
            return drive.Name;
        }
        catch
        {
            return "?";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}
