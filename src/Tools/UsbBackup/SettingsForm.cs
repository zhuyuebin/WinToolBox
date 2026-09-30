using System.Drawing;
using System.Windows.Forms;
using WinToolBox.Core;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// 简单的设置窗口：选择备份目标目录、开关自动备份、编辑排除后缀。
/// </summary>
public sealed class SettingsForm : Form
{
    /// <summary>排除后缀输入框下方的灰色提示文字。</summary>
    private const string ExtensionHintText = "多个后缀用英文逗号分隔，例如：.tmp,.bak；也可以只写 tmp。";

    private readonly ConfigManager _configManager;
    private readonly INotifier _notifier;
    private readonly Logger _logger;

    private readonly TextBox _targetDirectoryTextBox;
    private readonly TextBox _excludedExtensionsTextBox;
    private readonly CheckBox _autoBackupCheckBox;
    private readonly Label _configPathLabel;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _browseButton;

    /// <summary>创建设置窗口（构造函数注入 Core 组件）。</summary>
    public SettingsForm(ConfigManager configManager, INotifier notifier, Logger logger)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // ---------- 窗体本体 ----------
        Text = "WinToolBox - U盘备份 设置";
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(560, 300);

        // ---------- 标题 ----------
        Controls.Add(new Label
        {
            Text = "备份设置",
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(16, 14),
            Size = new Size(200, 24),
            TextAlign = ContentAlignment.MiddleLeft
        });

        // ---------- 备份目标目录 ----------
        Controls.Add(new Label
        {
            Text = "备份目标目录：",
            Location = new Point(16, 48),
            Size = new Size(100, 23),
            TextAlign = ContentAlignment.MiddleLeft
        });

        _targetDirectoryTextBox = new TextBox
        {
            Location = new Point(120, 48),
            Size = new Size(320, 23),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_targetDirectoryTextBox);

        _browseButton = new Button
        {
            Text = "浏览…",
            Location = new Point(448, 47),
            Size = new Size(96, 25),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            UseVisualStyleBackColor = true
        };
        _browseButton.Click += OnBrowseClick;
        Controls.Add(_browseButton);

        // ---------- 自动备份开关 ----------
        _autoBackupCheckBox = new CheckBox
        {
            Text = "检测到 U 盘插入时自动备份",
            Location = new Point(120, 82),
            Size = new Size(320, 24),
            Checked = true,
            UseVisualStyleBackColor = true
        };
        Controls.Add(_autoBackupCheckBox);

        // ---------- 排除后缀 ----------
        Controls.Add(new Label
        {
            Text = "排除的文件后缀：",
            Location = new Point(16, 118),
            Size = new Size(100, 23),
            TextAlign = ContentAlignment.MiddleLeft
        });

        _excludedExtensionsTextBox = new TextBox
        {
            Location = new Point(120, 118),
            Size = new Size(320, 23),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        Controls.Add(_excludedExtensionsTextBox);

        Controls.Add(new Label
        {
            Text = ExtensionHintText,
            ForeColor = SystemColors.GrayText,
            Location = new Point(120, 144),
            Size = new Size(430, 20),
            TextAlign = ContentAlignment.MiddleLeft
        });

        // ---------- 说明 ----------
        Controls.Add(new Label
        {
            Text = "备份为增量复制：只新增或更新发生变化的文件，不会删除或覆盖备份目录中的其它内容。",
            ForeColor = SystemColors.GrayText,
            Location = new Point(16, 174),
            Size = new Size(530, 20),
            TextAlign = ContentAlignment.MiddleLeft
        });

        _configPathLabel = new Label
        {
            ForeColor = SystemColors.GrayText,
            Location = new Point(16, 200),
            Size = new Size(530, 20),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(_configPathLabel);

        // ---------- 按钮 ----------
        _saveButton = new Button
        {
            Text = "保存",
            Location = new Point(344, 244),
            Size = new Size(96, 30),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            UseVisualStyleBackColor = true
        };
        _saveButton.Click += OnSaveClick;
        Controls.Add(_saveButton);

        _cancelButton = new Button
        {
            Text = "取消",
            Location = new Point(448, 244),
            Size = new Size(96, 30),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
            UseVisualStyleBackColor = true
        };
        Controls.Add(_cancelButton);

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        LoadConfiguration();
    }

    /// <summary>用磁盘上的配置填充界面（读取永不抛异常）。</summary>
    private void LoadConfiguration()
    {
        BackupConfig config;

        try
        {
            config = _configManager.Load();
            _configPathLabel.Text = "配置文件：" + _configManager.ConfigFilePath;
        }
        catch (Exception ex)
        {
            _logger.Warn("读取配置失败，设置窗口将显示默认值。", ex);
            config = BackupConfig.CreateDefault();
            _configPathLabel.Text = "读取配置失败，已显示默认值。";
        }

        _targetDirectoryTextBox.Text = config.BackupTargetDirectory;
        _autoBackupCheckBox.Checked = config.AutoBackupEnabled;
        _excludedExtensionsTextBox.Text = string.Join(", ", config.ExcludedExtensions ?? new List<string>());
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择 U 盘备份的保存位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        var current = _targetDirectoryTextBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedPath.Length > 0)
        {
            _targetDirectoryTextBox.Text = dialog.SelectedPath;
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var target = (_targetDirectoryTextBox.Text ?? string.Empty).Trim();

        if (target.Length == 0)
        {
            MessageBox.Show(
                this,
                "请先选择备份目标目录。",
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _targetDirectoryTextBox.Focus();
            return;
        }

        // 目录不存在时尝试创建，创建失败直接中止保存
        if (!EnsureTargetDirectory(target))
        {
            return;
        }

        // 目标目录位于可移动盘上时给出警告，但允许继续保存
        if (IsOnRemovableDrive(target, out var driveName))
        {
            var answer = MessageBox.Show(
                this,
                $"选择的目录位于可移动磁盘 {driveName} 上。" + Environment.NewLine +
                "把 U 盘数据备份到另一块可移动磁盘，拔盘后备份将不可用，且拔出时可能导致备份中断。" + Environment.NewLine +
                Environment.NewLine + "仍然保存该目录吗？",
                "目标目录可能不安全",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        try
        {
            var config = _configManager.Load();
            config.BackupTargetDirectory = target;
            config.AutoBackupEnabled = _autoBackupCheckBox.Checked;
            config.ExcludedExtensions = ParseExtensions(_excludedExtensionsTextBox.Text);
            config.Normalize();

            _configManager.Save(config);

            _logger.Info($"设置已保存：目标目录={config.BackupTargetDirectory}，自动备份={config.AutoBackupEnabled}，" +
                         $"排除后缀={string.Join(",", config.ExcludedExtensions)}");

            _notifier.ShowNotification("设置已保存", $"备份目标目录：{config.BackupTargetDirectory}");

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _logger.Error("保存设置失败。", ex);
            MessageBox.Show(
                this,
                "保存设置失败：" + ex.Message,
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>确保目标目录存在；失败时提示并返回 false。</summary>
    private bool EnsureTargetDirectory(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                return true;
            }

            AppPaths.EnsureDirectory(target);

            if (Directory.Exists(target))
            {
                _logger.Info($"已创建备份目标目录：{target}");
                return true;
            }

            MessageBox.Show(
                this,
                $"无法创建目标目录：{target}",
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warn($"创建备份目标目录失败：{target}", ex);
            MessageBox.Show(
                this,
                $"无法创建目标目录：{target}{Environment.NewLine}{ex.Message}",
                "WinToolBox - U盘备份",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>判断目标目录是否位于可移动磁盘上。</summary>
    private bool IsOnRemovableDrive(string target, out string driveName)
    {
        driveName = string.Empty;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Removable)
            {
                return false;
            }

            driveName = root;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"判断目标目录所在驱动器类型失败：{target}", ex);
            return false;
        }
    }

    /// <summary>把界面上的逗号/分号分隔文本解析为后缀列表（规范化交给 BackupConfig.Normalize）。</summary>
    private static List<string> ParseExtensions(string? text)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var part in text!.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Trim();
            if (value.Length > 0)
            {
                result.Add(value);
            }
        }

        return result;
    }
}
