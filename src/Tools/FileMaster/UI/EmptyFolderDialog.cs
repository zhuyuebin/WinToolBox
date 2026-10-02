using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.Tools.FileMaster.UI;

/// <summary>
/// 「空文件夹清理」窗口：扫描指定根目录（可递归子目录）下所有「自身及子目录中都不含文件」的目录，
/// 勾选后删除；默认放入回收站，另有「永久删除」入口（二次确认且明确不可恢复）。
/// </summary>
/// <remarks>
/// 继承 <see cref="FeatureDialogBase"/>：扫描与删除都在后台线程执行，进度与日志经 <see cref="Progress{T}"/> 回填到界面，
/// 单个功能出错不会影响主窗口。
/// </remarks>
public sealed class EmptyFolderDialog : FeatureDialogBase
{
    /// <summary>删除失败时最多在弹窗中列出的条数（其余引导用户查看运行日志）。</summary>
    private const int MaxDisplayedFailures = 10;

    /// <summary>扫描每处理多少个目录往日志里写一行（避免刷屏）。</summary>
    private const int ScanLogInterval = 200;

    /// <summary>删除每处理多少个目录往日志里写一行。</summary>
    private const int DeleteLogInterval = 20;

    /// <summary>空文件夹清理服务。</summary>
    private readonly EmptyFolderCleanerService _service;

    /// <summary>根目录选择控件（文本框 +「浏览…」按钮）。</summary>
    private readonly FolderPickerBox _pickerRoot;

    /// <summary>「包含子目录」复选框。</summary>
    private readonly CheckBox _chkIncludeSubDirectories;

    /// <summary>「删除时放入回收站」复选框。</summary>
    private readonly CheckBox _chkUseRecycleBin;

    /// <summary>「全选 / 全不选」按钮（文字随勾选情况切换）。</summary>
    private readonly Button _btnToggleAll;

    /// <summary>创建「空文件夹清理」窗口。</summary>
    /// <param name="logger">日志记录器（可为 null）。</param>
    public EmptyFolderDialog(Logger? logger)
        : base("空文件夹清理", logger)
    {
        _service = new EmptyFolderCleanerService(logger);

        // ---------- 第 1 行：根目录 ----------
        var rowDirectory = AddInputRow(InputLabelWidth, -100);
        _pickerRoot = CreateFolderPicker("请选择要扫描的根目录");
        AddField(rowDirectory, "根目录：", _pickerRoot, 0);

        // ---------- 第 2 行：扫描范围与删除方式 ----------
        var rowOptions = AddInputRow(InputLabelWidth, 140, 230);

        _chkIncludeSubDirectories = CreateInputCheckBox("包含子目录", isChecked: true);
        AddCell(rowOptions, _chkIncludeSubDirectories, 1);

        _chkUseRecycleBin = CreateInputCheckBox("删除时放入回收站（推荐）", isChecked: true);
        AddCell(rowOptions, _chkUseRecycleBin, 2);

        // 预览列表：勾选要删除的目录，另外展示层级、完整路径与大小状态
        PreviewList.CheckBoxes = true;
        AddColumn("相对路径", 300);
        AddColumn("层级", 90, HorizontalAlignment.Right);
        AddColumn("完整路径", 360);
        AddColumn("大小状态", 100);

        // 按钮区自右向左排列：先添加的在最右侧
        AddButton("关闭", (_, _) => Close());
        _btnToggleAll = AddButton("全选", (_, _) => ToggleAllChecks());

        // ListView.ItemChecked 使用专用委托类型，先用基类的 SafeHandler 包好再转接
        var itemChecked = SafeHandler((_, _) => UpdateCheckSummary());
        PreviewList.ItemChecked += (sender, e) => itemChecked(sender, e);
        AddButton("删除选中", AsyncHandler(() => DeleteSelectedAsync(_chkUseRecycleBin.Checked)));
        AddButton("永久删除", AsyncHandler(() => DeleteSelectedAsync(useRecycleBin: false)));
        AddButton("扫描空文件夹", AsyncHandler(ScanAsync), primary: true);

        UpdateCheckSummary();
        SetStatus("就绪：请选择根目录后点击「扫描空文件夹」。");
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

    /// <summary>忙碌状态变化：扫描 / 删除期间禁用输入控件与列表，避免操作交叉。</summary>
    protected override void OnBusyChanged(bool busy) => SetInputsEnabled(!busy);

    // ---------------------------------------------------------------- 扫描

    /// <summary>扫描根目录下的空文件夹，并填入预览列表（默认全部勾选）。</summary>
    private async Task ScanAsync()
    {
        if (!TryGetExistingDirectory(_pickerRoot.TextBox, "根目录", out var root))
        {
            return;
        }

        var includeSubDirectories = _chkIncludeSubDirectories.Checked;

        await RunBusyAsync(async token =>
        {
            AppendLog($"开始扫描：{root}（{(includeSubDirectories ? "包含子目录" : "仅当前目录")}）");

            var progress = new Progress<EmptyFolderScanProgress>(report =>
            {
                SetStatus($"正在扫描：已扫描 {report.ScannedDirectories} 个目录，发现 {report.FoundCount} 个空文件夹。");
                SetCountText($"已扫描 {report.ScannedDirectories} 个目录");

                if (report.ScannedDirectories % ScanLogInterval == 0)
                {
                    AppendLog($"扫描中：{report.CurrentPath}");
                }
            });

            var result = await Task.Run(
                () => _service.Scan(root, includeSubDirectories, progress, token),
                token);

            FillScanResult(result);
        }, "正在扫描空文件夹…");
    }

    /// <summary>把扫描结果填入列表：每行默认勾选，状态栏显示摘要，出错时给出警告。</summary>
    /// <param name="result">扫描结果。</param>
    private void FillScanResult(EmptyFolderScanResult result)
    {
        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();

            foreach (var item in result.Items)
            {
                var row = new ListViewItem(item.RelativePath)
                {
                    Tag = item,
                    ToolTipText = item.FullPath
                };

                row.SubItems.Add(item.Depth.ToString());
                row.SubItems.Add(item.FullPath);
                row.SubItems.Add("空目录");

                PreviewList.Items.Add(row);

                // 加入列表后再勾选，确保复选框状态正常刷新
                row.Checked = true;
            }
        }
        finally
        {
            PreviewList.EndUpdate();
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);
        UpdateCheckSummary();

        if (result.Error is not null)
        {
            ShowWarning(result.Error);
        }
    }

    // ---------------------------------------------------------------- 删除

    /// <summary>删除列表中勾选的空文件夹。</summary>
    /// <param name="useRecycleBin">true 放入回收站（可还原）；false 永久删除（不可恢复）。</param>
    private async Task DeleteSelectedAsync(bool useRecycleBin)
    {
        var items = CollectCheckedItems();
        if (items.Count == 0)
        {
            ShowWarning("请先勾选要删除的空文件夹（可先点击「扫描空文件夹」）。");
            return;
        }

        if (!ConfirmDelete(items.Count, useRecycleBin))
        {
            return;
        }

        var paths = items.Select(static item => item.FullPath).ToList();

        await RunBusyAsync(async token =>
        {
            AppendLog($"开始删除 {paths.Count} 个空文件夹（{(useRecycleBin ? "放入回收站" : "永久删除")}）。");

            var progress = new Progress<EmptyFolderDeleteProgress>(report =>
            {
                SetProgress(report.Percent);
                SetStatus($"正在删除（{report.Processed}/{report.Total}）：{report.CurrentPath}");
                SetCountText($"{report.Processed}/{report.Total}");

                if (report.Processed % DeleteLogInterval == 0)
                {
                    AppendLog($"已处理 {report.Processed}/{report.Total}：{report.CurrentPath}");
                }
            });

            var result = await Task.Run(
                () => _service.Delete(paths, useRecycleBin, progress, token),
                token);

            ApplyDeleteResult(result);
        }, useRecycleBin ? "正在把空文件夹放入回收站…" : "正在永久删除空文件夹…");
    }

    /// <summary>删除前的二次确认（永久删除时明确「不可恢复」）。</summary>
    /// <param name="count">待删除数量。</param>
    /// <param name="useRecycleBin">是否放入回收站。</param>
    private bool ConfirmDelete(int count, bool useRecycleBin)
    {
        var method = useRecycleBin
            ? "删除方式：放入回收站（需要时可从回收站还原）。"
            : "删除方式：永久删除（不可恢复，不会放入回收站）。";

        return ConfirmDanger(
            $"确定要删除选中的 {count} 个空文件夹吗？{Environment.NewLine}{Environment.NewLine}{method}");
    }

    /// <summary>把删除结果回填到界面：成功的行从列表移除，失败项汇总提示。</summary>
    /// <param name="result">删除结果。</param>
    private void ApplyDeleteResult(EmptyFolderDeleteResult result)
    {
        var removed = result.Items
            .Where(static item => item.Success)
            .Select(static item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removed.Count > 0)
        {
            PreviewList.BeginUpdate();
            try
            {
                for (var index = PreviewList.Items.Count - 1; index >= 0; index--)
                {
                    var row = PreviewList.Items[index];
                    if (row.Tag is EmptyFolderItem item && removed.Contains(item.FullPath))
                    {
                        PreviewList.Items.RemoveAt(index);
                    }
                }
            }
            finally
            {
                PreviewList.EndUpdate();
            }
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);
        UpdateCheckSummary();

        var failures = result.Items.Where(static item => !item.Success).ToList();
        if (failures.Count > 0)
        {
            ShowWarning(BuildFailureMessage(failures));
        }
    }

    /// <summary>拼装失败提示（最多列出前若干条，其余引导用户查看运行日志）。</summary>
    /// <param name="failures">失败明细。</param>
    private static string BuildFailureMessage(IReadOnlyList<EmptyFolderDeleteItem> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"有 {failures.Count} 个空文件夹删除失败：");

        foreach (var failure in failures.Take(MaxDisplayedFailures))
        {
            builder.AppendLine($"{failure.Path}：{failure.Error}");
        }

        if (failures.Count > MaxDisplayedFailures)
        {
            builder.AppendLine($"……其余 {failures.Count - MaxDisplayedFailures} 条请查看运行日志。");
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- 勾选

    /// <summary>收集列表中已勾选的空文件夹条目。</summary>
    private List<EmptyFolderItem> CollectCheckedItems()
    {
        var items = new List<EmptyFolderItem>();

        foreach (ListViewItem row in PreviewList.CheckedItems)
        {
            if (row.Tag is EmptyFolderItem item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>「全选 / 全不选」：已经全选时取消全部勾选，否则全部勾选。</summary>
    private void ToggleAllChecks()
    {
        if (PreviewList.Items.Count == 0)
        {
            ShowInfo("列表为空，请先点击「扫描空文件夹」。");
            return;
        }

        var check = PreviewList.CheckedItems.Count < PreviewList.Items.Count;

        PreviewList.BeginUpdate();
        try
        {
            foreach (ListViewItem row in PreviewList.Items)
            {
                row.Checked = check;
            }
        }
        finally
        {
            PreviewList.EndUpdate();
        }

        UpdateCheckSummary();
    }

    /// <summary>刷新右侧计数与「全选 / 全不选」按钮文字。</summary>
    private void UpdateCheckSummary()
    {
        var total = PreviewList.Items.Count;
        var checkedCount = PreviewList.CheckedItems.Count;

        SetCountText(total == 0 ? "共 0 项" : $"共 {total} 项，已勾选 {checkedCount} 项");
        _btnToggleAll.Text = total > 0 && checkedCount >= total ? "全不选" : "全选";
    }
}
