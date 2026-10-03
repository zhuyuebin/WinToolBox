using System.Text.Json.Serialization;

namespace WinToolBox.Core;

/// <summary>
/// 文件被移入 history 的原因。
/// </summary>
public enum HistoryReason
{
    /// <summary>U 盘里该文件已被删除，<c>current</c> 中的副本移入 history 保留（软删除）。</summary>
    DeletedOnSource,

    /// <summary>U 盘里该文件被修改，<c>current</c> 中的旧版本先移入 history 再写入新版本。</summary>
    OverwrittenByNewerVersion
}

/// <summary>一条「文件被移入 history」的记录。</summary>
public sealed class HistoryMoveRecord
{
    /// <summary>相对备份根（current / history 之下）的路径。</summary>
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>移入 history 的原因。</summary>
    [JsonPropertyName("reason")]
    public HistoryReason Reason { get; set; }

    /// <summary>移入 history 后的完整路径。</summary>
    [JsonPropertyName("archivedTo")]
    public string ArchivedTo { get; set; } = string.Empty;

    /// <summary>文件字节数。</summary>
    [JsonPropertyName("length")]
    public long Length { get; set; }
}

/// <summary>
/// 上一次备份与本次备份的对比结果。
/// </summary>
public sealed class BackupComparison
{
    /// <summary>本次新出现的文件数（U 盘里新增）。</summary>
    [JsonPropertyName("newFiles")]
    public int NewFiles { get; set; }

    /// <summary>内容发生变化、被重新复制的文件数。</summary>
    [JsonPropertyName("updatedFiles")]
    public int UpdatedFiles { get; set; }

    /// <summary>U 盘里已删除、<c>current</c> 副本被移入 history 的文件数。</summary>
    [JsonPropertyName("deletedFiles")]
    public int DeletedFiles { get; set; }

    /// <summary>内容未变、被跳过的文件数。</summary>
    [JsonPropertyName("unchangedFiles")]
    public int UnchangedFiles { get; set; }

    /// <summary>复制失败的文件数。</summary>
    [JsonPropertyName("failedFiles")]
    public int FailedFiles { get; set; }

    /// <summary>一句话摘要（中文）。</summary>
    [JsonIgnore]
    public string Summary =>
        $"新增 {NewFiles}，更新 {UpdatedFiles}，未变 {UnchangedFiles}，U 盘已删除并转入历史 {DeletedFiles}" +
        (FailedFiles > 0 ? $"，失败 {FailedFiles}" : string.Empty) + "。";
}

/// <summary>
/// 设备备份清单元数据（写入 <c>{目标目录}\{卷标}_{序列号}\manifest.json</c>）。
/// <para>记录最近一次备份的时间、文件总数、总大小、history 保留策略与上次备份对比结果。</para>
/// </summary>
public sealed class BackupManifest
{
    /// <summary>当前清单格式版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>清单格式版本。</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>最近一次备份完成时间（本地时间）。</summary>
    [JsonPropertyName("lastBackupTime")]
    public DateTime LastBackupTime { get; set; }

    /// <summary>设备唯一标识（卷标_序列号_容量）。</summary>
    [JsonPropertyName("deviceUniqueId")]
    public string DeviceUniqueId { get; set; } = string.Empty;

    /// <summary>设备显示名（便于人工查看）。</summary>
    [JsonPropertyName("deviceDisplayName")]
    public string DeviceDisplayName { get; set; } = string.Empty;

    /// <summary>备份根目录（设备目录）的完整路径。</summary>
    [JsonPropertyName("deviceRootDirectory")]
    public string DeviceRootDirectory { get; set; } = string.Empty;

    /// <summary><c>current</c> 中的文件总数。</summary>
    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    /// <summary><c>current</c> 中的文件总字节数。</summary>
    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    /// <summary>history 保留天数；0 表示永久保留。</summary>
    [JsonPropertyName("historyRetentionDays")]
    public int HistoryRetentionDays { get; set; } = BackupConfig.DefaultHistoryRetentionDays;

    /// <summary>history 是否永久保留。</summary>
    [JsonPropertyName("historyKeptForever")]
    public bool HistoryKeptForever { get; set; }

    /// <summary>清单写入时 history 目录占用（字节，尽力而为；统计失败为 0）。</summary>
    [JsonPropertyName("historyBytes")]
    public long HistoryBytes { get; set; }

    /// <summary>本次备份中被清理掉的过期 history 目录名。</summary>
    [JsonPropertyName("purgedHistoryFolders")]
    public List<string> PurgedHistoryFolders { get; set; } = new();

    /// <summary>与上次备份的对比结果。</summary>
    [JsonPropertyName("comparison")]
    public BackupComparison Comparison { get; set; } = new();

    /// <summary>本次移入 history 的文件记录（用于人工追溯）。</summary>
    [JsonPropertyName("historyMoves")]
    public List<HistoryMoveRecord> HistoryMoves { get; set; } = new();

    /// <summary>历史保留策略的可读描述。</summary>
    [JsonIgnore]
    public string HistoryPolicyText => HistoryKeptForever || HistoryRetentionDays <= 0
        ? "history 永久保留（不自动清理）"
        : $"history 保留 {HistoryRetentionDays} 天";
}
