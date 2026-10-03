using System.Globalization;
using System.Text;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;

namespace WinToolBox.Tools.FileMaster.Services;

/// <summary>
/// 批量移动 / 自动分类服务：按「扩展名 / 日期 / 首字母 / 兜底」规则表把源目录中的文件
/// 归类到目标目录的子目录中，支持移动或复制、覆盖控制与空目录清理。
/// </summary>
/// <remarks>
/// <para>规则文本每行一条，用 <c>|</c> 分成三列：<c>类型|参数|目标子目录</c>，<c>#</c> 开头为注释。</para>
/// <list type="bullet">
/// <item><c>扩展名|.jpg;.png|图片</c>：扩展名命中则归入「图片」。</item>
/// <item><c>日期|yyyy-MM|按日期</c>：按修改时间生成 <c>按日期\2026-10</c>。</item>
/// <item><c>首字母||按首字母</c>：生成 <c>按首字母\A</c>（非字母归入 <c>#</c>）。</item>
/// <item><c>其它||其它</c>：兜底规则，前面都不匹配时使用。</item>
/// </list>
/// <para>先「预览」后「执行」：<see cref="Apply"/> 只处理预览中可执行的条目。</para>
/// </remarks>
public sealed class AutoArchiverService
{
    /// <summary>非字母文件名的归类目录名。</summary>
    public const string OtherLetterFolder = "#";

    /// <summary>兜底规则的默认目标子目录名。</summary>
    public const string DefaultFallbackFolder = "其它";

    private readonly Logger? _logger;

    /// <summary>创建自动分类服务。</summary>
    public AutoArchiverService(Logger? logger = null) => _logger = logger;

    // ---------------------------------------------------------------- 规则解析

    /// <summary>解析规则文本（每行 <c>类型|参数|目标子目录</c>）。</summary>
    public static ArchiveRuleParseResult ParseRules(string? text)
    {
        var rules = new List<ArchiveRule>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ArchiveRuleParseResult
            {
                Rules = rules,
                Errors = new[] { "规则内容为空。" }
            };
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var fallbackCount = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            var lineNumber = index + 1;

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                errors.Add($"第 {lineNumber} 行：格式应为「类型|参数|目标子目录」。");
                continue;
            }

            var kindText = parts[0].Trim();
            var parameter = parts[1].Trim();
            var target = parts.Length > 2 ? parts[2].Trim() : string.Empty;

            var kind = kindText switch
            {
                "扩展名" => ArchiveRuleKind.Extension,
                "日期" => ArchiveRuleKind.Date,
                "首字母" => ArchiveRuleKind.FirstLetter,
                "其它" or "兜底" => ArchiveRuleKind.Fallback,
                _ => (ArchiveRuleKind?)null
            };

            if (kind is null)
            {
                errors.Add($"第 {lineNumber} 行：未知的规则类型「{kindText}」（可用：扩展名 / 日期 / 首字母 / 其它）。");
                continue;
            }

            if (kind == ArchiveRuleKind.Extension && parameter.Length == 0)
            {
                errors.Add($"第 {lineNumber} 行：扩展名规则必须填写扩展名（例如 .jpg;.png）。");
                continue;
            }

            if (kind == ArchiveRuleKind.Fallback)
            {
                fallbackCount++;
                if (fallbackCount > 1)
                {
                    errors.Add($"第 {lineNumber} 行：兜底规则（其它）只能出现一次。");
                    continue;
                }
            }

            if (kind == ArchiveRuleKind.Date && parameter.Length == 0)
            {
                errors.Add($"第 {lineNumber} 行：日期规则必须填写日期格式（例如 yyyy-MM）。");
                continue;
            }

            if (kind == ArchiveRuleKind.Date && !IsValidDateFormat(parameter))
            {
                errors.Add($"第 {lineNumber} 行：日期格式「{parameter}」不合法。");
                continue;
            }

            rules.Add(new ArchiveRule
            {
                Kind = kind.Value,
                Extensions = kind == ArchiveRuleKind.Extension ? parameter : string.Empty,
                DateFormat = kind == ArchiveRuleKind.Date ? parameter : "yyyy-MM",
                TargetSubDirectory = target,
                SourceText = line
            });
        }

        // 兜底规则永远排在最后匹配
        var ordered = rules
            .OrderBy(static rule => rule.Kind == ArchiveRuleKind.Fallback ? 1 : 0)
            .ToList();

        if (ordered.Count == 0 && errors.Count == 0)
        {
            errors.Add("没有解析到任何规则。");
        }

        return new ArchiveRuleParseResult { Rules = ordered, Errors = errors };
    }

    /// <summary>日期格式是否合法。</summary>
    private static bool IsValidDateFormat(string format)
    {
        try
        {
            _ = DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 目标目录计算

    /// <summary>
    /// 校验源目录与目标目录不能相同、也不能互相嵌套（P1-19）。
    /// </summary>
    /// <returns>合法时返回 null；非法时返回中文原因。</returns>
    /// <remarks>
    /// 用 <see cref="PathSafety"/> 的补分隔符前缀比较，而不是裸 <c>StartsWith</c>：
    /// 否则 <c>D:\database</c> 会被误判成 <c>D:\data</c> 的子目录而遭到误拒。
    /// </remarks>
    public static string? ValidateSourceAndTarget(string sourceDirectory, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return null;   // 空值由调用方的其它检查负责
        }

        try
        {
            var source = Path.GetFullPath(sourceDirectory);
            var target = Path.GetFullPath(targetDirectory);

            if (PathSafety.IsSamePath(source, target))
            {
                return "源目录与目标目录不能是同一个目录。";
            }

            if (PathSafety.IsChildPath(target, source))
            {
                return $"目标目录不能位于源目录内部（会导致重复运行时段落逐层自我归档）：" +
                       $"{Environment.NewLine}源目录：{source}{Environment.NewLine}目标目录：{target}";
            }

            if (PathSafety.IsChildPath(source, target))
            {
                return $"源目录不能位于目标目录内部（归档结果会被再次扫描）：" +
                       $"{Environment.NewLine}源目录：{source}{Environment.NewLine}目标目录：{target}";
            }

            return null;
        }
        catch (Exception)
        {
            // 路径非法：交给后面的逐文件校验给出更具体的提示
            return null;
        }
    }

    /// <summary>
    /// 纯函数：按规则计算某个文件应归入的子目录（相对目标根目录）。
    /// </summary>
    public static string ResolveTargetSubDirectory(ArchiveRule rule, string fileName, DateTime lastWriteTime)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        switch (rule.Kind)
        {
            case ArchiveRuleKind.Extension:
            {
                var extension = Path.GetExtension(fileName).TrimStart('.');
                var folder = rule.TargetSubDirectory.Length > 0 ? rule.TargetSubDirectory : extension;
                return string.IsNullOrEmpty(folder) ? "无扩展名" : folder;
            }

            case ArchiveRuleKind.Date:
            {
                var folder = lastWriteTime.ToString(
                    string.IsNullOrEmpty(rule.DateFormat) ? "yyyy-MM" : rule.DateFormat,
                    CultureInfo.InvariantCulture);

                return CombineRelative(rule.TargetSubDirectory, folder);
            }

            case ArchiveRuleKind.FirstLetter:
            {
                var first = fileName.Length > 0 ? fileName[0] : '#';
                var letter = char.IsLetter(first)
                    ? char.ToUpperInvariant(first).ToString()
                    : OtherLetterFolder;

                return CombineRelative(rule.TargetSubDirectory, letter);
            }

            default:
                return rule.TargetSubDirectory.Length > 0 ? rule.TargetSubDirectory : DefaultFallbackFolder;
        }
    }

    /// <summary>拼接相对子目录（父目录为空时只用子目录）。</summary>
    private static string CombineRelative(string parent, string child)
        => string.IsNullOrWhiteSpace(parent)
            ? child
            : Path.Combine(parent.Trim(), child);

    /// <summary>找出文件命中的第一条规则（扩展名规则需扩展名命中，其它规则总是命中）。</summary>
    public static ArchiveRule? MatchRule(IReadOnlyList<ArchiveRule> rules, string fileName)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var extension = Path.GetExtension(fileName);

        foreach (var rule in rules)
        {
            switch (rule.Kind)
            {
                case ArchiveRuleKind.Extension:
                    if (SplitExtensions(rule.Extensions).Contains(extension, StringComparer.OrdinalIgnoreCase))
                    {
                        return rule;
                    }

                    break;

                case ArchiveRuleKind.Fallback:
                    break;

                default:
                    // 日期 / 首字母规则对任何文件都适用
                    return rule;
            }
        }

        // 没有专门匹配的规则时使用兜底规则
        return rules.FirstOrDefault(static rule => rule.Kind == ArchiveRuleKind.Fallback);
    }

    /// <summary>把 <c>.jpg;.png</c> 拆成扩展名集合（自动补点、去空白）。</summary>
    public static IReadOnlyList<string> SplitExtensions(string? extensions)
    {
        if (string.IsNullOrWhiteSpace(extensions))
        {
            return Array.Empty<string>();
        }

        return extensions
            .Split(new[] { ';', ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static text => text.StartsWith('.') ? text : "." + text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>扫描源目录生成分类预览。</summary>
    public ArchivePlan BuildPlan(ArchiveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.SourceDirectory) || !Directory.Exists(options.SourceDirectory))
        {
            return new ArchivePlan { Error = "源目录不存在：" + options.SourceDirectory };
        }

        if (string.IsNullOrWhiteSpace(options.TargetDirectory))
        {
            return new ArchivePlan { Error = "目标目录不能为空。" };
        }

        if (options.Rules.Count == 0)
        {
            return new ArchivePlan { Error = "请至少配置一条分类规则。" };
        }

        // P1-19：目标目录与源目录互相嵌套会被「逐层自我归档」——
        // 每运行一次就把上一轮归档的结果再归一次，目录层级无限增长。
        // 必须在预览阶段就拒绝，而不是等到用户发现文件被搬来搬去。
        var containmentError = ValidateSourceAndTarget(options.SourceDirectory, options.TargetDirectory);
        if (containmentError is not null)
        {
            _logger?.Warn(containmentError);
            return new ArchivePlan { Error = containmentError };
        }

        var searchOption = options.IncludeSubDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var items = new List<ArchivePlanItem>();
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in Directory.EnumerateFiles(options.SourceDirectory, "*", searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(file);
                var rule = MatchRule(options.Rules, fileName);

                if (rule is null)
                {
                    items.Add(new ArchivePlanItem
                    {
                        SourcePath = file,
                        TargetPath = string.Empty,
                        RuleKind = ArchiveRuleKind.Fallback,
                        RuleDescription = "无匹配规则",
                        CanApply = false,
                        Message = "没有命中任何规则（可添加「其它」兜底规则）。"
                    });
                    continue;
                }

                DateTime lastWrite;
                try
                {
                    lastWrite = File.GetLastWriteTime(file);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"读取文件时间失败，已跳过：{file}", ex);
                    continue;
                }

                var subDirectory = ResolveTargetSubDirectory(rule, fileName, lastWrite);
                var description = DescribeRule(rule);

                // P1-12：规则第三列是自由文本，必须校验它解析后仍在目标根目录之内。
                // 否则 `..\..\Windows\Temp` 或绝对路径 `D:\elsewhere` 会把文件写到目标之外。
                if (!PathSafety.TryResolveInside(options.TargetDirectory, subDirectory, out var targetDirectoryFull))
                {
                    items.Add(new ArchivePlanItem
                    {
                        SourcePath = file,
                        TargetPath = string.Empty,
                        RuleKind = rule.Kind,
                        RuleDescription = description,
                        CanApply = false,
                        Message = $"规则的目标子目录「{subDirectory}」会写到目标目录之外，已拒绝。"
                    });

                    _logger?.Warn($"规则目标越界，已拒绝：规则「{description}」的子目录「{subDirectory}」→ 超出 {options.TargetDirectory}");

                    continue;
                }

                var targetPath = Path.Combine(targetDirectoryFull, fileName);

                var canApply = true;
                var message = string.Empty;

                // P1-19.3：源文件已经在目标根目录之内（且不是要把它搬到同一位置）时归档没有意义，
                // 只会把文件在目标目录里搬来搬去；这里按「是否位于目标根之下」完整判定，
                // 而不是只比「目标路径 == 源路径」。
                var sourceInsideTarget = PathSafety.IsChildPath(file, options.TargetDirectory);

                if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                {
                    canApply = false;
                    message = "目标与源文件相同（已在目标目录中）。";
                }
                else if (sourceInsideTarget)
                {
                    canApply = false;
                    message = "源文件已经位于目标目录之内，无需再次归档。";
                }
                else if (!plannedTargets.Add(targetPath))
                {
                    canApply = false;
                    message = "与本次预览中的其它文件目标重复。";
                }
                else if (File.Exists(targetPath) && !options.Overwrite)
                {
                    canApply = false;
                    message = "目标已存在同名文件（未启用覆盖）。";
                }

                items.Add(new ArchivePlanItem
                {
                    SourcePath = file,
                    TargetPath = targetPath,
                    RuleKind = rule.Kind,
                    RuleDescription = description,
                    CanApply = canApply,
                    Message = message
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error($"扫描源目录失败：{options.SourceDirectory}", ex);
            return new ArchivePlan { Items = items, Error = ex.Message };
        }

        var plan = new ArchivePlan { Items = items };
        _logger?.Info("自动分类预览：" + plan.Summary);
        return plan;
    }

    /// <summary>规则描述（界面显示用）。</summary>
    public static string DescribeRule(ArchiveRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Kind switch
        {
            ArchiveRuleKind.Extension => $"按扩展名（{rule.Extensions}）",
            ArchiveRuleKind.Date => $"按日期（{rule.DateFormat}）",
            ArchiveRuleKind.FirstLetter => "按首字母",
            _ => "兜底规则"
        };
    }

    // ---------------------------------------------------------------- 执行

    /// <summary>执行分类（移动或复制）。</summary>
    public ArchiveResult Apply(
        ArchivePlan plan,
        ArchiveOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        var candidates = plan.Items.Where(static item => item.CanApply).ToList();
        var results = new List<ArchiveResultItem>(candidates.Count);
        var processed = 0;

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var targetDirectory = Path.GetDirectoryName(item.TargetPath);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                if (options.Action == ArchiveAction.Move)
                {
                    File.Move(item.SourcePath, item.TargetPath, options.Overwrite);
                }
                else if (options.Overwrite && File.Exists(item.TargetPath))
                {
                    // 覆盖复制不能直接用 File.Copy(..., overwrite: true)：
                    // 它先截断目标，复制中途失败会留下「长度对、内容全 0」的坏文件，旧版本彻底丢失。
                    // 与 FolderSyncService 一致：先写 .tmp，成功后再原子替换。
                    FolderSyncService.CopyAtomically(item.SourcePath, item.TargetPath, _logger);
                }
                else
                {
                    File.Copy(item.SourcePath, item.TargetPath, options.Overwrite);
                }

                results.Add(new ArchiveResultItem
                {
                    SourcePath = item.SourcePath,
                    TargetPath = item.TargetPath,
                    Success = true
                });

                _logger?.Info($"已{(options.Action == ArchiveAction.Move ? "移动" : "复制")}：{item.SourcePath} -> {item.TargetPath}");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"分类失败：{item.SourcePath}", ex);
                results.Add(new ArchiveResultItem
                {
                    SourcePath = item.SourcePath,
                    TargetPath = item.TargetPath,
                    Success = false,
                    Error = ex.Message
                });
            }

            processed++;
            progress?.Report(new ArchiveProgress
            {
                Percent = candidates.Count == 0 ? 100 : processed * 100d / candidates.Count,
                Processed = processed,
                Total = candidates.Count,
                CurrentPath = item.SourcePath
            });
        }

        // 移动后清理源目录中的空文件夹（是否走回收站由 options.CleanEmptyFoldersUseRecycleBin 决定，
        // 默认放入回收站；不再硬编码永久删除）
        var removedEmptyFolders = 0;
        if (options.Action == ArchiveAction.Move && options.CleanEmptyFolders && Directory.Exists(options.SourceDirectory))
        {
            try
            {
                var cleaner = new EmptyFolderCleanerService(_logger);
                var scan = cleaner.Scan(options.SourceDirectory, includeSubDirectories: true, progress: null, cancellationToken);
                if (scan.Items.Count > 0)
                {
                    var deleted = cleaner.Delete(
                        scan.Items.Select(static item => item.FullPath),
                        options.CleanEmptyFoldersUseRecycleBin,
                        progress: null,
                        cancellationToken);

                    removedEmptyFolders = deleted.DeletedCount;

                    // 复查时发现已不再为空的目录会被跳过，如实记录，避免用户以为都清掉了
                    if (deleted.SkippedCount > 0)
                    {
                        _logger?.Warn($"有 {deleted.SkippedCount} 个目录在删除前复查时已不再为空，已跳过。");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn("清理空目录失败（不影响文件分类结果）。", ex);
            }
        }

        var result = new ArchiveResult { Items = results, RemovedEmptyFolderCount = removedEmptyFolders };
        _logger?.Info(result.Summary);
        return result;
    }

    /// <summary>生成分类结果的文本报告（便于日志与导出）。</summary>
    public static string BuildReport(ArchivePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        foreach (var item in plan.Items)
        {
            builder.Append(item.CanApply ? "[执行] " : "[跳过] ")
                .Append(item.SourcePath)
                .Append(" -> ")
                .Append(item.TargetPath.Length == 0 ? "（无目标）" : item.TargetPath);

            if (item.Message.Length > 0)
            {
                builder.Append("  // ").Append(item.Message);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
