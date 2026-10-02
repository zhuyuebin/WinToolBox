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
/// 「文件夹同步 / 镜像」窗口：以源目录为准生成同步计划（先删除、再创建目录、最后复制），
/// 计划先预览、二次确认后再执行，删除默认走回收站。
/// </summary>
/// <remarks>
/// <para>继承 <see cref="FeatureDialogBase"/>：生成计划与执行同步都在后台线程完成，
/// 进度经 <see cref="Progress{T}"/> 回填到界面，单个功能出错不会影响主窗口。</para>
/// <para>三种模式：单向复制（不动目标多余内容）、单向同步（删除目标多余内容）、镜像（目标完全等于源）。</para>
/// <para>行颜色：删除=红色，复制 / 更新=绿色，创建目录=灰蓝色；源与目标的安全校验（不能相同 / 互相包含）
/// 由 <see cref="FolderSyncService"/> 在生成计划时给出。</para>
/// </remarks>
public sealed class FolderSyncDialog : FeatureDialogBase
{
    /// <summary>失败明细在弹窗中最多列出的条数（其余引导用户查看运行日志）。</summary>
    private const int MaxDisplayedMessages = 10;

    /// <summary>执行过程中每处理多少项往日志里写一行（避免刷屏）。</summary>
    private const int LogInterval = 20;

    /// <summary>目录等没有大小的条目在「大小」列中的占位符。</summary>
    private const string NoValueText = "-";

    /// <summary>「同步模式」下拉框的选项（顺序与 <see cref="ReadMode"/> 的映射一致）。</summary>
    private static readonly string[] SyncModeNames =
    {
        "单向复制（不删除目标多余内容）",
        "单向同步（删除目标多余内容）",
        "镜像（目标完全等于源）"
    };

    /// <summary>文件夹同步服务。</summary>
    private readonly FolderSyncService _service;

    /// <summary>源目录选择控件（目录文本框 + 浏览…按钮）。</summary>
    private readonly FolderPickerBox _txtSource;

    /// <summary>目标目录选择控件（目录文本框 + 浏览…按钮；允许是不存在的新目录）。</summary>
    private readonly FolderPickerBox _txtTarget;

    /// <summary>「同步模式」下拉框（默认「单向同步」）。</summary>
    private readonly ComboBox _cboMode;

    /// <summary>「包含子目录」复选框（默认勾选）。</summary>
    private readonly CheckBox _chkIncludeSubDirectories;

    /// <summary>「用哈希比较内容」复选框（默认不勾选）。</summary>
    private readonly CheckBox _chkCompareByHash;

    /// <summary>「删除时放入回收站」复选框（默认勾选）。</summary>
    private readonly CheckBox _chkUseRecycleBin;

    /// <summary>最近一次生成的同步计划（「开始同步」按它执行）。</summary>
    private SyncPlan? _plan;

    /// <summary>最近一次生成计划时使用的选项（执行时沿用，保证与列表中的计划一致）。</summary>
    private FolderSyncOptions? _planOptions;

    /// <summary>创建「文件夹同步 / 镜像」窗口。</summary>
    /// <param name="logger">日志记录器（可为 null）。</param>
    public FolderSyncDialog(Logger? logger)
        : base("文件夹同步 / 镜像", logger)
    {
        _service = new FolderSyncService(logger);

        // ---------- 第 1 行：源目录 ----------
        var rowSource = AddInputRow(InputLabelWidth, -100);
        _txtSource = CreateFolderPicker("请选择同步的源目录（以该目录为准）");
        AddField(rowSource, "源目录：", _txtSource, 0);

        // ---------- 第 2 行：目标目录 ----------
        var rowTarget = AddInputRow(InputLabelWidth, -100);
        _txtTarget = CreateFolderPicker("请选择同步的目标目录（不存在时会自动创建）");
        AddField(rowTarget, "目标目录：", _txtTarget, 0);

        // ---------- 第 3 行：同步模式、递归、比较方式、删除方式 ----------
        var rowOptions = AddInputRow(InputLabelWidth, 224, -34, -33, -33);

        _cboMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = false,
            Height = 25
        };
        _cboMode.Items.AddRange(SyncModeNames);
        _cboMode.SelectedIndex = 1; // 默认「单向同步（删除目标多余内容）」
        AddField(rowOptions, "同步模式：", _cboMode, 0);

        _chkIncludeSubDirectories = CreateInputCheckBox("包含子目录", isChecked: true);
        AddCell(rowOptions, _chkIncludeSubDirectories, 2);

        _chkCompareByHash = CreateInputCheckBox("用哈希比较内容");
        AddCell(rowOptions, _chkCompareByHash, 3);

        _chkUseRecycleBin = CreateInputCheckBox("删除时放入回收站", isChecked: true);
        AddCell(rowOptions, _chkUseRecycleBin, 4);

        // ---------- 预览列表：显示同步计划中的每条动作 ----------
        AddColumn("动作", 120);
        AddColumn("相对路径", 320);
        AddColumn("大小", 110, HorizontalAlignment.Right);
        AddColumn("说明", 360);

        // ---------- 按钮区（自右向左排列：先添加的在最右侧） ----------
        AddButton("关闭", (_, _) => Close());
        AddButton("开始同步", AsyncHandler(ApplyAsync), primary: true);
        AddButton("生成计划", AsyncHandler(BuildPlanAsync));

        SetStatus("请选择源目录与目标目录，先点击「生成计划」预览差异。");
        AppendLog("提示：行颜色 红色=删除、绿色=复制 / 更新、灰蓝色=创建目录；删除默认放入回收站。");
        AppendLog("提示：源目录与目标目录不能相同，也不能互相包含（生成计划时会校验）。");
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

    /// <summary>忙碌状态变化：生成计划 / 同步期间禁用输入控件与列表，避免操作交叉。</summary>
    /// <param name="busy">是否忙碌。</param>
    protected override void OnBusyChanged(bool busy) => SetInputsEnabled(!busy);

    // ---------------------------------------------------------------- 生成计划

    /// <summary>比较源目录与目标目录，生成同步计划并填入列表（不修改任何文件）。</summary>
    private async Task BuildPlanAsync()
    {
        if (!TryGetExistingDirectory(_txtSource.TextBox, "源目录", out var source))
        {
            return;
        }

        var target = _txtTarget.Path;
        if (target.Length == 0)
        {
            ShowWarning("请先选择目标目录。");
            _txtTarget.TextBox.Focus();
            return;
        }

        // 源目录与目标目录的安全校验（相同 / 互相包含）由服务在生成计划时完成
        var options = BuildOptions(source, target);

        if (!Directory.Exists(target))
        {
            AppendLog($"提示：目标目录当前不存在，执行同步时会自动创建：{target}");
        }

        // 重新生成计划前先让旧计划失效，避免「开始同步」误用过期计划
        ClearPlan();

        SyncPlan? plan = null;
        var ok = await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始生成同步计划：{source} → {target}（{DescribeOptions(options)}）");

                plan = await Task.Run(
                    () => _service.BuildPlan(options, token),
                    token).ConfigureAwait(true);
            },
            "正在比较源目录与目标目录，生成同步计划…");

        if (!ok || plan is null || !IsUiUsable)
        {
            return;
        }

        if (plan.Error is not null)
        {
            ClearPlan();
            SetStatus("生成同步计划失败：" + plan.Error);
            AppendLog("生成同步计划失败：" + plan.Error);
            ShowWarning("生成同步计划失败：" + Environment.NewLine + plan.Error);
            return;
        }

        FillPlan(plan);

        _plan = plan;
        _planOptions = options;

        SetStatus(plan.Summary);
        AppendLog(plan.Summary);
    }

    /// <summary>把同步计划填入列表，并按动作类型着色。</summary>
    /// <param name="plan">同步计划。</param>
    private void FillPlan(SyncPlan plan)
    {
        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();
            PreviewList.Groups.Clear();

            foreach (var item in plan.Items)
            {
                var row = new ListViewItem(FolderSyncService.DescribeAction(item.Kind))
                {
                    Tag = item,
                    ForeColor = ResolveRowColor(item.Kind),
                    ToolTipText = item.SourcePath.Length == 0
                        ? item.TargetPath
                        : $"{item.SourcePath} → {item.TargetPath}"
                };

                row.SubItems.Add(item.RelativePath);
                row.SubItems.Add(FormatSize(item));
                row.SubItems.Add(item.Reason);

                PreviewList.Items.Add(row);
            }
        }
        finally
        {
            PreviewList.EndUpdate();
        }

        SetCountText(
            $"共 {plan.Items.Count} 项：复制 {plan.CopyCount}，" +
            $"新建目录 {plan.CreateDirectoryCount}，" +
            $"删除文件 {plan.DeleteFileCount}，删除目录 {plan.DeleteDirectoryCount}");
    }

    /// <summary>行颜色：删除=红色，复制 / 更新=绿色，创建目录=灰蓝色。</summary>
    /// <param name="kind">动作类型。</param>
    private static Color ResolveRowColor(SyncActionKind kind) => kind switch
    {
        SyncActionKind.DeleteFile or SyncActionKind.DeleteDirectory => Color.Red,
        SyncActionKind.CreateDirectory => Color.SteelBlue,
        _ => Color.Green
    };

    /// <summary>「大小」列文本：目录没有大小，显示占位符。</summary>
    /// <param name="item">计划条目。</param>
    private static string FormatSize(SyncPlanItem item)
        => item.Kind == SyncActionKind.CreateDirectory
            ? NoValueText
            : DuplicateScanResult.FormatSize(item.Size);

    // ---------------------------------------------------------------- 执行同步

    /// <summary>按最近一次生成的计划执行同步（必须先二次确认）。</summary>
    private async Task ApplyAsync()
    {
        SyncPlan plan;
        FolderSyncOptions options;

        if (_plan is null || _planOptions is null)
        {
            ShowWarning("请先点击「生成计划」，确认要执行的动作后再开始同步。");
            return;
        }

        // 取出非空引用后再交给后台线程：执行时沿用「生成计划时」的选项，保证与列表中的计划一致
        plan = _plan;
        options = _planOptions;

        if (!plan.HasChanges)
        {
            ShowInfo("目标目录已经与源目录一致，无需同步。");
            return;
        }

        // 计划里含删除 / 覆盖动作，一律使用警示确认框并写明数量与删除方式
        if (!ConfirmDanger(BuildApplyConfirmMessage(plan, options)))
        {
            return;
        }

        var progress = new Progress<SyncProgress>(report =>
        {
            SetProgress(report.Percent);
            SetStatus($"正在同步（{report.Processed}/{report.Total}）：{report.CurrentAction}");
            SetCountText($"{report.Processed}/{report.Total}");

            if (report.Processed % LogInterval == 0)
            {
                AppendLog($"已处理 {report.Processed}/{report.Total}：{report.CurrentAction}");
            }
        });

        SyncResult? result = null;
        var ok = await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始同步：共 {plan.Items.Count} 项（{DescribeOptions(options)}）");

                result = await Task.Run(
                    () => _service.Apply(plan, options, progress, token),
                    token).ConfigureAwait(true);
            },
            "正在同步文件夹…");

        if (!ok || result is null || !IsUiUsable)
        {
            return;
        }

        SetStatus(result.Summary + " 请重新点击「生成计划」查看当前差异。");
        AppendLog(result.Summary);

        foreach (var failed in result.Items.Where(static item => !item.Success).Take(MaxDisplayedMessages))
        {
            AppendLog($"失败：{FolderSyncService.DescribeAction(failed.Kind)}：{failed.RelativePath}：{failed.Error}");
        }

        if (!result.Success)
        {
            ShowWarning(BuildListMessage(
                $"有 {result.FailedCount} 项同步失败：",
                result.Items
                    .Where(static item => !item.Success)
                    .Select(static item =>
                        $"{FolderSyncService.DescribeAction(item.Kind)} {item.RelativePath}：{item.Error}"),
                MaxDisplayedMessages));
        }

        // 计划已执行完毕：清空列表与计划缓存，避免重复执行同一份计划
        ClearPlan();
        AppendLog("同步计划已执行完毕，如需再次同步请重新点击「生成计划」。");
    }

    /// <summary>执行同步前的二次确认文案（写明复制 / 删除数量与删除方式）。</summary>
    /// <param name="plan">同步计划。</param>
    /// <param name="options">本次执行使用的选项。</param>
    private static string BuildApplyConfirmMessage(SyncPlan plan, FolderSyncOptions options)
    {
        var method = options.UseRecycleBin
            ? "删除方式：放入回收站（需要时可从回收站还原）。"
            : "删除方式：永久删除（不可恢复，不会放入回收站）。";

        var newLine = Environment.NewLine;

        return $"即将按同步计划执行（模式：{DescribeMode(options.Mode)}）：" + newLine + newLine +
               $"· 复制 / 更新文件：{plan.CopyCount} 个（{DuplicateScanResult.FormatSize(plan.CopyBytes)}）" + newLine +
               $"· 新建目录：{plan.CreateDirectoryCount} 个" + newLine +
               $"· 删除文件：{plan.DeleteFileCount} 个" + newLine +
               $"· 删除目录：{plan.DeleteDirectoryCount} 个" + newLine + newLine +
               $"源目录：{options.SourceDirectory}" + newLine +
               $"目标目录：{options.TargetDirectory}" + newLine + newLine +
               method + newLine + newLine +
               "确定要继续吗？";
    }

    // ---------------------------------------------------------------- 选项与格式化

    /// <summary>按界面输入组装同步选项。</summary>
    /// <param name="source">源目录。</param>
    /// <param name="target">目标目录。</param>
    private FolderSyncOptions BuildOptions(string source, string target) => new()
    {
        SourceDirectory = source,
        TargetDirectory = target,
        Mode = ReadMode(),
        IncludeSubDirectories = _chkIncludeSubDirectories.Checked,
        CompareByHash = _chkCompareByHash.Checked,
        UseRecycleBin = _chkUseRecycleBin.Checked
    };

    /// <summary>读取下拉框选中的同步模式（0=单向复制、1=单向同步、2=镜像）。</summary>
    private SyncMode ReadMode() => _cboMode.SelectedIndex switch
    {
        0 => SyncMode.CopyOnly,
        2 => SyncMode.Mirror,
        _ => SyncMode.OneWaySync
    };

    /// <summary>同步模式的中文说明。</summary>
    /// <param name="mode">同步模式。</param>
    private static string DescribeMode(SyncMode mode) => mode switch
    {
        SyncMode.CopyOnly => "单向复制（不删除目标多余内容）",
        SyncMode.Mirror => "镜像（目标完全等于源）",
        _ => "单向同步（删除目标多余内容）"
    };

    /// <summary>把选项描述成一句话（用于日志）。</summary>
    /// <param name="options">同步选项。</param>
    private static string DescribeOptions(FolderSyncOptions options)
    {
        var scope = options.IncludeSubDirectories ? "包含子目录" : "仅当前目录";
        var compare = options.CompareByHash ? "按哈希比较内容" : "按大小 + 修改时间比较";
        var delete = options.UseRecycleBin ? "删除放入回收站" : "永久删除";

        return $"{DescribeMode(options.Mode)}，{scope}，{compare}，{delete}";
    }

    /// <summary>清空列表中的同步计划，并让计划缓存失效。</summary>
    private void ClearPlan()
    {
        _plan = null;
        _planOptions = null;

        PreviewList.Items.Clear();
        PreviewList.Groups.Clear();
        SetCountText("共 0 项");
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
