using System;
using System.Threading;

namespace MechrevoLite.Hardware;

/// <summary>
/// 钉住门控期间的心跳守护：每 4 秒核对一次门控与上限，偏离就重申一次。
/// <para>只在「开关打开 + 门控由本程序钉住」时运行；不勾开关时完全不起线程。</para>
/// <para>只重申门控 0x7C3 与上限 0x7B9，不碰复充下限 0x7D0、不碰 0x770。</para>
/// </summary>
internal static class ChargeGateKeepAlive
{
    private const int IntervalMs = 4000;

    private static readonly object Gate = new object();

    private static Timer? _timer;

    private static int _target;

    private static int _consecutiveReadFailures;

    /// <summary>钉住成功或上限改变后调用：记录目标值并确保心跳在跑。</summary>
    internal static void Start(int targetPercent)
    {
        if (!IsEligible())
        {
            Stop();
            return;
        }
        lock (Gate)
        {
            _target = targetPercent;
            _consecutiveReadFailures = 0;
            if (_timer == null)
            {
                _timer = new Timer(Tick, null, IntervalMs, IntervalMs);
                Logger.WriteLine($"[charge-gate] 心跳守护启动：每 {IntervalMs}ms 核对 0x{EcChargeLimit.GateRegister:X3} 与 0x{EcChargeLimit.UpperRegister:X3}（目标 {targetPercent}%）");
            }
        }
    }

    /// <summary>停止心跳（开关关闭、解除钉住、恢复到 100% 时调用）。</summary>
    internal static void Stop()
    {
        lock (Gate)
        {
            if (_timer == null)
            {
                return;
            }
            _timer.Dispose();
            _timer = null;
            _consecutiveReadFailures = 0;
            Logger.WriteLine("[charge-gate] 心跳守护已停止");
        }
    }

    /// <summary>程序启动时调用：若上次是钉住状态且开关仍打开，接着守。</summary>
    internal static void ResumeIfPinned()
    {
        if (!IsEligible())
        {
            return;
        }
        int target = ChargeGatePin.PinnedTarget;
        if (target >= EcChargeLimit.MinimumPercent && target < 100)
        {
            Start(target);
        }
    }

    private static bool IsEligible()
    {
        return ChargeGatePin.Enabled && ChargeGatePin.IsPinnedByUs;
    }

    private static void Tick(object? state)
    {
        try
        {
            lock (Gate)
            {
                if (_timer == null)
                {
                    return;
                }
            }
            if (!IsEligible())
            {
                Stop();
                return;
            }
            int target;
            lock (Gate)
            {
                target = _target;
            }
            if (target < EcChargeLimit.MinimumPercent || target >= 100)
            {
                return;
            }
            if (!AcpiDriverEc.TryReadByte(EcChargeLimit.GateRegister, out byte gateValue))
            {
                NoteReadFailure();
                return;
            }
            if (gateValue != EcChargeLimit.GateOpenValue)
            {
                if (AcpiDriverEc.TryWriteByte(EcChargeLimit.GateRegister, (byte)EcChargeLimit.GateOpenValue))
                {
                    Logger.WriteLine($"[charge-gate] 心跳自愈：门控 0x{EcChargeLimit.GateRegister:X3} 被改回 0x{gateValue:X2} ⇒ 重新钉住 {EcChargeLimit.GateOpenValue}");
                }
                else
                {
                    Logger.WriteErrorIfChanged("charge-gate-readd-fail", $"[charge-gate] 心跳自愈失败：门控 0x{EcChargeLimit.GateRegister:X3} 写不回 {EcChargeLimit.GateOpenValue}");
                }
            }
            if (!AcpiDriverEc.TryReadByte(EcChargeLimit.UpperRegister, out byte upperValue))
            {
                NoteReadFailure();
                return;
            }
            int present = upperValue & EcChargeLimit.PercentMask;
            if (present == target || (upperValue & 0x80) != 0)
            {
                ResetReadFailures();
                return;
            }
            if (AcpiDriverEc.TryWriteByte(EcChargeLimit.UpperRegister, (byte)target))
            {
                Logger.WriteLine($"[charge-gate] 心跳自愈：上限 0x{EcChargeLimit.UpperRegister:X3} 现 {present}% ⇒ 重申 {target}%");
            }
            else
            {
                Logger.WriteErrorIfChanged("charge-gate-reassert-fail", $"[charge-gate] 心跳自愈失败：上限 0x{EcChargeLimit.UpperRegister:X3} 写不回 {target}%");
            }
            ResetReadFailures();
        }
        catch (Exception ex)
        {
            Logger.WriteErrorIfChanged("charge-gate-tick", "[charge-gate] 心跳异常：" + ex.Message);
        }
    }

    private static void NoteReadFailure()
    {
        int failures;
        lock (Gate)
        {
            failures = ++_consecutiveReadFailures;
        }
        if (failures == 3)
        {
            Logger.WriteErrorIfChanged("charge-gate-read", "[charge-gate] 心跳连续 3 次读不到 EC（通道不可用），暂停自愈直到下次写入");
        }
    }

    private static void ResetReadFailures()
    {
        lock (Gate)
        {
            _consecutiveReadFailures = 0;
        }
    }
}
