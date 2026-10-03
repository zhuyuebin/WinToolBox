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
using WinToolBox.Tools.FileMaster.Models;
using WinToolBox.Tools.FileMaster.Services;

namespace WinToolBox.Tools.FileMaster.UI;

/// <summary>
/// 「文件占用解锁」窗口：输入文件（或整个文件夹）路径，用 Windows Restart Manager 查询正在占用它的进程，
/// 勾选后可强制结束进程；「仅查看（复制信息）」把完整占用报告复制到剪贴板，便于留存或转给他人。
/// </summary>
/// <remarks>
/// <para>查询与结束进程都通过 <see cref="FeatureDialogBase.RunBusyAsync"/> 在后台线程执行，界面全程不阻塞。</para>
/// <para>结束进程不可撤销：执行前必须经过 <see cref="FeatureDialogBase.ConfirmDanger"/> 二次确认；
/// 关键系统进程由 <see cref="FileUnlockerService"/> 直接拒绝结束，结果中会单独提示。</para>
/// <para>本功能只调用 Restart Manager 查询与 <see cref="System.Diagnostics.Process.Kill()"/>，
/// 不读写注册表、不修改任何系统配置。</para>
/// <para>列表选择方式：勾选要结束的进程行（未勾选任何行时使用当前选中的行），再点击「结束选中进程」。</para>
/// </remarks>
public sealed class FileUnlockerDialog : FeatureDialogBase
{
    /// <summary>二次确认文案中最多列出的进程条数（其余用「等 N 个」概括）。</summary>
    private const int MaxNamesInConfirm = 10;

    /// <summary>失败 / 被拒绝的明细最多在界面日志中逐条列出的条数（其余写入日志文件）。</summary>
    private const int MaxLoggedFailures = 10;

    /// <summary>文件占用查询与解锁服务。</summary>
    private readonly FileUnlockerService _service;

    /// <summary>路径输入框（文件或文件夹的全路径）。</summary>
    private readonly TextBox _txtPath;

    /// <summary>最近一次查询结果（供「仅查看（复制信息）」使用；从未查询时为 null）。</summary>
    private FileLockQueryResult? _lastResult;

    /// <summary>「结果不完整」警告标签（仅在查询被截断时显示，避免用户把「没查完」误读成「没有被占用」）。</summary>
    private readonly Label _lblTruncatedWarning;

    /// <summary>创建「文件占用解锁」窗口。</summary>
    /// <param name="logger">日志记录器（可为 null）。</param>
    public FileUnlockerDialog(Logger? logger)
        : base("文件占用解锁", logger)
    {
        _service = new FileUnlockerService(logger);

        // ---------- 第 1 行：文件路径 + 选择文件 / 选择文件夹 ----------
        var rowPath = AddInputRow(InputLabelWidth, -100, 110, 130);

        _txtPath = CreateInputTextBox();
        _txtPath.PlaceholderText = "文件或文件夹的完整路径，例如 D:\\data\\report.xlsx";
        AddField(rowPath, "文件路径：", _txtPath, 0);

        // 「选择文件…」：OpenFileDialog；相邻的「选择文件夹…」：FolderBrowserDialog（两者都直接填入路径框）
        var btnPickFile = CreateInputButton("选择文件…", (_, _) => PickFile());
        AddCell(rowPath, btnPickFile, 2);

        var btnPickFolder = CreateInputButton("选择文件夹…", (_, _) => PickFolder(), 116);
        AddCell(rowPath, btnPickFolder, 3);

        // ---------- 第 2 行：说明文字 ----------
        var rowHint = AddInputRow(-100);

        var lblHint = new Label
        {
            Text = "提示：查询使用 Windows Restart Manager，不会修改注册表；结束进程会丢失该程序未保存的数据。",
            AutoSize = true,
            ForeColor = Color.DimGray
        };
        AddCell(rowHint, lblHint, 0);

        // ---------- 第 3 行：结果不完整警告（截断时用警告色显示；未截断时整行隐藏） ----------
        var rowTruncated = AddInputRow(-100);

        _lblTruncatedWarning = new Label
        {
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Visible = false,
            Margin = new Padding(0)
        };
        AddCell(rowTruncated, _lblTruncatedWarning, 0);

        // ---------- 预览列表：一行为一个占用进程 ----------
        // 打开复选框：勾选要结束的进程（未勾选任何行时退回到「选中的行」，见 CollectSelectedProcesses）
        PreviewList.CheckBoxes = true;
        AddColumn("进程名", 200);
        AddColumn("PID", 80, HorizontalAlignment.Right);
        AddColumn("类型", 110);
        AddColumn("可结束", 120);
        AddColumn("应用路径", 380);

        // ---------- 按钮区自右向左排列：先添加的在最右侧 ----------
        AddButton("关闭", (_, _) => Close());
        AddButton("结束选中进程", AsyncHandler(TerminateSelectedAsync));
        AddButton("查询占用", AsyncHandler(QuerySelectedPathAsync), primary: true);
        AddButton("复制信息", (_, _) => CopyReport());

        AppendLog("提示：查询占用使用系统 Restart Manager；如需结束进程请先确认已保存工作。");
        AppendLog("选择方式：勾选列表中的进程行（未勾选任何行时使用当前选中的行），再点击「结束选中进程」。");
        SetStatus("就绪：请输入文件或文件夹路径，然后点击「查询占用」。");
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

    /// <summary>忙碌状态变化：查询 / 结束进程期间禁用输入控件与列表，避免操作交叉。</summary>
    protected override void OnBusyChanged(bool busy) => SetInputsEnabled(!busy);

    // ---------------------------------------------------------------- 选择路径

    /// <summary>「选择文件…」：用 <see cref="OpenFileDialog"/> 选一个文件填入路径框。</summary>
    private void PickFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要查询占用的文件",
            Filter = "所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var typed = _txtPath.Text.Trim();
        if (File.Exists(typed))
        {
            dialog.FileName = typed;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.FileName.Length > 0)
        {
            _txtPath.Text = dialog.FileName;
            SetStatus($"已选择文件：{dialog.FileName} 请点击「查询占用」。");
        }
    }

    /// <summary>
    /// 「选择文件夹…」：用 <see cref="FolderBrowserDialog"/> 选一个目录填入路径框
    /// （写法与 <see cref="FeatureDialogBase.CreateDirectoryPicker"/> 一致，只是按钮文字不同）。
    /// </summary>
    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择要查询占用的文件夹（也可以查询整个文件夹的占用）",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };

        var typed = _txtPath.Text.Trim();
        if (Directory.Exists(typed))
        {
            dialog.SelectedPath = typed;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedPath.Length > 0)
        {
            _txtPath.Text = dialog.SelectedPath;
            SetStatus($"已选择文件夹：{dialog.SelectedPath} 请点击「查询占用」。");
        }
    }

    // ---------------------------------------------------------------- 查询占用

    /// <summary>「查询占用」：校验路径后查询并回填列表。</summary>
    private async Task QuerySelectedPathAsync()
    {
        var path = _txtPath.Text.Trim();

        if (path.Length == 0)
        {
            ShowWarning("请先输入或选择要查询占用的文件（或文件夹）路径。");
            _txtPath.Focus();
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ShowWarning("路径不存在：" + path + Environment.NewLine + "请重新输入或点击「选择文件…」「选择文件夹…」。");
            _txtPath.Focus();
            return;
        }

        await QueryOccupancyAsync(path);
    }

    /// <summary>
    /// 查询指定路径的占用情况并回填界面（结束进程后也会调用本方法自动重新查询）。
    /// </summary>
    /// <param name="path">已经校验过存在性的文件 / 文件夹路径。</param>
    private async Task QueryOccupancyAsync(string path)
    {
        FileLockQueryResult? result = null;

        var ok = await RunBusyAsync(
            async token =>
            {
                result = await Task.Run(
                    () => _service.FindLockingProcesses(path),
                    token).ConfigureAwait(true);
            },
            "正在查询文件占用…");

        if (!ok || result is null || !IsUiUsable)
        {
            return;
        }

        FillResult(result);
    }

    /// <summary>把查询结果填入列表：状态栏显示摘要，右侧计数显示占用进程数，出错时弹警告。</summary>
    /// <param name="result">查询结果。</param>
    private void FillResult(FileLockQueryResult result)
    {
        _lastResult = result;

        PreviewList.BeginUpdate();
        try
        {
            PreviewList.Items.Clear();

            foreach (var process in result.Processes)
            {
                var row = new ListViewItem(process.DisplayName)
                {
                    Tag = process,
                    // 悬停显示完整的应用名（通常是被占用进程的完整路径）
                    ToolTipText = process.AppName.Length > 0 ? process.AppName : process.DisplayName,
                    // 关键系统进程不可结束，用灰色弱化提示
                    ForeColor = process.CanTerminate ? PreviewList.ForeColor : Color.Gray
                };

                row.SubItems.Add(process.ProcessId.ToString());
                row.SubItems.Add(process.AppTypeText);
                row.SubItems.Add(process.CanTerminate ? "是" : "否（关键进程）");
                row.SubItems.Add(FileUnlockerService.ShortenAppName(process.AppName));

                PreviewList.Items.Add(row);
            }
        }
        finally
        {
            PreviewList.EndUpdate();
        }

        SetStatus(result.Summary);
        AppendLog(result.Summary);
        SetCountText($"占用进程 {result.Processes.Count} 个");

        UpdateTruncationWarning(result);

        if (!string.IsNullOrEmpty(result.Error))
        {
            ShowWarning("占用查询未完全成功：" + Environment.NewLine + result.Error);
        }
    }

    /// <summary>
    /// 查询被截断（目录文件过多或部分内容无法枚举）时，用警告色说明「已检查多少 / 共多少，结果不完整」；
    /// 结果完整时隐藏该提示，避免把「没查完」当成「没有任何进程占用」。
    /// </summary>
    /// <param name="result">查询结果。</param>
    private void UpdateTruncationWarning(FileLockQueryResult result)
    {
        if (!result.Truncated)
        {
            _lblTruncatedWarning.Text = string.Empty;
            _lblTruncatedWarning.Visible = false;
            return;
        }

        var text =
            $"已检查 {result.ScannedFileCount} / 共 {result.TotalFileCount} 个文件，结果不完整：" +
            $"目录内文件数超过单次查询上限（{FileUnlockerService.MaxFilesPerQuery} 个）或部分子目录无法枚举，" +
            "请对剩余文件或子目录单独查询。";

        _lblTruncatedWarning.Text = text;
        _lblTruncatedWarning.Visible = true;

        AppendLog("警告：" + text);
    }

    // ---------------------------------------------------------------- 结束进程

    /// <summary>「结束选中进程」：二次确认后结束勾选（或选中）的进程，成功后自动重新查询一次。</summary>
    private async Task TerminateSelectedAsync()
    {
        var processes = CollectSelectedProcesses();

        if (processes.Count == 0)
        {
            ShowWarning(
                "请先在列表中选择要结束的进程。" + Environment.NewLine +
                "提示：勾选列表中的进程行（或直接选中该行）后再点击「结束选中进程」；列表为空时请先点击「查询占用」。");
            return;
        }

        if (!ConfirmTerminate(processes))
        {
            AppendLog("已取消结束进程。");
            return;
        }

        var names = string.Join("、", processes.Select(static process => $"{process.DisplayName}(PID {process.ProcessId})"));

        UnlockResult? result = null;

        var progress = new Progress<UnlockProgress>(report =>
        {
            SetProgress(report.Percent);
            SetStatus($"正在结束进程（{report.Processed}/{report.Total}）：{report.CurrentProcess}");
            SetCountText($"{report.Processed}/{report.Total}");
        });

        var ok = await RunBusyAsync(
            async token =>
            {
                AppendLog($"开始结束 {processes.Count} 个进程：{names}");

                result = await Task.Run(
                    () => _service.TerminateProcesses(processes, progress, token),
                    token).ConfigureAwait(true);
            },
            "正在结束选中的进程…");

        if (!ok || result is null || !IsUiUsable)
        {
            return;
        }

        ApplyTerminateResult(result);

        // 有进程被成功结束后自动重新查询一次占用，方便确认文件是否已经可以操作
        if (result.SucceededCount > 0)
        {
            var path = _txtPath.Text.Trim();
            if (path.Length > 0 && (File.Exists(path) || Directory.Exists(path)))
            {
                AppendLog("进程已结束，自动重新查询占用…");
                await QueryOccupancyAsync(path);
            }
        }
    }

    /// <summary>结束进程前的二次确认：写明数量、进程名与 PID，并提示未保存数据会丢失。</summary>
    /// <param name="processes">待结束的进程。</param>
    private bool ConfirmTerminate(IReadOnlyList<FileLockProcess> processes)
    {
        var builder = new StringBuilder();
        builder.Append($"将强制结束 {processes.Count} 个进程（");
        builder.Append(string.Join(
            "、",
            processes.Take(MaxNamesInConfirm).Select(static process => $"{process.DisplayName}(PID {process.ProcessId})")));

        if (processes.Count > MaxNamesInConfirm)
        {
            builder.Append($"……等 {processes.Count} 个");
        }

        builder.Append("），未保存的数据会丢失，确定继续吗？");

        var critical = processes.Count(static process => !process.CanTerminate);
        if (critical > 0)
        {
            builder.AppendLine().AppendLine();
            builder.Append($"注意：其中 {critical} 个是关键系统进程，出于系统稳定考虑会被自动拒绝结束。");
        }

        return ConfirmDanger(builder.ToString());
    }

    /// <summary>把结束结果回填到界面：摘要、失败 / 被拒绝明细逐条写日志，并单独提示被拒绝的关键进程。</summary>
    /// <param name="result">结束进程的结果。</param>
    private void ApplyTerminateResult(UnlockResult result)
    {
        SetStatus(result.Summary);
        AppendLog(result.Summary);

        var failures = result.Items.Where(static item => !item.Success).ToList();

        // 界面日志最多逐条列出 MaxLoggedFailures 条，其余写入日志文件，避免日志框被刷屏
        foreach (var failure in failures.Take(MaxLoggedFailures))
        {
            AppendLog($"{(failure.Rejected ? "已拒绝" : "结束失败")}：{DescribeProcess(failure)}：{failure.Error}");
        }

        foreach (var failure in failures.Skip(MaxLoggedFailures))
        {
            Logger?.Warn($"结束进程未成功：{DescribeProcess(failure)}：{failure.Error}");
        }

        if (failures.Count > MaxLoggedFailures)
        {
            AppendLog($"……其余 {failures.Count - MaxLoggedFailures} 条明细已写入日志文件：{AppPaths.LogDirectory}");
        }

        if (failures.Count == 0)
        {
            return;
        }

        var rejected = failures.Count(static item => item.Rejected);
        var rejectedNote = rejected > 0 ? $"（其中 {rejected} 个是关键系统进程，已拒绝结束）" : string.Empty;

        var message = new StringBuilder();
        message.AppendLine($"有 {failures.Count} 个进程未能结束{rejectedNote}：");

        foreach (var failure in failures.Take(MaxLoggedFailures))
        {
            message.AppendLine("· " + DescribeProcess(failure) + "：" +
                (failure.Rejected ? "关键系统进程，为避免系统不稳定已拒绝结束" : failure.Error));
        }

        if (failures.Count > MaxLoggedFailures)
        {
            message.AppendLine($"……其余 {failures.Count - MaxLoggedFailures} 条请查看运行日志。");
        }

        ShowWarning(message.ToString());
    }

    /// <summary>收集要结束的进程：优先取勾选的行，没有勾选时取当前选中的行（按 PID 去重）。</summary>
    private List<FileLockProcess> CollectSelectedProcesses()
    {
        var processes = new List<FileLockProcess>();
        var seen = new HashSet<int>();

        foreach (ListViewItem row in PreviewList.CheckedItems)
        {
            AddProcess(row, processes, seen);
        }

        if (processes.Count == 0)
        {
            foreach (ListViewItem row in PreviewList.SelectedItems)
            {
                AddProcess(row, processes, seen);
            }
        }

        return processes;
    }

    /// <summary>把一行列表项里的进程加入待处理集合（去重，忽略非进程行）。</summary>
    /// <param name="row">列表行。</param>
    /// <param name="processes">目标集合。</param>
    /// <param name="seen">已出现的进程 ID。</param>
    private static void AddProcess(ListViewItem row, List<FileLockProcess> processes, HashSet<int> seen)
    {
        if (row.Tag is FileLockProcess process && seen.Add(process.ProcessId))
        {
            processes.Add(process);
        }
    }

    /// <summary>结束结果的显示文本（进程名 + PID）。</summary>
    /// <param name="item">单个进程的结束结果。</param>
    private static string DescribeProcess(ProcessTerminateResult item)
    {
        var name = item.ProcessName.Length > 0 ? item.ProcessName : "（未知进程）";
        return $"{name}(PID {item.ProcessId})";
    }

    // ---------------------------------------------------------------- 复制信息

    /// <summary>「仅查看（复制信息）」：把最近一次查询的完整报告复制到剪贴板。</summary>
    private void CopyReport()
    {
        var result = _lastResult;
        if (result is null)
        {
            ShowWarning("还没有查询结果，请先点击「查询占用」。");
            return;
        }

        var report = FileUnlockerService.BuildReport(result);

        try
        {
            Clipboard.SetText(report);
        }
        catch (Exception ex)
        {
            // 剪贴板可能被其它程序占用，此时给出明确提示而不是抛出异常
            Logger?.Warn("复制占用信息到剪贴板失败。", ex);
            ShowWarning("复制到剪贴板失败（剪贴板可能正被其它程序占用）：" + Environment.NewLine + ex.Message);
            return;
        }

        AppendLog("占用信息已复制到剪贴板。");
        SetStatus("占用信息已复制到剪贴板。");
    }
}
