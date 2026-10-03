using System.Globalization;
using System.Text;

namespace WinToolBox.Core;

/// <summary>
/// UsbBackup 的核心业务规则：唯一标识、目标路径、默认排除规则、安全校验。
/// 这里集中放置可被单元测试覆盖的纯逻辑。
/// </summary>
public static class BackupRules
{
    /// <summary>卷标为空时的占位符。</summary>
    public const string UnknownLabel = "NOLABEL";

    /// <summary>卷序列号读取失败时的占位符。</summary>
    public const string UnknownSerial = "00000000";

    /// <summary>无法识别设备时的目录名占位符。</summary>
    public const string UnknownDeviceFolder = "UNKNOWN";

    /// <summary>U 盘最新副本的子目录名。</summary>
    public const string CurrentFolderName = "current";

    /// <summary>历史版本根目录名。</summary>
    public const string HistoryFolderName = "history";

    /// <summary>备份清单文件名。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>默认排除的目录名。</summary>
    public static string[] DefaultExcludedDirectoryNames => ExcludeRules.DefaultDirectoryNames;

    /// <summary>默认排除的文件名（含 autorun.inf）。</summary>
    public static string[] DefaultExcludedFileNames => ExcludeRules.DefaultFileNames;

    /// <summary>默认排除的文件后缀。</summary>
    public static string[] DefaultExcludedExtensions => ExcludeRules.DefaultExtensions;

    /// <summary>
    /// 设备唯一标识：卷标 + 卷序列号 + 容量（不使用盘符，因为盘符会变）。
    /// </summary>
    public static string BuildUniqueId(string? volumeLabel, string? volumeSerialNumber, long totalSize)
    {
        var label = string.IsNullOrWhiteSpace(volumeLabel) ? UnknownLabel : volumeLabel!.Trim();
        var serial = string.IsNullOrWhiteSpace(volumeSerialNumber) ? UnknownSerial : volumeSerialNumber!.Trim();

        return $"{label}_{serial}_{totalSize}";
    }

    /// <summary>
    /// 备份目录中的设备文件夹名：{卷标}_{序列号}（按任务书约定）。
    /// </summary>
    public static string BuildDeviceFolderName(UsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return BuildDeviceFolderName(device.VolumeLabel, device.VolumeSerialNumber);
    }

    /// <summary>备份目录中的设备文件夹名：{卷标}_{序列号}。</summary>
    public static string BuildDeviceFolderName(string? volumeLabel, string? volumeSerialNumber)
    {
        var label = SanitizeName(volumeLabel);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = UnknownLabel;
        }

        var serial = SanitizeName(volumeSerialNumber);
        if (string.IsNullOrWhiteSpace(serial))
        {
            serial = UnknownSerial;
        }

        return $"{label}_{serial}";
    }

    /// <summary>
    /// 设备备份根目录：<c>{用户配置的目标目录}\{卷标}_{序列号}\</c>。
    /// <para>产品语义：一个 U 盘只对应一个备份根目录，**不带日期**。</para>
    /// </summary>
    public static string BuildDeviceRootDirectory(string backupRootDirectory, UsbDeviceInfo device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRootDirectory);
        ArgumentNullException.ThrowIfNull(device);

        var root = Path.GetFullPath(backupRootDirectory);
        return Path.Combine(root, BuildDeviceFolderName(device));
    }

    /// <summary>
    /// U 盘最新副本目录：<c>{目标目录}\{卷标}_{序列号}\current\</c>（每次增量更新）。
    /// </summary>
    public static string BuildCurrentDirectory(string backupRootDirectory, UsbDeviceInfo device)
        => Path.Combine(BuildDeviceRootDirectory(backupRootDirectory, device), CurrentFolderName);

    /// <summary>
    /// 历史版本根目录：<c>{目标目录}\{卷标}_{序列号}\history\</c>。
    /// </summary>
    public static string BuildHistoryRootDirectory(string backupRootDirectory, UsbDeviceInfo device)
        => Path.Combine(BuildDeviceRootDirectory(backupRootDirectory, device), HistoryFolderName);

    /// <summary>
    /// 某一天的历史版本目录：<c>{目标目录}\{卷标}_{序列号}\history\{yyyy-MM-dd}\</c>。
    /// </summary>
    public static string BuildHistoryDirectory(string backupRootDirectory, UsbDeviceInfo device, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Path.Combine(BuildHistoryRootDirectory(backupRootDirectory, device), FormatHistoryFolderName(date));
    }

    /// <summary>把日期格式化为 history 子目录名（yyyy-MM-dd）。</summary>
    public static string FormatHistoryFolderName(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析 history 子目录名对应的日期；不是合法日期目录时返回 null（清理时据此忽略无关目录）。
    /// </summary>
    public static DateTime? TryParseHistoryFolderName(string folderName)
        => DateTime.TryParseExact(
            folderName,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// 备份清单文件路径：<c>{目标目录}\{卷标}_{序列号}\manifest.json</c>。
    /// </summary>
    public static string BuildManifestPath(string backupRootDirectory, UsbDeviceInfo device)
        => Path.Combine(BuildDeviceRootDirectory(backupRootDirectory, device), ManifestFileName);

    /// <summary>
    /// 本次备份的目标目录（= <c>current</c>）。
    /// <para>保留此方法名是为了兼容既有调用点；产品语义已改为「固定目录 + 真增量 + 软删除历史」，
    /// 因此结果里不再包含 <c>{yyyy-MM-dd}</c>。</para>
    /// </summary>
    public static string BuildTargetDirectory(string backupRootDirectory, UsbDeviceInfo device)
        => BuildCurrentDirectory(backupRootDirectory, device);

    /// <summary>默认排除规则（安全基线 + 配置里额外的后缀）。</summary>
    public static ExcludeRules CreateDefaultExcludeRules(IEnumerable<string>? extraExtensions = null)
        => ExcludeRules.CreateDefault(extraExtensions);

    /// <summary>把任意文本清洗成合法目录名片段。</summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = builder.ToString().Trim(' ', '.', '_');
        return result.Length > 64 ? result[..64] : result;
    }

    /// <summary>
    /// 目标目录是否与源设备在同一个卷上（防止把 U 盘备份回它自己）。
    /// </summary>
    public static bool IsTargetOnSameVolume(string sourceRootPath, string targetRootPath)
    {
        if (string.IsNullOrWhiteSpace(sourceRootPath) || string.IsNullOrWhiteSpace(targetRootPath))
        {
            return false;
        }

        try
        {
            var sourceVolume = Path.GetPathRoot(Path.GetFullPath(sourceRootPath));
            var targetVolume = Path.GetPathRoot(Path.GetFullPath(targetRootPath));

            if (string.IsNullOrEmpty(sourceVolume) || string.IsNullOrEmpty(targetVolume))
            {
                return false;
            }

            return string.Equals(
                sourceVolume.TrimEnd('\\', '/'),
                targetVolume.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
