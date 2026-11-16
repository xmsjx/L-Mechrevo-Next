using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Threading;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 界面线程相位计时：包裹指定代码块，超过阈值时记录耗时；
/// 另起后台心跳向界面线程投递空操作，回包延迟超过阈值时记录当时正在执行的相位。
/// 仅在 log_level=all 时生效，关闭时不产生任何计时开销。
/// <para>⚠️ 测量本身不能变成负担：界面线程一旦排不过来，心跳空操作会堆积、日志行会暴增，
/// 反过来把界面压得更死。所以这里投递有心跳上限、日志按时间限流、并定期给一条汇总。</para>
/// </summary>
internal static class UiTrace
{
    private const int PhaseThresholdMs = 40;
    private const int StallThresholdMs = 150;
    private const int HeartbeatIntervalMs = 100;
    /// <summary>同时排队的心跳空操作上限；超过就不再投递（说明界面线程已经在忙了）。</summary>
    private const int MaxPendingBeats = 3;
    /// <summary>同一条相位日志的最小间隔（避免同一相位每 100ms 刷一行）。</summary>
    private const int PhaseLogMinIntervalMs = 500;
    /// <summary>卡顿明细日志的最小间隔；其余次数只计数。</summary>
    private const int StallLogMinIntervalMs = 1000;
    /// <summary>汇总日志间隔。</summary>
    private const int SummaryIntervalMs = 60000;

    private static readonly bool Enabled = string.Equals(
        MechrevoLite.AppConfig.GetString("log_level"),
        "all",
        StringComparison.OrdinalIgnoreCase);
    private static readonly IDisposable Empty = new SilentScope();
    private static readonly ConcurrentDictionary<string, long> LastPhaseLogAt = new(StringComparer.Ordinal);
    private static string _inflight = string.Empty;
    private static Dispatcher? _dispatcher;
    private static Timer? _heartbeat;
    private static int _pendingBeats;
    private static int _stallCount;
    private static int _stallMaxMs;
    private static long _lastStallLogAt;
    private static long _windowStartedAt = Environment.TickCount64;

    internal static void Start(Dispatcher dispatcher)
    {
        if (!Enabled || _heartbeat != null)
        {
            return;
        }
        _dispatcher = dispatcher;
        _windowStartedAt = Environment.TickCount64;
        _heartbeat = new Timer(_ => Beat(), null, 1000, HeartbeatIntervalMs);
        Logger.WriteLine("[ui-trace] 相位计时已启动（明细限流：相位 500ms/条 · 卡顿 1s/条 · 每 60s 汇总）");
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
        // 界面线程已经积压了就不再投递：这时候测量只会加重卡顿，没有任何新信息。
        if (Interlocked.Increment(ref _pendingBeats) > MaxPendingBeats)
        {
            Interlocked.Decrement(ref _pendingBeats);
            return;
        }
        long posted = Environment.TickCount64;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Decrement(ref _pendingBeats);
                long delay = Environment.TickCount64 - posted;
                if (delay >= StallThresholdMs)
                {
                    NoteStall(delay);
                }
            }));
        }
        catch
        {
            Interlocked.Decrement(ref _pendingBeats);
        }
    }

    private static void NoteStall(long delayMs)
    {
        Interlocked.Increment(ref _stallCount);
        int max = Volatile.Read(ref _stallMaxMs);
        while (delayMs > max && Interlocked.CompareExchange(ref _stallMaxMs, (int)Math.Min(delayMs, int.MaxValue), max) != max)
        {
            max = Volatile.Read(ref _stallMaxMs);
        }
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastStallLogAt) >= StallLogMinIntervalMs)
        {
            Volatile.Write(ref _lastStallLogAt, now);
            Logger.WriteLine("[ui-stall] delay=" + delayMs + "ms inflight=" + Volatile.Read(ref _inflight));
        }
        if (now - Volatile.Read(ref _windowStartedAt) >= SummaryIntervalMs)
        {
            int count = Interlocked.Exchange(ref _stallCount, 0);
            int maxSeen = Interlocked.Exchange(ref _stallMaxMs, 0);
            Volatile.Write(ref _windowStartedAt, now);
            Logger.WriteLine("[ui-stall] 近 " + (SummaryIntervalMs / 1000) + " 秒卡顿 " + count + " 次 · 最长 "
                + maxSeen + "ms（明细已限流）· 若长期偏高优先把 log_level 调回默认");
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _tag;
        private readonly string _parent;
        private readonly long _started;
        private readonly bool _trackInflight;

        internal Scope(string tag)
        {
            _tag = tag;
            _started = Environment.TickCount64;
            // 相位一般都在界面线程上；其它线程进入时不改写 inflight，否则心跳会读到半截字符串。
            _trackInflight = _dispatcher == null || _dispatcher.CheckAccess();
            _parent = _trackInflight ? Volatile.Read(ref _inflight) : string.Empty;
            if (_trackInflight)
            {
                Volatile.Write(ref _inflight, tag);
            }
        }

        public void Dispose()
        {
            long elapsed = Environment.TickCount64 - _started;
            if (_trackInflight)
            {
                Volatile.Write(ref _inflight, _parent);
            }
            if (elapsed < PhaseThresholdMs)
            {
                return;
            }
            long now = Environment.TickCount64;
            long last = LastPhaseLogAt.TryGetValue(_tag, out long at) ? at : 0;
            if (now - last < PhaseLogMinIntervalMs)
            {
                return;
            }
            LastPhaseLogAt[_tag] = now;
            Logger.WriteLine("[ui-phase] " + _tag + " " + elapsed + "ms");
        }
    }

    private sealed class SilentScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
