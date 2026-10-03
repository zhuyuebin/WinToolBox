using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.Tools.FileMaster.UI;

/// <summary>
/// 「批量移动 / 自动分类」窗口：按「扩展名 / 日期 / 首字母 / 兜底」规则表，
/// 把源目录中的文件移动（或复制）到目标目录下规则指定的子目录里；先预览、再执行。
/// </summary>
/// <remarks>
/// <para>继承 <see cref="FeatureDialogBase"/>：扫描与执行都在后台线程完成，进度经 <see cref="Progress{T}"/> 回填到界面，
/// 单个功能出错不会影响主窗口。</para>
/// <para>「开始分类」沿用「预览时」的选项（移动 / 复制、覆盖、清理空目录），保证执行结果与列表中的预览一致。</para>
/// </remarks>
public sealed class AutoArchiveDialog : FeatureDialogBase
{
    /// <summary>错误 / 失败明细在弹窗中最多列出的条数（其余引导用户查看运行日志）。</summary>
    private const int MaxDisplayedMessages = 10;

    /// <summary>执行过程中每处理多少个文件往日志里写一行（避免刷屏）。</summary>
    private const int ApplyLogInterval = 20;

    /// <summary>默认填入规则框的示例规则（换行使用 CRLF，与 TextBox 内部格式一致）。</summary>
    private const string SampleRules =
        "# 每行一条规则：类型|参数|目标子目录（# 开头为注释）\r\n" +
        "扩展名|.jpg;.jpeg;.png;.gif|图片\r\n" +
        "扩展名|.mp4;.mkv;.avi|视频\r\n" +
        "扩展名|.zip;.rar;.7z|压缩包\r\n" +
        "日期|yyyy-MM|按日期\r\n" +
        "其它||其它";

    /// <summary>自动分类服务（规则解析为静态方法，其余操作都在后台线程调用）。</summary>
    private readonly AutoArchiverService _service;

    /// <summary>源目录选择控件（目录文本框 + 浏览…按钮）。</summary>
    private readonly FolderPickerBox _txtSource;

    /// <summary>目标目录选择控件（目录文本框 + 浏览…按钮）。</summary>
    private readonly FolderPickerBox _txtTarget;

    /// <summary>「包含子目录」复选框。</summary>
    private readonly CheckBox _chkIncludeSubDirectories = new();

    /// <summary>「移动文件」单选按钮（默认选中）。</summary>
    private readonly RadioButton _rdoMove = new();

    /// <summary>「复制文件」单选按钮。</summary>
    private readonly RadioButton _rdoCopy = new();

    /// <summary>「覆盖同名文件」复选框。</summary>
    private readonly CheckBox _chkOverwrite = new();

    /// <summary>「移动后清理空目录」复选框。</summary>
    private readonly CheckBox _chkCleanEmptyFolders = new();

    /// <summary>「空目录放入回收站」复选框（默认勾选）。</summary>
    private readonly CheckBox _chkCleanEmptyFoldersUseRecycleBin = new();

    /// <summary>分类规则多行文本框（每行 <c>类型|参数|目标子目录</c>）。</summary>
    private readonly TextBox _txtRules;

    /// <summary>最近一次预览结果（供「开始分类」使用，与列表内容保持一致）。</summary>
    private ArchivePlan? _plan;

    /// <summary>最近一次预览使用的选项（执行时沿用，保证与预览一致）。</summary>
    private ArchiveOptions? _planOptions;

    /// <summary>创建「批量移动 / 自动分类」窗口。</summary>
    /// <param name="logger">日志记录器（可为 null）。</param>
    public AutoArchiveDialog(Logger? logger)
        : base("批量移动 / 自动分类", logger)
    {
        _service = new AutoArchiverService(logger);

        // ---------- 第 1 行：源目录 ----------
        var rowSource = AddInputRow(InputLabelWidth, -100);
        _txtSource = CreateFolderPicker("请选择要自动分类的源目录");
        AddField(rowSource, "源目录：", _txtSource, 0);

        // ---------- 第 2 行：目标目录 ----------
        var rowTarget = AddInputRow(InputLabelWidth, -100);
        _txtTarget = CreateFolderPicker("请选择分类后的目标目录（不存在时会自动创建）");
        AddField(rowTarget, "目标目录：", _txtTarget, 0);

        // ---------- 第 3 行：范围、方式、覆盖与空目录 ----------
        var rowOptions = AddInputRow(InputLabelWidth, 100, 90, 90, 116, 148, 190);

        _chkIncludeSubDirectories.Text = "包含子目录";
        _chkIncludeSubDirectories.AutoSize = true;
        AddCell(rowOptions, _chkIncludeSubDirectories, 1);

        _rdoMove.Text = "移动文件";
        _rdoMove.Checked = true;
        _rdoMove.AutoSize = true;
        AddCell(rowOptions, _rdoMove, 2);

        _rdoCopy.Text = "复制文件";
        _rdoCopy.AutoSize = true;
        AddCell(rowOptions, _rdoCopy, 3);

        _chkOverwrite.Text = "覆盖同名文件";
        _chkOverwrite.AutoSize = true;
        AddCell(rowOptions, _chkOverwrite, 4);

        _chkCleanEmptyFolders.Text = "移动后清理空目录";
        _chkCleanEmptyFolders.AutoSize = true;
        AddCell(rowOptions, _chkCleanEmptyFolders, 5);

        // 清理空目录的删除方式（默认走回收站，避免误删后无法找回）
        _chkCleanEmptyFoldersUseRecycleBin.Text = "空目录放入回收站";
        _chkCleanEmptyFoldersUseRecycleBin.AutoSize = true;
        _chkCleanEmptyFoldersUseRecycleBin.Checked = true;
        AddCell(rowOptions, _chkCleanEmptyFoldersUseRecycleBin, 6);

        // ---------- 第 4 行：分类规则（多行，与右侧「载入示例规则」按钮同一行） ----------
        var rowRules = AddInputRow(InputLabelWidth, -100, 106);

        _txtRules = new TextBox
        {
            Text = SampleRules,
            Multiline = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9F),
            AutoSize = false,
            Height = 104
        };
        AddField(rowRules, "分类规则：", _txtRules, 0);

        var loadSampleButton = CreateInputButton("载入示例规则", (_, _) => LoadSampleRules());
        AddCell(rowRules, loadSampleButton, 2);

        // ---------- 第 5 行：说明 ----------
        var rowHint = AddInputRow(-100);
        AddCell(
            rowHint,
            new Label
            {
                Text = "提示：规则每行一条「类型|参数|目标子目录」（# 开头为注释）；移动与覆盖同名文件不可撤销。",
                ForeColor = Color.DimGray,
                AutoSize = true
            },
            0);

        // ---------- 预览列表 ----------
        AddColumn("规则", 150);
        AddColumn("源文件", 240);
        AddColumn("目标路径", 320);
        AddColumn("状态", 80);
        AddColumn("说明", 240);

        // ---------- 按钮区（自右向左排列：先添加的在最右侧） ----------
        AddButton("关闭", (_, _) => Close());
        AddButton("开始分类", AsyncHandler(ApplyAsync), primary: true);
        AddButton("预览", AsyncHandler(PreviewAsync));

        SetStatus("请选择源目录与目标目录，确认分类规则后点击「预览」。");
        AppendLog("提示：规则每行一条「类型|参数|目标子目录」（# 开头为注释）；先预览确认，再执行分类。");
    }

    /// <summary>
    /// 把异步操作包装成按钮事件处理器：内部兜底所有异常（含 await 之后的异常），
    /// 保证异常不会逃出 WinForms 事件循环。
    /// </summary>
    /// <param name="action">异步操作。</param>
    private EventHandler AsyncHandler(Func<Task> action) => async (_, _) =>
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger?.Error($"{Text} 执行失败。", ex);
            ShowError("操作失败：" + Environment.NewLine + ex.Message);
        }
    };

    /// <summary>忙碌状态变化：扫描 / 分类期间禁用输入控件与列表，避免操作交叉。</summary>
    /// <param name="busy">是否忙碌。</param>
    protected override void OnBusyChanged(bool busy) => SetInputsEnabled(!busy);

    // ---------------------------------------------------------------- 预览

    /// <summary>把示例规则填回规则框。</summary>
    private void LoadSampleRules()
    {
        _txtRules.Text = SampleRules;
        AppendLog("已载入示例分类规则。");
    }

    /// <summary>解析规则并扫描源目录，生成分类预览填入列表。</summary>
    private async Task PreviewAsync()
    {
        if (!TryGetExistingDirectory(_txtSource.TextBox, "源目录", out var source))
        {
            return;
        }

        if (!TryGetTargetDirectory(out var target))
        {
            return;
        }

        var parsed = AutoArchiverService.ParseRules(_txtRules.Text);
        if (!parsed.Success)
        {
            AppendLog(parsed.Summary);
            ShowError(BuildListMessage(
                "分类规则存在错误，请修正后重试：",
                parsed.Errors,
                MaxDisplayedMessages));
            return;
        }

        var options = BuildOptions(source, target, parsed.Rules);

        ArchivePlan? plan = null;
        var ok = await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始生成预览：{source} → {target}（{DescribeOptions(options)}）");

                plan = await Task.Run(() => _service.BuildPlan(options, token), token).ConfigureAwait(true);

                if (plan is not null)
                {
                    FillPreview(plan, source);
                }
            },
            "正在扫描源目录并生成分类预览…");

        if (!ok || plan is null || !IsUiUsable)
        {
            return;
        }

        _plan = plan;
        _planOptions = options;

        if (plan.Error is not null)
        {
            SetStatus("预览失败：" + plan.Error);
            AppendLog("预览失败：" + plan.Error);
            ShowWarning("生成预览失败：" + Environment.NewLine + plan.Error);
            return;
        }

        SetStatus(plan.Summary);
        AppendLog(plan.Summary);
    }

    /// <summary>把预览填入列表：绿色=可执行，红色=目标冲突 / 同名已存在，灰色=未命中规则。</summary>
    /// <param name="plan">预览结果。</param>
    /// <param name="sourceRoot">源目录（用于显示相对路径）。</param>
    private void FillPreview(ArchivePlan plan, string sourceRoot)
    {
        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();

            foreach (var item in plan.Items)
            {
                var row = new ListViewItem(
                    item.RuleDescription.Length == 0 ? "（未知规则）" : item.RuleDescription)
                {
                    ForeColor = ResolveRowColor(item),
                    ToolTipText = item.SourcePath
                };

                row.SubItems.Add(BuildSourceDisplay(sourceRoot, item.SourcePath));
                row.SubItems.Add(item.TargetPath.Length == 0 ? "（无目标）" : item.TargetPath);
                row.SubItems.Add(item.CanApply ? "可执行" : "跳过");
                row.SubItems.Add(item.Message);

                PreviewList.Items.Add(row);
            }
        }
        finally
        {
            PreviewList.EndUpdate();
        }

        SetCountText($"共 {plan.Items.Count} 项，可执行 {plan.ReadyCount} 项");
    }

    /// <summary>行颜色：可执行=绿色；有目标但被跳过（重复 / 同名未覆盖等）=红色；未命中规则=灰色。</summary>
    /// <param name="item">预览条目。</param>
    private static Color ResolveRowColor(ArchivePlanItem item)
    {
        if (item.CanApply)
        {
            return Color.Green;
        }

        return item.TargetPath.Length == 0 ? Color.Gray : Color.Red;
    }

    /// <summary>源文件列显示文本：优先显示相对源目录的路径，取不到时退回文件名。</summary>
    /// <param name="sourceRoot">源目录。</param>
    /// <param name="sourcePath">源文件完整路径。</param>
    private static string BuildSourceDisplay(string sourceRoot, string sourcePath)
    {
        try
        {
            var relative = Path.GetRelativePath(sourceRoot, sourcePath);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? Path.GetFileName(sourcePath)
                : relative;
        }
        catch (ArgumentException)
        {
            return Path.GetFileName(sourcePath);
        }
    }

    // ---------------------------------------------------------------- 执行

    /// <summary>「开始分类」：二次确认后按最近一次预览执行移动 / 复制。</summary>
    private async Task ApplyAsync()
    {
        ArchivePlan plan;
        ArchiveOptions options;

        if (_plan is null || _planOptions is null)
        {
            ShowWarning("请先点击「预览」，确认分类结果后再执行。");
            return;
        }

        if (_plan.Items.Count == 0)
        {
            ShowWarning("当前预览中没有文件，请检查源目录与分类规则后重新「预览」。");
            return;
        }

        // 取出非空引用后再交给后台线程：执行时沿用「预览时」的规则与选项，保证与列表一致
        plan = _plan;
        options = _planOptions;

        var ready = plan.ReadyCount;
        if (ready == 0)
        {
            ShowWarning("当前预览中没有可执行的条目，请检查分类规则与目标目录。");
            return;
        }

        var isMove = options.Action == ArchiveAction.Move;

        if (!ConfirmApply(plan, options, ready, isMove))
        {
            return;
        }

        var progress = new Progress<ArchiveProgress>(report =>
        {
            SetProgress(report.Percent);
            SetStatus($"正在{(isMove ? "移动" : "复制")}（{report.Processed}/{report.Total}）：{report.CurrentPath}");
            SetCountText($"{report.Processed}/{report.Total}");

            if (report.Processed % ApplyLogInterval == 0)
            {
                AppendLog($"已处理 {report.Processed}/{report.Total}：{report.CurrentPath}");
            }
        });

        ArchiveResult? result = null;
        var ok = await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始{(isMove ? "移动" : "复制")}：共 {ready} 个文件 → {options.TargetDirectory}");

                result = await Task.Run(
                    () => _service.Apply(plan, options, progress, token),
                    token).ConfigureAwait(true);
            },
            isMove ? "正在移动文件…" : "正在复制文件…");

        if (!ok || result is null || !IsUiUsable)
        {
            return;
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);

        foreach (var failed in result.Items.Where(static item => !item.Success).Take(MaxDisplayedMessages))
        {
            AppendLog($"失败：{failed.SourcePath} → {failed.TargetPath}：{failed.Error}");
        }

        if (!result.Success)
        {
            ShowWarning(BuildListMessage(
                $"有 {result.FailedCount} 个文件{(isMove ? "移动" : "复制")}失败：",
                result.Items
                    .Where(static item => !item.Success)
                    .Select(static item => $"{item.SourcePath}：{item.Error}"),
                MaxDisplayedMessages));
        }

        // 列表中的源文件状态已经过期，清空后提示重新预览
        _plan = null;
        _planOptions = null;
        PreviewList.Items.Clear();
        SetCountText("共 0 项");
        AppendLog("列表中的预览状态已过期，如需继续处理请重新点击「预览」。");
        SetStatus(result.Summary + " 请重新点击「预览」查看当前状态。");
    }

    /// <summary>执行分类前的二次确认（移动或覆盖属于破坏性操作，使用警示确认框）。</summary>
    /// <param name="plan">预览结果。</param>
    /// <param name="options">本次执行使用的选项。</param>
    /// <param name="ready">可执行数量。</param>
    /// <param name="isMove">是否移动。</param>
    private bool ConfirmApply(ArchivePlan plan, ArchiveOptions options, int ready, bool isMove)
    {
        var action = isMove ? "移动" : "复制";
        var message =
            $"将{action} {ready} 个文件（共预览 {plan.Items.Count} 个，跳过 {plan.SkippedCount} 个）。" +
            Environment.NewLine + Environment.NewLine +
            $"源目录：{options.SourceDirectory}" + Environment.NewLine +
            $"目标目录：{options.TargetDirectory}" + Environment.NewLine +
            $"分类规则：{options.Rules.Count} 条" +
            (options.Overwrite
                ? "，覆盖同名文件（目标文件会被替换，无法撤销）"
                : "，不覆盖同名文件") +
            (isMove && options.CleanEmptyFolders
                ? Environment.NewLine + (options.CleanEmptyFoldersUseRecycleBin
                    ? "移动后会清理源目录中的空目录（空目录放入回收站，可还原；不涉及文件）。"
                    : "移动后会清理源目录中的空目录（空目录【永久删除、不进回收站】，不可恢复；不涉及文件）。")
                : string.Empty) +
            Environment.NewLine + Environment.NewLine +
            $"确定要{action}这些文件吗？";

        // 移动或覆盖都会改动 / 覆盖已有文件，用警示图标 + 默认「否」的确认框
        return isMove || options.Overwrite ? ConfirmDanger(message) : Confirm(message);
    }

    // ---------------------------------------------------------------- 规则与选项

    /// <summary>按界面输入组装自动分类选项。</summary>
    /// <param name="source">源目录。</param>
    /// <param name="target">目标目录。</param>
    /// <param name="rules">已解析通过的规则列表。</param>
    private ArchiveOptions BuildOptions(string source, string target, IReadOnlyList<ArchiveRule> rules) => new()
    {
        SourceDirectory = source,
        TargetDirectory = target,
        IncludeSubDirectories = _chkIncludeSubDirectories.Checked,
        Action = _rdoCopy.Checked ? ArchiveAction.Copy : ArchiveAction.Move,
        Overwrite = _chkOverwrite.Checked,
        CleanEmptyFolders = _chkCleanEmptyFolders.Checked,
        CleanEmptyFoldersUseRecycleBin = _chkCleanEmptyFoldersUseRecycleBin.Checked,
        Rules = rules
    };

    /// <summary>读取目标目录：允许是不存在的新目录，因此只校验非空。</summary>
    /// <param name="target">目标目录路径。</param>
    private bool TryGetTargetDirectory(out string target)
    {
        target = _txtTarget.Path;

        if (target.Length == 0)
        {
            ShowWarning("请先选择目标目录。");
            _txtTarget.TextBox.Focus();
            return false;
        }

        return true;
    }

    /// <summary>把选项描述成一句话（用于日志）。</summary>
    /// <param name="options">自动分类选项。</param>
    private static string DescribeOptions(ArchiveOptions options)
    {
        var scope = options.IncludeSubDirectories ? "包含子目录" : "仅当前目录";
        var action = options.Action == ArchiveAction.Move ? "移动" : "复制";
        var overwrite = options.Overwrite ? "覆盖同名文件" : "不覆盖同名文件";
        var clean = options.CleanEmptyFolders
            ? (options.CleanEmptyFoldersUseRecycleBin ? "清理空目录（回收站）" : "清理空目录（永久删除）")
            : "不清理空目录";

        return $"{scope}，{action}，{overwrite}，{clean}，共 {options.Rules.Count} 条规则";
    }

    /// <summary>把多行明细拼成弹窗文本（最多列出前若干条，其余引导用户查看运行日志）。</summary>
    /// <param name="header">首行说明。</param>
    /// <param name="lines">明细行。</param>
    /// <param name="max">最多列出的条数。</param>
    private static string BuildListMessage(string header, IEnumerable<string> lines, int max)
    {
        var list = lines.ToList();

        var text = header + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine, list.Take(max).Select(static line => "· " + line));

        return list.Count > max
            ? text + Environment.NewLine + $"……其余 {list.Count - max} 条请查看运行日志。"
            : text;
    }
}
