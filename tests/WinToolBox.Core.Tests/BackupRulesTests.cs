using WinToolBox.Core;

namespace WinToolBox.Core.Tests;

/// <summary>
/// BackupRules 单元测试：唯一标识、设备目录名、目标目录拼装、名称清洗与同卷判断。
/// 全部为纯字符串路径逻辑，不访问任何真实盘符，也不插拔真实 U 盘。
/// </summary>
public sealed class BackupRulesTests
{
    /// <summary>构造一个用于测试的设备信息。</summary>
    private static UsbDeviceInfo Device(string label = "KINGSTON", string serial = "1234ABCD", long totalSize = 32L * 1024 * 1024 * 1024)
        => new()
        {
            RootPath = @"E:\",
            VolumeLabel = label,
            VolumeSerialNumber = serial,
            FileSystem = "FAT32",
            TotalSize = totalSize,
            FreeSpace = 8L * 1024 * 1024 * 1024
        };

    // ------------------------------------------------------------------
    // BuildUniqueId
    // ------------------------------------------------------------------

    [Fact]
    public void BuildUniqueId_CombinesLabelSerialAndCapacity()
    {
        var id = BackupRules.BuildUniqueId("KINGSTON", "1234ABCD", 32L * 1024 * 1024 * 1024);

        Assert.Equal("KINGSTON_1234ABCD_34359738368", id);
    }

    [Fact]
    public void BuildUniqueId_DoesNotDependOnDriveLetter()
    {
        // 盘符会变化，唯一标识里不能出现盘符
        var device = Device();

        Assert.Equal("KINGSTON_1234ABCD_34359738368", device.UniqueId);
        Assert.DoesNotContain("E:", device.UniqueId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildUniqueId_WhenLabelMissing_UsesNoLabelPlaceholder(string? label)
    {
        var id = BackupRules.BuildUniqueId(label, "1234ABCD", 1024);

        Assert.Equal($"NOLABEL_1234ABCD_1024", id);
        Assert.StartsWith("NOLABEL_", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildUniqueId_WhenSerialMissing_UsesZeroSerialPlaceholder(string? serial)
    {
        var id = BackupRules.BuildUniqueId("KINGSTON", serial, 1024);

        Assert.Equal("KINGSTON_00000000_1024", id);
    }

    [Fact]
    public void BuildUniqueId_WhenBothMissing_UsesBothPlaceholders()
    {
        var id = BackupRules.BuildUniqueId(null, null, 0);

        Assert.Equal("NOLABEL_00000000_0", id);
    }

    [Fact]
    public void BuildUniqueId_TrimsSurroundingWhitespace()
    {
        var id = BackupRules.BuildUniqueId("  KINGSTON  ", "  1234ABCD  ", 2048);

        Assert.Equal("KINGSTON_1234ABCD_2048", id);
    }

    [Fact]
    public void BuildUniqueId_DifferentCapacityProducesDifferentId()
    {
        var small = BackupRules.BuildUniqueId("KINGSTON", "1234ABCD", 16L * 1024 * 1024 * 1024);
        var large = BackupRules.BuildUniqueId("KINGSTON", "1234ABCD", 64L * 1024 * 1024 * 1024);

        Assert.NotEqual(small, large);
    }

    // ------------------------------------------------------------------
    // BuildDeviceFolderName
    // ------------------------------------------------------------------

    [Fact]
    public void BuildDeviceFolderName_ProducesLabelUnderscoreSerial()
    {
        var name = BackupRules.BuildDeviceFolderName(Device());

        Assert.Equal("KINGSTON_1234ABCD", name);
    }

    [Fact]
    public void BuildDeviceFolderName_WhenLabelMissing_UsesNoLabel()
    {
        Assert.Equal("NOLABEL_1234ABCD", BackupRules.BuildDeviceFolderName("", "1234ABCD"));
        Assert.Equal("NOLABEL_00000000", BackupRules.BuildDeviceFolderName(null, null));
    }

    [Fact]
    public void BuildDeviceFolderName_ReplacesInvalidPathCharacters()
    {
        var name = BackupRules.BuildDeviceFolderName("My:USB*Disk?", "1234ABCD");

        // 非法字符被替换为下划线；末尾由 '?' 产生的下划线会被 SanitizeName 去掉，保证目录名干净
        Assert.Equal("My_USB_Disk_1234ABCD", name);
        Assert.Equal(name, name.Trim());
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("*", name);
        Assert.DoesNotContain("?", name);
    }

    [Fact]
    public void BuildDeviceFolderName_WhenDeviceIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => BackupRules.BuildDeviceFolderName((UsbDeviceInfo)null!));
    }

    // ------------------------------------------------------------------
    // 目录结构（产品语义：固定目录 + 真增量 + 软删除历史，绝不带日期）
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCurrentDirectory_IsRootLabelSerialCurrent_WithoutDate()
    {
        var device = Device();

        var current = BackupRules.BuildCurrentDirectory(@"D:\UsbBackupRoot", device);

        Assert.Equal(
            Path.Combine(@"D:\UsbBackupRoot", "KINGSTON_1234ABCD", "current"),
            current);
    }

    [Fact]
    public void BuildCurrentDirectory_NeverContainsDate()
    {
        var device = Device();

        var current = BackupRules.BuildCurrentDirectory(@"D:\UsbBackupRoot", device);

        // 产品语义已明确：整个 U 盘只对应一个备份根目录，不再使用 {yyyy-MM-dd} 作为目标目录
        Assert.DoesNotContain(DateTime.Now.ToString("yyyy-MM-dd"), current);
        Assert.DoesNotContain(DateTime.UtcNow.ToString("yyyy-MM-dd"), current);
        Assert.EndsWith("current", current);
    }

    [Fact]
    public void BuildTargetDirectory_EqualsCurrentDirectory_ForBackwardCompatibility()
    {
        var device = Device();

        Assert.Equal(
            BackupRules.BuildCurrentDirectory(@"D:\root", device),
            BackupRules.BuildTargetDirectory(@"D:\root", device));
    }

    [Fact]
    public void BuildDeviceRootDirectory_IsRootLabelSerial()
    {
        Assert.Equal(
            Path.Combine(@"D:\root", "KINGSTON_1234ABCD"),
            BackupRules.BuildDeviceRootDirectory(@"D:\root", Device()));
    }

    [Fact]
    public void BuildHistoryDirectory_UsesYyyyMmDdUnderHistory()
    {
        var device = Device();
        var date = new DateTime(2026, 3, 9, 23, 59, 59);

        var history = BackupRules.BuildHistoryDirectory(@"D:\root", device, date);

        Assert.Equal(
            Path.Combine(@"D:\root", "KINGSTON_1234ABCD", "history", "2026-03-09"),
            history);
    }

    [Fact]
    public void BuildManifestPath_IsUnderDeviceRoot()
    {
        Assert.Equal(
            Path.Combine(@"D:\root", "KINGSTON_1234ABCD", "manifest.json"),
            BackupRules.BuildManifestPath(@"D:\root", Device()));
    }

    [Theory]
    [InlineData("2026-09-30", 2026, 9, 30)]
    [InlineData("2026-01-02", 2026, 1, 2)]
    public void TryParseHistoryFolderName_ParsesValidDates(string folder, int year, int month, int day)
    {
        var parsed = BackupRules.TryParseHistoryFolderName(folder);

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(year, month, day), parsed!.Value);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("2026-9-3")]
    [InlineData("not-a-date")]
    [InlineData("")]
    public void TryParseHistoryFolderName_ReturnsNullForNonDateFolders(string folder)
    {
        // 非日期目录（用户自己放进去的）必须被忽略，清理时绝不能误删
        Assert.Null(BackupRules.TryParseHistoryFolderName(folder));
    }

    [Fact]
    public void FormatHistoryFolderName_ZeroPadsMonthAndDay()
    {
        Assert.Equal("2026-09-03", BackupRules.FormatHistoryFolderName(new DateTime(2026, 9, 3)));
    }

    [Fact]
    public void BuildCurrentDirectory_WhenRootIsRelative_ReturnsAbsolutePath()
    {
        var current = BackupRules.BuildCurrentDirectory("RelativeRoot", Device());

        Assert.True(Path.IsPathFullyQualified(current));
        Assert.EndsWith(Path.Combine("RelativeRoot", "KINGSTON_1234ABCD", "current"), current);
    }

    [Fact]
    public void BuildCurrentDirectory_WhenRootHasTrailingSeparator_DoesNotDoubleSeparator()
    {
        var device = Device();

        var withSeparator = BackupRules.BuildCurrentDirectory(@"D:\UsbBackupRoot\", device);
        var withoutSeparator = BackupRules.BuildCurrentDirectory(@"D:\UsbBackupRoot", device);

        Assert.Equal(withoutSeparator, withSeparator);
        Assert.DoesNotContain(@"UsbBackupRoot" + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar, withSeparator);
    }

    [Fact]
    public void BuildCurrentDirectory_WhenRootOrDeviceInvalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => BackupRules.BuildCurrentDirectory("", Device()));
        Assert.Throws<ArgumentException>(() => BackupRules.BuildCurrentDirectory("   ", Device()));
        Assert.Throws<ArgumentNullException>(() => BackupRules.BuildCurrentDirectory(@"D:\root", null!));
    }

    [Fact]
    public void BuildCurrentDirectory_UsesUnknownPlaceholdersForBlankLabelAndSerial()
    {
        var device = new UsbDeviceInfo
        {
            RootPath = @"E:\",
            VolumeLabel = "   ",
            VolumeSerialNumber = "",
            TotalSize = 1024
        };

        var current = BackupRules.BuildCurrentDirectory(@"D:\root", device);

        Assert.EndsWith(
            Path.Combine(BackupRules.UnknownLabel + "_" + BackupRules.UnknownSerial, "current"),
            current);
    }

    // ------------------------------------------------------------------
    // SanitizeName
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void SanitizeName_WhenEmpty_ReturnsEmptyString(string? input)
    {
        Assert.Equal(string.Empty, BackupRules.SanitizeName(input));
    }

    [Fact]
    public void SanitizeName_KeepsValidCharacters()
    {
        Assert.Equal("USB_Backup-2026", BackupRules.SanitizeName("USB_Backup-2026"));
        Assert.Equal("我的U盘", BackupRules.SanitizeName("  我的U盘  "));
    }

    [Fact]
    public void SanitizeName_ReplacesInvalidCharactersWithUnderscore()
    {
        Assert.Equal("a_b_c", BackupRules.SanitizeName(@"a\b:c"));
        Assert.Equal("x_y", BackupRules.SanitizeName("x|y"));
    }

    [Fact]
    public void SanitizeName_TrimsLeadingAndTrailingDotsAndUnderscores()
    {
        // 结果会去除首尾的空格、点和下划线，避免 Windows 目录名歧义
        Assert.Equal("data", BackupRules.SanitizeName("...data..."));
        Assert.Equal("name", BackupRules.SanitizeName("_name_"));
        Assert.Equal("both", BackupRules.SanitizeName(" ._both_. "));
    }

    [Fact]
    public void SanitizeName_WhenAllCharactersInvalid_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, BackupRules.SanitizeName(":::"));
        Assert.Equal(string.Empty, BackupRules.SanitizeName("***"));
    }

    [Fact]
    public void SanitizeName_WhenTooLong_TruncatesTo64Characters()
    {
        var input = new string('中', 100);

        var result = BackupRules.SanitizeName(input);

        Assert.Equal(64, result.Length);
        Assert.Equal(input[..64], result);
    }

    [Fact]
    public void SanitizeName_TruncatesLongInputWithInvalidCharacters()
    {
        var input = "label:" + new string('x', 200);

        var result = BackupRules.SanitizeName(input);

        Assert.Equal(64, result.Length);
        Assert.StartsWith("label_", result);
    }

    // ------------------------------------------------------------------
    // IsTargetOnSameVolume
    // ------------------------------------------------------------------

    [Fact]
    public void IsTargetOnSameVolume_ForSameDriveLetter_ReturnsTrue()
    {
        // 只做字符串比较，不访问真实盘符
        Assert.True(BackupRules.IsTargetOnSameVolume(@"E:\", @"E:\UsbBackup"));
        Assert.True(BackupRules.IsTargetOnSameVolume(@"E:\", @"e:\"));
        Assert.True(BackupRules.IsTargetOnSameVolume(@"D:\photos", @"D:\一段路径\备份"));
    }

    [Fact]
    public void IsTargetOnSameVolume_ForDifferentDriveLetter_ReturnsFalse()
    {
        Assert.False(BackupRules.IsTargetOnSameVolume(@"E:\", @"D:\一段路径"));
        Assert.False(BackupRules.IsTargetOnSameVolume(@"D:\UsbBackupRoot", @"E:\UsbBackupRoot"));
    }

    [Theory]
    [InlineData(null, @"D:\target")]
    [InlineData("", @"D:\target")]
    [InlineData("   ", @"D:\target")]
    [InlineData(@"E:\", null)]
    [InlineData(@"E:\", "")]
    [InlineData(@"E:\", "   ")]
    public void IsTargetOnSameVolume_WhenArgumentsEmpty_ReturnsFalse(string? source, string? target)
    {
        Assert.False(BackupRules.IsTargetOnSameVolume(source!, target!));
    }

    [Fact]
    public void IsTargetOnSameVolume_WhenPathIsInvalid_ReturnsFalseWithoutThrowing()
    {
        // 非法路径不能抛异常，只能保守地返回 false
        // 注意：'|' 在 .NET Core 的 Path 处理中并不算非法字符，这里用内嵌空字符（\0）触发真正无效的路径
        Assert.False(BackupRules.IsTargetOnSameVolume("\0invalid", @"D:\target"));
    }

    // ------------------------------------------------------------------
    // 默认排除规则暴露
    // ------------------------------------------------------------------

    [Fact]
    public void DefaultExclusions_ContainSafetyBaseline()
    {
        Assert.Contains("System Volume Information", BackupRules.DefaultExcludedDirectoryNames);
        Assert.Contains("$RECYCLE.BIN", BackupRules.DefaultExcludedDirectoryNames);
        Assert.Contains("autorun.inf", BackupRules.DefaultExcludedFileNames);
        Assert.Contains(".tmp", BackupRules.DefaultExcludedExtensions);
    }

    [Fact]
    public void CreateDefaultExcludeRules_IncludesConfiguredExtraExtensions()
    {
        var rules = BackupRules.CreateDefaultExcludeRules(new[] { ".bak" });

        Assert.True(rules.IsExcludedFile("backup.bak"));
        Assert.True(rules.IsExcludedFile("autorun.inf"));
        Assert.True(rules.IsExcludedDirectory("System Volume Information"));
        Assert.True(rules.IsExcludedDirectory(@"$RECYCLE.BIN\"));
        Assert.False(rules.IsExcludedFile("keep.txt"));
    }
}
