namespace WinToolBox.Core;

/// <summary>
/// WinToolBox 统一路径定义。
/// 配置：%AppData%\WinToolBox\{工具名}\config.json
/// 模板：%AppData%\WinToolBox\FolderCreator\templates.json
/// 日志：%LocalAppData%\WinToolBox\logs\
/// </summary>
public static class AppPaths
{
    /// <summary>产品名称，用于拼装数据目录。</summary>
    public const string ProductName = "WinToolBox";

    /// <summary>UsbBackup 工具的配置子目录名。</summary>
    public const string UsbBackupFolderName = "UsbBackup";

    /// <summary>FolderCreator 工具的配置子目录名。</summary>
    public const string FolderCreatorFolderName = "FolderCreator";

    /// <summary>配置文件名。</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>FolderCreator 规则模板文件名。</summary>
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

    /// <summary>%AppData%\WinToolBox\FolderCreator</summary>
    public static string FolderCreatorConfigDirectory => Path.Combine(AppDataRoot, FolderCreatorFolderName);

    /// <summary>%AppData%\WinToolBox\FolderCreator\templates.json</summary>
    public static string FolderCreatorTemplatesFile => Path.Combine(FolderCreatorConfigDirectory, TemplatesFileName);

    /// <summary>%LocalAppData%\WinToolBox\logs</summary>
    public static string LogDirectory => Path.Combine(LocalAppDataRoot, "logs");

    /// <summary>确保目录存在（已存在时不做任何事）。</summary>
    public static void EnsureDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
