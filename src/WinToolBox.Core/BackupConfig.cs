using System.Text.Json.Serialization;

namespace WinToolBox.Core;

/// <summary>
/// UsbBackup 配置模型，序列化到 %AppData%\WinToolBox\UsbBackup\config.json。
/// </summary>
public sealed class BackupConfig
{
    /// <summary>备份目标目录（用户选择，例如 D:\UsbBackup）。</summary>
    [JsonPropertyName("backupTargetDirectory")]
    public string BackupTargetDirectory { get; set; } = string.Empty;

    /// <summary>排除的文件后缀（不区分大小写，例如 .tmp）。</summary>
    [JsonPropertyName("excludedExtensions")]
    public List<string> ExcludedExtensions { get; set; } = CreateDefaultExtensions();

    /// <summary>默认排除的文件后缀。</summary>
    public static List<string> CreateDefaultExtensions() => new() { ".tmp", ".part", ".crdownload" };

    /// <summary>默认配置（目标目录为空、使用默认排除后缀）。备份只由用户手动触发。</summary>
    public static BackupConfig CreateDefault() => new();

    /// <summary>深拷贝，避免调用方意外修改缓存实例。</summary>
    public BackupConfig Clone() => new()
    {
        BackupTargetDirectory = BackupTargetDirectory,
        ExcludedExtensions = new List<string>(ExcludedExtensions ?? new List<string>())
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
    }
}
