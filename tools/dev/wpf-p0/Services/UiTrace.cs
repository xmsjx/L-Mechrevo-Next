using System;
using System.Threading;
using System.Windows.Threading;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 界面线程相位计时：包裹指定代码块，超过阈值时记录耗时；
/// 另起后台心跳向界面线程投递空操作，回包延迟超过阈值时记录当时正在执行的相位。
/// 仅在 log_level=all 时生效，关闭时不产生任何计时开销。
/// </summary>
internal static class UiTrace
{
    private const int PhaseThresholdMs = 40;
    private const int StallThresholdMs = 150;
    private const int HeartbeatIntervalMs = 100;

    private static readonly bool Enabled = string.Equals(
        MechrevoLite.AppConfig.GetString("log_level"),
        "all",
        StringComparison.OrdinalIgnoreCase);
    private static readonly IDisposable Empty = new SilentScope();
    private static string _inflight = string.Empty;
    private static Dispatcher? _dispatcher;
    private static Timer? _heartbeat;

    internal static void Start(Dispatcher dispatcher)
    {
        if (!Enabled || _heartbeat != null)
        {
            return;
        }
        _dispatcher = dispatcher;
        _heartbeat = new Timer(_ => Beat(), null, 1000, HeartbeatIntervalMs);
        Logger.WriteLine("[ui-trace] 相位计时已启动");
    }

    /// <summary>包裹一段界面线程代码，超过阈值时记录其耗时。</summary>
    internal static IDisposable Phase(string tag)
    {
        return Enabled ? new Scope(tag) : Empty;
    }

    /// <summary>包裹一段带返回值的界面线程代码，超过阈值时记录其耗时。</summary>
    internal static T Measure<T>(string tag, Func<T> work)
    {
        if (!Enabled)
        {
            return work();
        }
        using (Phase(tag))
        {
            return work();
        }
    }

    private static void Beat()
    {
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return;
        }
        long posted = Environment.TickCount64;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                long delay = Environment.TickCount64 - posted;
                if (delay >= StallThresholdMs)
                {
                    Logger.WriteLine(
                        "[ui-stall] delay=" + delay + "ms inflight=" + Volatile.Read(ref _inflight));
                }
            }));
        }
        catch
        {
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _tag;
        private readonly string _parent;
        private readonly long _started;

        internal Scope(string tag)
        {
            _tag = tag;
            _parent = Volatile.Read(ref _inflight);
            Volatile.Write(ref _inflight, tag);
            _started = Environment.TickCount64;
        }

        public void Dispose()
        {
            long elapsed = Environment.TickCount64 - _started;
            Volatile.Write(ref _inflight, _parent);
            if (elapsed >= PhaseThresholdMs)
            {
                Logger.WriteLine("[ui-phase] " + _tag + " " + elapsed + "ms");
            }
        }
    }

    private sealed class SilentScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
