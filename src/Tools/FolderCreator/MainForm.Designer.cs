using System.Drawing;
using System.Windows.Forms;

namespace WinToolBox.Tools.FolderCreator;

/// <summary>
/// MainForm 的设计器代码：创建控件、设置布局与事件绑定。
/// 控件字段由 <see cref="InitializeComponent"/> 赋值，因此这里用 <c>null!</c> 抑制可空性警告。
/// </summary>
partial class MainForm
{
    /// <summary>设计器要求的组件容器（本窗口没有使用任何组件，保持为 null）。</summary>
    private System.ComponentModel.IContainer? components = null;

    private Label lblRoot = null!;

    private TextBox txtRoot = null!;

    private Button btnBrowse = null!;

    private Label lblRules = null!;

    private TextBox txtRules = null!;

    private GroupBox grpMode = null!;

    private RadioButton rdoStrict = null!;

    private RadioButton rdoLoose = null!;

    private Button btnCreate = null!;

    private Button btnCheck = null!;

    private Button btnSample = null!;

    private TreeView treeResult = null!;

    private StatusStrip statusStripResult = null!;

    private ToolStripStatusLabel lblStatus = null!;

    private ToolStripProgressBar barProgress = null!;

    /// <summary>释放设计器组件。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>初始化控件并完成布局（由设计器维护，业务逻辑写在 MainForm.cs）。</summary>
    private void InitializeComponent()
    {
        this.SuspendLayout();

        // ---------- 创建控件实例 ----------
        this.lblRoot = new Label();
        this.txtRoot = new TextBox();
        this.btnBrowse = new Button();
        this.lblRules = new Label();
        this.txtRules = new TextBox();
        this.grpMode = new GroupBox();
        this.rdoStrict = new RadioButton();
        this.rdoLoose = new RadioButton();
        this.btnCreate = new Button();
        this.btnCheck = new Button();
        this.btnSample = new Button();
        this.treeResult = new TreeView();
        this.statusStripResult = new StatusStrip();
        this.lblStatus = new ToolStripStatusLabel();
        this.barProgress = new ToolStripProgressBar();

        this.grpMode.SuspendLayout();
        this.statusStripResult.SuspendLayout();

        // ---------- 顶部：根目录 ----------
        this.lblRoot.AutoSize = true;
        this.lblRoot.Location = new Point(12, 16);
        this.lblRoot.Name = "lblRoot";
        this.lblRoot.Size = new Size(62, 17);
        this.lblRoot.TabIndex = 0;
        this.lblRoot.Text = "根目录：";

        this.txtRoot.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        this.txtRoot.Location = new Point(88, 10);
        this.txtRoot.Name = "txtRoot";
        this.txtRoot.Size = new Size(780, 25);
        this.txtRoot.TabIndex = 1;

        this.btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        this.btnBrowse.Location = new Point(876, 9);
        this.btnBrowse.Name = "btnBrowse";
        this.btnBrowse.Size = new Size(105, 27);
        this.btnBrowse.TabIndex = 2;
        this.btnBrowse.Text = "浏览…";
        this.btnBrowse.UseVisualStyleBackColor = true;
        this.btnBrowse.Click += new System.EventHandler(this.OnBrowseClick);

        // ---------- 左侧：规则输入 ----------
        this.lblRules.AutoSize = true;
        this.lblRules.Location = new Point(12, 46);
        this.lblRules.Name = "lblRules";
        this.lblRules.Size = new Size(420, 17);
        this.lblRules.TabIndex = 3;
        this.lblRules.Text = "文件夹规则（每行一个，行首 - 的数量代表层级，# 开头为注释）：";

        this.txtRules.AcceptsReturn = true;
        this.txtRules.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        this.txtRules.Font = new Font("Consolas", 10F);
        this.txtRules.Location = new Point(12, 72);
        this.txtRules.Multiline = true;
        this.txtRules.Name = "txtRules";
        this.txtRules.ScrollBars = ScrollBars.Vertical;
        this.txtRules.Size = new Size(470, 360);
        this.txtRules.TabIndex = 4;

        // ---------- 中间：检查模式 ----------
        this.grpMode.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        this.grpMode.Controls.Add(this.rdoStrict);
        this.grpMode.Controls.Add(this.rdoLoose);
        this.grpMode.Location = new Point(12, 442);
        this.grpMode.Name = "grpMode";
        this.grpMode.Size = new Size(470, 88);
        this.grpMode.TabIndex = 5;
        this.grpMode.TabStop = false;
        this.grpMode.Text = "检查模式";

        this.rdoStrict.AutoSize = true;
        this.rdoStrict.Checked = true;
        this.rdoStrict.Location = new Point(16, 26);
        this.rdoStrict.Name = "rdoStrict";
        this.rdoStrict.Size = new Size(210, 21);
        this.rdoStrict.TabIndex = 0;
        this.rdoStrict.TabStop = true;
        this.rdoStrict.Text = "严格模式（检查多余文件夹）";
        this.rdoStrict.UseVisualStyleBackColor = true;

        this.rdoLoose.AutoSize = true;
        this.rdoLoose.Location = new Point(16, 56);
        this.rdoLoose.Name = "rdoLoose";
        this.rdoLoose.Size = new Size(180, 21);
        this.rdoLoose.TabIndex = 1;
        this.rdoLoose.Text = "宽松模式（仅检查缺失）";
        this.rdoLoose.UseVisualStyleBackColor = true;

        // ---------- 中间：操作按钮 ----------
        this.btnCreate.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        this.btnCreate.Location = new Point(12, 542);
        this.btnCreate.Name = "btnCreate";
        this.btnCreate.Size = new Size(150, 38);
        this.btnCreate.TabIndex = 6;
        this.btnCreate.Text = "创建文件夹";
        this.btnCreate.UseVisualStyleBackColor = true;
        this.btnCreate.Click += new System.EventHandler(this.OnCreateClick);

        this.btnCheck.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        this.btnCheck.Location = new Point(172, 542);
        this.btnCheck.Name = "btnCheck";
        this.btnCheck.Size = new Size(150, 38);
        this.btnCheck.TabIndex = 7;
        this.btnCheck.Text = "检查一致性";
        this.btnCheck.UseVisualStyleBackColor = true;
        this.btnCheck.Click += new System.EventHandler(this.OnCheckClick);

        this.btnSample.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        this.btnSample.Location = new Point(332, 542);
        this.btnSample.Name = "btnSample";
        this.btnSample.Size = new Size(150, 38);
        this.btnSample.TabIndex = 8;
        this.btnSample.Text = "载入示例规则";
        this.btnSample.UseVisualStyleBackColor = true;
        this.btnSample.Click += new System.EventHandler(this.OnLoadSampleClick);

        // ---------- 右侧：结果树 ----------
        this.treeResult.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        this.treeResult.FullRowSelect = true;
        this.treeResult.HideSelection = false;
        this.treeResult.Location = new Point(500, 72);
        this.treeResult.Name = "treeResult";
        this.treeResult.ShowLines = true;
        this.treeResult.ShowNodeToolTips = true;
        this.treeResult.Size = new Size(481, 508);
        this.treeResult.TabIndex = 9;

        // ---------- 底部：状态栏 ----------
        this.statusStripResult.Dock = DockStyle.Bottom;
        this.statusStripResult.Items.AddRange(new ToolStripItem[] { this.lblStatus, this.barProgress });
        this.statusStripResult.Location = new Point(0, 618);
        this.statusStripResult.Name = "statusStripResult";
        this.statusStripResult.Size = new Size(1000, 22);
        this.statusStripResult.TabIndex = 10;

        this.lblStatus.Name = "lblStatus";
        this.lblStatus.Size = new Size(800, 17);
        this.lblStatus.Spring = true;
        this.lblStatus.Text = "就绪";
        this.lblStatus.TextAlign = ContentAlignment.MiddleLeft;

        this.barProgress.AutoSize = false;
        this.barProgress.Maximum = 100;
        this.barProgress.Minimum = 0;
        this.barProgress.Name = "barProgress";
        this.barProgress.Size = new Size(160, 16);
        this.barProgress.Step = 1;
        this.barProgress.Value = 0;

        // ---------- 窗体本体 ----------
        this.AutoScaleDimensions = new SizeF(96F, 96F);
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.ClientSize = new Size(1000, 640);
        this.Controls.Add(this.treeResult);
        this.Controls.Add(this.grpMode);
        this.Controls.Add(this.btnSample);
        this.Controls.Add(this.btnCheck);
        this.Controls.Add(this.btnCreate);
        this.Controls.Add(this.txtRules);
        this.Controls.Add(this.lblRules);
        this.Controls.Add(this.btnBrowse);
        this.Controls.Add(this.txtRoot);
        this.Controls.Add(this.lblRoot);
        this.Controls.Add(this.statusStripResult);
        this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
        this.MinimumSize = new Size(1016, 680);
        this.Name = "MainForm";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Text = "FolderCreator - 批量创建文件夹";
        this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.OnFormClosing);

        this.grpMode.ResumeLayout(false);
        this.grpMode.PerformLayout();
        this.statusStripResult.ResumeLayout(false);
        this.statusStripResult.PerformLayout();
        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
