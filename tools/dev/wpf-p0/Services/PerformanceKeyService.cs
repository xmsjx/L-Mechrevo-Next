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
    private int _started;
    private int _eventCount;
    private System.Threading.Timer? _heartbeat;
    private Thread? _listenThread;

    internal event Action? Pressed;

    /// <summary>所有 ACPI ULong 事件（176 性能键、177/178/185 键盘亮度、59-63 直接亮度档）。</summary>
    internal event Action<int>? AcpiCode;

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        _heartbeat = new System.Threading.Timer(_ =>
        {
            Logger.WriteErrorIfChanged("perfkey-alive-" + _eventCount, "[perfkey] 监听中 · 本次运行累计收到 ACPI 事件 " + _eventCount + " 条（0 条 = 事件没送到本进程）");
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        // WMI 事件流放在独立 MTA 线程：界面线程是 STA，事件回调在 STA 上容易收不到。
        _listenThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "acpi-event-watcher"
        };
        _listenThread.SetApartmentState(ApartmentState.MTA);
        _listenThread.Start();
    }

    private void ListenLoop()
    {
        using (WmiAcpiComListener com = new WmiAcpiComListener())
        {
            if (com.TryStart(out string comDetail) && ListenCom(com))
            {
                Logger.WriteError("[perfkey] 订阅方式=" + comDetail + " · COM 通道结束");
                return;
            }
            Logger.WriteError("[perfkey] COM 通道不可用（" + comDetail + "）⇒ 退回 System.Management");
        }
        ListenManagement();
    }
    /// <summary>COM 通道循环：半同步取事件，超时/异常都继续等。返回 true 表示通道正常收过事件。</summary>
    private bool ListenCom(WmiAcpiComListener com)
    {
        Logger.WriteError("[perfkey] 已订阅 ACPI 性能键事件 ULong=" + PerformanceKeyCode + "（COM 半同步 · MTA）");
        long lastEventTick = Environment.TickCount64;
        long lastReportTick = Environment.TickCount64;
        while (Volatile.Read(ref _started) != 0)
        {
            if (com.TryNext(2000, out int code))
            {
                lastEventTick = Environment.TickCount64;
                HandleCode(code);
                continue;
            }
            long now = Environment.TickCount64;
            if (now - lastEventTick >= 30000 && now - lastReportTick >= 60000)
            {
                lastReportTick = now;
                Logger.WriteError("[perfkey] COM 30 秒内无事件 · 最近异常=" + (com.LastError.Length == 0 ? "无" : com.LastError));
            }
        }
        return true;
    }

    private void ListenManagement()
    {
        while (Volatile.Read(ref _started) != 0)
        {
            ManagementEventWatcher? watcher = null;
            try
            {
                // 拉取模式：与执行通知查询后逐条取事件的语义一致，
                // 不依赖 WMI 反向回调（某些平台上回调收不到，拉取能收到）。
                ManagementScope scope = new ManagementScope(@"root\WMI");
                scope.Options.Impersonation = ImpersonationLevel.Impersonate;
                scope.Options.EnablePrivileges = true;
                scope.Connect();
                watcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM AcpiTest_EventULong"));
                watcher.Options.Timeout = TimeSpan.FromSeconds(3);
                _watcher = watcher;
                Logger.WriteError("[perfkey] 已订阅 ACPI 性能键事件 ULong=" + PerformanceKeyCode + "（MTA · 拉取模式）");
                while (Volatile.Read(ref _started) != 0)
                {
                    ManagementBaseObject? element = null;
                    try
                    {
                        element = watcher.WaitForNextEvent();
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    catch (ManagementException ex)
                    {
                        if (Volatile.Read(ref _started) == 0)
                        {
                            Logger.WriteError("[perfkey] 订阅已停止：" + ex.Message);
                            break;
                        }
                        continue;   // 取事件超时属正常，继续等
                    }
                    if (element == null)
                    {
                        continue;
                    }
                    try
                    {
                        HandleEventObject(element);
                    }
                    finally
                    {
                        element.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteError("[perfkey] 订阅失败：" + ex.Message);
                Thread.Sleep(2000);
            }
            finally
            {
                try
                {
                    watcher?.Stop();
                    watcher?.Dispose();
                }
                catch
                {
                }
                _watcher = null;
            }
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        HandleEventObject(e.NewEvent);
    }

    private void HandleEventObject(ManagementBaseObject? newEvent)
    {
        try
        {
            object? value = newEvent?.Properties["ULong"]?.Value;
            if (value == null)
            {
                return;
            }
            HandleCode(Convert.ToInt32(value));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[perfkey] 事件处理失败：" + ex.Message);
        }
    }

    private void HandleCode(int code)
    {
        try
        {
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
            Logger.WriteLine("[perfkey] 切换失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        System.Threading.Volatile.Write(ref _started, 0);
        _listenThread = null;
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
