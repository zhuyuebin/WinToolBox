using System.Text.Json.Serialization;

namespace WinToolBox.Core;

/// <summary>
/// UsbBackup 配置模型，序列化到 %AppData%\WinToolBox\UsbBackup\config.json。
/// </summary>
public sealed class BackupConfig
{
    /// <summary>历史版本默认保留天数（30 天）。</summary>
    public const int DefaultHistoryRetentionDays = 30;

    /// <summary>表示“永久保留历史版本”的 <see cref="HistoryRetentionDays"/> 取值（0 = 永久，不自动清理）。</summary>
    public const int HistoryRetentionForever = 0;

    /// <summary>UI 允许选择的保留天数（7 / 30 / 90 / 永久）。</summary>
    public static int[] HistoryRetentionChoices => new[] { 7, 30, 90, HistoryRetentionForever };

    /// <summary>备份目标目录（用户选择，例如 D:\UsbBackup）。</summary>
    [JsonPropertyName("backupTargetDirectory")]
    public string BackupTargetDirectory { get; set; } = string.Empty;

    /// <summary>排除的文件后缀（不区分大小写，例如 .tmp）。</summary>
    [JsonPropertyName("excludedExtensions")]
    public List<string> ExcludedExtensions { get; set; } = CreateDefaultExtensions();

    /// <summary>
    /// 严格内容校验（默认 true）。
    /// <para>true：增量判定在“大小相同”后继续做 SHA256 内容校验
    /// （大文件用“大小 + mtime + 首尾各 64 KiB 抽样哈希”折中），内容变化不会被静默跳过。</para>
    /// <para>false：只比大小 + 修改时间，跳过的文件会在摘要中标注“未做内容校验”。</para>
    /// </summary>
    [JsonPropertyName("strictContentVerification")]
    public bool StrictContentVerification { get; set; } = true;

    /// <summary>
    /// history 目录保留天数，默认 <see cref="DefaultHistoryRetentionDays"/>（30 天）。
    /// <see cref="HistoryRetentionForever"/>（0）表示永久保留，UI 需提示磁盘消耗风险。
    /// </summary>
    [JsonPropertyName("historyRetentionDays")]
    public int HistoryRetentionDays { get; set; } = DefaultHistoryRetentionDays;

    /// <summary>是否永久保留历史版本。</summary>
    [JsonIgnore]
    public bool KeepsHistoryForever => HistoryRetentionDays <= HistoryRetentionForever;

    /// <summary>默认排除的文件后缀。</summary>
    public static List<string> CreateDefaultExtensions() => new() { ".tmp", ".part", ".crdownload" };

    /// <summary>默认配置（目标目录为空、使用默认排除后缀）。备份只由用户手动触发。</summary>
    public static BackupConfig CreateDefault() => new();

    /// <summary>深拷贝，避免调用方意外修改缓存实例。</summary>
    public BackupConfig Clone() => new()
    {
        BackupTargetDirectory = BackupTargetDirectory,
        ExcludedExtensions = new List<string>(ExcludedExtensions ?? new List<string>()),
        StrictContentVerification = StrictContentVerification,
        HistoryRetentionDays = HistoryRetentionDays
    };

    /// <summary>
    /// 规范化：去空白、后缀统一为小写并以 '.' 开头、去重、保持稳定顺序。
    /// 反序列化损坏/手改的配置文件后调用，保证后续逻辑拿到干净数据。
    /// </summary>
    public void Normalize()
    {
        BackupTargetDirectory = (BackupTargetDirectory ?? string.Empty).Trim().TrimEnd('\\', '/');

        var normalized = new List<string>();
        foreach (var raw in ExcludedExtensions ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var extension = raw.Trim();
            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            extension = extension.ToLowerInvariant();
            if (extension.Length > 1 && !normalized.Contains(extension))
            {
                normalized.Add(extension);
            }
        }

        ExcludedExtensions = normalized;

        // 保留天数规范化：负数视为默认 30 天；0 表示永久保留。
        if (HistoryRetentionDays < 0)
        {
            HistoryRetentionDays = DefaultHistoryRetentionDays;
        }
    }
}
