using System.Text.Json;
using WinToolBox.Core;
using WinToolBox.Core.Services;

namespace WinToolBox.Core.Tests;

/// <summary>FolderCreator 规则模板管理（<see cref="TemplateManager"/>）的单元测试。</summary>
public class TemplateManagerTests
{
    private static string TemplatesPath(TempWorkspace ws) => Path.Combine(ws.Root, "FolderCreator", "templates.json");

    [Fact]
    public void Constructor_WhenPathIsNullOrWhitespace_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new TemplateManager(" "));
        Assert.Throws<ArgumentException>(() => new TemplateManager(string.Empty));
    }

    [Fact]
    public void Constructor_WhenFileMissing_SeedsBuiltInTemplatesInMemory()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        Assert.False(manager.Exists);
        Assert.Equal(2, manager.Count);
        Assert.True(manager.Contains(TemplateManager.WebProjectTemplateName));
        Assert.True(manager.Contains(TemplateManager.PythonProjectTemplateName));
    }

    [Fact]
    public void FilePath_IsAlwaysAbsolutePath()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(Path.Combine("relative", "templates.json"));

        Assert.True(Path.IsPathFullyQualified(manager.FilePath));
    }

    [Fact]
    public void DefaultFilePath_PointsToFileMasterTemplatesJson()
    {
        Assert.EndsWith(
            Path.Combine("WinToolBox", "FileMaster", "templates.json"),
            TemplateManager.DefaultFilePath);
    }

    [Fact]
    public void EnsureDefaults_WhenFileMissing_WritesBuiltInTemplatesToDisk()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);

        var manager = new TemplateManager(path);

        // 内置模板在构造时已进入内存，因此本次新增数为 0，但文件必须被创建
        Assert.Equal(0, manager.EnsureDefaults());
        Assert.True(File.Exists(path));

        // 落盘内容是「模板名 -> 规则文本」字典
        var json = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Contains("-src", parsed[TemplateManager.WebProjectTemplateName]);
    }

    [Fact]
    public void EnsureDefaults_WhenCalledTwice_SecondCallWritesNothingNew()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);
        var manager = new TemplateManager(path);

        Assert.Equal(0, manager.EnsureDefaults());
        var firstWrite = File.ReadAllText(path);

        Assert.Equal(0, manager.EnsureDefaults());
        Assert.Equal(firstWrite, File.ReadAllText(path));
        Assert.Equal(2, manager.Count);
    }

    [Fact]
    public void EnsureDefaults_DoesNotOverwriteUserTemplateWithBuiltInName()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // 用户已把「Web 项目」改成自己的内容，只缺少「Python 项目」
        File.WriteAllText(path, """{ "Web 项目": "-自定义内容" }""");

        var manager = new TemplateManager(path);
        var added = manager.EnsureDefaults();

        Assert.Equal(1, added);
        Assert.Equal("-自定义内容", manager.GetTemplate(TemplateManager.WebProjectTemplateName));
        Assert.True(manager.Contains(TemplateManager.PythonProjectTemplateName));
    }

    [Fact]
    public void SaveTemplate_NewName_ReturnsTrueAndPersists()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);
        var manager = new TemplateManager(path);

        var isNew = manager.SaveTemplate("我的模板", "-a\r\n--b");

        Assert.True(isNew);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        var reloaded = new TemplateManager(path);
        Assert.Equal("-a\r\n--b", reloaded.GetTemplate("我的模板"));
    }

    /// <summary>
    /// 同名保存会覆盖，并返回「保存成功」——返回值语义已从「是否新建」改为「是否成功落盘」，
    /// 以便界面能如实提示保存失败（P1-5）。
    /// </summary>
    [Fact]
    public void SaveTemplate_ExistingName_ReturnsTrueAndOverwrites()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        Assert.True(manager.SaveTemplate("模板A", "-旧"));
        var saved = manager.SaveTemplate("模板A", "-新");

        Assert.True(saved, "覆盖保存成功时也必须返回 true");
        Assert.Equal("-新", manager.GetTemplate("模板A"));
        Assert.Equal(3, manager.Count);

        // 覆盖后确实落盘了
        var reloaded = new TemplateManager(TemplatesPath(ws));
        Assert.Equal("-新", reloaded.GetTemplate("模板A"));
    }

    [Fact]
    public void SaveTemplate_EmptyName_ThrowsArgumentException()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        Assert.Throws<ArgumentException>(() => manager.SaveTemplate("   ", "-a"));
    }

    [Fact]
    public void SaveTemplate_CreatesMissingDirectory()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "深层", "目录", "templates.json");
        var manager = new TemplateManager(path);

        manager.SaveTemplate("模板", "-a");

        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveTemplateThenReloadFromNewInstance_RoundTripsChineseContent()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);

        new TemplateManager(path).SaveTemplate("中文模板", "# 注释\r\n-一级目录\r\n--二级目录");

        var reloaded = new TemplateManager(path);
        Assert.Equal("# 注释\r\n-一级目录\r\n--二级目录", reloaded.GetTemplate("中文模板"));
    }

    [Fact]
    public void DeleteTemplate_Existing_ReturnsTrueAndPersists()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);
        var manager = new TemplateManager(path);

        manager.SaveTemplate("待删除", "-a");
        Assert.True(manager.DeleteTemplate("待删除"));
        Assert.False(manager.Contains("待删除"));

        var reloaded = new TemplateManager(path);
        Assert.False(reloaded.Contains("待删除"));
    }

    [Fact]
    public void DeleteTemplate_MissingOrBlank_ReturnsFalse()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        Assert.False(manager.DeleteTemplate("不存在"));
        Assert.False(manager.DeleteTemplate(null));
        Assert.False(manager.DeleteTemplate("  "));
    }

    [Fact]
    public void GetTemplateNames_BuiltInsComeFirstThenUserTemplatesSorted()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        manager.SaveTemplate("zeta", "-z");
        manager.SaveTemplate("Alpha", "-a");

        var names = manager.GetTemplateNames();

        Assert.Equal(
            new[]
            {
                TemplateManager.WebProjectTemplateName,
                TemplateManager.PythonProjectTemplateName,
                "Alpha",
                "zeta"
            },
            names);
    }

    [Fact]
    public void GetTemplate_IsCaseInsensitiveAndTrimsName()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        manager.SaveTemplate("MyTemplate", "-a");

        Assert.Equal("-a", manager.GetTemplate("mytemplate"));
        Assert.Equal("-a", manager.GetTemplate("  MYTEMPLATE  "));
        Assert.Equal("-a", manager.GetTemplate("MyTemplate"));
    }

    [Fact]
    public void GetTemplate_Missing_ReturnsNullAndTryGetReturnsFalse()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        Assert.Null(manager.GetTemplate("没有这个"));
        Assert.Null(manager.GetTemplate(null));
        Assert.False(manager.TryGetTemplate("没有这个", out _));
    }

    [Fact]
    public void Reload_WhenJsonCorrupted_FallsBackToBuiltInsAndKeepsBrokenFile()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ 这不是合法 JSON");

        var manager = new TemplateManager(path);

        Assert.Equal(2, manager.Count);
        Assert.True(manager.Contains(TemplateManager.WebProjectTemplateName));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Reload_WhenJsonIsEmptyOrEmptyObject_FallsBackToBuiltIns()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "   ");
        Assert.Equal(2, new TemplateManager(path).Count);

        File.WriteAllText(path, "{}");
        Assert.Equal(2, new TemplateManager(path).Count);
    }

    [Fact]
    public void Reload_WhenJsonHasBlankKeys_DropsThem()
    {
        using var ws = new TempWorkspace();
        var path = TemplatesPath(ws);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "  ": "-空", "有效": "-a" }""");

        var manager = new TemplateManager(path);

        Assert.Equal(1, manager.Count);
        Assert.True(manager.Contains("有效"));
    }

    [Fact]
    public void Snapshot_IsIndependentCopy()
    {
        using var ws = new TempWorkspace();
        var manager = new TemplateManager(TemplatesPath(ws));

        var snapshot = manager.Snapshot();
        manager.SaveTemplate("新增", "-a");

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(3, manager.Count);
    }

    [Fact]
    public void BuiltInTemplates_AreValidRuleTexts()
    {
        var builtIns = TemplateManager.CreateBuiltInTemplates();

        Assert.Equal(2, builtIns.Count);
        foreach (var (name, rules) in builtIns)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Contains("-", rules, StringComparison.Ordinal);
            Assert.All(
                rules.Split("\r\n", StringSplitOptions.RemoveEmptyEntries),
                line => Assert.True(
                    line.StartsWith('#') || line.StartsWith('-'),
                    $"内置模板「{name}」出现既不是注释也不是规则的行：{line}"));
        }
    }
}
