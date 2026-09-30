using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinToolBox.Core;

namespace WinToolBox.Tools.UsbBackup;

/// <summary>
/// U 盘插拔监听器：接收 Windows 广播的 WM_DEVICECHANGE 消息，去抖后重新扫描可移动磁盘，
/// 与上一次快照按唯一标识（卷标 + 卷序列号 + 容量）比较后触发 DeviceArrived / DeviceRemoved。
/// 不使用 WMI，避免额外的依赖与权限问题。
/// </summary>
public sealed class UsbWatcher : IDisposable
{
    /// <summary>设备变化广播消息。</summary>
    private const int WmDeviceChange = 0x0219;

    /// <summary>有设备到达（插入）。</summary>
    private const int DbtDeviceArrival = 0x8000;

    /// <summary>设备移除完成（拔出）。</summary>
    private const int DbtDeviceRemoveComplete = 0x8004;

    /// <summary>设备节点发生变化（不区分插入/拔出，作为兜底触发）。</summary>
    private const int DbtDevNodesChanged = 0x0007;

    /// <summary>设备变化后的防抖时间（毫秒）。</summary>
    private const int DebounceMilliseconds = 800;

    private readonly Logger? _logger;
    private readonly UsbDetector _detector;

    /// <summary>用于接收设备广播的隐藏顶层窗口。</summary>
    private readonly DeviceWindow _deviceWindow;

    /// <summary>防抖定时器。</summary>
    private readonly System.Windows.Forms.Timer _debounceTimer;

    /// <summary>最近一次扫描到的设备快照：唯一标识 → 设备信息。</summary>
    private Dictionary<string, UsbDeviceInfo> _snapshot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>避免同一次设备变化（多条广播消息）重复触发扫描。</summary>
    private int _pendingScan;

    private bool _started;
    private bool _disposed;

    /// <summary>创建监听器。</summary>
    /// <param name="detector">可移动磁盘检测器。</param>
    /// <param name="logger">日志记录器。</param>
    public UsbWatcher(UsbDetector detector, Logger? logger = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _logger = logger;

        _deviceWindow = new DeviceWindow(OnDeviceChangeMessage);
        _debounceTimer = new System.Windows.Forms.Timer { Interval = DebounceMilliseconds };
        _debounceTimer.Tick += OnDebounceTimerTick;
    }

    /// <summary>检测到新的 U 盘（在创建监听器的线程上触发）。</summary>
    public event EventHandler<UsbDeviceInfo>? DeviceArrived;

    /// <summary>检测到 U 盘已拔出（在创建监听器的线程上触发）。</summary>
    public event EventHandler<UsbDeviceInfo>? DeviceRemoved;

    /// <summary>开始监听（必须在有消息循环的线程上调用，例如 UI 线程）。</summary>
    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        // 创建隐藏的顶层窗口：message-only 窗口收不到 WM_DEVICECHANGE 广播
        _deviceWindow.CreateHandle(new CreateParams
        {
            Caption = "WinToolBox.UsbBackup.Watcher"
        });

        // 建立初始快照，避免把“启动前就已插入的 U 盘”误判为刚插入
        _snapshot = ScanDevices();

        _started = true;
        _logger?.Info($"U 盘插拔监听已启动，当前可移动磁盘 {_snapshot.Count} 个。");
    }

    /// <summary>停止监听并销毁隐藏窗口。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        try
        {
            _debounceTimer.Stop();
            _deviceWindow.DestroyHandle();
        }
        catch (Exception ex)
        {
            _logger?.Warn("停止 U 盘插拔监听时发生异常。", ex);
        }

        _logger?.Info("U 盘插拔监听已停止。");
    }

    /// <summary>收到 WM_DEVICECHANGE 广播（由隐藏窗口的消息循环调用）。</summary>
    private void OnDeviceChangeMessage(int messageCode)
    {
        if (_disposed || !_started)
        {
            return;
        }

        var kind = messageCode switch
        {
            DbtDeviceArrival => "设备到达",
            DbtDeviceRemoveComplete => "设备移除",
            DbtDevNodesChanged => "设备节点变化",
            _ => $"其他（0x{messageCode:X4}）"
        };

        _logger?.Info($"收到设备变化广播：{kind}，{DebounceMilliseconds} 毫秒后重新扫描。");
        ScheduleScan();
    }

    /// <summary>重启防抖定时器；重复的消息只会推迟一次性扫描。</summary>
    private void ScheduleScan()
    {
        try
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
        catch (Exception ex)
        {
            _logger?.Warn("启动设备扫描防抖定时器失败。", ex);
        }
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();

        if (_disposed || !_started)
        {
            return;
        }

        // 同一批消息只扫描一次
        if (Interlocked.Exchange(ref _pendingScan, 1) == 1)
        {
            return;
        }

        try
        {
            CompareAndRaise(ScanDevices());
        }
        catch (Exception ex)
        {
            _logger?.Error("扫描可移动磁盘时发生异常。", ex);
        }
        finally
        {
            Volatile.Write(ref _pendingScan, 0);
        }
    }

    /// <summary>扫描可移动磁盘并生成新快照。</summary>
    private Dictionary<string, UsbDeviceInfo> ScanDevices()
    {
        var map = new Dictionary<string, UsbDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in _detector.GetRemovableDrives())
        {
            // 设备缺失序列号时唯一标识可能重复，保留第一个即可
            map.TryAdd(device.UniqueId, device);
        }

        return map;
    }

    /// <summary>与上一次快照比较，触发插入 / 拔出事件。</summary>
    private void CompareAndRaise(Dictionary<string, UsbDeviceInfo> current)
    {
        var previous = _snapshot;
        _snapshot = current;

        var arrived = new List<UsbDeviceInfo>();
        var removed = new List<UsbDeviceInfo>();

        foreach (var pair in current)
        {
            if (!previous.ContainsKey(pair.Key))
            {
                arrived.Add(pair.Value);
            }
        }

        foreach (var pair in previous)
        {
            if (!current.ContainsKey(pair.Key))
            {
                removed.Add(pair.Value);
            }
        }

        foreach (var device in arrived)
        {
            _logger?.Info($"检测到 U 盘插入：{device}");
            Raise(DeviceArrived, device, nameof(DeviceArrived));
        }

        foreach (var device in removed)
        {
            _logger?.Info($"检测到 U 盘拔出：{device}");
            Raise(DeviceRemoved, device, nameof(DeviceRemoved));
        }
    }

    private void Raise(EventHandler<UsbDeviceInfo>? handler, UsbDeviceInfo device, string eventName)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, device);
        }
        catch (Exception ex)
        {
            _logger?.Error($"处理 {eventName} 事件时发生异常。", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();

        try
        {
            _debounceTimer.Tick -= OnDebounceTimerTick;
            _debounceTimer.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.Warn("释放设备扫描定时器失败。", ex);
        }

        try
        {
            // NativeWindow 不实现 IDisposable，创建的窗口句柄需要用 DestroyHandle 释放
            _deviceWindow.DestroyHandle();
        }
        catch (Exception ex)
        {
            _logger?.Warn("释放设备消息窗口失败。", ex);
        }
    }

    /// <summary>
    /// 用于接收设备广播的隐藏顶层窗口。
    /// 注意：不能使用 message-only 窗口（HWND_MESSAGE），它收不到 WM_DEVICECHANGE 广播。
    /// </summary>
    private sealed class DeviceWindow : NativeWindow
    {
        private readonly Action<int> _onDeviceChange;

        /// <summary>创建消息窗口（此时尚未创建句柄）。</summary>
        public DeviceWindow(Action<int> onDeviceChange)
        {
            _onDeviceChange = onDeviceChange ?? throw new ArgumentNullException(nameof(onDeviceChange));
        }

        /// <inheritdoc />
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDeviceChange)
            {
                try
                {
                    _onDeviceChange(m.WParam.ToInt32());
                }
                catch
                {
                    // 消息处理中绝不抛异常，否则会打断窗口过程
                }
            }

            base.WndProc(ref m);
        }
    }
}
