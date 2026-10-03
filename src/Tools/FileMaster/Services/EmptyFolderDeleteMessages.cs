namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 空目录删除相关的用户可见文案。
/// <para>集中放置的原因：这些文案是「永久删除」这一危险操作的最后一道人为防线，
/// 必须能被单元测试断言（避免某次重构把「永久删除、不进回收站」改回含糊的「删除」）。</para>
/// </summary>
public static class EmptyFolderDeleteMessages
{
    /// <summary>永久删除时确认框的标题。</summary>
    public const string PermanentConfirmTitle = "确认永久删除（不进回收站）";

    /// <summary>放入回收站时确认框的标题。</summary>
    public const string RecycleConfirmTitle = "确认放入回收站";

    /// <summary>永久删除方式说明（必须同时出现「永久删除」与「不进回收站」「无法恢复」）。</summary>
    public const string PermanentMethodText = "删除方式：永久删除，不进回收站，无法还原、无法恢复！";

    /// <summary>放入回收站方式说明。</summary>
    public const string RecycleMethodText = "删除方式：放入回收站，需要时可从回收站还原。";

    /// <summary>
    /// 删除前的二次确认文案。
    /// </summary>
    /// <param name="count">待删除目录数量。</param>
    /// <param name="useRecycleBin">true 放入回收站；false 永久删除。</param>
    public static string BuildConfirmMessage(int count, bool useRecycleBin)
    {
        var method = useRecycleBin ? RecycleMethodText : PermanentMethodText;
        var extra = useRecycleBin
            ? string.Empty
            : $"{Environment.NewLine}这些目录及其内容会被直接抹除，不进入回收站。";

        return $"确定要删除选中的 {count} 个空文件夹吗？{Environment.NewLine}{Environment.NewLine}{method}{extra}";
    }

    /// <summary>确认框标题。</summary>
    /// <param name="useRecycleBin">true 放入回收站；false 永久删除。</param>
    public static string GetConfirmTitle(bool useRecycleBin)
        => useRecycleBin ? RecycleConfirmTitle : PermanentConfirmTitle;
}
