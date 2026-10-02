using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.Tools.FileMaster.UI;

/// <summary>
/// 「文件夹差异比对」窗口：遍历左右两个目录，按「大小 + 修改时间 / 仅大小 / SHA256 哈希」比较文件，
/// 用左右两栏的形式列出「相同 / 仅左侧 / 仅右侧 / 内容不同」四类结果，并可导出文本报告。
/// </summary>
/// <remarks>
/// <para>继承 <see cref="FeatureDialogBase"/>：比对在后台线程执行，进度经 <see cref="Progress{T}"/> 回填到界面，
/// 单个功能出错不会影响主窗口。</para>
/// <para>行颜色：绿色=相同，蓝色=仅左侧，红色=仅右侧，黄褐色=内容不同。</para>
/// </remarks>
public sealed class FolderDiffDialog : FeatureDialogBase
{
    /// <summary>时间显示格式。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>大小 / 时间等空值占位符。</summary>
    private const string NoValueText = "-";

    /// <summary>「比较方式」下拉框的选项（顺序与 <see cref="ReadOptions"/> 中的映射一致）。</summary>
    private static readonly string[] CompareModes =
    {
        "大小 + 修改时间",
        "仅大小",
        "SHA256 哈希"
    };

    /// <summary>文件夹差异比对服务。</summary>
    private readonly FolderDiffService _service;

    /// <summary>左侧目录选择控件（目录文本框 + 浏览…按钮）。</summary>
    private readonly FolderPickerBox _txtLeft;

    /// <summary>右侧目录选择控件（目录文本框 + 浏览…按钮）。</summary>
    private readonly FolderPickerBox _txtRight;

    /// <summary>「比较方式」下拉框。</summary>
    private readonly ComboBox _cboCompareMode = new();

    /// <summary>「包含子目录」复选框（默认勾选）。</summary>
    private readonly CheckBox _chkIncludeSubDirectories = new();

    /// <summary>「忽略名称」输入框（分号分隔，可留空）。</summary>
    private readonly TextBox _txtExcludeNames;

    /// <summary>最近一次比对结果（供「导出报告」使用）。</summary>
    private FolderDiffResult? _result;

    /// <summary>创建「文件夹差异比对」窗口。</summary>
    /// <param name="logger">日志记录器（可为 null）。</param>
    public FolderDiffDialog(Logger? logger)
        : base("文件夹差异比对", logger)
    {
        _service = new FolderDiffService(logger);

        // ---------- 第 1 行：左侧目录 ----------
        var rowLeft = AddInputRow(InputLabelWidth, -100);
        _txtLeft = CreateFolderPicker("请选择左侧（基准）目录");
        AddField(rowLeft, "左目录：", _txtLeft, 0);

        // ---------- 第 2 行：右侧目录 ----------
        var rowRight = AddInputRow(InputLabelWidth, -100);
        _txtRight = CreateFolderPicker("请选择右侧（对比）目录");
        AddField(rowRight, "右目录：", _txtRight, 0);

        // ---------- 第 3 行：比较方式、是否递归、忽略名单 ----------
        var rowOptions = AddInputRow(InputLabelWidth, 150, 106, InputLabelWidth, -100);

        _cboCompareMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboCompareMode.Items.AddRange(CompareModes);
        _cboCompareMode.SelectedIndex = 0;
        AddField(rowOptions, "比较方式：", _cboCompareMode, 0);

        _chkIncludeSubDirectories.Text = "包含子目录";
        _chkIncludeSubDirectories.Checked = true;
        _chkIncludeSubDirectories.AutoSize = true;
        AddCell(rowOptions, _chkIncludeSubDirectories, 2);

        _txtExcludeNames = CreateInputTextBox();
        _txtExcludeNames.PlaceholderText = "以 ; 分隔，可留空";
        AddField(rowOptions, "忽略名称：", _txtExcludeNames, 3);

        // ---------- 结果列表（左右两栏风格） ----------
        AddColumn("状态", 80);
        AddColumn("相对路径", 280);
        AddColumn("左侧大小", 100, HorizontalAlignment.Right);
        AddColumn("右侧大小", 100, HorizontalAlignment.Right);
        AddColumn("左侧修改时间", 145);
        AddColumn("右侧修改时间", 145);
        AddColumn("说明", 240);

        // ---------- 按钮区（自右向左排列：先添加的在最右侧） ----------
        AddButton("关闭", (_, _) => Close());
        AddButton("开始比对", AsyncHandler(CompareAsync), primary: true);
        AddButton("导出报告", AsyncHandler(ExportReportAsync));

        SetStatus("请选择左右两个目录，然后点击「开始比对」。");
        AppendLog("提示：绿色=相同，蓝色=仅左侧，红色=仅右侧，黄褐色=内容不同。");
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

    /// <summary>忙碌状态变化：比对 / 导出期间禁用输入控件与列表，避免操作交叉。</summary>
    /// <param name="busy">是否忙碌。</param>
    protected override void OnBusyChanged(bool busy) => SetInputsEnabled(!busy);

    // ---------------------------------------------------------------- 比对

    /// <summary>比对左右两个目录，并把结果填入列表。</summary>
    private async Task CompareAsync()
    {
        if (!TryGetExistingDirectory(_txtLeft.TextBox, "左侧目录", out var left))
        {
            return;
        }

        if (!TryGetExistingDirectory(_txtRight.TextBox, "右侧目录", out var right))
        {
            return;
        }

        if (IsSameDirectory(left, right))
        {
            ShowWarning("左右两个目录相同，无需比对，请重新选择。");
            return;
        }

        var options = ReadOptions(left, right);

        FolderDiffResult? result = null;
        var progress = new Progress<FolderDiffProgress>(report =>
        {
            SetStatus($"正在比对（已比较 {report.ComparedCount} 项）：{report.CurrentPath}");
            SetCountText($"已比较 {report.ComparedCount} 项");
        });

        var ok = await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始比对：{left} ⇄ {right}（{DescribeMode(options)}，{DescribeScope(options)}，{DescribeExcludes(options)}）");

                result = await Task.Run(
                    () => _service.Compare(options, progress, token),
                    token).ConfigureAwait(true);

                if (result is not null)
                {
                    FillResult(result);
                }
            },
            "正在比对文件夹差异…");

        if (!ok || result is null || !IsUiUsable)
        {
            return;
        }

        // 出错时结果不可用于导出报告
        _result = result.Error is null ? result : null;

        if (result.Error is not null)
        {
            ShowWarning("比对失败：" + Environment.NewLine + result.Error);
        }
    }

    /// <summary>把比对结果填入列表（左右两栏风格），并按状态设置行颜色。</summary>
    /// <param name="result">比对结果。</param>
    private void FillResult(FolderDiffResult result)
    {
        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();

            foreach (var item in result.Items)
            {
                var row = new ListViewItem(FolderDiffService.StatusText(item.Status))
                {
                    Tag = item,
                    ForeColor = ResolveRowColor(item.Status),
                    ToolTipText = item.LeftPath ?? item.RightPath ?? item.RelativePath
                };

                row.SubItems.Add(item.RelativePath);
                row.SubItems.Add(FormatSize(item.LeftSize));
                row.SubItems.Add(FormatSize(item.RightSize));
                row.SubItems.Add(FormatTime(item.LeftModified));
                row.SubItems.Add(FormatTime(item.RightModified));
                row.SubItems.Add(item.Message);

                PreviewList.Items.Add(row);
            }
        }
        finally
        {
            PreviewList.EndUpdate();
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);
        SetCountText(
            $"共 {result.Items.Count} 项：相同 {result.SameCount}，仅左侧 {result.LeftOnlyCount}，" +
            $"仅右侧 {result.RightOnlyCount}，内容不同 {result.DifferentCount}");
    }

    /// <summary>行颜色：绿色=相同，蓝色=仅左侧，红色=仅右侧，黄褐色=内容不同。</summary>
    /// <param name="status">差异状态。</param>
    private static Color ResolveRowColor(DiffStatus status) => status switch
    {
        DiffStatus.Same => Color.Green,
        DiffStatus.LeftOnly => Color.RoyalBlue,
        DiffStatus.RightOnly => Color.Red,
        _ => Color.DarkGoldenrod
    };

    // ---------------------------------------------------------------- 导出

    /// <summary>把最近一次比对结果导出为 UTF-8（带 BOM）文本报告。</summary>
    private async Task ExportReportAsync()
    {
        FolderDiffResult result;
        if (_result is null)
        {
            ShowWarning("请先点击「开始比对」，生成结果后再导出报告。");
            return;
        }

        result = _result;

        string path;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出文件夹差异报告",
                Filter = "文本文件 (*.txt)|*.txt",
                FileName = "文件夹差异报告.txt",
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
            Logger?.Error("打开保存对话框失败。", ex);
            ShowError("打开保存对话框时发生错误：" + Environment.NewLine + ex.Message);
            return;
        }

        var ok = await RunBusyAsync(
            async token =>
            {
                var text = FolderDiffService.BuildReport(result);

                // 带 BOM 的 UTF-8：Windows 记事本可直接识别
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(true), token).ConfigureAwait(true);

                if (!IsUiUsable)
                {
                    return;
                }

                SetStatus($"报告已导出：{path}");
                AppendLog($"报告已导出：{path}");
            },
            "正在导出差异报告…");

        if (ok && IsUiUsable)
        {
            ShowInfo("差异报告已导出：" + Environment.NewLine + path);
        }
    }

    // ---------------------------------------------------------------- 选项与格式化

    /// <summary>按界面输入组装比对选项。</summary>
    /// <param name="left">左侧目录。</param>
    /// <param name="right">右侧目录。</param>
    private FolderDiffOptions ReadOptions(string left, string right) => new()
    {
        LeftDirectory = left,
        RightDirectory = right,
        IncludeSubDirectories = _chkIncludeSubDirectories.Checked,
        CompareMode = _cboCompareMode.SelectedIndex switch
        {
            1 => DiffCompareMode.SizeOnly,
            2 => DiffCompareMode.Hash,
            _ => DiffCompareMode.SizeAndTime
        },
        ExcludeNames = NullIfEmpty(_txtExcludeNames.Text)
    };

    /// <summary>判断两个目录是否是同一个目录（同目录比对没有意义）。</summary>
    /// <param name="left">左侧目录。</param>
    /// <param name="right">右侧目录。</param>
    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>把比对方式描述成中文（用于日志）。</summary>
    /// <param name="options">比对选项。</param>
    private static string DescribeMode(FolderDiffOptions options) => options.CompareMode switch
    {
        DiffCompareMode.SizeOnly => "仅大小",
        DiffCompareMode.Hash => "SHA256 哈希",
        _ => "大小 + 修改时间"
    };

    /// <summary>把递归范围描述成中文（用于日志）。</summary>
    /// <param name="options">比对选项。</param>
    private static string DescribeScope(FolderDiffOptions options)
        => options.IncludeSubDirectories ? "包含子目录" : "仅当前目录";

    /// <summary>把忽略名单描述成中文（用于日志）。</summary>
    /// <param name="options">比对选项。</param>
    private static string DescribeExcludes(FolderDiffOptions options)
    {
        var excludes = FolderDiffService.ParseExcludes(options.ExcludeNames);
        return excludes.Count == 0 ? "不忽略任何名称" : $"忽略 {excludes.Count} 个名称";
    }

    /// <summary>大小格式化：字节数加千分位（无值时显示 <see cref="NoValueText"/>）。</summary>
    /// <param name="size">字节数。</param>
    private static string FormatSize(long? size)
        => size is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : NoValueText;

    /// <summary>时间格式化（无值时显示 <see cref="NoValueText"/>）。</summary>
    /// <param name="time">修改时间。</param>
    private static string FormatTime(DateTime? time)
        => time is { } value ? value.ToString(TimeFormat, CultureInfo.InvariantCulture) : NoValueText;

    /// <summary>空字符串转 null（选项里用 null 表示「未设置」）。</summary>
    /// <param name="text">输入文本。</param>
    private static string? NullIfEmpty(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
