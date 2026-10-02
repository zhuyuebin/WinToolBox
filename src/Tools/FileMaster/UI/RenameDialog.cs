using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WinToolBox.Core;
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.Tools.FileMaster.UI;

/// <summary>
/// 批量重命名窗口：支持文件 / 文件夹 / 两者，规则包括删除字符、查找替换（文本或正则）、
/// 前后缀、序号、日期与统一扩展名；先预览再执行。
/// </summary>
public sealed class RenameDialog : FeatureDialogBase
{
    /// <summary>供「日期格式」下拉框选择的常用格式。</summary>
    private static readonly string[] DateFormats =
    {
        "（不添加）",
        "yyyyMMdd",
        "yyyy-MM-dd",
        "yyyyMMdd-HHmmss",
        "HHmmss"
    };

    private readonly FileRenamerService _service;

    private readonly FolderPickerBox _pickerDirectory;
    private readonly ComboBox _cboTarget = new();
    private readonly CheckBox _chkSubDirectories = new();
    private readonly CheckBox _chkKeepExtension = new();
    private readonly CheckBox _chkOrderByCreation = new();

    private readonly TextBox _txtRemoveChars;
    private readonly TextBox _txtFind;
    private readonly TextBox _txtReplace;
    private readonly CheckBox _chkUseRegex = new();
    private readonly CheckBox _chkCaseSensitive = new();

    private readonly TextBox _txtPrefix;
    private readonly TextBox _txtSuffix;
    private readonly CheckBox _chkSequence = new();
    private readonly NumericUpDown _numSequenceStart = new();
    private readonly NumericUpDown _numSequenceDigits = new();
    private readonly TextBox _txtSeparator;
    private readonly ComboBox _cboDateFormat = new();
    private readonly TextBox _txtNewExtension;

    /// <summary>实时示例文字（随规则变化更新）。</summary>
    private readonly Label _lblExample = new();

    /// <summary>示例用文件名。</summary>
    private const string SampleFileName = "示例 2024.txt";

    private RenamePlan? _plan;

    /// <summary>创建批量重命名窗口。</summary>
    public RenameDialog(Logger? logger)
        : base("批量重命名", logger)
    {
        _service = new FileRenamerService(logger);

        PreviewList.CheckBoxes = false;

        // ---------- 第 1 行：目标目录 ----------
        var rowDirectory = AddInputRow(InputLabelWidth, -100);
        _pickerDirectory = CreateFolderPicker("请选择要批量重命名的目录");
        AddField(rowDirectory, "目标目录：", _pickerDirectory, 0);
        SetInputTooltip(_pickerDirectory, "要批量重命名的目录；建议先复制一份做试验。");

        // ---------- 第 2 行：处理对象与范围 ----------
        var rowScope = AddInputRow(InputLabelWidth, 124, -34, -33, -33);

        _cboTarget.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboTarget.Items.AddRange(new object[] { "文件", "文件夹", "文件 + 文件夹" });
        _cboTarget.SelectedIndex = 0;
        AddField(rowScope, "处理对象：", _cboTarget, 0);
        SetInputTooltip(_cboTarget, "选择要重命名的对象：文件、文件夹，或两者都处理。");

        _chkSubDirectories.Text = "包含子目录";
        _chkSubDirectories.AutoSize = true;
        AddCell(rowScope, _chkSubDirectories, 2);
        SetInputTooltip(_chkSubDirectories, "勾选后连子目录里的文件一起处理。");

        _chkKeepExtension.Text = "保留扩展名";
        _chkKeepExtension.Checked = true;
        _chkKeepExtension.AutoSize = true;
        AddCell(rowScope, _chkKeepExtension, 3);
        SetInputTooltip(_chkKeepExtension, "勾选后只改文件名主体，扩展名（.txt 之类）保持不变。");

        _chkOrderByCreation.Text = "序号按创建时间排序";
        _chkOrderByCreation.AutoSize = true;
        AddCell(rowScope, _chkOrderByCreation, 4);
        SetInputTooltip(_chkOrderByCreation, "勾选后序号按文件创建时间先后编号，不勾选则按名称排序。");

        // ---------- 第 3 行：删除字符与查找替换 ----------
        var rowReplace = AddInputRow(InputLabelWidth, 150, 54, 150, 64, 150, -50, -50);

        _txtRemoveChars = CreateInputTextBox();
        _txtRemoveChars.PlaceholderText = "如 空格-_";
        AddField(rowReplace, "删除字符：", _txtRemoveChars, 0);
        SetInputTooltip(_txtRemoveChars, "这里填写的每一个字符都会被删掉，例如填「 -」就会去掉名称里的空格和短横线。");

        _txtFind = CreateInputTextBox();
        _txtFind.PlaceholderText = "要查找的内容";
        AddField(rowReplace, "查找：", _txtFind, 2, 1, 54);
        SetInputTooltip(_txtFind, "要查找并替换掉的文字；勾选「使用正则」时按正则表达式解释。");

        _txtReplace = CreateInputTextBox();
        _txtReplace.PlaceholderText = "留空＝删除";
        AddField(rowReplace, "替换为：", _txtReplace, 4, 1, 64);
        SetInputTooltip(_txtReplace, "替换成什么；留空表示直接把查找到的内容删掉。正则模式下可用 $1 引用分组。");

        _chkUseRegex.Text = "使用正则";
        _chkUseRegex.AutoSize = true;
        AddCell(rowReplace, _chkUseRegex, 6);
        SetInputTooltip(_chkUseRegex, "把「查找」当作正则表达式，例如 (\\d{4}) 匹配 4 位数字。");

        _chkCaseSensitive.Text = "区分大小写";
        _chkCaseSensitive.Checked = true;
        _chkCaseSensitive.AutoSize = true;
        AddCell(rowReplace, _chkCaseSensitive, 7);
        SetInputTooltip(_chkCaseSensitive, "查找时是否区分英文大小写；正则模式下由表达式自身决定。");

        // ---------- 第 4 行：前后缀、扩展名、日期 ----------
        var rowAffix = AddInputRow(InputLabelWidth, 130, 54, 130, 92, 120, 92, 130, -100);

        _txtPrefix = CreateInputTextBox();
        _txtPrefix.PlaceholderText = "加在名称最前";
        AddField(rowAffix, "前缀：", _txtPrefix, 0);
        SetInputTooltip(_txtPrefix, "在原名称最前面加上这段文字。");

        _txtSuffix = CreateInputTextBox();
        _txtSuffix.PlaceholderText = "加在扩展名前";
        AddField(rowAffix, "后缀：", _txtSuffix, 2, 1, 54);
        SetInputTooltip(_txtSuffix, "加在原名称后面（扩展名之前）。");

        _txtNewExtension = CreateInputTextBox();
        _txtNewExtension.PlaceholderText = "留空＝不改";
        AddField(rowAffix, "新扩展名：", _txtNewExtension, 4, 1, 92);
        SetInputTooltip(_txtNewExtension, "统一把扩展名换成这个（含点，例如 .txt）；留空表示不改扩展名。");

        _cboDateFormat.DropDownStyle = ComboBoxStyle.DropDown;
        _cboDateFormat.Items.AddRange(DateFormats);
        _cboDateFormat.Text = DateFormats[0];
        AddField(rowAffix, "日期格式：", _cboDateFormat, 6, 1, 92);
        SetInputTooltip(_cboDateFormat, "在名称中加入日期；选「（不添加）」表示不加。日期取文件的修改时间。");

        // ---------- 第 5 行：序号 ----------
        var rowSequence = AddInputRow(InputLabelWidth, 96, 68, 76, 68, 66, 76, 90, -100);

        _chkSequence.Text = "添加序号";
        _chkSequence.AutoSize = true;
        AddCell(rowSequence, _chkSequence, 1);
        SetInputTooltip(_chkSequence, "给名称加上 001、002… 这样的连续编号。");

        _numSequenceStart.Minimum = 0;
        _numSequenceStart.Maximum = 999999;
        _numSequenceStart.Value = 1;
        AddField(rowSequence, "起始：", _numSequenceStart, 2, 1, 68);
        SetInputTooltip(_numSequenceStart, "第一个序号的值，例如从 1 开始。");

        _numSequenceDigits.Minimum = FileRenamerService.MinSequenceDigits;
        _numSequenceDigits.Maximum = FileRenamerService.MaxSequenceDigits;
        _numSequenceDigits.Value = 3;
        AddField(rowSequence, "位数：", _numSequenceDigits, 4, 1, 68);
        SetInputTooltip(_numSequenceDigits, "序号补零后的位数，3 表示 001、002。");

        _txtSeparator = CreateInputTextBox("_");
        AddField(rowSequence, "分隔符：", _txtSeparator, 6, 1, 76);
        SetInputTooltip(_txtSeparator, "序号与名称之间的连接符号，例如下划线。");

        // ---------- 第 6 行：实时示例与重置 ----------
        var rowExample = AddInputRow(InputLabelWidth, -100, 110);

        _lblExample.AutoSize = false;
        _lblExample.Dock = DockStyle.Fill;
        _lblExample.TextAlign = ContentAlignment.MiddleLeft;
        _lblExample.ForeColor = Color.DimGray;
        AddCell(rowExample, _lblExample, 1, 1, stretch: true);

        AddCell(rowExample, CreateInputButton("重置规则", (_, _) => ResetRules(), 96), 2);

        // ---------- 预览列表 ----------
        AddColumn("类型", 70);
        AddColumn("原名称", 220);
        AddColumn("新名称", 220);
        AddColumn("状态", 70);
        AddColumn("说明", 280);

        // ---------- 按钮 ----------
        AddButton("关闭", (_, _) => Close());
        AddButton("执行重命名", OnApplyClick, primary: true);
        AddButton("预览", OnPreviewClick);

        // ---------- 事件：规则变化时刷新示例与联动状态 ----------
        foreach (var box in new[] { _txtRemoveChars, _txtFind, _txtReplace, _txtPrefix, _txtSuffix, _txtNewExtension, _txtSeparator })
        {
            box.TextChanged += OnRuleChanged;
        }

        foreach (var check in new[] { _chkUseRegex, _chkCaseSensitive, _chkSequence, _chkKeepExtension })
        {
            check.CheckedChanged += OnRuleChanged;
        }

        _numSequenceStart.ValueChanged += OnRuleChanged;
        _numSequenceDigits.ValueChanged += OnRuleChanged;
        _cboDateFormat.TextChanged += OnRuleChanged;
        _cboDateFormat.SelectedIndexChanged += OnRuleChanged;
        _cboTarget.SelectedIndexChanged += OnRuleChanged;
        _chkSubDirectories.CheckedChanged += OnRuleChanged;
        _chkOrderByCreation.CheckedChanged += OnRuleChanged;

        SetStatus("请选择目标目录并配置重命名规则，然后点击「预览」。");
        AppendLog("提示：先预览确认新名称，再执行；重命名不可撤销。");
        AppendLog("小技巧：把鼠标停在任意输入框上会显示该字段的说明；最后一行的「示例」会实时显示重命名效果。");

        UpdateDependentStates();
        UpdateExample();
    }

    /// <summary>忙碌时禁用全部输入控件与列表；恢复后重新应用「序号相关输入」的联动状态。</summary>
    protected override void OnBusyChanged(bool busy)
    {
        SetInputsEnabled(!busy);
        UpdateDependentStates();
    }

    // ---------------------------------------------------------------- 规则辅助

    /// <summary>任一规则控件发生变化：刷新联动状态与实时示例。</summary>
    private void OnRuleChanged(object? sender, EventArgs e)
    {
        UpdateDependentStates();
        UpdateExample();
    }

    /// <summary>序号相关的输入只有在勾选「添加序号」时才可用。</summary>
    private void UpdateDependentStates()
    {
        var enabled = _chkSequence.Checked && !IsBusy;

        _numSequenceStart.Enabled = enabled;
        _numSequenceDigits.Enabled = enabled;
        _txtSeparator.Enabled = enabled;
        _chkOrderByCreation.Enabled = enabled;
    }

    /// <summary>用示例文件名实时演示当前规则的重命名效果。</summary>
    private void UpdateExample()
    {
        if (!IsUiUsable || _lblExample.IsDisposed)
        {
            return;
        }

        try
        {
            var options = ReadOptions();

            if (FileRenamerService.Validate(options) is not null)
            {
                _lblExample.Text = "示例：填写任意一条规则后，这里会实时显示重命名效果。";
                return;
            }

            var now = DateTime.Now;
            var newName = FileRenamerService.BuildNewName(options, SampleFileName, 0, now, now);

            _lblExample.Text = newName.Length == 0
                ? "示例：当前规则会生成空名称，请调整规则。"
                : $"示例：{SampleFileName} → {newName}";
        }
        catch (Exception ex)
        {
            _lblExample.Text = "示例：规则暂时无法计算（" + ex.Message + "）";
            Logger?.Warn("刷新重命名示例失败。", ex);
        }
    }

    /// <summary>把全部重命名规则恢复为默认值（不动目标目录与处理对象）。</summary>
    private void ResetRules()
    {
        _txtRemoveChars.Text = string.Empty;
        _txtFind.Text = string.Empty;
        _txtReplace.Text = string.Empty;
        _chkUseRegex.Checked = false;
        _chkCaseSensitive.Checked = true;
        _txtPrefix.Text = string.Empty;
        _txtSuffix.Text = string.Empty;
        _txtNewExtension.Text = string.Empty;
        _cboDateFormat.SelectedIndex = 0;
        _chkSequence.Checked = false;
        _numSequenceStart.Value = 1;
        _numSequenceDigits.Value = 3;
        _txtSeparator.Text = "_";
        _chkKeepExtension.Checked = true;
        _chkOrderByCreation.Checked = false;

        AppendLog("已重置重命名规则。");
        UpdateDependentStates();
        UpdateExample();
    }

    // ---------------------------------------------------------------- 事件

    /// <summary>「预览」：按当前规则生成重命名计划并填入列表。</summary>
    private async void OnPreviewClick(object? sender, EventArgs e)
    {
        if (!TryGetExistingDirectory(_pickerDirectory.TextBox, "目标目录", out var directory))
        {
            return;
        }

        var options = ReadOptions();
        var ruleError = FileRenamerService.Validate(options);
        if (ruleError is not null)
        {
            ShowWarning(ruleError);
            return;
        }

        RenamePlan? plan = null;
        var ok = await RunBusyAsync(
            async token =>
            {
                plan = await Task.Run(
                    () => _service.BuildPlanFromDirectory(directory, options, token),
                    token).ConfigureAwait(true);
            },
            "正在扫描并生成重命名预览…");

        if (!ok || plan is null || !IsUiUsable)
        {
            return;
        }

        _plan = plan;
        FillPreview(plan);

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

    /// <summary>「执行重命名」：二次确认后执行预览中可重命名的条目。</summary>
    private async void OnApplyClick(object? sender, EventArgs e)
    {
        var plan = _plan;
        if (plan is null || plan.Items.Count == 0)
        {
            ShowWarning("请先点击「预览」，确认新名称后再执行。");
            return;
        }

        var ready = plan.ReadyCount;
        if (ready == 0)
        {
            ShowWarning("当前预览中没有可执行的条目。");
            return;
        }

        if (!ConfirmDanger(
                $"将按预览重命名 {ready} 项，重命名后无法用「撤销」恢复。" +
                Environment.NewLine + Environment.NewLine + "确定继续吗？"))
        {
            return;
        }

        RenameResult? result = null;
        var progress = new Progress<RenameProgress>(p =>
        {
            SetProgress(p.Percent);
            SetStatus($"正在重命名（{p.Processed}/{p.Total}）：{p.CurrentPath}");
        });

        var ok = await RunBusyAsync(
            async token =>
            {
                result = await Task.Run(
                    () => _service.Apply(plan, progress, token),
                    token).ConfigureAwait(true);
            },
            "正在重命名…");

        if (!ok || result is null || !IsUiUsable)
        {
            return;
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);

        foreach (var failed in result.Items.Where(static item => !item.Success).Take(10))
        {
            AppendLog($"失败：{failed.SourcePath} → {failed.TargetPath}：{failed.Error}");
        }

        if (!result.Success)
        {
            ShowWarning(
                "部分条目重命名失败：" + Environment.NewLine + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    result.Items.Where(static item => !item.Success).Take(10)
                        .Select(static item => $"· {item.SourcePath}：{item.Error}")));
        }

        // 重新预览，刷新列表状态
        _plan = null;
        PreviewList.Items.Clear();
        SetStatus(result.Summary + " 请重新点击「预览」查看当前状态。");
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>按界面输入组装重命名规则。</summary>
    private RenameOptions ReadOptions()
    {
        var dateFormat = _cboDateFormat.Text.Trim();
        if (dateFormat.Length == 0 || dateFormat == DateFormats[0])
        {
            dateFormat = null;
        }

        var extension = _txtNewExtension.Text.Trim();

        return new RenameOptions
        {
            Target = _cboTarget.SelectedIndex switch
            {
                1 => RenameTarget.Folders,
                2 => RenameTarget.Both,
                _ => RenameTarget.Files
            },
            IncludeSubDirectories = _chkSubDirectories.Checked,
            RemoveChars = NullIfEmpty(_txtRemoveChars.Text),
            FindText = NullIfEmpty(_txtFind.Text),
            ReplaceText = _txtReplace.Text,
            UseRegex = _chkUseRegex.Checked,
            CaseSensitive = _chkCaseSensitive.Checked,
            Prefix = NullIfEmpty(_txtPrefix.Text),
            Suffix = NullIfEmpty(_txtSuffix.Text),
            SequenceStart = _chkSequence.Checked ? (int)_numSequenceStart.Value : null,
            SequenceDigits = (int)_numSequenceDigits.Value,
            SequenceSeparator = _txtSeparator.Text,
            DateFormat = dateFormat,
            KeepExtension = _chkKeepExtension.Checked,
            NewExtension = extension.Length == 0 ? null : extension,
            OrderByCreationTime = _chkOrderByCreation.Checked
        };
    }

    /// <summary>把预览填入列表：绿色=可执行，灰色=未变化，红色=冲突，橙红色=非法。</summary>
    private void FillPreview(RenamePlan plan)
    {
        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();

            foreach (var item in plan.Items)
            {
                var row = new ListViewItem(item.Kind == RenameItemKind.Folder ? "文件夹" : "文件")
                {
                    ForeColor = item.Status switch
                    {
                        RenameItemStatus.Ready => Color.Green,
                        RenameItemStatus.Unchanged => Color.Gray,
                        RenameItemStatus.Conflict => Color.Red,
                        _ => Color.DarkOrange
                    },
                    ToolTipText = item.SourcePath
                };

                row.SubItems.Add(item.OriginalName);
                row.SubItems.Add(item.NewName.Length == 0 ? "（空）" : item.NewName);
                row.SubItems.Add(StatusText(item.Status));
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

    /// <summary>状态文字。</summary>
    private static string StatusText(RenameItemStatus status) => status switch
    {
        RenameItemStatus.Ready => "可重命名",
        RenameItemStatus.Unchanged => "未变化",
        RenameItemStatus.Conflict => "冲突",
        RenameItemStatus.Invalid => "非法",
        _ => status.ToString()
    };

    /// <summary>空字符串转 null（规则里用 null 表示「未启用」）。</summary>
    private static string? NullIfEmpty(string? text)
        => string.IsNullOrEmpty(text) ? null : text;
}
