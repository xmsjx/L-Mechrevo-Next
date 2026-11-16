using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 充电门控「钉住」开关（EC 0x7C3 保持 4）。
/// <para>默认关闭：沿用"临时开门控 → 让固件把上限抄进存储区 → 还原"的做法，零残留。</para>
/// <para>打开后：写完不还原门控，门控保持到下次关闭该开关为止；用于固件没有在保持窗口内
/// 把上限抄进存储区、表现为"设置后重启就丢"的机型。</para>
/// <para>关闭开关后，下一次写入会把门控还原成当初记录的原始值。</para>
/// </summary>
internal static class ChargeGatePin
{
    internal const string ConfigKey = "charge_gate_pin";

    private const string PinnedMarkerKey = "charge_gate_pinned_by_lm";

    private const string PinnedOriginalKey = "charge_gate_pinned_original";

    private const string PinnedTargetKey = "charge_gate_pinned_target";

    /// <summary>开关是否打开（默认 0 = 关闭，行为与改前一致）。</summary>
    internal static bool Enabled
    {
        get
        {
            try
            {
                return MechrevoLite.AppConfig.Get(ConfigKey, 0) != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>门控是否处于"本程序钉住且尚未还原"的状态。</summary>
    internal static bool IsPinnedByUs
    {
        get
        {
            try
            {
                return MechrevoLite.AppConfig.Get(PinnedMarkerKey, 0) != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>钉住前记录下来的原始平台值（还原用）。</summary>
    internal static int PinnedOriginal
    {
        get
        {
            try
            {
                return MechrevoLite.AppConfig.Get(PinnedOriginalKey, 7);
            }
            catch
            {
                return 7;
            }
        }
    }

    /// <summary>钉住时的目标百分比（心跳核对用），未知为 -1。</summary>
    internal static int PinnedTarget
    {
        get
        {
            try
            {
                return MechrevoLite.AppConfig.Get(PinnedTargetKey, -1);
            }
            catch
            {
                return -1;
            }
        }
    }

    /// <summary>钉住门控时调用：记下原始值与目标百分比，供关闭开关后还原、供心跳核对。</summary>
    internal static void MarkPinned(int originalValue, int targetPercent)
    {
        try
        {
            MechrevoLite.AppConfig.Set(PinnedOriginalKey, originalValue >= 0 ? originalValue : 7);
            MechrevoLite.AppConfig.Set(PinnedTargetKey, targetPercent);
            MechrevoLite.AppConfig.Set(PinnedMarkerKey, 1);
            MechrevoLite.AppConfig.Flush();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[charge-gate] 记录钉住状态失败：" + ex.Message);
        }
    }

    /// <summary>门控已还原后调用：清掉钉住标记并停掉心跳。</summary>
    internal static void ClearPinned()
    {
        ChargeGateKeepAlive.Stop();
        try
        {
            MechrevoLite.AppConfig.Remove(PinnedMarkerKey);
            MechrevoLite.AppConfig.Remove(PinnedOriginalKey);
            MechrevoLite.AppConfig.Remove(PinnedTargetKey);
            MechrevoLite.AppConfig.Flush();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[charge-gate] 清除钉住状态失败：" + ex.Message);
        }
    }

    /// <summary>立刻把门控还原成当初记录的原值（关掉开关时调用），失败返回 false。</summary>
    internal static bool TryReleaseNow()
    {
        try
        {
            if (!IsPinnedByUs)
            {
                ChargeGateKeepAlive.Stop();
                return true;
            }
            int back = PinnedOriginal;
            if (!AcpiDriverEc.TryWriteByte(EcChargeLimit.GateRegister, (byte)back))
            {
                Logger.WriteLine("[charge-gate] 还原门控失败：写 0x" + EcChargeLimit.GateRegister.ToString("X3") + "=0x" + back.ToString("X2") + " 不成功");
                return false;
            }
            ClearPinned();
            Logger.WriteLine("[charge-gate] 开关已关闭，门控 0x" + EcChargeLimit.GateRegister.ToString("X3") + " 还原为 0x" + back.ToString("X2"));
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[charge-gate] 还原门控异常：" + ex.Message);
            return false;
        }
    }
}
