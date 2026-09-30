using System.Text;
using System.Text.Json;

namespace WinToolBox.Core;

/// <summary>
/// 配置读写：默认读写 %AppData%\WinToolBox\UsbBackup\config.json。
/// 读取永不抛异常：文件缺失或损坏时返回默认配置（损坏文件不会被自动覆盖）。
/// </summary>
public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Logger? _logger;

    /// <summary>使用默认配置文件路径。</summary>
    public ConfigManager()
        : this(AppPaths.UsbBackupConfigFile, null)
    {
    }

    /// <summary>使用指定配置文件路径（测试可注入临时文件）。</summary>
    public ConfigManager(string configFilePath)
        : this(configFilePath, null)
    {
    }

    /// <summary>使用指定配置文件路径与日志记录器。</summary>
    public ConfigManager(string configFilePath, Logger? logger)
    {
        if (string.IsNullOrWhiteSpace(configFilePath))
        {
            throw new ArgumentException("配置文件路径不能为空", nameof(configFilePath));
        }

        ConfigFilePath = Path.GetFullPath(configFilePath);
        _logger = logger;
    }

    /// <summary>默认配置文件完整路径。</summary>
    public static string DefaultConfigFilePath => AppPaths.UsbBackupConfigFile;

    /// <summary>当前使用的配置文件完整路径。</summary>
    public string ConfigFilePath { get; }

    /// <summary>配置文件所在目录。</summary>
    public string ConfigDirectory => Path.GetDirectoryName(ConfigFilePath) ?? ".";

    /// <summary>配置文件是否已存在。</summary>
    public bool Exists => File.Exists(ConfigFilePath);

    /// <summary>读取配置；文件缺失或内容损坏时返回默认配置。</summary>
    public BackupConfig Load() => TryLoad(out var config, out _) ? config : BackupConfig.CreateDefault();

    /// <summary>尝试读取配置，失败时返回 false 并给出原因。</summary>
    public bool TryLoad(out BackupConfig config, out string? error)
    {
        config = BackupConfig.CreateDefault();
        error = null;

        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                _logger?.Info($"配置文件不存在，使用默认配置：{ConfigFilePath}");
                return true;
            }

            var json = File.ReadAllText(ConfigFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "配置文件内容为空";
                _logger?.Warn($"配置文件为空，使用默认配置：{ConfigFilePath}");
                return false;
            }

            var loaded = JsonSerializer.Deserialize<BackupConfig>(json, SerializerOptions);
            if (loaded is null)
            {
                error = "配置文件反序列化结果为空";
                _logger?.Warn($"配置反序列化失败，使用默认配置：{ConfigFilePath}");
                return false;
            }

            loaded.Normalize();
            config = loaded;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger?.Warn($"读取配置失败，使用默认配置：{ConfigFilePath}", ex);
            return false;
        }
    }

    /// <summary>保存配置（先写临时文件再替换，避免写入中断损坏配置）。</summary>
    public void Save(BackupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var snapshot = config.Clone();
        snapshot.Normalize();

        AppPaths.EnsureDirectory(ConfigDirectory);

        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        var tempFile = ConfigFilePath + ".tmp";

        File.WriteAllText(tempFile, json, new UTF8Encoding(false));
        File.Move(tempFile, ConfigFilePath, overwrite: true);

        _logger?.Info($"配置已保存：{ConfigFilePath}");
    }

    /// <summary>删除配置文件（下次加载回到默认配置）。</summary>
    public void Delete()
    {
        if (File.Exists(ConfigFilePath))
        {
            File.Delete(ConfigFilePath);
            _logger?.Info($"配置已删除：{ConfigFilePath}");
        }
    }
}
