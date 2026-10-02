using System;
using System.Drawing;
using System.Windows.Forms;

namespace WinToolBox.Tools.FileMaster.UI;

/// <summary>
/// 「目录文本框 + 浏览…按钮」组合控件：文本框可直接输入，按钮弹出文件夹选择框；
/// 在网格布局中作为一个整体占用一个单元格，避免文本框与按钮各占一格造成的对不齐。
/// </summary>
public sealed class FolderPickerBox : TableLayoutPanel
{
    /// <summary>浏览按钮的固定宽度（96 DPI 基准）。</summary>
    private const int BrowseButtonWidth = 92;

    private readonly TextBox _textBox;
    private readonly Button _browseButton;

    /// <summary>创建目录选择控件。</summary>
    /// <param name="description">文件夹选择对话框的说明文字。</param>
    /// <param name="owner">用于对话框定位的父窗口。</param>
    public FolderPickerBox(string description, IWin32Window? owner)
    {
        Description = description;
        Owner = owner;

        ColumnCount = 2;
        RowCount = 1;
        AutoSize = false;
        Height = 27;
        Margin = new Padding(0, 4, 10, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, BrowseButtonWidth));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 8, 1)
        };

        _browseButton = new Button
        {
            Text = "浏览…",
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            UseVisualStyleBackColor = true
        };

        _browseButton.Click += (_, _) => Browse();

        Controls.Add(_textBox, 0, 0);
        Controls.Add(_browseButton, 1, 0);
    }

    /// <summary>文件夹选择对话框的说明文字。</summary>
    public string Description { get; }

    /// <summary>父窗口（用于对话框定位，可为 null）。</summary>
    public IWin32Window? Owner { get; }

    /// <summary>内部文本框（需要做输入校验时可访问）。</summary>
    public TextBox TextBox => _textBox;

    /// <summary>当前路径（已去除首尾空白）。</summary>
    public string Path
    {
        get => _textBox.Text.Trim();
        set => _textBox.Text = value;
    }

    /// <summary>把路径写入文本框。</summary>
    public void SetPath(string? path) => _textBox.Text = path ?? string.Empty;

    /// <summary>弹出文件夹选择框；用户取消时不改变内容。</summary>
    public void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Description,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        var typed = Path;
        if (typed.Length > 0 && System.IO.Directory.Exists(typed))
        {
            dialog.SelectedPath = typed;
        }

        var result = Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner);
        if (result == DialogResult.OK && dialog.SelectedPath.Length > 0)
        {
            _textBox.Text = dialog.SelectedPath;
        }
    }
}
