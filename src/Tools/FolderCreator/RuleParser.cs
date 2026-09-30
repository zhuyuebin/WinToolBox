using WinToolBox.Core;
using WinToolBox.Tools.FolderCreator.Models;

namespace WinToolBox.Tools.FolderCreator;

/// <summary>
/// 规则解析器：把“短横线缩进”文本解析成文件夹层级规则。
/// </summary>
/// <remarks>
/// 解析规则：
/// <list type="number">
/// <item>行首连续的 <c>-</c> 数量代表层级深度（1 个为第一级，2 个为第二级，依此类推）。</item>
/// <item>短横线后允许有空格，也允许直接跟目录名，前导空格会被去掉。</item>
/// <item>以 <c>#</c> 开头的行强制视为注释；行首没有 <c>-</c> 的普通行同样被忽略（可当作根目录提示）。</item>
/// <item>缺少上级目录时自动补齐为 <c>未命名目录</c>，并给出警告，保证创建逻辑始终正确。</item>
/// <item>目录名含 Windows 非法字符时记录错误并跳过该行，由界面提示用户。</item>
/// </list>
/// </remarks>
public sealed class RuleParser
{
    /// <summary>允许的最大层级深度。</summary>
    public const int MaxDepth = 32;

    /// <summary>单个目录名允许的最大长度。</summary>
    public const int MaxNameLength = 255;

    private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly string[] ReservedNames = BuildReservedNames();

    private readonly Logger? _logger;

    /// <summary>创建解析器。</summary>
    public RuleParser(Logger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 解析规则文本。该方法不会因为内容问题抛异常，问题会体现在
    /// <see cref="RuleParseResult.Errors"/> / <see cref="RuleParseResult.Warnings"/> 中。
    /// </summary>
    public RuleParseResult Parse(string? text)
    {
        var rules = new List<FolderRule>();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new RuleParseResult
            {
                Success = true,
                Rules = rules,
                Errors = errors,
                Warnings = warnings
            };
        }

        // 以 \r\n / \n / \r 统一拆行
        var lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // depth -> 该深度最近一条规则，用于拼装相对路径
        var lastAtDepth = new Dictionary<int, FolderRule>();

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var lineNumber = index + 1;
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            // 以 # 开头的行强制视为注释
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            // 行首连续的 '-' 数量即层级
            var dashCount = 0;
            while (dashCount < raw.Length && raw[dashCount] == '-')
            {
                dashCount++;
            }

            if (dashCount == 0)
            {
                // 行首没有 '-' 的行不参与创建；若只是“缩进后才出现 -”，额外提醒一次，避免用户困惑
                if (trimmed.StartsWith('-'))
                {
                    warnings.Add($"第 {lineNumber} 行：该行以空格或制表符开头，未参与解析（层级请直接写在行首）。");
                }

                continue;
            }

            // 只去掉短横线后的“前导”空格：尾随空格属于目录名的一部分，会被下方的名称校验拦下
            var name = raw[dashCount..].TrimStart();

            if (name.Length == 0)
            {
                errors.Add($"第 {lineNumber} 行：短横线后缺少文件夹名称。");
                continue;
            }

            if (dashCount > MaxDepth)
            {
                errors.Add($"第 {lineNumber} 行：层级为 {dashCount} 级，超过上限 {MaxDepth} 级。");
                continue;
            }

            if (!IsValidFolderName(name, out var nameError))
            {
                errors.Add($"第 {lineNumber} 行：{nameError}");
                continue;
            }

            // 父级自动补齐：缺少哪一级就补哪一级（固定名称“未命名目录”）
            for (var depth = 1; depth < dashCount; depth++)
            {
                if (lastAtDepth.ContainsKey(depth))
                {
                    continue;
                }

                var autoRule = new FolderRule
                {
                    Name = FolderRule.AutoCreatedName,
                    Depth = depth,
                    LineNumber = lineNumber,
                    RawLine = raw,
                    RelativePath = BuildRelativePath(depth, FolderRule.AutoCreatedName, lastAtDepth),
                    IsAutoCreated = true
                };

                lastAtDepth[depth] = autoRule;
                rules.Add(autoRule);
                warnings.Add($"第 {lineNumber} 行：缺少第 {depth} 级父目录，已自动补齐为“{FolderRule.AutoCreatedName}”。");
            }

            var relativePath = BuildRelativePath(dashCount, name, lastAtDepth);

            if (!seenPaths.Add(relativePath))
            {
                warnings.Add($"第 {lineNumber} 行：规则“{relativePath}”重复，已忽略。");
                continue;
            }

            var rule = new FolderRule
            {
                Name = name,
                Depth = dashCount,
                LineNumber = lineNumber,
                RawLine = raw,
                RelativePath = relativePath,
                IsAutoCreated = false
            };

            lastAtDepth[dashCount] = rule;

            // 丢弃更深层的历史记录，避免后面把子目录挂到错误的父级上
            foreach (var deeper in lastAtDepth.Keys.Where(d => d > dashCount).ToList())
            {
                lastAtDepth.Remove(deeper);
            }

            rules.Add(rule);
        }

        _logger?.Info($"规则解析完成：{rules.Count} 条规则，{errors.Count} 个错误，{warnings.Count} 个警告。");

        return new RuleParseResult
        {
            Success = errors.Count == 0,
            Rules = rules,
            Errors = errors,
            Warnings = warnings
        };
    }

    /// <summary>
    /// 校验单个目录名是否符合 Windows 命名要求。
    /// </summary>
    /// <param name="name">目录名。</param>
    /// <param name="error">不合法时的中文原因；合法时为 null。</param>
    public static bool IsValidFolderName(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "文件夹名称不能为空。";
            return false;
        }

        // 只去掉前导空白：尾随空格在 Windows 下不合法，必须保留到后面的校验里
        var value = name.TrimStart();

        var invalidIndex = value.IndexOfAny(InvalidNameChars);
        if (invalidIndex >= 0)
        {
            error = $"文件夹名称“{value}”包含非法字符“{value[invalidIndex]}”（不允许 \\ / : * ? \" < > |）。";
            return false;
        }

        if (value.Any(char.IsControl))
        {
            error = $"文件夹名称“{value}”包含控制字符。";
            return false;
        }

        if (value.Length > MaxNameLength)
        {
            error = $"文件夹名称超过 {MaxNameLength} 个字符。";
            return false;
        }

        if (value.EndsWith('.') || value.EndsWith(' '))
        {
            error = $"文件夹名称“{value}”不能以点或空格结尾。";
            return false;
        }

        if (value is "." or "..")
        {
            error = "文件夹名称不能是“.”或“..”。";
            return false;
        }

        var stem = GetNameStem(value);
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            error = $"“{value}”是 Windows 保留设备名，不能用作文件夹名称。";
            return false;
        }

        return true;
    }

    /// <summary>取目录名中第一个点之前的部分，用于保留设备名判断（CON.txt 同样是保留名）。</summary>
    private static string GetNameStem(string value)
    {
        var dotIndex = value.IndexOf('.');
        return dotIndex > 0 ? value[..dotIndex] : value;
    }

    /// <summary>拼装相对路径：父级路径 + 分隔符 + 当前名称。</summary>
    private static string BuildRelativePath(int depth, string name, Dictionary<int, FolderRule> lastAtDepth)
    {
        if (depth <= 1)
        {
            return name;
        }

        return lastAtDepth.TryGetValue(depth - 1, out var parent)
            ? parent.RelativePath + Path.DirectorySeparatorChar + name
            : name;
    }

    private static string[] BuildReservedNames()
    {
        var names = new List<string> { "CON", "PRN", "AUX", "NUL" };

        for (var i = 1; i <= 9; i++)
        {
            names.Add("COM" + i);
            names.Add("LPT" + i);
        }

        return names.ToArray();
    }
}
