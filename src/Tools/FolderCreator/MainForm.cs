using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinToolBox.Core;
using WinToolBox.Tools.FolderCreator.Models;

namespace WinToolBox.Tools.FolderCreator;

/// <summary>
/// FolderCreator 主窗口：按“短横线缩进”规则批量创建文件夹，并检查现有目录与规则是否一致。
/// </summary>
public partial class MainForm : Form
{
    /// <summary>「载入示例规则」填入的示例文本（与任务书示例一致）。</summary>
    private const string SampleRulesText =
        "# 这是一个注释行，以 # 开头将被忽略\r\n" +
        "根目录名称\r\n" +
        "\r\n" +
        "-一级目录\r\n" +
        "--二级目录\r\n" +
        "---三级目录\r\n" +
        "-一级目录2\r\n" +
        "--二级目录2";

    /// <summary>规则输入框的占位提示。</summary>
    private const string RulesPlaceholderText =
        "# 以 # 开头为注释行\r\n" +
        "-一级目录\r\n" +
        "--二级目录\r\n" +
        "---三级目录";

    /// <summary>弹窗中最多展示多少条解析错误。</summary>
    private const int MaxDisplayedErrors = 10;

    /// <summary>状态栏中路径的最大显示长度。</summary>
    private const int MaxStatusPathLength = 80;

    /// <summary>结果树节点数不超过该值时自动全部展开。</summary>
    private const int AutoExpandNodeLimit = 300;

    /// <summary>相对路径分隔符。</summary>
    private const char PathSeparator = '\\';

    private readonly Logger? _logger;
    private readonly INotifier? _notifier;
    private readonly RuleParser _parser;
    private readonly FolderGenerator _generator;
    private readonly FolderChecker _checker;

    /// <summary>上次浏览选择过的目录（只用实例字段记忆，不引入额外配置体系）。</summary>
    private string _lastSelectedDirectory = string.Empty;

    /// <summary>最近一次规则解析产生的警告，展示在状态栏与结果树的「提示」节点。</summary>
    private IReadOnlyList<string> _ruleWarnings = Array.Empty<string>();

    /// <summary>是否有耗时操作正在执行（执行期间禁用相关按钮）。</summary>
    private bool _isBusy;

    /// <summary>设计器需要的无参构造。</summary>
    public MainForm()
        : this(null, null)
    {
    }

    /// <summary>
    /// 创建主窗口。<paramref name="logger"/> 与 <paramref name="notifier"/> 允许为 null：
    /// 为空时只写日志/状态栏，不弹通知（本工具是普通窗口程序，没有托盘图标）。
    /// </summary>
    public MainForm(Logger? logger, INotifier? notifier)
    {
        _logger = logger;
        _notifier = notifier;

        InitializeComponent();

        _parser = new RuleParser(logger);
        _generator = new FolderGenerator(logger);
        _checker = new FolderChecker(logger);

        ApplyPreferredFont();
        txtRules.PlaceholderText = RulesPlaceholderText;
        SetStatus("就绪：请选择根目录并输入文件夹规则。");
    }

    // ---------------------------------------------------------------- 事件处理

    /// <summary>「浏览…」：选择根目录，并记住本次选择。</summary>
    private void OnBrowseClick(object? sender, EventArgs e)
    {
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "请选择根目录",
                ShowNewFolderButton = true,
                UseDescriptionForTitle = true
            };

            // 优先定位到文本框里的路径，其次是上次选择过的目录
            var typed = txtRoot.Text.Trim();
            if (Directory.Exists(typed))
            {
                dialog.SelectedPath = typed;
            }
            else if (Directory.Exists(_lastSelectedDirectory))
            {
                dialog.SelectedPath = _lastSelectedDirectory;
            }

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var selected = dialog.SelectedPath;
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            _lastSelectedDirectory = selected;
            txtRoot.Text = selected;
            SetStatus($"已选择根目录：{Shorten(selected)}");
            _logger?.Info($"FolderCreator 选择根目录：{selected}");
        }
        catch (Exception ex)
        {
            _logger?.Error("选择根目录失败。", ex);
            ShowError("选择根目录时发生错误：" + Environment.NewLine + ex.Message);
        }
    }

    /// <summary>「载入示例规则」：把任务书里的示例文本填入规则框。</summary>
    private void OnLoadSampleClick(object? sender, EventArgs e)
    {
        try
        {
            txtRules.Text = SampleRulesText;
            txtRules.SelectionStart = 0;
            txtRules.SelectionLength = 0;
            txtRules.Focus();
            SetStatus("已载入示例规则，可直接点击「创建文件夹」。");
        }
        catch (Exception ex)
        {
            _logger?.Error("载入示例规则失败。", ex);
            ShowError("载入示例规则时发生错误：" + Environment.NewLine + ex.Message);
        }
    }

    /// <summary>「创建文件夹」：后台批量创建，进度实时回填状态栏与进度条。</summary>
    private async void OnCreateClick(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!TryGetRootDirectory(out var root))
        {
            return;
        }

        var rules = TryParseRules();
        if (rules is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            SetProgress(0);
            SetStatus("正在创建文件夹…");
            _logger?.Info($"FolderCreator 开始创建文件夹：根目录={root}，规则数={rules.Count}");

            // GenerateAsync 内部已是 Task.Run；Progress 会自动回到 UI 线程
            var progress = new Progress<FolderProgress>(OnFolderProgress);
            var result = await _generator.GenerateAsync(root, rules, progress);

            // 窗口可能在等待期间被关闭
            if (!IsUiUsable)
            {
                return;
            }

            _lastSelectedDirectory = root;
            FillCreateTree(result, rules);
            SetProgress(result.Success ? 100 : 0);
            SetStatus(AppendWarningHint(result.Summary));

            if (result.Success)
            {
                _logger?.Info($"FolderCreator 创建完成：{result.Summary}");
            }
            else
            {
                _logger?.Warn($"FolderCreator 创建完成，但存在失败项：{result.Summary}");
                ShowWarning("部分文件夹创建失败：" + Environment.NewLine + Environment.NewLine + BuildFailureDetail(result));
            }

            _notifier?.ShowNotification("FolderCreator", result.Summary);
        }
        catch (Exception ex)
        {
            _logger?.Error("创建文件夹失败。", ex);
            SetStatus("创建文件夹失败：" + Shorten(ex.Message));
            ShowError("创建文件夹时发生错误：" + Environment.NewLine + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>「检查一致性」：在后台线程比对目标目录与规则。</summary>
    private async void OnCheckClick(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!TryGetRootDirectory(out var root))
        {
            return;
        }

        var rules = TryParseRules();
        if (rules is null)
        {
            return;
        }

        var mode = rdoStrict.Checked ? FolderCheckMode.Strict : FolderCheckMode.Loose;

        SetBusy(true);
        try
        {
            SetProgress(0);
            SetStatus("正在检查目录结构…");
            _logger?.Info($"FolderCreator 开始检查目录：根目录={root}，模式={mode}，规则数={rules.Count}");

            var result = await Task.Run(() => _checker.Check(root, rules, mode));

            // 窗口可能在等待期间被关闭
            if (!IsUiUsable)
            {
                return;
            }

            _lastSelectedDirectory = root;
            FillCheckTree(result, rules);
            SetProgress(100);

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                SetStatus("检查失败：" + Shorten(result.Error));
                _logger?.Warn($"FolderCreator 检查失败：{result.Error}");
                ShowWarning("检查目录结构失败：" + Environment.NewLine + result.Error);
                return;
            }

            SetStatus(AppendWarningHint(result.Summary));
            _logger?.Info($"FolderCreator 检查完成：{result.Summary}");
            _notifier?.ShowNotification("FolderCreator", result.Summary);
        }
        catch (Exception ex)
        {
            _logger?.Error("检查目录结构失败。", ex);
            SetStatus("检查目录结构失败：" + Shorten(ex.Message));
            ShowError("检查目录结构时发生错误：" + Environment.NewLine + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>关闭窗口：操作未完成时先确认，避免用户误关。</summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isBusy)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "当前还有操作尚未完成，确定要退出吗？",
            "FolderCreator",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            e.Cancel = true;
        }
    }

    // ---------------------------------------------------------------- 创建流程

    /// <summary>创建进度回调（由 <see cref="Progress{T}"/> 自动切回 UI 线程）。</summary>
    private void OnFolderProgress(FolderProgress progress)
    {
        if (progress is null)
        {
            return;
        }

        SetProgress((int)Math.Round(progress.Percent));
        SetStatus($"正在创建（{progress.Processed}/{progress.Total}）：{Shorten(progress.CurrentPath)}");
    }

    /// <summary>把创建结果按层级填进结果树：绿色=已创建，灰色=已存在，红色=失败。</summary>
    private void FillCreateTree(FolderGenerationResult result, IReadOnlyList<FolderRule> rules)
    {
        var itemMap = new Dictionary<string, FolderItemResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.Items)
        {
            itemMap[item.RelativePath] = item;
        }

        treeResult.BeginUpdate();
        try
        {
            treeResult.Nodes.Clear();

            var rootNode = new TreeNode($"根目录：{result.RootDirectory}")
            {
                Name = string.Empty,
                ToolTipText = result.RootDirectory
            };
            treeResult.Nodes.Add(rootNode);

            foreach (var rule in rules)
            {
                if (SplitRelativePath(rule.RelativePath).Length == 0)
                {
                    continue;
                }

                var node = EnsurePathNode(rootNode, rule.RelativePath);
                itemMap.TryGetValue(rule.RelativePath, out var item);
                ApplyCreateStatus(node, rule, item);
            }

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                rootNode.ToolTipText = result.Summary;
            }

            AppendWarningNode(rootNode);
            ExpandResultTree(rootNode);
        }
        finally
        {
            treeResult.EndUpdate();
        }
    }

    /// <summary>按单条创建结果给节点上色与加说明。</summary>
    private static void ApplyCreateStatus(TreeNode node, FolderRule rule, FolderItemResult? item)
    {
        var name = GetRuleName(rule);
        var autoText = rule.IsAutoCreated ? "（自动补齐）" : string.Empty;
        node.ToolTipText = rule.IsAutoCreated
            ? $"{rule.RelativePath}（父级缺失，已按「{FolderRule.AutoCreatedName}」自动补齐）"
            : rule.RelativePath;

        switch (item?.Status)
        {
            case FolderItemStatus.Created:
                node.ForeColor = Color.Green;
                node.Text = $"{name}（已创建）{autoText}";
                break;

            case FolderItemStatus.Existed:
                node.ForeColor = Color.Gray;
                node.Text = $"{name}（已存在，已跳过）{autoText}";
                break;

            case FolderItemStatus.Failed:
                node.ForeColor = Color.Red;
                node.Text = $"{name}（创建失败）{autoText}";

                var message = item?.Message;
                if (!string.IsNullOrWhiteSpace(message))
                {
                    node.Nodes.Add(new TreeNode("错误：" + message) { ForeColor = Color.Red });
                }

                break;

            default:
                // 正常流程不会走到这里：仅在生成结果缺少对应明细时兜底
                node.ForeColor = Color.Gray;
                node.Text = $"{name}（未返回结果）{autoText}";
                break;
        }
    }

    /// <summary>创建失败明细（弹窗用，最多展示前若干条）。</summary>
    private static string BuildFailureDetail(FolderGenerationResult result)
    {
        var failed = result.Items
            .Where(static item => item.Status == FolderItemStatus.Failed)
            .Take(MaxDisplayedErrors)
            .Select(static item => string.IsNullOrWhiteSpace(item.Message)
                ? "· " + item.RelativePath
                : $"· {item.RelativePath}：{item.Message}")
            .ToList();

        if (failed.Count == 0)
        {
            return result.Summary;
        }

        var text = string.Join(Environment.NewLine, failed);
        if (result.FailedCount > failed.Count)
        {
            text += Environment.NewLine + $"（仅显示前 {failed.Count} 条，共 {result.FailedCount} 条失败）";
        }

        return text;
    }

    // ---------------------------------------------------------------- 检查流程

    /// <summary>把检查结果按层级填进结果树：绿色=匹配，红色=缺失，黄褐色=多余。</summary>
    private void FillCheckTree(FolderCheckResult result, IReadOnlyList<FolderRule> rules)
    {
        var autoCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule.IsAutoCreated)
            {
                autoCreated.Add(rule.RelativePath);
            }
        }

        treeResult.BeginUpdate();
        try
        {
            treeResult.Nodes.Clear();

            var modeText = result.Mode == FolderCheckMode.Strict ? "严格模式" : "宽松模式";
            var rootNode = new TreeNode($"检查根目录：{result.RootDirectory}（{modeText}）")
            {
                Name = string.Empty,
                ToolTipText = result.RootDirectory
            };
            treeResult.Nodes.Add(rootNode);

            // 检查结果按深度升序返回，父级一定先于子级出现，因此多余节点能挂到对应父级下
            foreach (var item in result.Items)
            {
                if (SplitRelativePath(item.RelativePath).Length == 0)
                {
                    continue;
                }

                var node = EnsurePathNode(rootNode, item.RelativePath);
                ApplyCheckStatus(node, item, autoCreated.Contains(item.RelativePath));
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                rootNode.Nodes.Add(new TreeNode("检查失败：" + result.Error) { ForeColor = Color.Red });
            }

            AppendWarningNode(rootNode);
            ExpandResultTree(rootNode);
        }
        finally
        {
            treeResult.EndUpdate();
        }
    }

    /// <summary>按单条检查结果给节点上色与加说明。</summary>
    private static void ApplyCheckStatus(TreeNode node, FolderCheckItem item, bool isAutoCreated)
    {
        var name = GetLeafName(item.RelativePath);
        var autoText = isAutoCreated ? "（自动补齐）" : string.Empty;
        node.ToolTipText = isAutoCreated
            ? $"{item.DisplayText}（该层级由解析器自动补齐）"
            : item.DisplayText;

        switch (item.Status)
        {
            case FolderCheckStatus.Matched:
                node.ForeColor = Color.Green;
                node.Text = $"{name}（匹配）{autoText}";
                break;

            case FolderCheckStatus.Missing:
                node.ForeColor = Color.Red;
                node.Text = $"{name}（缺失）{autoText}";
                break;

            case FolderCheckStatus.Extra:
                node.ForeColor = Color.DarkGoldenrod;
                node.Text = $"{name}（多余）";
                break;

            default:
                node.Text = name + autoText;
                break;
        }
    }

    // ---------------------------------------------------------------- 校验与提示

    /// <summary>取根目录并做基本校验，为空或非法时给出提示并返回 false。</summary>
    private bool TryGetRootDirectory(out string root)
    {
        root = txtRoot.Text.Trim();

        if (root.Length == 0)
        {
            ShowWarning("请先选择根目录");
            txtRoot.Focus();
            return false;
        }

        try
        {
            if (Path.GetFullPath(root).IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                ShowWarning("根目录路径包含非法字符，请重新选择。");
                txtRoot.Focus();
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"根目录路径不合法：{root}", ex);
            ShowWarning("根目录路径不合法，请重新选择。" + Environment.NewLine + ex.Message);
            txtRoot.Focus();
            return false;
        }

        return true;
    }

    /// <summary>
    /// 解析规则文本：文本为空、解析失败或没有规则时给出友好提示并返回 null（调用方不再继续创建/检查）。
    /// </summary>
    private IReadOnlyList<FolderRule>? TryParseRules()
    {
        var text = txtRules.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowWarning("请输入文件夹规则");
            txtRules.Focus();
            return null;
        }

        RuleParseResult result;
        try
        {
            result = _parser.Parse(text);
        }
        catch (Exception ex)
        {
            _logger?.Error("解析文件夹规则时发生异常。", ex);
            SetStatus("规则解析失败：" + Shorten(ex.Message));
            ShowError("解析文件夹规则时发生错误：" + Environment.NewLine + ex.Message);
            return null;
        }

        if (!result.Success || result.Errors.Count > 0)
        {
            _logger?.Warn($"FolderCreator 规则解析失败：{result.Errors.Count} 处错误。");
            SetStatus($"规则解析失败：{result.Errors.Count} 处错误，未执行任何操作。");
            ShowError(BuildParseErrorDetail(result));
            return null;
        }

        if (result.Rules.Count == 0)
        {
            ShowWarning("没有解析到任何文件夹规则，请检查输入内容（行首用 - 表示层级）。");
            txtRules.Focus();
            return null;
        }

        _ruleWarnings = result.Warnings;
        return result.Rules;
    }

    /// <summary>解析错误详情（弹窗用，含行号，最多展示前若干条）。</summary>
    private static string BuildParseErrorDetail(RuleParseResult result)
    {
        var head = "规则存在错误，请修正后再试：" + Environment.NewLine + Environment.NewLine;

        if (result.Errors.Count == 0)
        {
            return head + result.Summary;
        }

        var lines = result.Errors
            .Take(MaxDisplayedErrors)
            .Select(static error => "· " + error);

        var text = head + string.Join(Environment.NewLine, lines);
        if (result.Errors.Count > MaxDisplayedErrors)
        {
            text += Environment.NewLine + $"（仅显示前 {MaxDisplayedErrors} 条，共 {result.Errors.Count} 条错误）";
        }

        return text;
    }

    private void ShowWarning(string message)
    {
        if (!IsUiUsable)
        {
            return;
        }

        MessageBox.Show(this, message, "FolderCreator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowError(string message)
    {
        if (!IsUiUsable)
        {
            return;
        }

        MessageBox.Show(this, message, "FolderCreator", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---------------------------------------------------------------- 结果树与状态栏

    /// <summary>按相对路径逐级建树（父级不存在时补占位节点），返回最末级节点。</summary>
    private static TreeNode EnsurePathNode(TreeNode rootNode, string relativePath)
    {
        var current = rootNode;
        var path = string.Empty;

        foreach (var segment in SplitRelativePath(relativePath))
        {
            // 关键：必须把当前层级名也拼进键。若只拼分隔符，同一父级下的兄弟节点会共用同一个键，
            // 后加入的节点会复用并覆盖前一个节点的文字与颜色（例如缺失项被多余项覆盖）。
            path = TreePath.Combine(path, segment);

            // 节点 Name 存完整相对路径，保证同一父级下能唯一定位
            var found = current.Nodes.Find(path, searchAllChildren: false);
            if (found.Length > 0)
            {
                current = found[0];
                continue;
            }

            var node = new TreeNode(segment) { Name = path };
            current.Nodes.Add(node);
            current = node;
        }

        return current;
    }

    /// <summary>把解析警告作为「提示」节点挂在结果树根部（仅在有警告时添加）。</summary>
    private void AppendWarningNode(TreeNode rootNode)
    {
        if (_ruleWarnings.Count == 0)
        {
            return;
        }

        var warningNode = new TreeNode($"提示（{_ruleWarnings.Count} 条）")
        {
            ToolTipText = "规则解析警告",
            ForeColor = Color.DarkOrange
        };

        foreach (var warning in _ruleWarnings)
        {
            warningNode.Nodes.Add(new TreeNode(warning) { ForeColor = Color.DarkOrange });
        }

        rootNode.Nodes.Insert(0, warningNode);
    }

    /// <summary>展开结果树：节点不多时全部展开，否则只展开根节点。</summary>
    private void ExpandResultTree(TreeNode rootNode)
    {
        rootNode.Expand();

        if (CountNodes(rootNode) <= AutoExpandNodeLimit)
        {
            treeResult.ExpandAll();
        }

        treeResult.SelectedNode = rootNode;
    }

    /// <summary>解析警告条数追加到摘要后面，提示用户查看结果树的「提示」节点。</summary>
    private string AppendWarningHint(string summary)
        => _ruleWarnings.Count == 0
            ? summary
            : $"{summary} 另有 {_ruleWarnings.Count} 条解析警告（见结果树「提示」节点）。";

    /// <summary>窗口是否仍然可用（后台操作期间窗口可能已被关闭）。</summary>
    private bool IsUiUsable => !IsDisposed && !Disposing;

    private void SetBusy(bool busy)
    {
        _isBusy = busy;

        if (!IsUiUsable)
        {
            return;
        }

        // 执行期间禁用会引发新操作的按钮；「载入示例规则」也一并禁用，避免中途改写规则
        btnCreate.Enabled = !busy;
        btnCheck.Enabled = !busy;
        btnBrowse.Enabled = !busy;
        btnSample.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text)
    {
        if (!IsUiUsable || lblStatus.IsDisposed)
        {
            return;
        }

        lblStatus.Text = text;
    }

    private void SetProgress(int percent)
    {
        if (!IsUiUsable || barProgress.IsDisposed)
        {
            return;
        }

        barProgress.Value = Math.Clamp(percent, barProgress.Minimum, barProgress.Maximum);
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>把相对路径拆成层级片段（同时兼容 \ 与 /，并去掉空白片段）。</summary>
    private static string[] SplitRelativePath(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? Array.Empty<string>()
            : relativePath.Split(
                new[] { PathSeparator, '/' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>取相对路径的最后一段作为节点显示名。</summary>
    private static string GetLeafName(string relativePath)
    {
        var segments = SplitRelativePath(relativePath);
        return segments.Length == 0 ? relativePath : segments[^1];
    }

    /// <summary>取规则对应的目录名（名称为空时用“未命名目录”）。</summary>
    private static string GetRuleName(FolderRule rule)
        => string.IsNullOrWhiteSpace(rule.Name) ? FolderRule.AutoCreatedName : rule.Name;

    /// <summary>统计以指定节点为根的子树节点数（含自身）。</summary>
    private static int CountNodes(TreeNode node)
    {
        var count = 1;
        foreach (TreeNode child in node.Nodes)
        {
            count += CountNodes(child);
        }

        return count;
    }

    /// <summary>状态栏里的长文本截断显示。</summary>
    private static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= MaxStatusPathLength
            ? text
            : "…" + text[^(MaxStatusPathLength - 1)..];
    }

    /// <summary>
    /// 设计器指定了「Microsoft YaHei UI」，极少数系统可能没有该字体；
    /// 这里探测一次，缺失时退回系统消息字体（新建实例，避免持有系统共享字体）。
    /// </summary>
    private void ApplyPreferredFont()
    {
        const string preferredFontName = "Microsoft YaHei UI";

        try
        {
            var probe = new Font(preferredFontName, 9F);
            if (string.Equals(probe.Name, preferredFontName, StringComparison.OrdinalIgnoreCase))
            {
                Font = probe;
                return;
            }

            probe.Dispose();

            var fallback = SystemFonts.MessageBoxFont;
            Font = new Font(fallback.FontFamily, fallback.Size);
            _logger?.Warn($"系统未安装字体「{preferredFontName}」，界面已退回 {fallback.Name}。");
        }
        catch (Exception ex)
        {
            _logger?.Warn("设置界面字体失败，沿用系统默认字体。", ex);
        }
    }
}
