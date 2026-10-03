namespace WinToolBox.Core;

/// <summary>
/// WinToolBox 统一路径定义。
/// 配置：%AppData%\WinToolBox\{工具名}\config.json
/// 模板：%AppData%\WinToolBox\FileMaster\templates.json
/// 日志：%LocalAppData%\WinToolBox\logs\
/// </summary>
public static class AppPaths
{
    /// <summary>产品名称，用于拼装数据目录。</summary>
    public const string ProductName = "WinToolBox";

    /// <summary>UsbBackup 工具的配置子目录名。</summary>
    public const string UsbBackupFolderName = "UsbBackup";

    /// <summary>FileMaster 工具的配置子目录名。</summary>
    public const string FileMasterFolderName = "FileMaster";

    /// <summary>FileMaster 的旧目录名（原 FolderCreator，仅用于一次性迁移模板）。</summary>
    public const string LegacyFileMasterFolderName = "FolderCreator";

    /// <summary>配置文件名。</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>FileMaster 规则模板文件名。</summary>
    public const string TemplatesFileName = "templates.json";

    /// <summary>%AppData%\WinToolBox</summary>
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductName);

    /// <summary>%LocalAppData%\WinToolBox</summary>
    public static string LocalAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    /// <summary>%AppData%\WinToolBox\UsbBackup</summary>
    public static string UsbBackupConfigDirectory => Path.Combine(AppDataRoot, UsbBackupFolderName);

    /// <summary>%AppData%\WinToolBox\UsbBackup\config.json</summary>
    public static string UsbBackupConfigFile => Path.Combine(UsbBackupConfigDirectory, ConfigFileName);

    /// <summary>%AppData%\WinToolBox\FileMaster</summary>
    public static string FileMasterConfigDirectory => Path.Combine(AppDataRoot, FileMasterFolderName);

    /// <summary>%AppData%\WinToolBox\FileMaster\templates.json</summary>
    public static string FileMasterTemplatesFile => Path.Combine(FileMasterConfigDirectory, TemplatesFileName);

    /// <summary>旧版 %AppData%\WinToolBox\FolderCreator\templates.json（迁移来源）。</summary>
    public static string LegacyFileMasterTemplatesFile => Path.Combine(
        AppDataRoot,
        LegacyFileMasterFolderName,
        TemplatesFileName);

    /// <summary>%LocalAppData%\WinToolBox\logs</summary>
    public static string LogDirectory => Path.Combine(LocalAppDataRoot, "logs");

    /// <summary>
    /// 某个工具专属的日志目录：<c>%LocalAppData%\WinToolBox\logs\{工具名}\</c>。
    /// </summary>
    /// <param name="toolName">工具名（例如 <c>UsbBackup</c> / <c>FileMaster</c>）。</param>
    /// <remarks>
    /// 为什么按工具分目录：两个工具曾共用同一个日志文件，虽然用 <c>FileShare.ReadWrite</c> 能并发写，
    /// 但一旦排障时需要「只看 U 盘备份的日志」，就得在混合文件里翻找；分开写还顺带消除了
    /// 「一方按保留期删除、另一方仍在写」这类互相干扰。
    /// <para>传入空工具名时退回共享目录 <see cref="LogDirectory"/>，保持旧行为可用。</para>
    /// </remarks>
    public static string LogDirectoryFor(string? toolName)
    {
        var name = SanitizeFolderName(toolName);

        return name.Length == 0
            ? LogDirectory
            : Path.Combine(LogDirectory, name);
    }

    /// <summary>把工具名清洗成合法目录名片段（非法字符替换为 <c>_</c>）。</summary>
    private static string SanitizeFolderName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(toolName.Length);

        foreach (var ch in toolName.Trim())
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 || ch == '.' ? '_' : ch);
        }

        return builder.ToString().Trim('_', ' ');
    }

    /// <summary>确保目录存在（已存在时不做任何事）。</summary>
    public static void EnsureDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
