using MechrevoLite;
using MechrevoLite.Battery;
using MechrevoLite.Hardware;

using L_Mechrevo.WpfP0.Resources;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 滑块执行结果：<see cref="ReadbackPercent" /> &lt; 0 表示无需回滚滑块，
/// <see cref="Detail" /> 为空串表示不覆盖状态栏文案。
/// </summary>
internal readonly record struct ChargeSliderOutcome(bool Ok, string Detail, int ReadbackPercent);

/// <summary>
/// 充电上限**执行**适配器（EC 直写 / WMI 阈值 / NUC 0x07B9 / 无极限充回写满充）。
/// <para>窗口只负责校验输入、控件状态与状态栏；EC 与 WMI 的写入顺序留在本类，
/// 协议数值不放这里。</para>
/// </summary>
internal static class ChargeLimitApplyService
{
    /// <summary>启用阈值：EC 直写优先，其次 WMI 阈值。</summary>
    internal static ServiceResult ApplyThreshold(int lowPercent, int highPercent)
    {
        if (MechrevoLite.Hardware.EcChargeLimit.IsEcDirectWriteAllowed())
        {
            bool ecOk = MechrevoLite.Hardware.EcChargeLimit.TrySet(highPercent, out int applied);
            string ecDetail = "EC 限充 high=" + highPercent + "%" + (applied > 0 ? " 回读=" + applied + "%" : "");
            return ecOk ? ServiceResult.Success(ecDetail) : ServiceResult.Fail(ecDetail);
        }
        bool ok = BatteryLimitWriter.TryEnable(lowPercent, highPercent, allowInitialDischarge: true, out string detail);
        return ok ? ServiceResult.Success(detail) : ServiceResult.Fail(detail);
    }

    /// <summary>关闭阈值：EC 直写恢复 100%，其次 WMI 阈值。</summary>
    internal static ServiceResult DisableThreshold()
    {
        if (MechrevoLite.Hardware.EcChargeLimit.IsEcDirectWriteAllowed())
        {
            bool ecOk = MechrevoLite.Hardware.EcChargeLimit.TrySet(100, out int applied);
            string ecDetail = "EC 限充恢复 100%" + (applied > 0 ? " 回读=" + applied + "%" : "");
            return ecOk ? ServiceResult.Success(ecDetail) : ServiceResult.Fail(ecDetail);
        }
        bool ok = BatteryLimitWriter.TryDisable(out string detail);
        return ok ? ServiceResult.Success(detail) : ServiceResult.Fail(detail);
    }

    /// <summary>无极限充滑块落值：NUC 走 EC 0x07B9，其它走 WMI 手势。</summary>
    internal static ChargeSliderOutcome ApplySliderPercent(MechrevoHw? hw, int percent)
    {
        if (hw?.IsNucUmiFamily == true)
        {
            if (!MechrevoLite.Hardware.NucBatteryMode.TrySetPercent(percent, out string nucDetail))
            {
                Logger.WriteLine("[nuc-battery] 滑块 EC 0x07B9 失败：" + nucDetail);
                int readback = MechrevoLite.Hardware.NucBatteryMode.TryReadPercent(out int nucNow) ? nucNow : -1;
                return new ChargeSliderOutcome(false, WpfStrings.OriginalStyleWindow_xaml_Text_28, readback);
            }
            BatteryControl.SetSelectedModePercent(percent);
            Logger.WriteLine("[nuc-battery] 滑块 EC 0x07B9 = " + percent + "%");
            return new ChargeSliderOutcome(true, WpfStrings.OriginalStyleWindow_xaml_Text_29 + percent + "%", -1);
        }
        if (!BatteryControl.CaptureChargeLimitGesture() || !BatteryControl.ApplyChargeLimitFromUserGesture(percent))
        {
            return new ChargeSliderOutcome(false, WpfStrings.OriginalStyleWindow_xaml_Text_28, BatteryControl.ResolveDisplayLimitPercent());
        }
        BatteryControl.SetSelectedModePercent(percent);
        return new ChargeSliderOutcome(true, WpfStrings.OriginalStyleWindow_xaml_Text_29 + percent + "%", -1);
    }

    /// <summary>关闭无极限充 / 关闭 EC 直写后回写满充。</summary>
    internal static async Task<ServiceResult> WriteBackFullChargeAsync(MechrevoHw? hw)
    {
        try
        {
            if (hw?.IsNucUmiFamily == true)
            {
                bool nucOk = await Task.Run(() => MechrevoLite.Hardware.NucBatteryMode.TrySetPercent(100, out _));
                if (nucOk)
                {
                    MarkFullCharge();
                    return ServiceResult.Success(WpfStrings.OriginalStyleWindow_xaml_Text_30);
                }
                return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_31);
            }
            bool ecAllowed = !string.Equals(AppConfig.GetString("ec_charge_limit"), "0", StringComparison.Ordinal)
                && MechrevoLite.Hardware.EcChargeLimit.IsEcDirectWriteAllowed();
            if (ecAllowed)
            {
                int current = await Task.Run(() => MechrevoLite.Hardware.EcChargeLimit.ReadPercent());
                if (current == 100)
                {
                    MarkFullCharge();
                    return ServiceResult.Success("");
                }
                (bool ok, int readback) = await Task.Run(() =>
                {
                    bool written = MechrevoLite.Hardware.EcChargeLimit.TrySet(100, out _, out _);
                    return (written, MechrevoLite.Hardware.EcChargeLimit.ReadPercent());
                });
                if (readback == 100)
                {
                    MarkFullCharge();
                    return ServiceResult.Success(WpfStrings.OriginalStyleWindow_xaml_Text_10);
                }
                return ServiceResult.Fail(
                    WpfStrings.OriginalStyleWindow_xaml_Text_11 + ok + WpfStrings.StatusReadbackSuffix + readback + "%" + WpfStrings.ClosingParen);
            }
            if (hw is { IsConnected: true })
            {
                if (hw.BatteryProtection == 0)
                {
                    MarkFullCharge();
                    return ServiceResult.Success("");
                }
                bool confirmed = await hw.SetBatteryProtection(0);
                if (confirmed && hw.BatteryProtection == 0)
                {
                    MarkFullCharge();
                    return ServiceResult.Success(WpfStrings.OriginalStyleWindow_xaml_Text_12);
                }
                return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_13);
            }
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_14);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_15 + ex.Message);
        }
    }

    /// <summary>关闭 EC 直写前真实清掉硬件限充（只改配置不算清除）。</summary>
    internal static async Task<ServiceResult> ClearLimitForEcDirectOffAsync()
    {
        (bool cleared, string detail) = await Task.Run(() =>
        {
            bool ok = MechrevoLite.Hardware.EcChargeLimitOverride.TrySetNoSmartHint(100, out string clearDetail);
            return (ok, clearDetail);
        });
        Logger.WriteError("[battery-ec-off] 清除限充=" + cleared + " " + detail);
        return cleared ? ServiceResult.Success(detail) : ServiceResult.Fail(detail);
    }

    private static void MarkFullCharge()
    {
        AppConfig.Set("charge_limit", 100);
        AppConfig.Flush();
        BatteryControl.chargeFull = true;
    }
}
