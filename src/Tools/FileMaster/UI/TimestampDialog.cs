using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
/// 「时间戳批量修改」窗口：支持「修改时间 = 创建时间」与「统一改成指定时间」两种规则，
/// 可作用于文件 / 文件夹 / 两者，先预览出每一项的当前时间与目标时间，确认后再批量写入。
/// </summary>
/// <remarks>
/// 预览与写入都在后台线程执行；写入前会二次确认，界面上列出的「当前时间」在写入后即失效，
/// 因此写入完成后会清空列表并提示重新预览。
/// </remarks>
public sealed class TimestampDialog : FeatureDialogBase
{
    /// <summary>时间显示格式。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>无需修改（或无计划值）时显示的占位符。</summary>
    private const string NoValueText = "-";

    /// <summary>写入过程中每处理多少项往日志里写一行。</summary>
    private const int ApplyLogInterval = 50;

    /// <summary>时间戳服务。</summary>
    private readonly TimestampService _service;

    /// <summary>根目录选择控件（文本框 +「浏览…」按钮）。</summary>
    private readonly FolderPickerBox _pickerRoot;

    /// <summary>处理对象下拉框（文件 / 文件夹 / 两者）。</summary>
    private readonly ComboBox _cboTarget;

    /// <summary>「包含子目录」复选框。</summary>
    private readonly CheckBox _chkIncludeSubDirectories;

    /// <summary>「修改时间 = 创建时间」单选按钮（默认选中）。</summary>
    private readonly RadioButton _rdoLastWriteEqualsCreation;

    /// <summary>「统一改成指定时间」单选按钮。</summary>
    private readonly RadioButton _rdoFixedTime;

    /// <summary>指定时间选择框。</summary>
    private readonly DateTimePicker _dtpFixedTime;

    /// <summary>「创建时间」复选框。</summary>
    private readonly CheckBox _chkCreationTime;

    /// <summary>「修改时间」复选框。</summary>
    private readonly CheckBox _chkLastWriteTime;

    /// <summary>「访问时间」复选框。</summary>
    private readonly CheckBox _chkLastAccessTime;

    /// <summary>最近一次预览的结果（供「应用修改」使用；与列表内容保持一致）。</summary>
    private IReadOnlyList<TimestampPreviewItem> _previewItems = Array.Empty<TimestampPreviewItem>();

    /// <summary>创建「时间戳批量修改」窗口。</summary>
    /// <param name="logger">日志记录器（可为 null）。</param>
    public TimestampDialog(Logger? logger)
        : base("时间戳批量修改", logger)
    {
        _service = new TimestampService(logger);

        // ---------- 第 1 行：根目录 ----------
        var rowDirectory = AddInputRow(InputLabelWidth, -100);
        _pickerRoot = CreateFolderPicker("请选择要处理的根目录");
        AddField(rowDirectory, "根目录：", _pickerRoot, 0);

        // ---------- 第 2 行：处理对象 + 是否递归 ----------
        var rowScope = AddInputRow(InputLabelWidth, 140, -100);

        _cboTarget = CreateInputComboBox("文件", "文件夹", "文件 + 文件夹");
        AddField(rowScope, "处理对象：", _cboTarget, 0);

        _chkIncludeSubDirectories = CreateInputCheckBox("包含子目录", isChecked: true);
        AddCell(rowScope, _chkIncludeSubDirectories, 2);

        // ---------- 第 3 行：处理模式 ----------
        var rowMode = AddInputRow(InputLabelWidth, 200, 280);

        _rdoLastWriteEqualsCreation = CreateInputRadioButton("修改时间 = 创建时间", isChecked: true);
        AddCell(rowMode, _rdoLastWriteEqualsCreation, 1);

        _rdoFixedTime = CreateInputRadioButton("统一改成指定时间");
        AddCell(rowMode, _rdoFixedTime, 2);

        // ---------- 第 4 行：指定时间与要写入的时间字段 ----------
        var rowFields = AddInputRow(InputLabelWidth, 190, 110, 110, 110);

        _dtpFixedTime = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = TimeFormat,
            ShowCheckBox = false,
            ShowUpDown = true,
            Value = DateTime.Now,
            AutoSize = false
        };
        AddField(rowFields, "时间字段：", _dtpFixedTime, 0);

        _chkCreationTime = CreateInputCheckBox("创建时间", isChecked: true);
        AddCell(rowFields, _chkCreationTime, 2);

        _chkLastWriteTime = CreateInputCheckBox("修改时间", isChecked: true);
        AddCell(rowFields, _chkLastWriteTime, 3);

        _chkLastAccessTime = CreateInputCheckBox("访问时间");
        AddCell(rowFields, _chkLastAccessTime, 4);

        // 全部控件创建完成后再挂事件：切换模式时联动「指定时间」相关控件的可用性
        _rdoFixedTime.CheckedChanged += SafeHandler((_, _) => UpdateFixedTimeControls());

        // 预览列表：显示当前时间与计划写入的时间
        AddColumn("类型", 70);
        AddColumn("名称", 250);
        AddColumn("当前创建时间", 150);
        AddColumn("当前修改时间", 150);
        AddColumn("新创建时间", 150);
        AddColumn("新修改时间", 150);

        // 按钮区自右向左排列：先添加的在最右侧
        AddButton("关闭", (_, _) => Close());
        AddButton("应用修改", AsyncHandler(ApplyAsync));
        AddButton("预览", AsyncHandler(PreviewAsync), primary: true);

        UpdateFixedTimeControls();
        SetStatus("就绪：请选择根目录与规则后点击「预览」。");
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

    /// <summary>忙碌状态变化：执行期间禁用输入控件与列表，避免规则在扫描中途被改动。</summary>
    protected override void OnBusyChanged(bool busy)
    {
        SetInputsEnabled(!busy);

        if (!busy)
        {
            // 统一恢复可用性后，再按当前模式刷新「统一改成指定时间」相关控件
            UpdateFixedTimeControls();
        }
    }

    /// <summary>按当前模式设置「指定时间」及三个字段复选框是否可用。</summary>
    private void UpdateFixedTimeControls()
    {
        var enabled = _rdoFixedTime.Checked;

        _dtpFixedTime.Enabled = enabled;
        _chkCreationTime.Enabled = enabled;
        _chkLastWriteTime.Enabled = enabled;
        _chkLastAccessTime.Enabled = enabled;
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>按当前规则扫描根目录，生成预览并填入列表。</summary>
    private async Task PreviewAsync()
    {
        if (!TryGetExistingDirectory(_pickerRoot.TextBox, "根目录", out var root))
        {
            return;
        }

        var options = BuildOptions();
        var ruleError = TimestampService.Validate(options);
        if (ruleError is not null)
        {
            ShowWarning(ruleError);
            return;
        }

        await RunBusyAsync(async token =>
        {
            AppendLog($"开始生成预览：{root}（{DescribeOptions(options)}）");

            var result = await Task.Run(
                () => _service.BuildPreviewFromDirectory(root, options, token),
                token);

            FillPreview(result);
        }, "正在扫描并生成预览…");
    }

    /// <summary>把预览结果填入列表：无变化的行用灰色显示，状态栏显示摘要。</summary>
    /// <param name="result">预览结果。</param>
    private void FillPreview(TimestampPreviewResult result)
    {
        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();

            foreach (var item in result.Items)
            {
                var name = Path.GetFileName(item.Path);
                if (name.Length == 0)
                {
                    name = item.Path;
                }

                var row = new ListViewItem(item.IsDirectory ? "文件夹" : "文件")
                {
                    Tag = item,
                    ToolTipText = item.Path
                };

                row.SubItems.Add(name);
                row.SubItems.Add(FormatTime(item.CurrentCreationTime));
                row.SubItems.Add(FormatTime(item.CurrentLastWriteTime));
                row.SubItems.Add(FormatNewTime(item.NewCreationTime, item.CurrentCreationTime));
                row.SubItems.Add(FormatNewTime(item.NewLastWriteTime, item.CurrentLastWriteTime));

                // 无变化的行整行置灰，方便一眼看出哪些会被修改
                if (!item.WillChange)
                {
                    row.ForeColor = Color.Gray;
                }

                PreviewList.Items.Add(row);
            }
        }
        finally
        {
            PreviewList.EndUpdate();
        }

        // 列表内容与 _previewItems 保持一致，只有扫描成功时才替换
        if (result.Error is null)
        {
            _previewItems = result.Items;
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);
        SetCountText($"共 {PreviewList.Items.Count} 项");

        if (result.Error is not null)
        {
            ShowWarning(result.Error);
        }
    }

    // ---------------------------------------------------------------- 应用

    /// <summary>应用最近一次预览的结果（只处理有变化的条目），执行前二次确认。</summary>
    private async Task ApplyAsync()
    {
        var items = _previewItems;

        if (items.Count == 0)
        {
            ShowWarning("请先点击预览生成待处理列表，再执行「应用修改」。");
            return;
        }

        var changeCount = items.Count(static item => item.WillChange);
        if (changeCount == 0)
        {
            ShowInfo("当前预览中没有需要修改的条目。");
            return;
        }

        var confirmed = Confirm(
            $"将按当前预览结果修改 {changeCount} 项的时间戳（共预览 {items.Count} 项，其中 {changeCount} 项有变化）。" +
            Environment.NewLine + Environment.NewLine +
            "时间戳修改后无法通过「撤销」还原，确定继续吗？");

        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            AppendLog($"开始修改时间戳：共 {changeCount} 项。");

            var progress = new Progress<TimestampProgress>(report =>
            {
                SetProgress(report.Percent);
                SetStatus($"正在修改（{report.Processed}/{report.Total}）：{report.CurrentPath}");
                SetCountText($"{report.Processed}/{report.Total}");

                if (report.Processed % ApplyLogInterval == 0)
                {
                    AppendLog($"已处理 {report.Processed}/{report.Total}：{report.CurrentPath}");
                }
            });

            var result = await Task.Run(() => _service.Apply(items, progress, token), token);

            ApplyResult(result);
        }, "正在修改时间戳…");
    }

    /// <summary>把修改结果回填到界面：列表中的「当前时间」已过期，清空列表并提示重新预览。</summary>
    /// <param name="result">修改结果。</param>
    private void ApplyResult(TimestampResult result)
    {
        SetStatus(result.Summary);
        AppendLog(result.Summary);

        PreviewList.Items.Clear();
        _previewItems = Array.Empty<TimestampPreviewItem>();
        SetCountText("共 0 项");
        AppendLog("列表中的时间戳已过期，如需继续处理请重新点击「预览」。");

        var failures = result.Items.Where(static item => !item.Success).ToList();
        if (failures.Count > 0)
        {
            ShowWarning(BuildFailureMessage(failures));
        }
    }

    /// <summary>拼装失败提示（最多列出前若干条，其余引导用户查看运行日志）。</summary>
    /// <param name="failures">失败明细。</param>
    private static string BuildFailureMessage(IReadOnlyList<TimestampResultItem> failures)
    {
        const int maxDisplayed = 10;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"有 {failures.Count} 项时间戳修改失败：");

        foreach (var failure in failures.Take(maxDisplayed))
        {
            builder.AppendLine($"{failure.Path}：{failure.Error}");
        }

        if (failures.Count > maxDisplayed)
        {
            builder.AppendLine($"……其余 {failures.Count - maxDisplayed} 条请查看运行日志。");
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- 规则

    /// <summary>按当前界面选择组装时间戳规则。</summary>
    private TimestampOptions BuildOptions() => new()
    {
        Target = GetTarget(),
        IncludeSubDirectories = _chkIncludeSubDirectories.Checked,
        Mode = _rdoLastWriteEqualsCreation.Checked ? TimestampMode.LastWriteEqualsCreation : TimestampMode.SetFixedTime,
        FixedTime = _dtpFixedTime.Value,
        Fields = GetFields()
    };

    /// <summary>读取处理对象下拉框。</summary>
    private TimestampTarget GetTarget() => _cboTarget.SelectedIndex switch
    {
        1 => TimestampTarget.Folders,
        2 => TimestampTarget.Both,
        _ => TimestampTarget.Files
    };

    /// <summary>读取「统一改成指定时间」要修改的时间字段。</summary>
    private TimestampField GetFields()
    {
        var fields = TimestampField.None;

        if (_chkCreationTime.Checked)
        {
            fields |= TimestampField.CreationTime;
        }

        if (_chkLastWriteTime.Checked)
        {
            fields |= TimestampField.LastWriteTime;
        }

        if (_chkLastAccessTime.Checked)
        {
            fields |= TimestampField.LastAccessTime;
        }

        return fields;
    }

    /// <summary>把规则描述成一句话（用于日志）。</summary>
    /// <param name="options">规则。</param>
    private static string DescribeOptions(TimestampOptions options)
    {
        var target = options.Target switch
        {
            TimestampTarget.Files => "文件",
            TimestampTarget.Folders => "文件夹",
            _ => "文件 + 文件夹"
        };

        var scope = options.IncludeSubDirectories ? "包含子目录" : "仅当前目录";
        var mode = options.Mode == TimestampMode.LastWriteEqualsCreation
            ? "修改时间 = 创建时间"
            : $"统一改成 {options.FixedTime.ToString(TimeFormat, CultureInfo.InvariantCulture)}（{DescribeFields(options.Fields)}）";

        return $"{target}，{scope}，规则：{mode}";
    }

    /// <summary>把时间字段枚举描述成中文。</summary>
    /// <param name="fields">时间字段。</param>
    private static string DescribeFields(TimestampField fields)
    {
        var parts = new List<string>(3);

        if (fields.HasFlag(TimestampField.CreationTime))
        {
            parts.Add("创建时间");
        }

        if (fields.HasFlag(TimestampField.LastWriteTime))
        {
            parts.Add("修改时间");
        }

        if (fields.HasFlag(TimestampField.LastAccessTime))
        {
            parts.Add("访问时间");
        }

        return parts.Count == 0 ? "未选择字段" : string.Join("、", parts);
    }

    // ---------------------------------------------------------------- 格式化

    /// <summary>时间格式化（无值时显示 <see cref="NoValueText"/>）。</summary>
    /// <param name="time">时间。</param>
    private static string FormatTime(DateTime? time)
        => time is { } value ? value.ToString(TimeFormat, CultureInfo.InvariantCulture) : NoValueText;

    /// <summary>新时间格式化：未计划修改或与当前时间相同时显示 <see cref="NoValueText"/>。</summary>
    /// <param name="newTime">计划写入的时间。</param>
    /// <param name="currentTime">当前时间。</param>
    private static string FormatNewTime(DateTime? newTime, DateTime currentTime)
        => newTime is { } value && value != currentTime
            ? value.ToString(TimeFormat, CultureInfo.InvariantCulture)
            : NoValueText;
}
