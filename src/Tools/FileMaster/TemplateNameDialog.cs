using System.Drawing;
using System.Windows.Forms;

namespace WinToolBox.Tools.FileMaster;

/// <summary>
/// 输入模板名称的小对话框：「保存为模板」时使用，默认填入当前下拉框内容，
/// 输入一个不存在的名字即为「新建模板」，输入已有名字会即时提示将要覆盖。
/// </summary>
internal sealed class TemplateNameDialog : Form
{
    /// <summary>提示里最多显示多少个字符的名称（过长时截断，避免撑破布局）。</summary>
    private const int HintNameLimit = 16;

    private readonly TextBox _txtName;
    private readonly Label _lblPrompt;
    private readonly Label _lblHint;
    private readonly Button _btnOk;
    private readonly Button _btnCancel;
    private readonly HashSet<string> _existingNames;

    /// <summary>创建对话框。</summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="initialName">初始模板名（通常取下拉框当前内容）。</param>
    /// <param name="existingNames">已存在的模板名（只用于同名提示，不逐个列出）。</param>
    public TemplateNameDialog(string title, string initialName, IReadOnlyList<string> existingNames)
    {
        _existingNames = new HashSet<string>(
            existingNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        _lblPrompt = new Label();
        _txtName = new TextBox();
        _lblHint = new Label();
        _btnOk = new Button();
        _btnCancel = new Button();

        SuspendLayout();

        _lblPrompt.AutoSize = true;
        _lblPrompt.Location = new Point(16, 18);
        _lblPrompt.Name = "lblPrompt";
        _lblPrompt.Size = new Size(68, 17);
        _lblPrompt.TabIndex = 0;
        _lblPrompt.Text = "模板名称：";

        _txtName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _txtName.Location = new Point(16, 42);
        _txtName.MaxLength = 60;
        _txtName.Name = "txtName";
        _txtName.Size = new Size(388, 25);
        _txtName.TabIndex = 1;
        _txtName.Text = initialName;
        _txtName.TextChanged += OnNameChanged;
        _txtName.SelectAll();

        // 固定宽度的单行提示：过长自动省略号，绝不撑破对话框
        _lblHint.AutoEllipsis = true;
        _lblHint.ForeColor = SystemColors.GrayText;
        _lblHint.Location = new Point(16, 76);
        _lblHint.Name = "lblHint";
        _lblHint.Size = new Size(388, 20);
        _lblHint.TabIndex = 2;

        _btnOk.DialogResult = DialogResult.OK;
        _btnOk.Location = new Point(212, 112);
        _btnOk.Name = "btnOk";
        _btnOk.Size = new Size(90, 30);
        _btnOk.TabIndex = 3;
        _btnOk.Text = "确定";
        _btnOk.UseVisualStyleBackColor = true;

        _btnCancel.DialogResult = DialogResult.Cancel;
        _btnCancel.Location = new Point(314, 112);
        _btnCancel.Name = "btnCancel";
        _btnCancel.Size = new Size(90, 30);
        _btnCancel.TabIndex = 4;
        _btnCancel.Text = "取消";
        _btnCancel.UseVisualStyleBackColor = true;

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        // 与主窗口一致：按 DPI 缩放布局
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 158);
        Controls.Add(_lblHint);
        Controls.Add(_txtName);
        Controls.Add(_lblPrompt);
        Controls.Add(_btnOk);
        Controls.Add(_btnCancel);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "TemplateNameDialog";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = title;

        ResumeLayout(false);
        PerformLayout();

        UpdateHint();
    }

    /// <summary>用户输入的模板名（已去掉首尾空白）。</summary>
    public string TemplateName => _txtName.Text.Trim();

    /// <summary>输入变化：刷新提示与「确定」按钮状态。</summary>
    private void OnNameChanged(object? sender, EventArgs e) => UpdateHint();

    /// <summary>
    /// 一行短提示，随输入实时变化：
    /// 空 → 提示输入；新名称 → 将新建；已有名称 → 将覆盖（橙色提示注意）。
    /// </summary>
    private void UpdateHint()
    {
        var name = TemplateName;

        if (name.Length == 0)
        {
            _lblHint.Text = "请输入模板名称。";
            _lblHint.ForeColor = SystemColors.GrayText;
            _btnOk.Enabled = false;
            return;
        }

        _btnOk.Enabled = true;

        if (_existingNames.Contains(name))
        {
            _lblHint.Text = $"「{Shorten(name)}」已存在，确定后将覆盖它。";
            _lblHint.ForeColor = Color.FromArgb(196, 96, 0);
        }
        else
        {
            _lblHint.Text = "输入新名称将新建模板。";
            _lblHint.ForeColor = SystemColors.GrayText;
        }
    }

    /// <summary>名称过长时截断，避免提示被省略号吃掉关键信息。</summary>
    private static string Shorten(string name)
        => name.Length <= HintNameLimit ? name : name[..HintNameLimit] + "…";
}
