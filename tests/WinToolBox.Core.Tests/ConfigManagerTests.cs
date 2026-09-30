using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// ConfigManager 单元测试：默认路径、配置文件缺失/损坏/为空时的降级行为、
/// 保存与读取的往返一致性、目录自动创建、.tmp 残留、Normalize 规范化与删除。
/// 全部使用注入的临时路径，不读写系统 %AppData%。
/// </summary>
public sealed class ConfigManagerTests
{
    private static string ConfigPath(TempWorkspace ws, string fileName = "config.json")
        => Path.Combine(ws.Root, "cfg", fileName);

    // ------------------------------------------------------------------
    // 默认值与路径
    // ------------------------------------------------------------------

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaultConfig()
    {
        using var ws = new TempWorkspace();
        var manager = new ConfigManager(ConfigPath(ws));

        Assert.False(manager.Exists);

        var config = manager.Load();

        // 默认配置：自动备份开启、目标目录为空、使用默认排除后缀
        Assert.True(config.AutoBackupEnabled);
        Assert.Equal(string.Empty, config.BackupTargetDirectory);
        Assert.Equal(3, config.ExcludedExtensions.Count);
        Assert.Contains(".tmp", config.ExcludedExtensions);
        Assert.Contains(".part", config.ExcludedExtensions);
        Assert.Contains(".crdownload", config.ExcludedExtensions);

        // 读取不存在的文件不应创建任何文件
        Assert.False(manager.Exists);
    }

    [Fact]
    public void TryLoad_WhenFileMissing_ReturnsTrueWithDefaultConfig()
    {
        using var ws = new TempWorkspace();
        var manager = new ConfigManager(ConfigPath(ws));

        var ok = manager.TryLoad(out var config, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(config.AutoBackupEnabled);
        Assert.Empty(config.BackupTargetDirectory);
    }

    [Fact]
    public void ConfigFilePath_IsAlwaysAbsolutePath()
    {
        using var ws = new TempWorkspace();
        var manager = new ConfigManager(ConfigPath(ws));

        Assert.True(Path.IsPathFullyQualified(manager.ConfigFilePath));
        Assert.Equal(Path.GetFullPath(ConfigPath(ws)), manager.ConfigFilePath);
        Assert.Equal(Path.GetDirectoryName(manager.ConfigFilePath), manager.ConfigDirectory);
    }

    [Fact]
    public void DefaultConfigFilePath_PointsToUsbBackupConfigJson()
    {
        var path = ConfigManager.DefaultConfigFilePath;

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal("config.json", Path.GetFileName(path));
        Assert.EndsWith(Path.Combine("WinToolBox", "UsbBackup", "config.json"), path);
    }

    [Fact]
    public void Constructor_WhenPathIsNullOrWhitespace_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ConfigManager(""));
        Assert.Throws<ArgumentException>(() => new ConfigManager("   "));
    }

    // ------------------------------------------------------------------
    // 保存与读取往返
    // ------------------------------------------------------------------

    [Fact]
    public void SaveThenLoad_RoundTripsAllFieldsIncludingChinesePath()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var manager = new ConfigManager(configPath);

        var chineseTarget = Path.Combine(ws.Root, "备份目标", "U盘资料");
        var config = new BackupConfig
        {
            BackupTargetDirectory = chineseTarget,
            AutoBackupEnabled = false,
            ExcludedExtensions = new List<string> { ".tmp", ".bak", ".log" }
        };

        manager.Save(config);
        Assert.True(manager.Exists);

        var loaded = manager.Load();

        Assert.Equal(chineseTarget, loaded.BackupTargetDirectory);
        Assert.False(loaded.AutoBackupEnabled);
        Assert.Equal(3, loaded.ExcludedExtensions.Count);
        Assert.Contains(".bak", loaded.ExcludedExtensions);
        Assert.Contains(".log", loaded.ExcludedExtensions);
        Assert.Contains(".tmp", loaded.ExcludedExtensions);
    }

    [Fact]
    public void Save_CreatesMissingDirectoriesAndLeavesNoTempFile()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws, "nested\\deeper\\config.json");

        Assert.False(Directory.Exists(Path.GetDirectoryName(configPath)));

        new ConfigManager(configPath).Save(BackupConfig.CreateDefault());

        // 目录自动创建，且不残留 .tmp 临时文件
        Assert.True(Directory.Exists(Path.GetDirectoryName(configPath)));
        Assert.True(File.Exists(configPath));
        Assert.False(File.Exists(configPath + ".tmp"));
    }

    [Fact]
    public void Save_OverwritesExistingConfig()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var manager = new ConfigManager(configPath);

        manager.Save(new BackupConfig { BackupTargetDirectory = Path.Combine(ws.Root, "first"), AutoBackupEnabled = true });
        manager.Save(new BackupConfig { BackupTargetDirectory = Path.Combine(ws.Root, "second"), AutoBackupEnabled = false });

        var loaded = manager.Load();

        Assert.Equal(Path.Combine(ws.Root, "second"), loaded.BackupTargetDirectory);
        Assert.False(loaded.AutoBackupEnabled);
        Assert.False(File.Exists(configPath + ".tmp"));
    }

    [Fact]
    public void Save_DoesNotMutateCallerConfigInstance()
    {
        using var ws = new TempWorkspace();
        var manager = new ConfigManager(ConfigPath(ws));

        var config = new BackupConfig
        {
            BackupTargetDirectory = Path.Combine(ws.Root, "target") + "\\",
            ExcludedExtensions = new List<string> { "TMP" }
        };

        manager.Save(config);

        // Save 内部使用 Clone，调用方传入的实例不应被规范化改写
        Assert.EndsWith("\\", config.BackupTargetDirectory);
        Assert.Equal("TMP", config.ExcludedExtensions[0]);

        // 但落盘内容已被规范化
        var loaded = manager.Load();
        Assert.Equal(Path.Combine(ws.Root, "target"), loaded.BackupTargetDirectory);
        Assert.Contains(".tmp", loaded.ExcludedExtensions);
    }

    // ------------------------------------------------------------------
    // 损坏 / 空文件降级
    // ------------------------------------------------------------------

    [Fact]
    public void Load_WhenJsonCorrupted_ReturnsDefaultAndKeepsBrokenFile()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var manager = new ConfigManager(configPath);

        const string brokenJson = "{ \"backupTargetDirectory\": ";
        ws.CreateFile(configPath, brokenJson, null, ws.Root);

        var loaded = manager.Load();

        Assert.True(loaded.AutoBackupEnabled);
        Assert.Equal(string.Empty, loaded.BackupTargetDirectory);

        // 损坏文件不能被自动覆盖，便于用户手工修复
        Assert.True(File.Exists(configPath));
        Assert.Equal(brokenJson, TempWorkspace.ReadAllText(configPath));
    }

    [Fact]
    public void TryLoad_WhenJsonCorrupted_ReturnsFalseWithError()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var manager = new ConfigManager(configPath);

        ws.CreateFile(configPath, "{ not-valid-json !!", null, ws.Root);

        var ok = manager.TryLoad(out var config, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.NotEmpty(error);

        // 失败时仍然给出默认配置，避免调用方拿到 null
        Assert.NotNull(config);
        Assert.True(config.AutoBackupEnabled);
    }

    [Fact]
    public void TryLoad_WhenFileEmpty_ReturnsFalseAndDefaultConfig()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var manager = new ConfigManager(configPath);

        ws.CreateFile(configPath, string.Empty, null, ws.Root);

        var ok = manager.TryLoad(out var config, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal("配置文件内容为空", error);
        Assert.True(config.AutoBackupEnabled);
        Assert.Equal(string.Empty, config.BackupTargetDirectory);
    }

    [Fact]
    public void Load_WhenFileEmpty_ReturnsDefaultConfig()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        ws.CreateFile(configPath, "   ", null, ws.Root);

        var loaded = new ConfigManager(configPath).Load();

        Assert.Equal(string.Empty, loaded.BackupTargetDirectory);
        Assert.Equal(3, loaded.ExcludedExtensions.Count);
    }

    [Fact]
    public void Load_WhenJsonIsLiteralNull_ReturnsDefaultConfig()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        ws.CreateFile(configPath, "null", null, ws.Root);

        var ok = new ConfigManager(configPath).TryLoad(out var config, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.True(config.AutoBackupEnabled);
        Assert.Equal(string.Empty, config.BackupTargetDirectory);
    }

    [Fact]
    public void Load_WhenJsonHasPartialFields_UsesDefaultsForMissingOnes()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        ws.CreateFile(configPath, "{ \"autoBackupEnabled\": false }", null, ws.Root);

        var loaded = new ConfigManager(configPath).Load();

        Assert.False(loaded.AutoBackupEnabled);
        Assert.Equal(string.Empty, loaded.BackupTargetDirectory);
        Assert.Equal(3, loaded.ExcludedExtensions.Count);
    }

    [Fact]
    public void Load_WhenJsonHasUnknownFields_IgnoresThem()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var json = "{ \"backupTargetDirectory\": \"" + Path.Combine(ws.Root, "target").Replace("\\", "\\\\") + "\", " +
                   "\"futureOption\": 42 }";
        ws.CreateFile(configPath, json, null, ws.Root);

        var loaded = new ConfigManager(configPath).Load();

        Assert.Equal(Path.Combine(ws.Root, "target"), loaded.BackupTargetDirectory);
    }

    [Fact]
    public void Load_AcceptsPropertyNamesCaseInsensitivelyAndTrailingCommas()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var target = Path.Combine(ws.Root, "target");

        var json = "{\n" +
                   "  // 用户手工编辑时允许注释\n" +
                   "  \"BackupTargetDirectory\": \"" + target.Replace("\\", "\\\\") + "\",\n" +
                   "  \"AUTOBACKUPENABLED\": true,\n" +
                   "}";
        ws.CreateFile(configPath, json, null, ws.Root);

        var ok = new ConfigManager(configPath).TryLoad(out var config, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(target, config.BackupTargetDirectory);
    }

    // ------------------------------------------------------------------
    // Normalize 规范化
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_AddsDotLowercasesDeduplicatesAndDropsEmptyExtensions()
    {
        var config = new BackupConfig
        {
            BackupTargetDirectory = @"  D:\UsbBackup\  ",
            ExcludedExtensions = new List<string> { "TMP", ".Tmp", "  .LOG ", "log", "", "   ", ".bak" }
        };

        config.Normalize();

        // 目标目录去空白、去尾部斜杠
        Assert.Equal(@"D:\UsbBackup", config.BackupTargetDirectory);

        // 后缀补点、转小写、去重、去空
        Assert.Equal(3, config.ExcludedExtensions.Count);
        Assert.Equal(new[] { ".tmp", ".log", ".bak" }, config.ExcludedExtensions);
    }

    [Fact]
    public void Normalize_HandlesEmptyAndNullCollections()
    {
        var config = new BackupConfig
        {
            BackupTargetDirectory = "   ",
            ExcludedExtensions = null!
        };

        config.Normalize();

        Assert.Equal(string.Empty, config.BackupTargetDirectory);
        Assert.NotNull(config.ExcludedExtensions);
        Assert.Empty(config.ExcludedExtensions);
    }

    [Theory]
    [InlineData("D:\\UsbBackup\\", "D:\\UsbBackup")]
    [InlineData("D:\\UsbBackup///", "D:\\UsbBackup")]
    [InlineData("  D:\\UsbBackup  ", "D:\\UsbBackup")]
    [InlineData("\\\\server\\share\\backup\\", "\\\\server\\share\\backup")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_TrimsTargetDirectory(string input, string expected)
    {
        var config = new BackupConfig { BackupTargetDirectory = input };

        config.Normalize();

        Assert.Equal(expected, config.BackupTargetDirectory);
    }

    [Fact]
    public void Load_AppliesNormalizeToPersistedValues()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var rawTarget = Path.Combine(ws.Root, "target") + "\\";

        var json = "{\n" +
                   "  \"backupTargetDirectory\": \"" + rawTarget.Replace("\\", "\\\\") + "\",\n" +
                   "  \"excludedExtensions\": [\"TMP\", \"tmp\", \"  \", \".LOG\"]\n" +
                   "}";
        ws.CreateFile(configPath, json, null, ws.Root);

        var loaded = new ConfigManager(configPath).Load();

        Assert.Equal(Path.Combine(ws.Root, "target"), loaded.BackupTargetDirectory);
        Assert.Equal(2, loaded.ExcludedExtensions.Count);
        Assert.Contains(".tmp", loaded.ExcludedExtensions);
        Assert.Contains(".log", loaded.ExcludedExtensions);
    }

    // ------------------------------------------------------------------
    // Exists / Delete
    // ------------------------------------------------------------------

    [Fact]
    public void Delete_RemovesConfigFileAndResetsToDefaults()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var manager = new ConfigManager(configPath);

        manager.Save(new BackupConfig
        {
            BackupTargetDirectory = Path.Combine(ws.Root, "target"),
            AutoBackupEnabled = false
        });

        Assert.True(manager.Exists);

        manager.Delete();

        Assert.False(manager.Exists);
        Assert.False(File.Exists(configPath));

        var reloaded = manager.Load();
        Assert.True(reloaded.AutoBackupEnabled);
        Assert.Equal(string.Empty, reloaded.BackupTargetDirectory);
    }

    [Fact]
    public void Delete_WhenFileMissing_DoesNotThrow()
    {
        using var ws = new TempWorkspace();
        var manager = new ConfigManager(ConfigPath(ws, "missing.json"));

        manager.Delete();

        Assert.False(manager.Exists);
    }

    [Fact]
    public void SaveThenDeleteThenSave_WorksRepeatedly()
    {
        using var ws = new TempWorkspace();
        var configPath = ConfigPath(ws);
        var manager = new ConfigManager(configPath);

        manager.Save(new BackupConfig { BackupTargetDirectory = Path.Combine(ws.Root, "one") });
        manager.Delete();
        manager.Save(new BackupConfig { BackupTargetDirectory = Path.Combine(ws.Root, "two") });

        Assert.True(manager.Exists);
        Assert.Equal(Path.Combine(ws.Root, "two"), manager.Load().BackupTargetDirectory);
        Assert.False(File.Exists(configPath + ".tmp"));
    }

    // ------------------------------------------------------------------
    // BackupConfig 自身行为
    // ------------------------------------------------------------------

    [Fact]
    public void BackupConfig_Clone_IsDeepCopy()
    {
        var original = new BackupConfig
        {
            BackupTargetDirectory = @"D:\UsbBackup",
            AutoBackupEnabled = false
        };

        var clone = original.Clone();

        Assert.Equal(original.BackupTargetDirectory, clone.BackupTargetDirectory);
        Assert.False(clone.AutoBackupEnabled);

        // 修改副本不能影响原对象（尤其是 List 必须深拷贝）
        clone.BackupTargetDirectory = @"E:\Other";
        clone.ExcludedExtensions.Add(".zzz");
        clone.AutoBackupEnabled = true;

        Assert.Equal(@"D:\UsbBackup", original.BackupTargetDirectory);
        Assert.False(original.AutoBackupEnabled);
        Assert.DoesNotContain(".zzz", original.ExcludedExtensions);
    }

    [Fact]
    public void BackupConfig_CreateDefaultExtensions_ReturnsFreshInstanceEachTime()
    {
        var first = BackupConfig.CreateDefaultExtensions();
        var second = BackupConfig.CreateDefaultExtensions();

        Assert.Equal(3, first.Count);
        first.Add(".extra");

        Assert.Equal(3, second.Count);
        Assert.Equal(new[] { ".tmp", ".part", ".crdownload" }, second);
    }

    [Fact]
    public void BackupConfig_CreateDefault_HasExpectedDefaults()
    {
        var config = BackupConfig.CreateDefault();

        Assert.Equal(string.Empty, config.BackupTargetDirectory);
        Assert.True(config.AutoBackupEnabled);
        Assert.Equal(3, config.ExcludedExtensions.Count);
    }
}
