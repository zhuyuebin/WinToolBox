using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinToolBox.Core;

namespace WinToolBox.Tools.FileMaster.UI;

/// <summary>
/// FileMaster 各功能窗口的公共基类：统一布局（顶部输入区 / 中部预览列表 / 底部按钮 + 日志 + 状态栏），
/// 统一提供「后台线程 + 进度回调 + 可取消」的执行外壳与二次确认弹窗。
/// </summary>
/// <remarks>
/// <para>子类只负责往 <see cref="InputPanel"/> 里放输入控件、给 <see cref="PreviewList"/> 配置列、
/// 在 <see cref="ButtonPanel"/> 上添加功能按钮，然后调用 <see cref="RunBusyAsync"/> 执行耗时操作。</para>
/// <para>所有耗时操作都通过 <see cref="RunBusyAsync"/> 执行：内部已做异常兜底，
/// 单个功能出错不会影响主窗口与其它功能。</para>
/// </remarks>
public abstract class FeatureDialogBase : Form
{
    /// <summary>日志框最多保留的行数。</summary>
    private const int MaxLogLines = 500;

    /// <summary>设计期（96 DPI）窗口客户区大小，同时作为「框架是否已自动缩放」的探针。</summary>
    private static readonly Size DesignClientSize = new(940, 660);

    private bool _dpiScaleApplied;

    private readonly Panel _inputPanel;
    private readonly TableLayoutPanel _inputGrid;
    private readonly ListView _previewList;
    private readonly Panel _buttonPanel;
    private readonly GroupBox _logGroup;
    private readonly TextBox _logBox;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripProgressBar _progressBar;
    private readonly ToolStripStatusLabel _countLabel;

    private int _logLineCount;
    private bool _isBusy;
    private ToolTip? _toolTip;

    /// <summary>创建功能窗口。</summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="logger">日志记录器（可为 null）。</param>
    protected FeatureDialogBase(string title, Logger? logger)
    {
        Logger = logger;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = DesignClientSize;
        MinimumSize = new Size(700, 520);
        Font = ResolvePreferredFont(logger);

        _inputGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _inputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _inputPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 12, 12, 8)
        };
        _inputPanel.Controls.Add(_inputGrid);

        _previewList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = true,
            ShowItemToolTips = true
        };

        _buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 8)
        };

        _logGroup = new GroupBox
        {
            Dock = DockStyle.Bottom,
            Height = 116,
            Text = "运行日志",
            Padding = new Padding(8)
        };

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = SystemColors.Window,
            Font = new Font("Consolas", 9F)
        };
        _logGroup.Controls.Add(_logBox);

        _progressBar = new ToolStripProgressBar
        {
            AutoSize = false,
            Maximum = 100,
            Minimum = 0,
            Size = new Size(180, 16),
            Step = 1,
            Value = 0
        };

        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "就绪"
        };

        _countLabel = new ToolStripStatusLabel
        {
            Text = string.Empty,
            TextAlign = ContentAlignment.MiddleRight
        };

        _statusStrip = new StatusStrip { Dock = DockStyle.Bottom };
        _statusStrip.Items.AddRange(new ToolStripItem[] { _statusLabel, _countLabel, _progressBar });

        // 停靠顺序：先加 Fill，再从上到下加其余停靠控件
        Controls.Add(_previewList);
        Controls.Add(_inputPanel);
        Controls.Add(_buttonPanel);
        Controls.Add(_logGroup);
        Controls.Add(_statusStrip);

        AppendLog($"日志目录：{AppPaths.LogDirectory}");
    }

    /// <summary>日志记录器（可为 null）。</summary>
    protected Logger? Logger { get; }

    /// <summary>顶部输入区（子类在此放置输入控件）。</summary>
    protected Panel InputPanel => _inputPanel;

    /// <summary>中部预览列表。</summary>
    protected ListView PreviewList => _previewList;

    /// <summary>底部按钮区（子类在此放置功能按钮）。</summary>
    protected Panel ButtonPanel => _buttonPanel;

    /// <summary>日志框。</summary>
    protected TextBox LogBox => _logBox;

    /// <summary>是否有耗时操作正在执行。</summary>
    protected bool IsBusy => _isBusy;

    /// <summary>当前后台操作的取消源。</summary>
    protected CancellationTokenSource? OperationCts { get; private set; }

    /// <summary>窗口是否仍然可用。</summary>
    protected bool IsUiUsable => !IsDisposed && !Disposing;

    /// <summary>
    /// 纯代码创建的窗口不会像设计器生成的窗口那样自动按 DPI 缩放，
    /// 这里在窗口加载时补一次：先判断框架是否已经缩放（客户区仍是设计值说明没有），
    /// 避免高 DPI 下字体变大而布局不变导致文字被截断，也避免被缩放两次。
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDpiScaleOnce();
    }

    /// <summary>按当前 DPI 缩放一次（幂等）。</summary>
    private void ApplyDpiScaleOnce()
    {
        if (_dpiScaleApplied)
        {
            return;
        }

        _dpiScaleApplied = true;

        var scale = DeviceDpi <= 0 ? 1f : DeviceDpi / 96f;
        if (Math.Abs(scale - 1f) < 0.001f)
        {
            return;
        }

        // 框架已经自动缩放过的窗口，客户区不再是设计值，不再重复缩放
        if (ClientSize.Width != DesignClientSize.Width || ClientSize.Height != DesignClientSize.Height)
        {
            return;
        }

        SuspendLayout();
        try
        {
            Scale(new SizeF(scale, scale));

            if (MinimumSize != Size.Empty)
            {
                MinimumSize = new Size(
                    (int)Math.Round(MinimumSize.Width * scale),
                    (int)Math.Round(MinimumSize.Height * scale));
            }

            if (ClientSize.Width == DesignClientSize.Width && ClientSize.Height == DesignClientSize.Height)
            {
                // Scale 未影响窗体自身时兜底
                ClientSize = new Size(
                    (int)Math.Round(DesignClientSize.Width * scale),
                    (int)Math.Round(DesignClientSize.Height * scale));
            }
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    /// <summary>
    /// 统一启用 / 禁用输入区控件与预览列表（忙碌时由 <see cref="OnBusyChanged"/> 调用）。
    /// 标签不参与，避免整块输入区变灰影响可读性。
    /// </summary>
    protected void SetInputsEnabled(bool enabled)
    {
        SetChildrenEnabled(_inputPanel, enabled);
        _previewList.Enabled = enabled;
    }

    /// <summary>递归设置子控件可用性（跳过标签）。</summary>
    private static void SetChildrenEnabled(Control parent, bool enabled)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Label)
            {
                continue;
            }

            child.Enabled = enabled;

            if (child.HasChildren)
            {
                SetChildrenEnabled(child, enabled);
            }
        }
    }

    // ---------------------------------------------------------------- 输入区网格布局

    /// <summary>输入区单行的建议高度（96 DPI 基准；实际行高由内容自动撑开）。</summary>
    protected const int InputRowHeight = 34;

    /// <summary>输入区标签列默认宽度（96 DPI 基准）。</summary>
    protected const int InputLabelWidth = 92;

    /// <summary>输入区控件之间的水平间距。</summary>
    private const int InputGap = 10;

    /// <summary>输入区网格（每行一个子布局，子布局内可放多组「标签 + 控件」）。</summary>
    protected TableLayoutPanel InputGrid => _inputGrid;

    /// <summary>
    /// 新建一行输入区，并返回该行的子布局。
    /// </summary>
    /// <param name="columnWidths">
    /// 列宽：正数表示固定像素，负数表示百分比（例如 <c>-50</c> 表示 50%）。
    /// 惯例是「标签列 + 控件列」成对出现，标签列用 <see cref="InputLabelWidth"/>。
    /// </param>
    protected TableLayoutPanel AddInputRow(params float[] columnWidths)
    {
        var widths = columnWidths.Length == 0 ? new float[] { -100 } : columnWidths;

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = widths.Length,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6),
            Padding = new Padding(0)
        };

        foreach (var width in widths)
        {
            row.ColumnStyles.Add(width < 0
                ? new ColumnStyle(SizeType.Percent, -width)
                : new ColumnStyle(SizeType.Absolute, width));
        }

        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var rowIndex = _inputGrid.RowCount;
        _inputGrid.RowCount = rowIndex + 1;
        _inputGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _inputGrid.Controls.Add(row, 0, rowIndex);
        return row;
    }

    /// <summary>
    /// 在输入行里放一组「标签 + 控件」：标签右对齐（冒号对齐），控件左对齐并垂直居中。
    /// </summary>
    /// <param name="row">由 <see cref="AddInputRow"/> 返回的行布局。</param>
    /// <param name="labelText">标签文字（建议带全角冒号）。</param>
    /// <param name="field">输入控件。</param>
    /// <param name="column">标签所在的列号（控件放在下一列）。</param>
    /// <param name="span">控件跨越的列数。</param>
    protected static void AddField(TableLayoutPanel row, string labelText, Control field, int column, int span = 1)
        => AddField(row, labelText, field, column, span, InputLabelWidth);

    /// <summary>在输入行里放一组「标签 + 控件」，可指定标签列宽。</summary>
    protected static void AddField(
        TableLayoutPanel row,
        string labelText,
        Control field,
        int column,
        int span,
        int labelWidth)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(field);

        var label = new Label
        {
            Text = labelText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 0, 6, 0)
        };

        row.Controls.Add(label, column, 0);

        if (labelWidth > 0 && row.ColumnStyles.Count > column && row.ColumnStyles[column].SizeType == SizeType.Absolute)
        {
            row.ColumnStyles[column].Width = Math.Max(row.ColumnStyles[column].Width, labelWidth);
        }

        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = new Padding(0, 4, InputGap, 4);

        row.Controls.Add(field, column + 1, 0);

        if (span > 1)
        {
            row.SetColumnSpan(field, span);
        }
    }

    /// <summary>在输入行里放一个不带标签的控件（复选框、单选按钮、按钮等），垂直居中。</summary>
    protected static void AddCell(TableLayoutPanel row, Control control, int column, int span = 1, bool stretch = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(control);

        if (stretch)
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        else
        {
            control.Anchor = AnchorStyles.Left;
        }

        control.Margin = new Padding(0, 4, InputGap, 4);
        row.Controls.Add(control, column, 0);

        if (span > 1)
        {
            row.SetColumnSpan(control, span);
        }
    }

    /// <summary>创建统一样式的输入区标签（右对齐、垂直居中）。</summary>
    protected static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleRight,
        Margin = new Padding(0, 0, 6, 0)
    };

    /// <summary>创建统一样式的单行文本框（高度自适应，宽度由布局决定）。</summary>
    protected TextBox CreateInputTextBox(string text = "")
        => new()
        {
            Text = text,
            AutoSize = false,
            Height = 25
        };

    /// <summary>创建统一样式的下拉框。</summary>
    protected ComboBox CreateInputComboBox(params string[] items)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = false,
            Height = 25
        };

        combo.Items.AddRange(items);
        if (items.Length > 0)
        {
            combo.SelectedIndex = 0;
        }

        return combo;
    }

    /// <summary>创建统一样式的复选框。</summary>
    protected static CheckBox CreateInputCheckBox(string text, bool isChecked = false)
        => new()
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            Margin = new Padding(0, 4, InputGap, 4)
        };

    /// <summary>创建统一样式的单选框。</summary>
    protected static RadioButton CreateInputRadioButton(string text, bool isChecked = false)
        => new()
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            Margin = new Padding(0, 4, InputGap, 4)
        };

    /// <summary>创建输入区用的小按钮（固定宽高，垂直居中）。</summary>
    protected Button CreateInputButton(string text, EventHandler onClick, int width = 96)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(width, 27),
            UseVisualStyleBackColor = true,
            Margin = new Padding(0, 4, InputGap, 4)
        };

        button.Click += SafeHandler(onClick);
        return button;
    }

    /// <summary>创建「目录文本框 + 浏览…按钮」组合控件。</summary>
    protected FolderPickerBox CreateFolderPicker(string description = "请选择文件夹")
        => new(description, this);

    /// <summary>给输入控件挂一段鼠标悬停提示（用于解释字段含义）。</summary>
    protected void SetInputTooltip(Control control, string text)
    {
        ArgumentNullException.ThrowIfNull(control);

        _toolTip ??= new ToolTip
        {
            AutoPopDelay = 12000,
            InitialDelay = 350,
            ReshowDelay = 150,
            ShowAlways = true
        };

        _toolTip.SetToolTip(control, text);
    }

    // ---------------------------------------------------------------- 布局辅助

    /// <summary>给预览列表添加一列。</summary>
    protected void AddColumn(string header, int width, HorizontalAlignment align = HorizontalAlignment.Left)
        => _previewList.Columns.Add(new ColumnHeader
        {
            Text = header,
            Width = width,
            TextAlign = align
        });

    /// <summary>在按钮区添加一个按钮（自右向左排列，先添加的在最右侧）。</summary>
    /// <param name="text">按钮文字。</param>
    /// <param name="onClick">点击事件。</param>
    /// <param name="primary">是否主按钮（加粗显示）。</param>
    protected Button AddButton(string text, EventHandler onClick, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(primary ? 150 : 118, 34),
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = true,
            Font = primary ? new Font(Font, FontStyle.Bold) : Font
        };

        button.Click += SafeHandler(onClick);
        _buttonPanel.Controls.Add(button);
        return button;
    }

    /// <summary>
    /// 包装事件处理器：任何异常都记录日志并弹窗，绝不让异常逃出事件循环
    /// （保证一个功能出错不影响其它功能）。
    /// </summary>
    protected EventHandler SafeHandler(EventHandler handler) => (sender, e) =>
    {
        try
        {
            handler(sender, e);
        }
        catch (Exception ex)
        {
            Logger?.Error($"{Text} 处理事件时发生未捕获异常。", ex);
            ShowError("操作发生错误：" + Environment.NewLine + ex.Message);
        }
    };

    // ---------------------------------------------------------------- 状态与日志

    /// <summary>更新状态栏文字。</summary>
    protected void SetStatus(string text)
    {
        if (!IsUiUsable || _statusLabel.IsDisposed)
        {
            return;
        }

        _statusLabel.Text = text;
    }

    /// <summary>更新状态栏右侧的计数文字。</summary>
    protected void SetCountText(string text)
    {
        if (!IsUiUsable || _countLabel.IsDisposed)
        {
            return;
        }

        _countLabel.Text = text;
    }

    /// <summary>更新进度条（0-100）。</summary>
    protected void SetProgress(double percent)
    {
        if (!IsUiUsable || _progressBar.IsDisposed)
        {
            return;
        }

        _progressBar.Value = Math.Clamp((int)Math.Round(percent), _progressBar.Minimum, _progressBar.Maximum);
    }

    /// <summary>追加一行日志（超出上限时裁掉最早的内容）。</summary>
    protected void AppendLog(string? text)
    {
        if (!IsUiUsable || _logBox.IsDisposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _logBox.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");
        _logLineCount = _logBox.Lines.Length;

        if (_logLineCount > MaxLogLines)
        {
            _logBox.Lines = _logBox.Lines.Skip(_logLineCount - MaxLogLines + 200).ToArray();
            _logLineCount = _logBox.Lines.Length;
        }
    }

    // ---------------------------------------------------------------- 弹窗

    /// <summary>二次确认（默认焦点在「否」上，避免误操作）。</summary>
    protected bool Confirm(string message, string? caption = null)
        => MessageBox.Show(
            this,
            message,
            caption ?? Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>危险操作确认（警告图标 + 默认「否」）。</summary>
    protected bool ConfirmDanger(string message, string? caption = null)
        => MessageBox.Show(
            this,
            message,
            caption ?? Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>提示信息。</summary>
    protected void ShowInfo(string message)
    {
        if (IsUiUsable)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>警告信息。</summary>
    protected void ShowWarning(string message)
    {
        if (IsUiUsable)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>错误信息。</summary>
    protected void ShowError(string message)
    {
        if (IsUiUsable)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------------------------------------------------------- 后台执行

    /// <summary>
    /// 在后台线程执行耗时操作：自动切换忙碌状态、创建取消源、回填进度与日志，
    /// 并统一兜底异常（取消不算错误）。
    /// </summary>
    /// <param name="work">后台工作（在工作线程上执行）。</param>
    /// <param name="busyStatus">执行期间显示的状态文字。</param>
    /// <returns>执行成功返回 true；被取消或抛异常返回 false。</returns>
    protected async Task<bool> RunBusyAsync(Func<CancellationToken, Task> work, string busyStatus)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_isBusy)
        {
            return false;
        }

        SetBusy(true);
        try
        {
            SetProgress(0);
            SetStatus(busyStatus);

            OperationCts?.Dispose();
            OperationCts = new CancellationTokenSource();
            var token = OperationCts.Token;

            await work(token).ConfigureAwait(true);

            if (!IsUiUsable)
            {
                return false;
            }

            SetProgress(100);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消。");
            AppendLog("操作已取消。");
            SetProgress(0);
            return false;
        }
        catch (Exception ex)
        {
            Logger?.Error($"{Text} 执行失败。", ex);
            SetStatus("执行失败：" + ex.Message);
            AppendLog("执行失败：" + ex.Message);
            ShowError("操作失败：" + Environment.NewLine + ex.Message);
            return false;
        }
        finally
        {
            OperationCts?.Dispose();
            OperationCts = null;
            SetBusy(false);
        }
    }

    /// <summary>切换忙碌状态（禁用按钮、显示等待光标）。</summary>
    protected void SetBusy(bool busy)
    {
        _isBusy = busy;

        if (!IsUiUsable)
        {
            return;
        }

        foreach (Control control in _buttonPanel.Controls)
        {
            if (control is Button button)
            {
                button.Enabled = !busy;
            }
        }

        UseWaitCursor = busy;
        OnBusyChanged(busy);
    }

    /// <summary>忙碌状态变化时的扩展点（子类可禁用输入控件）。</summary>
    protected virtual void OnBusyChanged(bool busy)
    {
    }

    /// <summary>窗口关闭：正在执行时先确认，确认后取消后台操作。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isBusy && e.CloseReason == CloseReason.UserClosing)
        {
            if (!Confirm("当前还有操作正在执行，确定要关闭吗？"))
            {
                e.Cancel = true;
                return;
            }
        }

        try
        {
            OperationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 后台操作刚好结束，忽略
        }

        base.OnFormClosing(e);
    }

    /// <summary>尝试获取一个必须存在的目录路径（不存在时提示并返回 false）。</summary>
    protected bool TryGetExistingDirectory(TextBox box, string fieldName, out string directory)
    {
        directory = box.Text.Trim();

        if (directory.Length == 0)
        {
            ShowWarning($"请先选择{fieldName}。");
            box.Focus();
            return false;
        }

        if (!System.IO.Directory.Exists(directory))
        {
            ShowWarning($"{fieldName}不存在：{directory}");
            box.Focus();
            return false;
        }

        return true;
    }

    /// <summary>
    /// 设计器指定了「Microsoft YaHei UI」，极少数系统可能没有该字体，缺失时退回系统消息字体。
    /// </summary>
    private static Font ResolvePreferredFont(Logger? logger)
    {
        const string preferredFontName = "Microsoft YaHei UI";

        try
        {
            var probe = new Font(preferredFontName, 9F);
            if (string.Equals(probe.Name, preferredFontName, StringComparison.OrdinalIgnoreCase))
            {
                return probe;
            }

            probe.Dispose();
            var fallback = SystemFonts.MessageBoxFont;
            return fallback is null
                ? new Font(FontFamily.GenericSansSerif, 9F)
                : new Font(fallback.FontFamily, fallback.Size);
        }
        catch (Exception ex)
        {
            logger?.Warn("设置界面字体失败，沿用系统默认字体。", ex);
            return SystemFonts.MessageBoxFont ?? new Font(FontFamily.GenericSansSerif, 9F);
        }
    }
}
