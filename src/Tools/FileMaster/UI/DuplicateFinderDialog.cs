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
/// 「重复文件查找」窗口：先按文件大小粗筛，再对同大小的候选文件计算哈希（默认 SHA256），
/// 找出内容完全相同的重复组；每组保留修改时间最早的一个文件，其余默认勾选，二次确认后统一放入回收站。
/// </summary>
/// <remarks>
/// <para>继承 <see cref="FeatureDialogBase"/>：扫描与删除都在后台线程执行，进度经 <see cref="Progress{T}"/> 回填到界面，
/// 单个功能出错不会影响主窗口。</para>
/// <para>列表按重复组用 <see cref="ListViewGroup"/> 分组，组标题形如「第 N 组 · 可回收 X」；
/// 组内第一行（修改时间最早）为建议保留项，状态列显示「保留」且默认不勾选，其余为「重复」并默认勾选。</para>
/// <para>删除一律走回收站：执行前必须二次确认，并在确认文案中写明数量、可释放空间与删除方式。</para>
/// </remarks>
public sealed class DuplicateFinderDialog : FeatureDialogBase
{
    /// <summary>失败明细在弹窗中最多列出的条数（其余引导用户查看运行日志）。</summary>
    private const int MaxDisplayedMessages = 10;

    /// <summary>扫描 / 删除过程中每处理多少项往日志里写一行（避免刷屏）。</summary>
    private const int LogInterval = 50;

    /// <summary>时间显示格式。</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>建议保留项在状态列中的文字。</summary>
    private const string KeepStatusText = "保留";

    /// <summary>可删除的重复项在状态列中的文字。</summary>
    private const string DuplicateStatusText = "重复";

    /// <summary>删除失败项在状态列中的文字。</summary>
    private const string FailedStatusText = "删除失败";

    /// <summary>「哈希算法」下拉框的选项（顺序与 <see cref="ReadAlgorithm"/> 的映射一致）。</summary>
    private static readonly string[] AlgorithmNames = { "MD5", "SHA1", "SHA256" };

    /// <summary>重复文件查找服务。</summary>
    private readonly DuplicateFinderService _service;

    /// <summary>扫描目录输入框（多行，每行一个目录）。</summary>
    private readonly TextBox _txtDirectories;

    /// <summary>「包含子目录」复选框（默认勾选）。</summary>
    private readonly CheckBox _chkIncludeSubDirectories;

    /// <summary>「哈希算法」下拉框（默认 SHA256）。</summary>
    private readonly ComboBox _cboAlgorithm;

    /// <summary>「最小文件大小(KB)」数字框（0 表示不限制）。</summary>
    private readonly NumericUpDown _numMinSizeKb;

    /// <summary>「忽略名称」输入框（分号分隔，可留空）。</summary>
    private readonly TextBox _txtExcludeNames;

    /// <summary>批量勾选 / 移除列表行期间挂起计数刷新（避免逐行触发 O(n²) 统计）。</summary>
    private bool _suspendCheckSummary;

    /// <summary>创建「重复文件查找」窗口。</summary>
    /// <param name="logger">日志记录器（可为 null）。</param>
    public DuplicateFinderDialog(Logger? logger)
        : base("重复文件查找", logger)
    {
        _service = new DuplicateFinderService(logger);

        // ---------- 第 1 行：扫描目录（多行文本框）+ 浏览添加目录按钮 ----------
        var rowDirectories = AddInputRow(InputLabelWidth, -100, 128);

        _txtDirectories = new TextBox
        {
            Multiline = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Vertical,
            AutoSize = false,
            Height = 72,
            PlaceholderText = @"每行一个目录，例如 D:\照片"
        };
        AddField(rowDirectories, "扫描目录：", _txtDirectories, 0);

        var addDirectoryButton = CreateInputButton(
            "浏览添加目录…",
            (_, _) => AppendDirectoryFromBrowser(),
            118);
        AddCell(rowDirectories, addDirectoryButton, 2);

        // ---------- 第 2 行：哈希算法、最小文件大小、包含子目录 ----------
        var rowOptions = AddInputRow(InputLabelWidth, 140, 136, 96, -100);

        _cboAlgorithm = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AutoSize = false,
            Height = 25
        };
        _cboAlgorithm.Items.AddRange(AlgorithmNames);
        _cboAlgorithm.SelectedIndex = 2; // 默认 SHA256
        AddField(rowOptions, "哈希算法：", _cboAlgorithm, 0);

        _numMinSizeKb = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 1048576,
            Value = 1,
            AutoSize = false,
            Height = 25
        };
        AddField(rowOptions, "最小文件大小(KB)：", _numMinSizeKb, 2, 1, 136);

        _chkIncludeSubDirectories = CreateInputCheckBox("包含子目录", isChecked: true);
        AddCell(rowOptions, _chkIncludeSubDirectories, 4);

        // ---------- 第 3 行：忽略名称 ----------
        var rowExcludeNames = AddInputRow(InputLabelWidth, -100);

        _txtExcludeNames = CreateInputTextBox();
        _txtExcludeNames.PlaceholderText = "以 ; 分隔，可留空";
        AddField(rowExcludeNames, "忽略名称：", _txtExcludeNames, 0);

        // ---------- 预览列表：按重复组分组，勾选要删除的重复项 ----------
        PreviewList.CheckBoxes = true;
        PreviewList.ShowGroups = true;
        AddColumn("文件", 620);
        AddColumn("大小", 100, HorizontalAlignment.Right);
        AddColumn("修改时间", 145);
        AddColumn("状态", 100);

        // ---------- 按钮区（自右向左排列：先添加的在最右侧） ----------
        AddButton("关闭", (_, _) => Close());
        AddButton("删除选中", AsyncHandler(() => DeleteSelectedAsync(useRecycleBin: true)), primary: true);
        AddButton("全选重复项", (_, _) => SelectDuplicates());
        AddButton("开始扫描", AsyncHandler(ScanAsync));

        // ListView.ItemChecked 使用专用委托类型，先用基类的 SafeHandler 包好再转接
        var itemChecked = SafeHandler((_, _) => UpdateCheckSummary());
        PreviewList.ItemChecked += (sender, e) => itemChecked(sender, e);

        UpdateCheckSummary();
        SetStatus("就绪：请输入或添加要扫描的目录，然后点击「开始扫描」。");
        AppendLog("提示：最小文件大小以 KB 为单位，填 0 表示不限制；列表中的「保留」行默认不勾选。");
        AppendLog("提示：删除重复文件一律放入回收站（可从回收站还原），不会永久删除。");
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
    /// <param name="busy">是否忙碌。</param>
    protected override void OnBusyChanged(bool busy) => SetInputsEnabled(!busy);

    // ---------------------------------------------------------------- 输入

    /// <summary>浏览选择一个目录并追加到「扫描目录」框（已存在时不重复追加）。</summary>
    private void AppendDirectoryFromBrowser()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择要参与重复文件查找的目录",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };

        var typed = _txtDirectories.Text.Trim();
        if (Directory.Exists(typed))
        {
            dialog.SelectedPath = typed;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedPath.Length == 0)
        {
            return;
        }

        var selected = dialog.SelectedPath;

        if (_txtDirectories.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), selected, StringComparison.OrdinalIgnoreCase)))
        {
            AppendLog($"该目录已在列表中，未重复添加：{selected}");
            return;
        }

        var existing = _txtDirectories.Text.TrimEnd('\r', '\n');
        _txtDirectories.Text = existing.Length == 0
            ? selected
            : existing + Environment.NewLine + selected;

        _txtDirectories.SelectionStart = _txtDirectories.TextLength;
        AppendLog($"已添加扫描目录：{selected}");

        AppendMergeHint(selected);
    }

    /// <summary>
    /// 提示新添加的目录与已在列表中的目录之间的包含关系：递归扫描时子目录会被父目录自动合并，
    /// 避免用户以为同一批文件会被统计两次（否则会出现「自己和自己重复」的假重复组）。
    /// </summary>
    /// <param name="added">刚添加的目录。</param>
    private void AppendMergeHint(string added)
    {
        var others = _txtDirectories.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(line => line.Length > 0 && !string.Equals(line, added, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (others.Count == 0)
        {
            return;
        }

        // 只扫顶层时父子目录互不覆盖（父目录扫不到子目录里的文件），两者会各自保留
        if (!_chkIncludeSubDirectories.Checked)
        {
            var overlapping = others.FirstOrDefault(other =>
                DuplicateFinderService.IsSameOrSubPathOf(added, other) ||
                DuplicateFinderService.IsSameOrSubPathOf(other, added));

            if (overlapping is not null)
            {
                AppendLog(
                    $"提示：当前未勾选「包含子目录」，{added} 与「{overlapping}」会各自只扫描顶层文件，不会互相覆盖。");
            }

            return;
        }

        var parent = others.FirstOrDefault(other => DuplicateFinderService.IsSameOrSubPathOf(added, other));
        if (parent is not null)
        {
            AppendLog($"提示：{added} 已被「{parent}」包含，扫描时会自动合并，同一批文件不会重复统计。");
            return;
        }

        var children = others.Where(other => DuplicateFinderService.IsSameOrSubPathOf(other, added)).ToList();
        if (children.Count > 0)
        {
            AppendLog($"提示：已添加的目录已被 {added} 包含，扫描时会自动合并：{string.Join("、", children)}");
        }
    }

    /// <summary>
    /// 解析「扫描目录」框：按行拆分（兼容 CRLF / LF）、去空白、去重，并要求每个目录都真实存在。
    /// </summary>
    /// <param name="directories">解析通过的目录列表（保序、去重）。</param>
    private bool TryGetScanDirectories(out List<string> directories)
    {
        directories = new List<string>();

        // Environment.NewLine 在 Windows 上即 CRLF；这里按 \r / \n 拆分，兼容手工粘贴的各种换行
        var lines = _txtDirectories.Text.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var line in lines)
        {
            var directory = line.Trim();
            if (directory.Length == 0 || !seen.Add(directory))
            {
                continue;
            }

            if (Directory.Exists(directory))
            {
                directories.Add(directory);
            }
            else
            {
                missing.Add(directory);
            }
        }

        if (directories.Count == 0 && missing.Count == 0)
        {
            ShowWarning(@"请先输入要扫描的目录（每行一个，例如 D:\照片），也可以点击「浏览添加目录…」。");
            _txtDirectories.Focus();
            return false;
        }

        if (missing.Count > 0)
        {
            ShowWarning(BuildListMessage("以下目录不存在，请修正后再扫描：", missing, MaxDisplayedMessages));
            _txtDirectories.Focus();
            return false;
        }

        return true;
    }

    /// <summary>按界面输入组装重复文件查找选项。</summary>
    /// <param name="directories">已校验存在且去重的扫描目录。</param>
    private DuplicateFinderOptions BuildOptions(IReadOnlyList<string> directories) => new()
    {
        Directories = directories,
        IncludeSubDirectories = _chkIncludeSubDirectories.Checked,
        MinFileSize = (long)_numMinSizeKb.Value * 1024, // 界面以 KB 为单位，0 表示不限制
        Algorithm = ReadAlgorithm(),
        ExcludeNames = NullIfEmpty(_txtExcludeNames.Text)
    };

    /// <summary>读取下拉框选中的哈希算法（0=MD5、1=SHA1、其它=SHA256）。</summary>
    private HashAlgorithmKind ReadAlgorithm() => _cboAlgorithm.SelectedIndex switch
    {
        0 => HashAlgorithmKind.MD5,
        1 => HashAlgorithmKind.SHA1,
        _ => HashAlgorithmKind.SHA256
    };

    /// <summary>把选项描述成一句话（用于日志）。</summary>
    /// <param name="options">重复文件查找选项。</param>
    private static string DescribeOptions(DuplicateFinderOptions options)
    {
        var scope = options.IncludeSubDirectories ? "包含子目录" : "仅当前目录";
        var algorithm = HashEngine.GetAlgorithmName(options.Algorithm);
        var minSize = options.MinFileSize <= 0
            ? "不限最小大小"
            : "最小 " + DuplicateScanResult.FormatSize(options.MinFileSize);

        var excludes = FolderDiffService.ParseExcludes(options.ExcludeNames).Count;
        var excludeText = excludes == 0 ? "不忽略任何名称" : $"忽略 {excludes} 个名称";

        return $"{scope}，{algorithm}，{minSize}，{excludeText}";
    }

    // ---------------------------------------------------------------- 扫描

    /// <summary>扫描指定目录，找出重复文件分组并填入列表（每组保留项默认不勾选）。</summary>
    private async Task ScanAsync()
    {
        if (!TryGetScanDirectories(out var directories))
        {
            return;
        }

        var options = BuildOptions(directories);

        DuplicateScanResult? result = null;
        var ok = await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始扫描 {directories.Count} 个目录（{DescribeOptions(options)}）");

                var progress = new Progress<DuplicateScanProgress>(report =>
                {
                    SetProgress(report.Percent);
                    SetStatus($"正在{report.Phase}（{report.Processed}/{report.Total}）：{report.CurrentPath}");
                    SetCountText($"{report.Phase} {report.Processed}/{report.Total}");

                    if (report.Processed % LogInterval == 0)
                    {
                        AppendLog($"已处理 {report.Processed}/{report.Total}：{report.CurrentPath}");
                    }
                });

                result = await Task.Run(
                    () => _service.Find(options, progress, token),
                    token).ConfigureAwait(true);

                if (result is not null)
                {
                    FillScanResult(result);
                }
            },
            "正在扫描重复文件…");

        if (!ok || result is null || !IsUiUsable)
        {
            return;
        }

        if (result.Error is not null)
        {
            SetStatus("查找失败：" + result.Error);
            AppendLog("查找失败：" + result.Error);
            ShowWarning("查找重复文件失败：" + Environment.NewLine + result.Error);
            return;
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);

        if (result.MergedDirectories.Count > 0)
        {
            AppendLog(
                $"已自动合并 {result.MergedDirectories.Count} 个扫描目录（等价的写法或被父目录包含），未重复统计：" +
                string.Join("、", result.MergedDirectories));
        }

        if (result.Groups.Count == 0)
        {
            ShowInfo("扫描完成，没有发现重复文件。");
        }
    }

    /// <summary>
    /// 把扫描结果填入列表：每个重复组一个 <see cref="ListViewGroup"/>（标题「第 N 组 · 可回收 X」），
    /// 组内第一行（修改时间最早）状态为「保留」且不勾选，其余状态为「重复」并默认勾选。
    /// </summary>
    /// <param name="result">扫描结果。</param>
    private void FillScanResult(DuplicateScanResult result)
    {
        _suspendCheckSummary = true;

        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();
            PreviewList.Groups.Clear();

            for (var index = 0; index < result.Groups.Count; index++)
            {
                var group = result.Groups[index];
                var groupIndex = index + 1;

                var listGroup = new ListViewGroup(
                    $"第 {groupIndex} 组 · 可回收 {DuplicateScanResult.FormatSize(group.WastedBytes)}")
                {
                    HeaderAlignment = HorizontalAlignment.Left,
                    Tag = group
                };
                PreviewList.Groups.Add(listGroup);

                var keepPath = group.SuggestedKeepPath;

                foreach (var item in group.Files)
                {
                    var isKeep = string.Equals(item.Path, keepPath, StringComparison.OrdinalIgnoreCase);

                    var row = new ListViewItem(item.Path)
                    {
                        Tag = new DuplicateRow { Item = item, IsKeep = isKeep },
                        ToolTipText = $"第 {groupIndex} 组：{item.Path}",
                        Group = listGroup,
                        ForeColor = isKeep ? Color.DimGray : PreviewList.ForeColor
                    };

                    row.SubItems.Add(DuplicateScanResult.FormatSize(item.Size));
                    row.SubItems.Add(item.LastWriteTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
                    row.SubItems.Add(isKeep ? KeepStatusText : DuplicateStatusText);

                    PreviewList.Items.Add(row);

                    // 加入列表后再勾选，确保复选框状态正常刷新；保留项不勾选
                    row.Checked = !isKeep;
                }
            }
        }
        finally
        {
            PreviewList.EndUpdate();
            _suspendCheckSummary = false;
        }

        UpdateCheckSummary();
    }

    // ---------------------------------------------------------------- 勾选

    /// <summary>「全选重复项」：勾选所有「重复」行，并取消「保留」行的勾选。</summary>
    private void SelectDuplicates()
    {
        if (PreviewList.Items.Count == 0)
        {
            ShowInfo("列表为空，请先点击「开始扫描」。");
            return;
        }

        _suspendCheckSummary = true;

        PreviewList.BeginUpdate();
        try
        {
            foreach (ListViewItem row in PreviewList.Items)
            {
                row.Checked = row.Tag is DuplicateRow info && !info.IsKeep;
            }
        }
        finally
        {
            PreviewList.EndUpdate();
            _suspendCheckSummary = false;
        }

        UpdateCheckSummary();

        var (count, bytes) = GetCheckedStats();
        AppendLog($"已全选重复项：共勾选 {count} 个，可回收 {DuplicateScanResult.FormatSize(bytes)}。");
    }

    /// <summary>统计已勾选行数与可回收空间。</summary>
    private (int Count, long Bytes) GetCheckedStats()
    {
        var count = 0;
        var bytes = 0L;

        foreach (ListViewItem row in PreviewList.CheckedItems)
        {
            if (row.Tag is DuplicateRow info)
            {
                count++;
                bytes += info.Item.Size;
            }
        }

        return (count, bytes);
    }

    /// <summary>刷新状态栏右侧的「已勾选数量 + 可回收空间」。</summary>
    private void UpdateCheckSummary()
    {
        if (_suspendCheckSummary)
        {
            return;
        }

        var (count, bytes) = GetCheckedStats();
        SetCountText($"已勾选 {count} 个 · 可回收 {DuplicateScanResult.FormatSize(bytes)}");
    }

    /// <summary>收集列表中已勾选的重复文件行。</summary>
    private List<DuplicateRow> CollectCheckedRows()
    {
        var rows = new List<DuplicateRow>();

        foreach (ListViewItem row in PreviewList.CheckedItems)
        {
            if (row.Tag is DuplicateRow info)
            {
                rows.Add(info);
            }
        }

        return rows;
    }

    // ---------------------------------------------------------------- 删除

    /// <summary>删除列表中勾选的重复文件。</summary>
    /// <param name="useRecycleBin">true 放入回收站（可还原）；false 永久删除（不可恢复）。</param>
    private async Task DeleteSelectedAsync(bool useRecycleBin)
    {
        var rows = CollectCheckedRows();
        if (rows.Count == 0)
        {
            ShowWarning("请先勾选要删除的重复文件（可先点击「开始扫描」，再点击「全选重复项」）。");
            return;
        }

        var bytes = rows.Sum(static row => row.Item.Size);
        var keepCount = rows.Count(static row => row.IsKeep);

        // 危险操作：数量、可释放空间与删除方式都写在二次确认里，绝不静默删除
        if (!ConfirmDanger(BuildDeleteConfirmMessage(rows.Count, bytes, useRecycleBin, keepCount)))
        {
            return;
        }

        var paths = rows.Select(static row => row.Item.Path).ToList();

        await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始删除 {paths.Count} 个重复文件（{(useRecycleBin ? "放入回收站" : "永久删除")}）。");

                var progress = new Progress<DuplicateDeleteProgress>(report =>
                {
                    SetProgress(report.Percent);
                    SetStatus($"正在删除（{report.Processed}/{report.Total}）：{report.CurrentPath}");
                    SetCountText($"{report.Processed}/{report.Total}");

                    if (report.Processed % LogInterval == 0)
                    {
                        AppendLog($"已处理 {report.Processed}/{report.Total}：{report.CurrentPath}");
                    }
                });

                var result = await Task.Run(
                    () => _service.Delete(paths, useRecycleBin, progress, token),
                    token).ConfigureAwait(true);

                ApplyDeleteResult(result);
            },
            useRecycleBin ? "正在把重复文件放入回收站…" : "正在永久删除重复文件…");
    }

    /// <summary>删除前的二次确认文案（写明实际数量、可释放空间与删除方式）。</summary>
    /// <param name="count">待删除数量。</param>
    /// <param name="bytes">可释放空间（字节）。</param>
    /// <param name="useRecycleBin">是否放入回收站。</param>
    /// <param name="keepCount">勾选项中「保留」文件的数量（用户手动勾选时 &gt; 0）。</param>
    private static string BuildDeleteConfirmMessage(int count, long bytes, bool useRecycleBin, int keepCount)
    {
        var method = useRecycleBin ? "放入回收站" : "永久删除";
        var hint = useRecycleBin
            ? "删除方式为放入回收站，需要时可以还原。"
            : "删除方式为永久删除，不会进入回收站，删除后不可恢复。";

        var keepNote = keepCount == 0
            ? string.Empty
            : Environment.NewLine +
              $"注意：勾选项中包含 {keepCount} 个「保留」文件（该组最后一份原始副本），删除后这一组将不再保留任何文件。";

        return $"将删除 {count} 个重复文件（释放 {DuplicateScanResult.FormatSize(bytes)}），{method}，确定继续吗？" +
               Environment.NewLine + Environment.NewLine + hint + keepNote;
    }

    /// <summary>把删除结果回填到界面：成功的行从列表移除，失败的行标记后保留（可重试），并提示重新扫描。</summary>
    /// <param name="result">删除结果。</param>
    private void ApplyDeleteResult(DuplicateDeleteResult result)
    {
        var deleted = new HashSet<string>(result.DeletedPaths, StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(
            result.FailedItems.Select(static item => item.Path),
            StringComparer.OrdinalIgnoreCase);

        _suspendCheckSummary = true;

        PreviewList.BeginUpdate();
        try
        {
            for (var index = PreviewList.Items.Count - 1; index >= 0; index--)
            {
                var row = PreviewList.Items[index];
                if (row.Tag is not DuplicateRow info)
                {
                    continue;
                }

                if (deleted.Contains(info.Item.Path))
                {
                    PreviewList.Items.RemoveAt(index);
                }
                else if (failed.Contains(info.Item.Path))
                {
                    // 失败项保留在列表中（保持勾选以便重试），状态列标记为删除失败
                    row.SubItems[3].Text = FailedStatusText;
                    row.ForeColor = Color.Firebrick;
                }
            }

            if (PreviewList.Items.Count == 0)
            {
                PreviewList.Groups.Clear();
            }
        }
        finally
        {
            PreviewList.EndUpdate();
            _suspendCheckSummary = false;
        }

        UpdateCheckSummary();

        SetStatus(result.Summary + " 请重新点击「开始扫描」刷新结果。");
        AppendLog(result.Summary);
        AppendLog("列表中的分组信息可能已过期，如需继续清理请重新点击「开始扫描」。");

        if (!result.Success)
        {
            ShowWarning(BuildListMessage(
                $"有 {result.FailedItems.Count} 个文件删除失败（列表中的失败项已标记，可重试）：",
                result.FailedItems.Select(static item => $"{item.Path}：{item.Error}"),
                MaxDisplayedMessages));
        }
    }

    // ---------------------------------------------------------------- 通用

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

    /// <summary>空字符串转 null（选项里用 null 表示「未设置」）。</summary>
    /// <param name="text">输入文本。</param>
    private static string? NullIfEmpty(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>列表行携带的信息：文件条目 + 是否为该组建议保留的文件。</summary>
    private sealed class DuplicateRow
    {
        /// <summary>文件条目（路径、大小、修改时间）。</summary>
        public DuplicateFileItem Item { get; init; } = new();

        /// <summary>是否为该组建议保留（默认不勾选）的文件。</summary>
        public bool IsKeep { get; init; }
    }
}
