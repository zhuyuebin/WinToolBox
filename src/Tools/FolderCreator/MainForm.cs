using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinToolBox.Core;
using WinToolBox.Core.Services;
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
    private readonly TemplateManager _templateManager;
    private readonly RuleGenerator _ruleGenerator;
    private readonly FolderCreatorService _folderService;

    /// <summary>当前后台操作的取消源（同一时刻只有一个操作，窗口关闭时取消）。</summary>
    private CancellationTokenSource? _operationCts;

    /// <summary>最近一次成功的检查结果（供「导出检查报告」使用）。</summary>
    private FolderCheckResult? _lastCheckResult;

    /// <summary>最近一次检查的时间（报告里显示的检查时间）。</summary>
    private DateTimeOffset? _lastCheckTime;

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
        _templateManager = new TemplateManager(AppPaths.FolderCreatorTemplatesFile, logger);
        _ruleGenerator = new RuleGenerator(logger);
        _folderService = new FolderCreatorService(logger);

        // 首次运行把内置模板写入 %AppData%\WinToolBox\FolderCreator\templates.json
        _templateManager.EnsureDefaults();

        ApplyPreferredFont();
        txtRules.PlaceholderText = RulesPlaceholderText;
        SetupLogPanel();
        ReloadTemplateList();
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

    // ---------------------------------------------------------------- 模板与目录反向

    /// <summary>「模板」下拉框：选中模板后把规则文本填入规则框（覆盖前先确认）。</summary>
    private void OnTemplateSelected(object? sender, EventArgs e)
    {
        try
        {
            var name = (cboTemplate.SelectedItem as string ?? cboTemplate.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return;
            }

            var rules = _templateManager.GetTemplate(name);
            if (rules is null || string.Equals(txtRules.Text, rules, StringComparison.Ordinal))
            {
                return;
            }

            if (txtRules.Text.Trim().Length > 0 &&
                MessageBox.Show(
                    this,
                    $"用模板「{name}」覆盖当前规则内容吗？",
                    "FolderCreator",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            txtRules.Text = rules;
            SetStatus($"已载入模板：{name}");
            _logger?.Info($"FolderCreator 载入模板：{name}");
        }
        catch (Exception ex)
        {
            _logger?.Error("载入模板失败。", ex);
            ShowError("载入模板时发生错误：" + Environment.NewLine + ex.Message);
        }
    }

    /// <summary>
    /// 「保存为模板」：弹出名称输入框（默认填入下拉框当前内容），
    /// 输入新名字即新建模板，输入已有名字则询问是否覆盖。
    /// </summary>
    private void OnSaveTemplateClick(object? sender, EventArgs e)
    {
        try
        {
            if (txtRules.Text.Trim().Length == 0)
            {
                ShowWarning("规则内容为空，无法保存为模板。");
                txtRules.Focus();
                return;
            }

            var initialName = (cboTemplate.Text ?? string.Empty).Trim();

            string name;
            using (var dialog = new TemplateNameDialog("保存为模板", initialName, _templateManager.GetTemplateNames()))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    SetStatus("已取消保存模板。");
                    return;
                }

                name = dialog.TemplateName;
            }

            if (name.Length == 0)
            {
                ShowWarning("模板名不能为空。");
                return;
            }

            if (_templateManager.Contains(name) &&
                MessageBox.Show(
                    this,
                    $"模板「{name}」已存在，是否覆盖？",
                    "FolderCreator",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                SetStatus("已取消保存模板。");
                return;
            }

            var isNew = _templateManager.SaveTemplate(name, txtRules.Text);
            ReloadTemplateList(name);
            SetStatus($"模板已保存：{name}（{(isNew ? "新建" : "覆盖")}）");
            _logger?.Info($"FolderCreator 保存模板：{name}（{(isNew ? "新建" : "覆盖")}）");
        }
        catch (Exception ex)
        {
            _logger?.Error("保存模板失败。", ex);
            ShowError("保存模板时发生错误：" + Environment.NewLine + ex.Message);
        }
    }

    /// <summary>「删除模板」：删除下拉框中当前模板（内置模板同样可删除）。</summary>
    private void OnDeleteTemplateClick(object? sender, EventArgs e)
    {
        try
        {
            var name = (cboTemplate.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                ShowWarning("请先选择要删除的模板。");
                cboTemplate.Focus();
                return;
            }

            if (!_templateManager.Contains(name))
            {
                ShowWarning($"模板「{name}」不存在。");
                return;
            }

            if (MessageBox.Show(
                    this,
                    $"确定删除模板「{name}」吗？该操作不可撤销。",
                    "FolderCreator",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            _templateManager.DeleteTemplate(name);
            ReloadTemplateList();
            SetStatus($"模板已删除：{name}");
        }
        catch (Exception ex)
        {
            _logger?.Error("删除模板失败。", ex);
            ShowError("删除模板时发生错误：" + Environment.NewLine + ex.Message);
        }
    }

    /// <summary>「从现有目录生成规则」：后台扫描根目录，生成短横线规则填入规则框。</summary>
    private async void OnGenerateFromDirClick(object? sender, EventArgs e)
    {
        if (_isBusy || !TryGetRootDirectory(out var root))
        {
            return;
        }

        if (txtRules.Text.Trim().Length > 0 &&
            MessageBox.Show(
                this,
                "生成规则会覆盖当前规则内容，是否继续？",
                "FolderCreator",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            SetProgress(0);
            SetStatus("正在扫描目录生成规则…");
            _logger?.Info($"FolderCreator 开始反向生成规则：{root}");

            var cts = BeginOperation();
            var text = await Task.Run(
                () => _ruleGenerator.GenerateRuleFromDirectory(root, null, cts.Token),
                cts.Token);

            if (!IsUiUsable)
            {
                return;
            }

            txtRules.Text = text;
            SetProgress(100);

            var count = text.Length == 0
                ? 0
                : text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            SetStatus(count == 0
                ? "该目录下没有可用的子目录（默认已排除 .git、node_modules、bin、obj、.vs）。"
                : $"已生成规则：{count} 个目录。");

            _logger?.Info($"FolderCreator 反向生成规则完成：{root}，目录 {count} 个");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消生成规则。");
        }
        catch (Exception ex)
        {
            _logger?.Error("反向生成规则失败。", ex);
            SetStatus("生成规则失败：" + Shorten(ex.Message));
            ShowError("从现有目录生成规则时发生错误：" + Environment.NewLine + ex.Message);
        }
        finally
        {
            EndOperation();
            SetBusy(false);
        }
    }

    /// <summary>「导出目录树」：把根目录的 tree 文本写入用户选择的文件。</summary>
    private async void OnExportTreeClick(object? sender, EventArgs e)
    {
        if (_isBusy || !TryGetRootDirectory(out var root))
        {
            return;
        }

        string path;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出目录树",
                Filter = "文本文件 (*.txt)|*.txt|Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
                FileName = "目录树.txt",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            path = dialog.FileName;
        }
        catch (Exception ex)
        {
            _logger?.Error("打开保存对话框失败。", ex);
            ShowError("打开保存对话框时发生错误：" + Environment.NewLine + ex.Message);
            return;
        }

        SetBusy(true);
        try
        {
            SetProgress(0);
            SetStatus("正在导出目录树…");

            var cts = BeginOperation();
            var text = await Task.Run(
                () => _ruleGenerator.GenerateTreeText(root, null, includeFiles: false, cts.Token),
                cts.Token);

            // 带 BOM 的 UTF-8：Windows 记事本可直接识别（含 ├─ 等制表符）
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true), cts.Token);

            if (!IsUiUsable)
            {
                return;
            }

            SetProgress(100);
            SetStatus($"目录树已导出：{Shorten(path)}");
            _logger?.Info($"FolderCreator 导出目录树：{root} -> {path}");
            _notifier?.ShowNotification("FolderCreator", "目录树已导出。");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消导出目录树。");
        }
        catch (Exception ex)
        {
            _logger?.Error("导出目录树失败。", ex);
            SetStatus("导出目录树失败：" + Shorten(ex.Message));
            ShowError("导出目录树时发生错误：" + Environment.NewLine + ex.Message);
        }
        finally
        {
            EndOperation();
            SetBusy(false);
        }
    }

    /// <summary>「导出检查报告」：把最近一次检查结果导出为 Markdown 报告。</summary>
    private async void OnExportReportClick(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var checkResult = _lastCheckResult;
        if (checkResult is null)
        {
            ShowWarning("请先执行一次「检查一致性」，再导出检查报告。");
            return;
        }

        string path;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出检查报告",
                Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
                FileName = "目录检查报告.md",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            path = dialog.FileName;
        }
        catch (Exception ex)
        {
            _logger?.Error("打开保存对话框失败。", ex);
            ShowError("打开保存对话框时发生错误：" + Environment.NewLine + ex.Message);
            return;
        }

        SetBusy(true);
        try
        {
            SetProgress(0);
            SetStatus("正在导出检查报告…");

            var report = FolderChecker.ToReport(checkResult, _lastCheckTime ?? DateTimeOffset.Now);
            var cts = BeginOperation();

            var written = await Task.Run(
                () => _folderService.ExportCheckReport(path, report),
                cts.Token);

            if (!IsUiUsable)
            {
                return;
            }

            SetProgress(100);
            SetStatus($"检查报告已导出：{Shorten(written)}");
            _logger?.Info($"FolderCreator 导出检查报告：{written}");
            _notifier?.ShowNotification("FolderCreator", "检查报告已导出。");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消导出检查报告。");
        }
        catch (Exception ex)
        {
            _logger?.Error("导出检查报告失败。", ex);
            SetStatus("导出检查报告失败：" + Shorten(ex.Message));
            ShowError("导出检查报告时发生错误：" + Environment.NewLine + ex.Message);
        }
        finally
        {
            EndOperation();
            SetBusy(false);
        }
    }

    /// <summary>「导出检查报告」只在检查成功过之后可用。</summary>
    private void RefreshReportButton()
    {
        if (!IsUiUsable)
        {
            return;
        }

        btnExportReport.Enabled = !_isBusy && _lastCheckResult is not null;
    }

    /// <summary>把模板名列表填入下拉框，可选选中指定模板。</summary>
    private void ReloadTemplateList(string? selectName = null)
    {
        if (!IsUiUsable)
        {
            return;
        }

        var names = _templateManager.GetTemplateNames();

        cboTemplate.BeginUpdate();
        try
        {
            cboTemplate.Items.Clear();
            foreach (var name in names)
            {
                cboTemplate.Items.Add(name);
            }
        }
        finally
        {
            cboTemplate.EndUpdate();
        }

        if (string.IsNullOrWhiteSpace(selectName))
        {
            return;
        }

        var match = names.FirstOrDefault(
            name => string.Equals(name, selectName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            cboTemplate.SelectedItem = match;
        }
    }

    /// <summary>开始一次可取消的后台操作（调用方已通过 <see cref="_isBusy"/> 保证不会并发）。</summary>
    private CancellationTokenSource BeginOperation()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        return _operationCts;
    }

    /// <summary>结束后台操作并释放取消源。</summary>
    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
    }

    /// <summary>
    /// 「创建文件夹」：后台批量创建（支持单根与多根目录、支持为空目录创建占位文件），
    /// 进度实时回填状态栏与进度条。
    /// </summary>
    private async void OnCreateClick(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!TryGetRoots(out var roots))
        {
            return;
        }

        var rules = TryParseRules();
        if (rules is null)
        {
            return;
        }

        var createPlaceholder = chkPlaceholder.Checked;
        var multiRoot = roots.Count > 1;

        SetBusy(true);
        try
        {
            SetProgress(0);
            _logger?.Info(
                $"FolderCreator 开始创建文件夹：根目录数={roots.Count}，规则数={rules.Count}，占位文件={createPlaceholder}");

            var cts = BeginOperation();

            if (!multiRoot)
            {
                await CreateInSingleRootAsync(roots[0], rules, createPlaceholder, cts.Token);
            }
            else
            {
                await CreateInMultipleRootsAsync(roots, rules, createPlaceholder, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消创建。");
        }
        catch (Exception ex)
        {
            _logger?.Error("创建文件夹失败。", ex);
            SetStatus("创建文件夹失败：" + Shorten(ex.Message));
            ShowError("创建文件夹时发生错误：" + Environment.NewLine + ex.Message);
        }
        finally
        {
            EndOperation();
            SetBusy(false);
        }
    }

    /// <summary>单根目录创建流程。</summary>
    private async Task CreateInSingleRootAsync(
        string root,
        IReadOnlyList<FolderRule> rules,
        bool createPlaceholder,
        CancellationToken cancellationToken)
    {
        SetStatus("正在创建文件夹…");

        // GenerateAsync 内部已是 Task.Run；Progress 会自动回到 UI 线程
        var progress = new Progress<FolderProgress>(OnFolderProgress);
        var result = await _generator.GenerateAsync(
            root, rules, progress, cancellationToken, createPlaceholder);

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
            ShowWarning(
                "部分文件夹创建失败：" + Environment.NewLine + Environment.NewLine + BuildFailureDetail(result));
        }

        _notifier?.ShowNotification("FolderCreator", result.Summary);
    }

    /// <summary>多根目录批量创建流程：单个根目录失败不中断整体，结果按根目录分组展示。</summary>
    private async Task CreateInMultipleRootsAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<FolderRule> rules,
        bool createPlaceholder,
        CancellationToken cancellationToken)
    {
        SetStatus($"正在批量创建（共 {roots.Count} 个根目录）…");

        var relativePaths = rules.Select(static rule => rule.RelativePath).ToList();
        var progress = new Progress<MultiRootCreateProgress>(OnMultiRootProgress);

        var multiResult = await Task.Run(
            () => _folderService.CreateFoldersInMultipleRoots(
                roots,
                relativePaths,
                createPlaceholder,
                FolderCreatorService.DefaultPlaceholderName,
                progress,
                cancellationToken),
            cancellationToken);

        if (!IsUiUsable)
        {
            return;
        }

        _lastSelectedDirectory = roots[0];

        var results = multiResult.Results
            .Select(FolderGenerator.ToGenerationResult)
            .ToList();

        FillCreateTrees(results, rules);
        SetProgress(multiResult.Success ? 100 : 0);
        SetStatus(AppendWarningHint(multiResult.Summary));

        if (multiResult.Success)
        {
            _logger?.Info($"FolderCreator 多根目录创建完成：{multiResult.Summary}");
        }
        else
        {
            _logger?.Warn($"FolderCreator 多根目录创建完成，但存在失败项：{multiResult.Summary}");
            ShowWarning(
                "部分文件夹创建失败：" + Environment.NewLine + Environment.NewLine + BuildMultiRootFailureDetail(multiResult));
        }

        _notifier?.ShowNotification("FolderCreator", multiResult.Summary);
    }

    /// <summary>「检查一致性」：在后台线程比对目标目录与规则。</summary>
    private async void OnCheckClick(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!TryGetRoots(out var roots))
        {
            return;
        }

        var rules = TryParseRules();
        if (rules is null)
        {
            return;
        }

        // 一致性检查按单个根目录执行：多根目录模式下取第一个（创建流程支持多根）
        var root = roots[0];
        var multiRootHint = roots.Count > 1
            ? $"（多根目录模式下仅检查第 1 个：{Shorten(root)}）"
            : string.Empty;

        var mode = rdoStrict.Checked ? FolderCheckMode.Strict : FolderCheckMode.Loose;

        SetBusy(true);
        try
        {
            SetProgress(0);
            SetStatus("正在检查目录结构…");
            _logger?.Info($"FolderCreator 开始检查目录：根目录={root}，模式={mode}，规则数={rules.Count}");

            var cts = BeginOperation();
            var result = await Task.Run(() => _checker.Check(root, rules, mode, cts.Token), cts.Token);

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
                // 检查失败时不保留旧结果，避免导出过期报告
                _lastCheckResult = null;
                _lastCheckTime = null;
                RefreshReportButton();

                SetStatus("检查失败：" + Shorten(result.Error));
                _logger?.Warn($"FolderCreator 检查失败：{result.Error}");
                ShowWarning("检查目录结构失败：" + Environment.NewLine + result.Error);
                return;
            }

            // 记住本次结果与时间，供「导出检查报告」使用
            _lastCheckResult = result;
            _lastCheckTime = DateTimeOffset.Now;
            RefreshReportButton();

            SetStatus(AppendWarningHint(result.Summary) + multiRootHint);
            _logger?.Info($"FolderCreator 检查完成：{result.Summary}{multiRootHint}");
            _notifier?.ShowNotification("FolderCreator", result.Summary);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消检查目录结构。");
        }
        catch (Exception ex)
        {
            _logger?.Error("检查目录结构失败。", ex);
            SetStatus("检查目录结构失败：" + Shorten(ex.Message));
            ShowError("检查目录结构时发生错误：" + Environment.NewLine + ex.Message);
        }
        finally
        {
            EndOperation();
            SetBusy(false);
        }
    }

    /// <summary>关闭窗口：操作未完成时先确认；确认退出后取消后台操作。</summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_isBusy)
        {
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
                return;
            }
        }

        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 后台操作刚好结束并释放了取消源，忽略即可
        }

        if (_logger is not null)
        {
            _logger.EntryWritten -= OnLoggerEntryWritten;
        }
    }

    // ---------------------------------------------------------------- 多根目录与选项

    /// <summary>多根目录模式下规则区整体下移的逻辑像素数（会按 DPI 缩放）。</summary>
    private const int MultiRootShift = 98;

    /// <summary>日志框最多保留的行数（超出后裁掉最早的部分）。</summary>
    private const int MaxLogLines = 500;

    /// <summary>日志框当前行数缓存。</summary>
    private int _logLineCount;

    /// <summary>自动 DPI 缩放后的基准布局（在 <see cref="OnLoad"/> 中采集一次）。</summary>
    private int _baseRulesLabelTop;
    private int _baseContentTop;
    private int _baseContentBottom;
    private int _baseTreeBottom;
    private bool _baseLayoutCaptured;

    /// <summary>窗口加载完成：采集缩放后的基准位置，再应用当前的多根目录布局。</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (!_baseLayoutCaptured)
        {
            _baseRulesLabelTop = lblRules.Top;
            _baseContentTop = txtRules.Top;
            _baseContentBottom = txtRules.Bottom;
            _baseTreeBottom = treeResult.Bottom;
            _baseLayoutCaptured = true;
        }

        ApplyMultiRootLayout(chkMultiRoot.Checked);
    }

    /// <summary>订阅全局日志事件，把日志同时显示在窗口底部。</summary>
    private void SetupLogPanel()
    {
        txtLog.Text = $"操作日志会显示在这里，同时写入日志目录：{AppPaths.LogDirectory}";

        if (_logger is null)
        {
            AppendLogText("[提示] 未启用日志记录器，仅显示界面提示。");
            return;
        }

        _logger.EntryWritten += OnLoggerEntryWritten;
    }

    /// <summary>日志事件（可能来自后台线程）：切回 UI 线程追加。</summary>
    private void OnLoggerEntryWritten(object? sender, LogEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        RunOnUiThread(() => AppendLogText(entry.FormattedText));
    }

    /// <summary>把文本追加到日志框，超过上限时保留最近的若干行。</summary>
    private void AppendLogText(string? text)
    {
        if (!IsUiUsable || txtLog.IsDisposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        txtLog.AppendText(text + Environment.NewLine);
        _logLineCount = txtLog.Lines.Length;

        if (_logLineCount <= MaxLogLines)
        {
            return;
        }

        txtLog.Lines = txtLog.Lines.Skip(_logLineCount - MaxLogLines + 200).ToArray();
        _logLineCount = txtLog.Lines.Length;
    }

    /// <summary>在 UI 线程上执行动作（窗口已关闭时静默忽略）。</summary>
    private void RunOnUiThread(Action action)
    {
        try
        {
            if (!IsUiUsable || txtLog.IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(action);
                return;
            }

            action();
        }
        catch (Exception)
        {
            // 窗口关闭后 BeginInvoke / 访问控件会抛异常，忽略即可（不能在此写日志，避免递归）
        }
    }

    /// <summary>「启用多根目录」勾选状态变化：切换布局并调整输入可用性。</summary>
    private void OnMultiRootChanged(object? sender, EventArgs e)
    {
        try
        {
            ApplyMultiRootLayout(chkMultiRoot.Checked);
            SetStatus(chkMultiRoot.Checked
                ? "已启用多根目录：请在下方每行输入一个根目录路径。"
                : "已关闭多根目录：使用上方的单个根目录。");
        }
        catch (Exception ex)
        {
            _logger?.Error("切换多根目录模式失败。", ex);
            ShowError("切换多根目录模式时发生错误：" + Environment.NewLine + ex.Message);
        }
    }

    /// <summary>按多根目录开关重排规则区与结果树，给多行路径框腾出空间。</summary>
    /// <remarks>
    /// 关键：所有坐标都以「自动 DPI 缩放后的真实位置」为基准（<see cref="OnLoad"/> 中采集），
    /// 不能使用设计期的硬编码值 —— 否则会覆盖 WinForms 的自动缩放，导致控件错位、文字互相穿透。
    /// </remarks>
    private void ApplyMultiRootLayout(bool multiRoot)
    {
        if (!IsUiUsable)
        {
            return;
        }

        txtMultiRoots.Visible = multiRoot;

        if (!_baseLayoutCaptured)
        {
            ApplyOptionAvailability();
            return;
        }

        var scale = DeviceDpi <= 0 ? 1f : DeviceDpi / 96f;
        var shift = multiRoot ? (int)Math.Round(MultiRootShift * scale) : 0;

        lblRules.Top = _baseRulesLabelTop + shift;

        txtRules.Top = _baseContentTop + shift;
        txtRules.Height = Math.Max(80, _baseContentBottom - txtRules.Top);

        treeResult.Top = _baseContentTop + shift;
        treeResult.Height = Math.Max(80, _baseTreeBottom - treeResult.Top);

        ApplyOptionAvailability();
    }

    /// <summary>按「是否多根目录」与「是否忙碌」决定输入控件的可用性。</summary>
    private void ApplyOptionAvailability()
    {
        if (!IsUiUsable)
        {
            return;
        }

        var singleRootEnabled = !_isBusy && !chkMultiRoot.Checked;
        txtRoot.Enabled = singleRootEnabled;
        btnBrowse.Enabled = singleRootEnabled;
        txtMultiRoots.Enabled = !_isBusy && chkMultiRoot.Checked;
    }

    /// <summary>
    /// 取本次操作的根目录列表：单根模式返回一个路径，多根模式返回去重并校验后的路径列表。
    /// 校验失败时给出提示并返回 false。
    /// </summary>
    private bool TryGetRoots(out IReadOnlyList<string> roots)
    {
        roots = Array.Empty<string>();

        if (!chkMultiRoot.Checked)
        {
            if (!TryGetRootDirectory(out var single))
            {
                return false;
            }

            roots = new[] { single };
            return true;
        }

        var lines = txtMultiRoots.Lines
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            ShowWarning("已启用多根目录模式，请每行输入一个根目录路径。");
            txtMultiRoots.Focus();
            return false;
        }

        var normalized = FolderCreatorService.NormalizeRoots(lines);
        if (normalized.Count == 0)
        {
            ShowWarning("多根目录中没有合法的路径，请检查输入内容。");
            txtMultiRoots.Focus();
            return false;
        }

        if (normalized.Count < lines.Count)
        {
            var dropped = lines.Count - normalized.Count;
            SetStatus($"已忽略 {dropped} 个无效或重复的路径。");

            if (normalized.Count == 0)
            {
                return false;
            }
        }

        roots = normalized;
        return true;
    }

    /// <summary>多根目录创建进度：状态栏显示「当前处理第 X / 共 Y 个」。</summary>
    private void OnMultiRootProgress(MultiRootCreateProgress progress)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            SetProgress((int)Math.Round(progress.Percent));
            SetStatus($"{progress.RootText}：{Shorten(progress.CurrentRoot)} → {Shorten(progress.CurrentPath)}");
        }
        catch (Exception ex)
        {
            // 进度回调在 UI 线程上执行，异常不应影响后台创建
            _logger?.Warn("更新多根目录创建进度失败。", ex);
        }
    }

    /// <summary>多根目录失败明细（最多展示前若干条根目录与失败项）。</summary>
    private static string BuildMultiRootFailureDetail(MultiRootCreateResult result)
    {
        var lines = new List<string>();

        foreach (var item in result.Results)
        {
            if (item.Success)
            {
                continue;
            }

            if (item.Error is not null)
            {
                lines.Add($"● {item.RootDirectory}：{item.Error}");
                continue;
            }

            lines.Add($"● {item.RootDirectory}：失败 {item.FailedCount} 个");

            foreach (var failed in item.Items.Where(static i => i.Status == FolderCreateStatus.Failed).Take(3))
            {
                lines.Add($"    - {failed.RelativePath}：{failed.Message}");
            }
        }

        if (result.SkippedRoots.Count > 0)
        {
            lines.Add("已忽略的路径：" + string.Join("；", result.SkippedRoots.Take(5)));
        }

        return lines.Count == 0 ? result.Summary : string.Join(Environment.NewLine, lines);
    }

    // ---------------------------------------------------------------- 创建流程

    /// <summary>创建进度回调（由 <see cref="Progress{T}"/> 自动切回 UI 线程）。</summary>
    private void OnFolderProgress(FolderProgress progress)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            SetProgress((int)Math.Round(progress.Percent));
            SetStatus($"正在创建（{progress.Processed}/{progress.Total}）：{Shorten(progress.CurrentPath)}");
        }
        catch (Exception ex)
        {
            // 进度回调在 UI 线程上执行，异常不应影响后台创建
            _logger?.Warn("更新创建进度失败。", ex);
        }
    }

    /// <summary>把单个根目录的创建结果填进结果树。</summary>
    private void FillCreateTree(FolderGenerationResult result, IReadOnlyList<FolderRule> rules)
        => FillCreateTrees(new[] { result }, rules);

    /// <summary>
    /// 把多个根目录的创建结果按根目录分组填进结果树：
    /// 绿色=已创建，灰色=已存在，红色=失败。
    /// </summary>
    private void FillCreateTrees(IReadOnlyList<FolderGenerationResult> results, IReadOnlyList<FolderRule> rules)
    {
        treeResult.BeginUpdate();
        try
        {
            treeResult.Nodes.Clear();

            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];

                var title = results.Count == 1
                    ? $"根目录：{result.RootDirectory}"
                    : $"第 {index + 1} / {results.Count} 个根目录：{result.RootDirectory}";

                var rootNode = new TreeNode(title)
                {
                    Name = string.Empty,
                    ToolTipText = result.RootDirectory
                };
                treeResult.Nodes.Add(rootNode);

                FillCreateNodes(rootNode, result, rules);

                if (index == 0)
                {
                    // 解析警告只挂一次，避免多根目录时重复
                    AppendWarningNode(rootNode);
                }

                ExpandResultTree(rootNode);
            }
        }
        finally
        {
            treeResult.EndUpdate();
        }
    }

    /// <summary>把单个根目录的创建明细挂到指定根节点下。</summary>
    private static void FillCreateNodes(
        TreeNode rootNode,
        FolderGenerationResult result,
        IReadOnlyList<FolderRule> rules)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            rootNode.Nodes.Add(new TreeNode("创建失败：" + result.Error) { ForeColor = Color.Red });
            return;
        }

        var itemMap = new Dictionary<string, FolderItemResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.Items)
        {
            itemMap[item.RelativePath] = item;
        }

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
    }

    /// <summary>按单条创建结果给节点上色与加说明。</summary>
    private static void ApplyCreateStatus(TreeNode node, FolderRule rule, FolderItemResult? item)
    {
        var name = GetRuleName(rule);
        var autoText = rule.IsAutoCreated ? "（自动补齐）" : string.Empty;
        var placeholderText = item?.PlaceholderCreated == true
            ? $"（已创建 {Path.GetFileName(item.PlaceholderPath ?? FolderCreatorService.DefaultPlaceholderName)}）"
            : string.Empty;

        node.ToolTipText = rule.IsAutoCreated
            ? $"{rule.RelativePath}（父级缺失，已按「{FolderRule.AutoCreatedName}」自动补齐）"
            : rule.RelativePath;

        switch (item?.Status)
        {
            case FolderItemStatus.Created:
                node.ForeColor = Color.Green;
                node.Text = $"{name}（已创建）{autoText}{placeholderText}";
                break;

            case FolderItemStatus.Existed:
                node.ForeColor = Color.Gray;
                node.Text = $"{name}（已存在，已跳过）{autoText}{placeholderText}";
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
        btnSample.Enabled = !busy;
        btnSaveTemplate.Enabled = !busy;
        btnDeleteTemplate.Enabled = !busy;
        btnGenerateFromDir.Enabled = !busy;
        btnExportTree.Enabled = !busy;
        cboTemplate.Enabled = !busy;
        chkMultiRoot.Enabled = !busy;
        chkPlaceholder.Enabled = !busy;
        UseWaitCursor = busy;

        // 单根 / 多根输入框的可用性由「当前模式 + 是否忙碌」共同决定
        ApplyOptionAvailability();

        // 报告导出依赖上一次检查结果
        RefreshReportButton();
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
