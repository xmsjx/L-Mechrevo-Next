using System;
using System.Management;
using System.Threading;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// NUC / UMI 独立性能键：监听 ACPI WMI 事件，ULong=176。
/// 只读事件，不依赖厂商 WCF；模式切换仍由窗口调用现有 GCU 路径。
/// </summary>
internal sealed class PerformanceKeyService : IDisposable
{
    internal const int PerformanceKeyCode = 176;
    /// <summary>部分机型把同一按键报到 192(SC_SwitchOC) / 201(SC_CustomMode)，一并当性能键处理。</summary>
    private static readonly int[] PerformanceKeyAliases = new int[2] { 192, 201 };
    private ManagementEventWatcher? _watcher;
    private long _lastPressedTick;
    private bool _started;
    private int _eventCount;
    private System.Threading.Timer? _heartbeat;

    internal event Action? Pressed;

    /// <summary>所有 ACPI ULong 事件（176 性能键、177/178/185 键盘亮度、59-63 直接亮度档）。</summary>
    internal event Action<int>? AcpiCode;

    internal void Start()
    {
        if (_started)
        {
            return;
        }
        try
        {
            WqlEventQuery query = new("SELECT * FROM AcpiTest_EventULong");
            _watcher = new ManagementEventWatcher(new ManagementScope("root\\WMI"), query);
            _watcher.EventArrived += OnEventArrived;
            _watcher.Stopped += (_, e) => Logger.WriteError("[perfkey] 监听停止：status=" + e.Status);
            _watcher.Start();
            _started = true;
            Logger.WriteError("[perfkey] 已订阅 ACPI 性能键事件 ULong=" + PerformanceKeyCode);
            _heartbeat = new System.Threading.Timer(_ =>
            {
                Logger.WriteErrorIfChanged("perfkey-alive-" + _eventCount, "[perfkey] 监听中 · 本次运行累计收到 ACPI 事件 " + _eventCount + " 条（0 条 = 事件没送到本进程）");
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            Logger.WriteError("[perfkey] 订阅失败：" + ex.Message);
            Dispose();
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            object? value = e.NewEvent?.Properties["ULong"]?.Value;
            if (value == null)
            {
                return;
            }
            int code = Convert.ToInt32(value);
            System.Threading.Interlocked.Increment(ref _eventCount);
            try
            {
                AcpiCode?.Invoke(code);
            }
            catch
            {
            }
            Logger.WriteErrorIfChanged("acpi-code-" + code, "[acpi] 收到 ACPI ULong=" + code);
            if (code != PerformanceKeyCode && Array.IndexOf(PerformanceKeyAliases, code) < 0)
            {
                return;
            }
            long now = Environment.TickCount64;
            long previous = Interlocked.Exchange(ref _lastPressedTick, now);
            if (previous != 0 && now - previous < 350)
            {
                return;
            }
            Logger.WriteError("[perfkey] ACPI ULong=" + code + " -> 切换性能模式");
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[perfkey] 事件处理失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        _started = false;
        if (_watcher != null)
        {
            try
            {
                _watcher.EventArrived -= OnEventArrived;
                _watcher.Stop();
                _watcher.Dispose();
            }
            catch
            {
            }
            _watcher = null;
        }
    }
}
