namespace WinToolBox.Core;

/// <summary>复制进度快照（每次处理完一个文件回调一次）。</summary>
public sealed class CopyProgress
{
    /// <summary>当前正在处理的文件（相对源目录）。</summary>
    public string CurrentFile { get; init; } = string.Empty;

    /// <summary>需要处理的文件总数（已排除不参与复制的文件）。</summary>
    public int TotalFiles { get; init; }

    /// <summary>已处理文件数（复制 + 跳过 + 失败）。</summary>
    public int ProcessedFiles { get; init; }

    /// <summary>已复制（新增或更新）文件数。</summary>
    public int CopiedFiles { get; init; }

    /// <summary>已跳过（未修改）文件数。</summary>
    public int SkippedFiles { get; init; }

    /// <summary>失败文件数。</summary>
    public int FailedFiles { get; init; }

    /// <summary>已复制字节数。</summary>
    public long CopiedBytes { get; init; }

    /// <summary>待复制总字节数。</summary>
    public long TotalBytes { get; init; }

    /// <summary>完成百分比（0-100）。</summary>
    public double Percent => TotalFiles == 0 ? 100d : Math.Round(ProcessedFiles * 100d / TotalFiles, 1);
}

/// <summary>单次目录复制的结果汇总。</summary>
public sealed class CopyResult
{
    /// <summary>源目录。</summary>
    public string SourceDirectory { get; init; } = string.Empty;

    /// <summary>目标目录。</summary>
    public string TargetDirectory { get; init; } = string.Empty;

    /// <summary>新增或更新的文件数。</summary>
    public int CopiedFiles { get; set; }

    /// <summary>因为未修改而跳过的文件数。</summary>
    public int SkippedFiles { get; set; }

    /// <summary>
    /// 跳过且【已做 SHA256 全量内容校验】并确认一致的文件数
    /// （即 <see cref="SkippedFiles"/> 中经过严格校验的那部分）。
    /// </summary>
    public int VerifiedFiles { get; set; }

    /// <summary>
    /// 跳过但【未做全量内容校验】的文件数：仅凭大小 / 修改时间 / 抽样哈希推断为未修改。
    /// 摘要会据此标注“未做内容校验”。
    /// </summary>
    public int UnchangedAssumed { get; set; }

    /// <summary>复制失败的文件数。</summary>
    public int FailedFiles { get; set; }

    /// <summary>新建的目录数。</summary>
    public int CreatedDirectories { get; set; }

    /// <summary>已复制字节数。</summary>
    public long CopiedBytes { get; set; }

    /// <summary>是否被取消。</summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// 是否因为「连续失败达到阈值」而提前中止（区别于用户主动取消）。
    /// <para>用于给用户一个准确的结论：是「你取消了」还是「盘/目标出问题了」。</para>
    /// </summary>
    public bool AbortedByConsecutiveFailures { get; set; }

    /// <summary>失败详情（文件路径 + 原因）。</summary>
    public IList<string> Errors { get; } = new List<string>();

    /// <summary>是否全部成功。</summary>
    public bool Success => FailedFiles == 0 && !Cancelled && !AbortedByConsecutiveFailures;

    /// <summary>人类可读的中文摘要，用于日志与气泡通知。</summary>
    public string Summary =>
        $"新增/更新 {CopiedFiles} 个文件，跳过 {SkippedFiles} 个未修改文件" +
        VerificationNote +
        (FailedFiles > 0 ? $"，失败 {FailedFiles} 个" : string.Empty) +
        (Cancelled ? "（已取消）" : string.Empty) +
        (AbortedByConsecutiveFailures ? "（连续失败过多已中止）" : string.Empty) +
        $"，共 {FormatSize(CopiedBytes)}。";

    /// <summary>
    /// 校验方式说明：明确区分“已 SHA256 校验”和“未做内容校验”，避免让用户误以为做过内容比对。
    /// </summary>
    public string VerificationNote =>
        VerifiedFiles > 0 && UnchangedAssumed == 0 ? "（已 SHA256 校验）"
        : UnchangedAssumed > 0 && VerifiedFiles == 0 ? "（未做内容校验）"
        : VerifiedFiles > 0 ? $"（已 SHA256 校验 {VerifiedFiles} 个，未做内容校验 {UnchangedAssumed} 个）"
        : string.Empty;

    /// <summary>把字节数格式化为易读文本。</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.##} {units[unit]}";
    }
}
