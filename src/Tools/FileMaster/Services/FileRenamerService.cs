using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 批量重命名服务：支持文件 / 文件夹 / 两者，规则包括删除字符、查找替换（文本或正则）、
/// 前缀、后缀、序号、日期与统一扩展名。
/// </summary>
/// <remarks>
/// <para>规则执行顺序固定为：删除字符 → 查找替换 → 前缀 → 后缀 → 序号 → 日期 → 统一扩展名 → 清理非法字符。</para>
/// <para>默认保护扩展名（<see cref="RenameOptions.KeepExtension"/>）：只有文件名主体参与规则运算。</para>
/// <para>先「预览」后「执行」：<see cref="Apply"/> 只处理预览中标记为可执行的条目。</para>
/// </remarks>
public sealed class FileRenamerService
{
    /// <summary>序号位数允许的范围。</summary>
    public const int MinSequenceDigits = 1;

    /// <summary>序号位数上限。</summary>
    public const int MaxSequenceDigits = 10;

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private readonly Logger? _logger;

    /// <summary>创建重命名服务。</summary>
    public FileRenamerService(Logger? logger = null) => _logger = logger;

    // ---------------------------------------------------------------- 规则引擎

    /// <summary>
    /// 校验规则是否可用；返回错误说明，规则合法时返回 null。
    /// </summary>
    public static string? Validate(RenameOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.UseRegex && !string.IsNullOrEmpty(options.FindText))
        {
            try
            {
                _ = new Regex(options.FindText, RegexOptions.None, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                return "正则表达式不合法：" + ex.Message;
            }
        }

        if (options.SequenceStart is not null &&
            (options.SequenceDigits < MinSequenceDigits || options.SequenceDigits > MaxSequenceDigits))
        {
            return $"序号位数必须在 {MinSequenceDigits}-{MaxSequenceDigits} 之间。";
        }

        if (string.IsNullOrEmpty(options.FindText) &&
            string.IsNullOrEmpty(options.Prefix) &&
            string.IsNullOrEmpty(options.Suffix) &&
            string.IsNullOrEmpty(options.RemoveChars) &&
            options.SequenceStart is null &&
            string.IsNullOrEmpty(options.DateFormat) &&
            string.IsNullOrEmpty(options.NewExtension))
        {
            return "至少需要设置一条规则（删除字符 / 查找替换 / 前后缀 / 序号 / 日期 / 扩展名）。";
        }

        if (!string.IsNullOrEmpty(options.DateFormat))
        {
            try
            {
                _ = DateTime.Now.ToString(options.DateFormat, CultureInfo.InvariantCulture);
            }
            catch (FormatException ex)
            {
                return "日期格式不合法：" + ex.Message;
            }
        }

        return null;
    }

    /// <summary>
    /// 按规则计算新名称（纯函数，便于单元测试）。
    /// </summary>
    /// <param name="options">规则。</param>
    /// <param name="originalName">原名称（可含扩展名）。</param>
    /// <param name="sequenceIndex">序号下标（从 0 开始，最终序号 = <see cref="RenameOptions.SequenceStart"/> + 下标）。</param>
    /// <param name="creationTime">创建时间（日期规则取值的来源之一）。</param>
    /// <param name="lastWriteTime">修改时间（日期规则取值的来源之一）。</param>
    /// <returns>新名称（保留或替换后的扩展名）。</returns>
    public static string BuildNewName(
        RenameOptions options,
        string originalName,
        int sequenceIndex,
        DateTime creationTime,
        DateTime lastWriteTime)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(originalName);

        var extension = Path.GetExtension(originalName);
        var body = options.KeepExtension && extension.Length > 0
            ? originalName[..^extension.Length]
            : originalName;

        if (!options.KeepExtension)
        {
            extension = string.Empty;
        }

        // 1. 删除指定字符
        if (!string.IsNullOrEmpty(options.RemoveChars))
        {
            var removeSet = new HashSet<char>(options.RemoveChars);
            var builder = new StringBuilder(body.Length);
            foreach (var ch in body)
            {
                if (!removeSet.Contains(ch))
                {
                    builder.Append(ch);
                }
            }

            body = builder.ToString();
        }

        // 2. 查找替换
        if (!string.IsNullOrEmpty(options.FindText))
        {
            if (options.UseRegex)
            {
                var regex = new Regex(
                    options.FindText,
                    options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(2));
                body = regex.Replace(body, options.ReplaceText ?? string.Empty);
            }
            else
            {
                var comparison = options.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                body = body.Replace(options.FindText, options.ReplaceText ?? string.Empty, comparison);
            }
        }

        // 3/4. 前缀与后缀
        if (!string.IsNullOrEmpty(options.Prefix))
        {
            body = options.Prefix + body;
        }

        if (!string.IsNullOrEmpty(options.Suffix))
        {
            body += options.Suffix;
        }

        // 5. 序号
        if (options.SequenceStart is { } start)
        {
            var digits = Math.Clamp(options.SequenceDigits, MinSequenceDigits, MaxSequenceDigits);
            var text = (start + sequenceIndex).ToString("D" + digits, CultureInfo.InvariantCulture);
            body = InsertAffix(body, text, options.SequenceSeparator, options.SequencePosition);
        }

        // 6. 日期
        if (!string.IsNullOrEmpty(options.DateFormat))
        {
            var source = options.DateSource == RenameDateSource.CreationTime ? creationTime : lastWriteTime;
            var text = source.ToString(options.DateFormat, CultureInfo.InvariantCulture);
            body = InsertAffix(body, text, options.SequenceSeparator, options.DatePosition);
        }

        // 7. 扩展名
        var finalExtension = string.IsNullOrWhiteSpace(options.NewExtension)
            ? extension
            : NormalizeExtension(options.NewExtension);

        // 8. 清理：去掉非法字符、折叠首尾空白
        var finalBody = CleanName(body);
        if (finalBody.Length == 0)
        {
            return string.Empty;
        }

        return finalBody + finalExtension;
    }

    /// <summary>把文本拼到名称的前面或后面（自动补分隔符，避免出现空分隔符引起的双分隔）。</summary>
    private static string InsertAffix(string body, string text, string separator, RenameAffixPosition position)
    {
        if (text.Length == 0)
        {
            return body;
        }

        if (position == RenameAffixPosition.Prefix)
        {
            return body.Length == 0 ? text : text + separator + body;
        }

        return body.Length == 0 ? text : body + separator + text;
    }

    /// <summary>把扩展名规范化为 <c>.ext</c> 形式。</summary>
    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    /// <summary>去掉非法文件名字符与首尾空白。</summary>
    private static string CleanName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(InvalidNameChars, ch) >= 0 ? '_' : ch);
        }

        // 目录分隔符不是非法文件名字符，但会造成路径穿越，这里一并处理
        var cleaned = builder.ToString()
            .Replace('\\', '_')
            .Replace('/', '_')
            .Trim();

        return cleaned.TrimEnd('.');
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>
    /// 扫描目录（或目录列表）生成重命名预览。文件先处理，文件夹按「深度从深到浅」处理，
    /// 保证重命名父目录时其内部文件已经处理完毕。
    /// </summary>
    public RenamePlan BuildPlanFromDirectory(
        string directory,
        RenameOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(directory))
        {
            return new RenamePlan { Error = "目录不能为空。" };
        }

        if (!Directory.Exists(directory))
        {
            return new RenamePlan { Error = "目录不存在：" + directory };
        }

        var paths = new List<string>();
        var searchOption = options.IncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            if (options.Target is RenameTarget.Files or RenameTarget.Both)
            {
                paths.AddRange(Directory.EnumerateFiles(directory, "*", searchOption));
            }

            if (options.Target is RenameTarget.Folders or RenameTarget.Both)
            {
                paths.AddRange(Directory.EnumerateDirectories(directory, "*", searchOption));
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"扫描目录失败：{directory}", ex);
            return new RenamePlan { Error = ex.Message };
        }

        return BuildPlan(paths, options, cancellationToken);
    }

    /// <summary>按显式路径列表生成重命名预览（路径不存在时忽略）。</summary>
    public RenamePlan BuildPlan(
        IEnumerable<string> paths,
        RenameOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        var ruleError = Validate(options);
        if (ruleError is not null)
        {
            return new RenamePlan { Error = ruleError };
        }

        var entries = new List<Entry>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    if (options.Target is RenameTarget.Folders)
                    {
                        continue;
                    }

                    var info = new FileInfo(path);
                    entries.Add(new Entry(path, RenameItemKind.File, info.CreationTime, info.LastWriteTime, 0));
                }
                else if (Directory.Exists(path))
                {
                    if (options.Target is RenameTarget.Files)
                    {
                        continue;
                    }

                    var info = new DirectoryInfo(path);
                    entries.Add(new Entry(
                        path,
                        RenameItemKind.Folder,
                        info.CreationTime,
                        info.LastWriteTime,
                        CountDepth(path)));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"读取条目信息失败，已跳过：{path}", ex);
            }
        }

        // 排序：文件在前（按名称或创建时间），文件夹在后且「越深越先处理」
        var ordered = entries
            .OrderBy(static entry => entry.Kind == RenameItemKind.File ? 0 : 1)
            .ThenBy(static entry => entry.Kind == RenameItemKind.Folder ? -entry.Depth : 0)
            .ThenBy(entry => options.OrderByCreationTime ? entry.CreationTime.Ticks : 0)
            .ThenBy(static entry => Path.GetFileName(entry.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<RenamePlanItem>(ordered.Count);
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = ordered[index];
            var originalName = Path.GetFileName(entry.Path);

            string newName;
            try
            {
                newName = BuildNewName(options, originalName, index, entry.CreationTime, entry.LastWriteTime);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"规则计算失败：{entry.Path}", ex);
                items.Add(new RenamePlanItem
                {
                    SourcePath = entry.Path,
                    OriginalName = originalName,
                    NewName = string.Empty,
                    TargetPath = string.Empty,
                    Kind = entry.Kind,
                    Status = RenameItemStatus.Invalid,
                    Message = "规则计算失败：" + ex.Message
                });
                continue;
            }

            var directory = Path.GetDirectoryName(entry.Path) ?? string.Empty;
            var targetPath = newName.Length == 0 ? string.Empty : Path.Combine(directory, newName);

            var status = RenameItemStatus.Ready;
            var message = string.Empty;

            if (newName.Length == 0)
            {
                status = RenameItemStatus.Invalid;
                message = "规则生成的名称是空字符串。";
            }
            else if (string.Equals(newName, originalName, StringComparison.Ordinal))
            {
                status = RenameItemStatus.Unchanged;
                message = "名称没有变化。";
            }
            else if (!plannedTargets.Add(targetPath))
            {
                status = RenameItemStatus.Conflict;
                message = "与本次预览中的其它条目标名称重复。";
            }
            else if (Exists(targetPath, entry.Kind))
            {
                status = RenameItemStatus.Conflict;
                message = "目标名称已存在。";
            }

            items.Add(new RenamePlanItem
            {
                SourcePath = entry.Path,
                OriginalName = originalName,
                NewName = newName,
                TargetPath = targetPath,
                Kind = entry.Kind,
                Status = status,
                Message = message
            });
        }

        _logger?.Info($"重命名预览完成：共 {items.Count} 项，可执行 {items.Count(static i => i.Status == RenameItemStatus.Ready)} 项。");
        return new RenamePlan { Items = items };
    }

    // ---------------------------------------------------------------- 执行

    /// <summary>执行预览中标记为可重命名的条目。</summary>
    public RenameResult Apply(
        RenamePlan plan,
        IProgress<RenameProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var candidates = plan.Items.Where(static item => item.CanApply).ToList();
        var results = new List<RenameResultItem>(candidates.Count);
        var processed = 0;

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (item.Kind == RenameItemKind.Folder)
                {
                    if (Directory.Exists(item.SourcePath))
                    {
                        Directory.Move(item.SourcePath, item.TargetPath);
                    }
                    else
                    {
                        throw new DirectoryNotFoundException("源目录不存在：" + item.SourcePath);
                    }
                }
                else
                {
                    if (File.Exists(item.SourcePath))
                    {
                        File.Move(item.SourcePath, item.TargetPath);
                    }
                    else
                    {
                        throw new FileNotFoundException("源文件不存在：" + item.SourcePath);
                    }
                }

                results.Add(new RenameResultItem
                {
                    SourcePath = item.SourcePath,
                    TargetPath = item.TargetPath,
                    Success = true
                });

                _logger?.Info($"已重命名：{item.SourcePath} -> {item.TargetPath}");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"重命名失败：{item.SourcePath}", ex);
                results.Add(new RenameResultItem
                {
                    SourcePath = item.SourcePath,
                    TargetPath = item.TargetPath,
                    Success = false,
                    Error = ex.Message
                });
            }

            processed++;
            progress?.Report(new RenameProgress
            {
                Percent = candidates.Count == 0 ? 100 : processed * 100d / candidates.Count,
                Processed = processed,
                Total = candidates.Count,
                CurrentPath = item.SourcePath
            });
        }

        var result = new RenameResult { Items = results };
        _logger?.Info("批量重命名结束：" + result.Summary);
        return result;
    }

    /// <summary>目标是否已被占用（文件与目录分别判断）。</summary>
    private static bool Exists(string path, RenameItemKind kind)
        => kind == RenameItemKind.Folder ? Directory.Exists(path) : File.Exists(path);

    /// <summary>计算路径深度（用于让子目录先于父目录重命名）。</summary>
    private static int CountDepth(string path)
        => path.Count(static ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);

    /// <summary>内部使用的扫描条目。</summary>
    private readonly record struct Entry(
        string Path,
        RenameItemKind Kind,
        DateTime CreationTime,
        DateTime LastWriteTime,
        int Depth);
}
