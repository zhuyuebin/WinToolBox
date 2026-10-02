using System.Text;
using System.Text.Json;

namespace WinToolBox.Core.Services;

/// <summary>
/// FileMaster 规则模板管理：读写 <c>%AppData%\WinToolBox\FileMaster\templates.json</c>。
/// </summary>
/// <remarks>
/// <para>数据结构是「模板名 -&gt; 规则文本」的字典，模板名不区分大小写。</para>
/// <para>读取永不抛异常：文件缺失或内容损坏时退回内置模板（损坏的文件不会被自动覆盖）。</para>
/// <para>保存采用「先写 .tmp 再替换」，避免写入中途失败损坏模板库。</para>
/// </remarks>
public sealed class TemplateManager
{
    /// <summary>内置「Web 项目」模板名。</summary>
    public const string WebProjectTemplateName = "Web 项目";

    /// <summary>内置「Python 项目」模板名。</summary>
    public const string PythonProjectTemplateName = "Python 项目";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _gate = new();
    private readonly Logger? _logger;
    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>使用默认模板文件路径。</summary>
    public TemplateManager()
        : this(AppPaths.FileMasterTemplatesFile, null)
    {
    }

    /// <summary>使用指定模板文件路径（测试可注入临时文件）。</summary>
    public TemplateManager(string templatesFilePath)
        : this(templatesFilePath, null)
    {
    }

    /// <summary>使用指定模板文件路径与日志记录器。</summary>
    public TemplateManager(string templatesFilePath, Logger? logger)
    {
        if (string.IsNullOrWhiteSpace(templatesFilePath))
        {
            throw new ArgumentException("模板文件路径不能为空", nameof(templatesFilePath));
        }

        FilePath = Path.GetFullPath(templatesFilePath);
        _logger = logger;

        Reload();
    }

    /// <summary>默认模板文件完整路径。</summary>
    public static string DefaultFilePath => AppPaths.FileMasterTemplatesFile;

    /// <summary>
    /// 一次性迁移：把旧版 <c>%AppData%\WinToolBox\FolderCreator\templates.json</c> 复制到新的 FileMaster 目录。
    /// 仅当「新文件不存在且旧文件存在」时执行，失败不抛异常（迁移失败不影响程序启动）。
    /// </summary>
    /// <returns>发生迁移返回 true，否则返回 false。</returns>
    public static bool MigrateLegacyTemplates(Logger? logger = null)
    {
        var target = DefaultFilePath;
        var legacy = AppPaths.LegacyFileMasterTemplatesFile;

        try
        {
            if (File.Exists(target) || !File.Exists(legacy))
            {
                return false;
            }

            AppPaths.EnsureDirectory(Path.GetDirectoryName(target));
            File.Copy(legacy, target, overwrite: false);
            logger?.Info($"已把旧版模板迁移到 FileMaster 目录：{legacy} -> {target}");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn($"迁移旧版模板失败（不影响使用）：{legacy} -> {target}", ex);
            return false;
        }
    }

    /// <summary>当前使用的模板文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>模板文件所在目录。</summary>
    public string DirectoryPath => Path.GetDirectoryName(FilePath) ?? ".";

    /// <summary>模板文件是否已存在。</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>当前模板数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _templates.Count;
            }
        }
    }

    /// <summary>内置模板（模板名 -&gt; 规则文本），首次运行会写入模板文件。</summary>
    public static IReadOnlyDictionary<string, string> CreateBuiltInTemplates()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [WebProjectTemplateName] =
                "# Web 项目骨架\r\n" +
                "-src\r\n" +
                "--assets\r\n" +
                "--components\r\n" +
                "--pages\r\n" +
                "--styles\r\n" +
                "-public\r\n" +
                "--images\r\n" +
                "-tests\r\n" +
                "-docs",

            [PythonProjectTemplateName] =
                "# Python 项目骨架\r\n" +
                "-src\r\n" +
                "--package\r\n" +
                "-tests\r\n" +
                "-docs\r\n" +
                "-scripts\r\n" +
                "-.github\r\n" +
                "--workflows"
        };

    /// <summary>获取全部模板名：内置模板在前，其余按名称（序数、忽略大小写）排序。</summary>
    public IReadOnlyList<string> GetTemplateNames()
    {
        lock (_gate)
        {
            var builtInOrder = CreateBuiltInTemplates().Keys.ToList();
            var names = new List<string>();

            foreach (var name in builtInOrder)
            {
                if (_templates.ContainsKey(name))
                {
                    names.Add(name);
                }
            }

            names.AddRange(_templates.Keys
                .Where(name => !builtInOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

            return names;
        }
    }

    /// <summary>是否存在指定名称的模板。</summary>
    public bool Contains(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        lock (_gate)
        {
            return _templates.ContainsKey(name.Trim());
        }
    }

    /// <summary>获取模板规则文本；不存在时返回 null。</summary>
    public string? GetTemplate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (_gate)
        {
            return _templates.TryGetValue(name.Trim(), out var rules) ? rules : null;
        }
    }

    /// <summary>尝试获取模板规则文本。</summary>
    public bool TryGetTemplate(string? name, out string rules)
    {
        rules = GetTemplate(name) ?? string.Empty;
        return rules.Length > 0;
    }

    /// <summary>当前全部模板的快照（模板名 -&gt; 规则文本）。</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_templates, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 保存模板（同名则覆盖）并立即落盘。
    /// </summary>
    /// <returns>true 表示新建，false 表示覆盖了已有模板。</returns>
    public bool SaveTemplate(string name, string rules)
    {
        var trimmedName = NormalizeName(name);
        ArgumentNullException.ThrowIfNull(rules);

        bool isNew;
        lock (_gate)
        {
            isNew = !_templates.ContainsKey(trimmedName);
            _templates[trimmedName] = rules;
        }

        Persist();
        _logger?.Info($"FolderCreator 模板已保存：{trimmedName}（{(isNew ? "新建" : "覆盖")}）");
        return isNew;
    }

    /// <summary>删除模板并立即落盘。</summary>
    /// <returns>true 表示确实删除了模板。</returns>
    public bool DeleteTemplate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        bool removed;
        lock (_gate)
        {
            removed = _templates.Remove(name.Trim());
        }

        if (!removed)
        {
            return false;
        }

        Persist();
        _logger?.Info($"FolderCreator 模板已删除：{name.Trim()}");
        return true;
    }

    /// <summary>
    /// 补齐缺失的内置模板并确保模板文件已落盘（首次运行时写入；同名模板不会被覆盖）。
    /// </summary>
    /// <returns>本次在内存中新增的内置模板数量（模板文件缺失时会额外落盘一次）。</returns>
    public int EnsureDefaults()
    {
        var builtIns = CreateBuiltInTemplates();
        var added = 0;

        lock (_gate)
        {
            foreach (var (name, rules) in builtIns)
            {
                if (_templates.ContainsKey(name))
                {
                    continue;
                }

                _templates[name] = rules;
                added++;
            }
        }

        // 文件不存在时也要写一次：把内置模板真正落到磁盘（首次运行），
        // 这样用户下次打开就能在模板文件里看到并直接编辑它们。
        if (added > 0 || !Exists)
        {
            Persist();
            _logger?.Info($"FolderCreator 已写入内置模板（新增 {added} 个）：{FilePath}");
        }

        return added;
    }

    /// <summary>从磁盘重新加载模板；文件缺失或损坏时退回内置模板。</summary>
    public void Reload()
    {
        Dictionary<string, string> loaded;

        try
        {
            if (!File.Exists(FilePath))
            {
                loaded = new Dictionary<string, string>(CreateBuiltInTemplates(), StringComparer.OrdinalIgnoreCase);
                _logger?.Info($"模板文件不存在，使用内置模板：{FilePath}");
            }
            else
            {
                var json = File.ReadAllText(FilePath);
                loaded = ParseJson(json);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"读取模板失败，使用内置模板：{FilePath}", ex);
            loaded = new Dictionary<string, string>(CreateBuiltInTemplates(), StringComparer.OrdinalIgnoreCase);
        }

        lock (_gate)
        {
            _templates.Clear();
            foreach (var (name, rules) in loaded)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _templates[name.Trim()] = rules ?? string.Empty;
                }
            }
        }
    }

    /// <summary>解析模板 JSON；损坏或类型不符时退回内置模板。</summary>
    private Dictionary<string, string> ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger?.Warn($"模板文件内容为空，使用内置模板：{FilePath}");
            return new Dictionary<string, string>(CreateBuiltInTemplates(), StringComparer.OrdinalIgnoreCase);
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions);
        if (parsed is null || parsed.Count == 0)
        {
            _logger?.Warn($"模板文件无法解析出模板，使用内置模板：{FilePath}");
            return new Dictionary<string, string>(CreateBuiltInTemplates(), StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rules) in parsed)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                result[name.Trim()] = rules ?? string.Empty;
            }
        }

        return result.Count > 0
            ? result
            : new Dictionary<string, string>(CreateBuiltInTemplates(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把当前模板写回磁盘（原子替换）。</summary>
    private void Persist()
    {
        Dictionary<string, string> snapshot;
        lock (_gate)
        {
            snapshot = new Dictionary<string, string>(_templates, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            AppPaths.EnsureDirectory(DirectoryPath);

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var tempFile = FilePath + ".tmp";

            File.WriteAllText(tempFile, json, new UTF8Encoding(false));
            File.Move(tempFile, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 模板保存失败不应让调用方崩溃（模板仍在内存中可用）
            _logger?.Warn($"保存模板失败：{FilePath}", ex);
        }
    }

    /// <summary>模板名规范化：去首尾空白并校验非空。</summary>
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("模板名不能为空", nameof(name));
        }

        return name.Trim();
    }
}
