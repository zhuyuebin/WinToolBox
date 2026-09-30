using System.Drawing;
using System.Windows.Forms;

namespace WinToolBox.Core;

/// <summary>
/// 通知抽象：便于托盘程序注入，也便于单元测试使用假实现。
/// </summary>
public interface INotifier
{
    /// <summary>显示一条通知。</summary>
    void ShowNotification(string title, string message);
}

/// <summary>
/// 通知实现：优先复用托盘程序的 NotifyIcon 弹气泡；没有传入时自动创建一个临时托盘图标。
/// 在后台线程调用时会自动切回创建通知器时的 UI 线程（气泡需要消息循环）。
/// </summary>
public sealed class Notifier : INotifier, IDisposable
{
    private const int BalloonTimeoutMilliseconds = 5000;

    private readonly Logger? _logger;
    private readonly SynchronizationContext? _uiContext;
    private readonly bool _ownsNotifyIcon;

    private NotifyIcon? _notifyIcon;
    private bool _disposed;

    /// <summary>创建自带托盘图标的通知器（仅在拿不到宿主 NotifyIcon 时使用）。</summary>
    public Notifier(Logger? logger = null)
    {
        _logger = logger;
        _uiContext = SynchronizationContext.Current;
        _ownsNotifyIcon = true;
    }

    /// <summary>复用宿主托盘程序的 NotifyIcon（推荐）。</summary>
    public Notifier(NotifyIcon notifyIcon, Logger? logger = null)
    {
        _notifyIcon = notifyIcon ?? throw new ArgumentNullException(nameof(notifyIcon));
        _logger = logger;
        _uiContext = SynchronizationContext.Current;
        _ownsNotifyIcon = false;
    }

    /// <inheritdoc />
    public void ShowNotification(string title, string message)
        => ShowNotification(title, message, ToolTipIcon.Info);

    /// <summary>显示带图标类型的气泡通知。</summary>
    public void ShowNotification(string title, string message, ToolTipIcon icon)
    {
        if (_disposed)
        {
            return;
        }

        var context = _uiContext;
        if (context is not null && context != SynchronizationContext.Current)
        {
            // 备份在后台线程执行，气泡必须在 UI 线程弹出
            context.Post(_ => ShowCore(title, message, icon), null);
            return;
        }

        ShowCore(title, message, icon);
    }

    private void ShowCore(string title, string message, ToolTipIcon icon)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var notifyIcon = EnsureNotifyIcon();
            notifyIcon.Visible = true;
            notifyIcon.ShowBalloonTip(
                BalloonTimeoutMilliseconds,
                title ?? string.Empty,
                message ?? string.Empty,
                icon);

            _logger?.Info($"已发送通知：{title} - {message}");
        }
        catch (Exception ex)
        {
            _logger?.Warn("发送托盘通知失败", ex);
        }
    }

    private NotifyIcon EnsureNotifyIcon()
    {
        if (_notifyIcon is not null)
        {
            return _notifyIcon;
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "WinToolBox",
            Visible = true
        };

        return _notifyIcon;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsNotifyIcon && _notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _notifyIcon = null;
    }
}
