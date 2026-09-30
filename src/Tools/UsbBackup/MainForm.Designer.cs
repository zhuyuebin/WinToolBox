#nullable enable

using System.Drawing;
using System.Windows.Forms;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// MainForm 的设计器代码：创建控件、设置布局与事件绑定。
/// 控件字段由 <see cref="InitializeComponent"/> 赋值，因此这里用 <c>null!</c> 抑制可空性警告。
/// 注意：本文件不重复定义 <c>Dispose(bool)</c>——释放逻辑（退订日志事件 + 释放 components）
/// 统一写在 MainForm.cs 的覆写里，保证 <c>base.Dispose(disposing)</c> 只被调用一次。
/// </summary>
partial class MainForm
{
    /// <summary>设计器要求的组件容器（本窗口没有使用任何组件，保持为 null）。</summary>
    private System.ComponentModel.IContainer? components = null;

    // ---------- 备份设置 ----------
    private GroupBox grpSettings = null!;

    private Label lblTarget = null!;

    private TextBox txtTarget = null!;

    private Button btnBrowse = null!;

    private Label lblExclude = null!;

    private TextBox txtExclude = null!;

    private Label lblExcludeHint = null!;

    private Button btnSave = null!;

    // ---------- 操作 ----------
    private GroupBox grpActions = null!;

    private Button btnBackup = null!;

    private Button btnRefresh = null!;

    private Button btnOpenLog = null!;

    private Button btnHide = null!;

    private Button btnExit = null!;

    private Label lblDevices = null!;

    // ---------- 运行日志 ----------
    private GroupBox grpLog = null!;

    private Button btnClearLog = null!;

    private TextBox txtLog = null!;

    // ---------- 状态栏 ----------
    private StatusStrip statusStripMain = null!;

    private ToolStripStatusLabel lblStatus = null!;

    /// <summary>初始化控件并完成布局（由设计器维护，业务逻辑写在 MainForm.cs）。</summary>
    private void InitializeComponent()
    {
        this.SuspendLayout();

        // ---------- 创建控件实例 ----------
        this.grpSettings = new GroupBox();
        this.lblTarget = new Label();
        this.txtTarget = new TextBox();
        this.btnBrowse = new Button();
        this.lblExclude = new Label();
        this.txtExclude = new TextBox();
        this.lblExcludeHint = new Label();
        this.btnSave = new Button();
        this.grpActions = new GroupBox();
        this.btnBackup = new Button();
        this.btnRefresh = new Button();
        this.btnOpenLog = new Button();
        this.btnHide = new Button();
        this.btnExit = new Button();
        this.lblDevices = new Label();
        this.grpLog = new GroupBox();
        this.btnClearLog = new Button();
        this.txtLog = new TextBox();
        this.statusStripMain = new StatusStrip();
        this.lblStatus = new ToolStripStatusLabel();

        this.grpSettings.SuspendLayout();
        this.grpActions.SuspendLayout();
        this.grpLog.SuspendLayout();
        this.statusStripMain.SuspendLayout();

        // ---------- 备份设置 ----------
        this.grpSettings.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        this.grpSettings.Controls.Add(this.lblTarget);
        this.grpSettings.Controls.Add(this.txtTarget);
        this.grpSettings.Controls.Add(this.btnBrowse);
        this.grpSettings.Controls.Add(this.lblExclude);
        this.grpSettings.Controls.Add(this.txtExclude);
        this.grpSettings.Controls.Add(this.lblExcludeHint);
        this.grpSettings.Controls.Add(this.btnSave);
        this.grpSettings.Location = new Point(12, 12);
        this.grpSettings.Name = "grpSettings";
        this.grpSettings.Size = new Size(856, 116);
        this.grpSettings.TabIndex = 0;
        this.grpSettings.TabStop = false;
        this.grpSettings.Text = "备份设置";

        this.lblTarget.AutoSize = true;
        this.lblTarget.Location = new Point(16, 33);
        this.lblTarget.Name = "lblTarget";
        this.lblTarget.TabIndex = 0;
        this.lblTarget.Text = "备份目标目录：";

        this.txtTarget.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        this.txtTarget.Location = new Point(130, 30);
        this.txtTarget.Name = "txtTarget";
        this.txtTarget.Size = new Size(610, 25);
        this.txtTarget.TabIndex = 1;

        this.btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        this.btnBrowse.Location = new Point(750, 29);
        this.btnBrowse.Name = "btnBrowse";
        this.btnBrowse.Size = new Size(90, 27);
        this.btnBrowse.TabIndex = 2;
        this.btnBrowse.Text = "浏览…";
        this.btnBrowse.UseVisualStyleBackColor = true;
        this.btnBrowse.Click += new System.EventHandler(this.OnBrowseClick);

        this.lblExclude.AutoSize = true;
        this.lblExclude.Location = new Point(16, 69);
        this.lblExclude.Name = "lblExclude";
        this.lblExclude.TabIndex = 4;
        this.lblExclude.Text = "排除的文件后缀：";

        this.txtExclude.Location = new Point(130, 66);
        this.txtExclude.Name = "txtExclude";
        this.txtExclude.Size = new Size(380, 25);
        this.txtExclude.TabIndex = 5;

        this.lblExcludeHint.AutoSize = true;
        this.lblExcludeHint.ForeColor = SystemColors.GrayText;
        this.lblExcludeHint.Location = new Point(520, 69);
        this.lblExcludeHint.Name = "lblExcludeHint";
        this.lblExcludeHint.TabIndex = 6;
        this.lblExcludeHint.Text = "（逗号分隔，如 .tmp,.part）";

        this.btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        this.btnSave.Location = new Point(750, 65);
        this.btnSave.Name = "btnSave";
        this.btnSave.Size = new Size(90, 27);
        this.btnSave.TabIndex = 7;
        this.btnSave.Text = "保存设置";
        this.btnSave.UseVisualStyleBackColor = true;
        this.btnSave.Click += new System.EventHandler(this.OnSaveClick);

        // ---------- 操作 ----------
        this.grpActions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        this.grpActions.Controls.Add(this.btnBackup);
        this.grpActions.Controls.Add(this.btnRefresh);
        this.grpActions.Controls.Add(this.btnOpenLog);
        this.grpActions.Controls.Add(this.btnHide);
        this.grpActions.Controls.Add(this.btnExit);
        this.grpActions.Controls.Add(this.lblDevices);
        this.grpActions.Location = new Point(12, 150);
        this.grpActions.Name = "grpActions";
        this.grpActions.Size = new Size(856, 78);
        this.grpActions.TabIndex = 1;
        this.grpActions.TabStop = false;
        this.grpActions.Text = "操作";

        this.btnBackup.Location = new Point(16, 28);
        this.btnBackup.Name = "btnBackup";
        this.btnBackup.Size = new Size(140, 36);
        this.btnBackup.TabIndex = 0;
        this.btnBackup.Text = "立即备份";
        this.btnBackup.UseVisualStyleBackColor = true;
        this.btnBackup.Click += new System.EventHandler(this.OnBackupClick);

        this.btnRefresh.Location = new Point(166, 28);
        this.btnRefresh.Name = "btnRefresh";
        this.btnRefresh.Size = new Size(120, 36);
        this.btnRefresh.TabIndex = 1;
        this.btnRefresh.Text = "刷新设备";
        this.btnRefresh.UseVisualStyleBackColor = true;
        this.btnRefresh.Click += new System.EventHandler(this.OnRefreshClick);

        this.btnOpenLog.Location = new Point(296, 28);
        this.btnOpenLog.Name = "btnOpenLog";
        this.btnOpenLog.Size = new Size(140, 36);
        this.btnOpenLog.TabIndex = 2;
        this.btnOpenLog.Text = "打开日志目录";
        this.btnOpenLog.UseVisualStyleBackColor = true;
        this.btnOpenLog.Click += new System.EventHandler(this.OnOpenLogClick);

        this.btnHide.Location = new Point(446, 28);
        this.btnHide.Name = "btnHide";
        this.btnHide.Size = new Size(130, 36);
        this.btnHide.TabIndex = 3;
        this.btnHide.Text = "隐藏到托盘";
        this.btnHide.UseVisualStyleBackColor = true;
        this.btnHide.Click += new System.EventHandler(this.OnHideClick);

        this.btnExit.Location = new Point(586, 28);
        this.btnExit.Name = "btnExit";
        this.btnExit.Size = new Size(120, 36);
        this.btnExit.TabIndex = 4;
        this.btnExit.Text = "退出程序";
        this.btnExit.UseVisualStyleBackColor = true;
        this.btnExit.Click += new System.EventHandler(this.OnExitClick);

        this.lblDevices.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        this.lblDevices.AutoSize = false;
        this.lblDevices.Location = new Point(720, 38);
        this.lblDevices.Name = "lblDevices";
        this.lblDevices.Size = new Size(130, 22);
        this.lblDevices.TabIndex = 5;
        this.lblDevices.Text = "设备：0 个";
        this.lblDevices.TextAlign = ContentAlignment.MiddleLeft;

        // ---------- 运行日志 ----------
        this.grpLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        this.grpLog.Controls.Add(this.btnClearLog);
        this.grpLog.Controls.Add(this.txtLog);
        this.grpLog.Location = new Point(12, 238);
        this.grpLog.Name = "grpLog";
        this.grpLog.Size = new Size(856, 330);
        this.grpLog.TabIndex = 2;
        this.grpLog.TabStop = false;
        this.grpLog.Text = "运行日志";

        this.btnClearLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        this.btnClearLog.Location = new Point(750, 18);
        this.btnClearLog.Name = "btnClearLog";
        this.btnClearLog.Size = new Size(90, 26);
        this.btnClearLog.TabIndex = 0;
        this.btnClearLog.Text = "清空";
        this.btnClearLog.UseVisualStyleBackColor = true;
        this.btnClearLog.Click += new System.EventHandler(this.OnClearLogClick);

        this.txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        this.txtLog.BackColor = Color.White;
        this.txtLog.Font = new Font("Consolas", 9F);
        this.txtLog.HideSelection = false;
        this.txtLog.Location = new Point(16, 50);
        this.txtLog.Multiline = true;
        this.txtLog.Name = "txtLog";
        this.txtLog.ReadOnly = true;
        this.txtLog.ScrollBars = ScrollBars.Vertical;
        this.txtLog.Size = new Size(824, 266);
        this.txtLog.TabIndex = 1;
        this.txtLog.WordWrap = false;

        // ---------- 状态栏 ----------
        this.statusStripMain.Dock = DockStyle.Bottom;
        this.statusStripMain.Items.AddRange(new ToolStripItem[] { this.lblStatus });
        this.statusStripMain.Location = new Point(0, 618);
        this.statusStripMain.Name = "statusStripMain";
        this.statusStripMain.Size = new Size(880, 22);
        this.statusStripMain.TabIndex = 3;

        this.lblStatus.Name = "lblStatus";
        this.lblStatus.Size = new Size(865, 17);
        this.lblStatus.Spring = true;
        this.lblStatus.Text = "就绪";
        this.lblStatus.TextAlign = ContentAlignment.MiddleLeft;

        // ---------- 窗体本体 ----------
        this.AutoScaleDimensions = new SizeF(96F, 96F);
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.ClientSize = new Size(880, 640);
        this.Controls.Add(this.grpLog);
        this.Controls.Add(this.grpActions);
        this.Controls.Add(this.grpSettings);
        this.Controls.Add(this.statusStripMain);
        this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
        this.MaximizeBox = true;
        this.MinimumSize = new Size(896, 680);
        this.Name = "MainForm";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Text = "WinToolBox - U盘备份";

        this.grpSettings.ResumeLayout(false);
        this.grpSettings.PerformLayout();
        this.grpActions.ResumeLayout(false);
        this.grpActions.PerformLayout();
        this.grpLog.ResumeLayout(false);
        this.grpLog.PerformLayout();
        this.statusStripMain.ResumeLayout(false);
        this.statusStripMain.PerformLayout();
        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
