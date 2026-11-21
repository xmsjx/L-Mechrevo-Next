using System;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Contracts;
using MechrevoLite.Hardware;
using MechrevoLite.Hardware.Architecture;
using L_Mechrevo.WpfP0.Resources;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 开机灯光重放：把本机**磁盘上记得的**「效果 + 开关 + 亮度」重新下发一次。
/// <para>⭐ 固件重启会把自己的默认灯效盖回来（与用户上次的选择无关）⇒ 只能由本机记住并重放。</para>
/// <para>⛔ 用户从未在本机保存过灯光（磁盘无标记/无 .cfg）⇒ 什么都不下发，硬件状态可能来自官方控制台。</para>
/// <para>⭐ 跳过规则（机主 2026-11-19 要求）：能力位**自动判定不支持**的通道、以及用户在设置里**强制隐藏**的通道，
/// 既不记忆也不重放 —— 否则会给不存在的灯带/Logo 发帧，或者把用户故意藏起来的通道又点亮。</para>
/// </summary>
internal static class LightingStartupRestore
{
    private const string KeyboardTopic = "Keyboard/Ctrl";
    private const string LightbarTopic = "HidLightbar/Ctrl";
    private const string LogoTopic = "HidLightbar_Logo/Ctrl";
    private const string HingeTopic = "HidLightbar_Hinge/Ctrl";
    private const string SyncTopic = "HidLightbar_Sync/Ctrl";

    /// <summary>设置页里「灯带 / Logo」行的显示方式键（auto/show/hide）。</summary>
    internal const string StripUiKey = "light_strip_ui";
    internal const string LogoUiKey = "light_logo_ui";

    internal static async Task RunOnceAsync(MechrevoHw? hw)
    {
        try
        {
            if (hw == null)
            {
                Logger.WriteLine("[light-restore] 后端未就绪 ⇒ 跳过灯光重放");
                return;
            }
            if (!LightingSettingsStore.HasPersistedLightingIntent())
            {
                Logger.WriteLine("[light-restore] 磁盘上没有本机灯光设置 ⇒ 不覆盖固件状态");
                return;
            }
            if (WpfBackend.IsLightingTemporarilySuspended)
            {
                Logger.WriteLine("[light-restore] 灯光临时挂起中 ⇒ 本次不重放");
                return;
            }
            if (IsLightOffOnBatteryNow())
            {
                // 机主反馈：离电自动关灯之后再重启，灯应该保持熄灭（插电/唤醒由电源事件恢复）。
                Logger.WriteError("[light-restore] 离电自动关灯生效中 ⇒ 本次不点亮（保持熄灭）");
                return;
            }
            bool hidKeyboard = KeyboardChannelPolicy.IsHidKeyboard(hw);
            // 单键 RGB 是帧流，等固件/GCU 稳定后再发；EC 单区只需等灯控初始化。
            await Task.Delay(hidKeyboard ? 5000 : 3000).ConfigureAwait(false);
            await RestoreKeyboardAsync(hw).ConfigureAwait(false);
            await RestoreChannelAsync(hw, LightbarTopic, FeatureId.Lightbar, StripUiKey, WpfStrings.LightChannelLightbar).ConfigureAwait(false);
            await RestoreChannelAsync(hw, LogoTopic, FeatureId.LogoLight, LogoUiKey, WpfStrings.LightChannelLogo).ConfigureAwait(false);
            await RestoreChannelAsync(hw, HingeTopic, FeatureId.HingeLight, null, WpfStrings.LightChannelHinge).ConfigureAwait(false);
            await RestoreChannelAsync(hw, SyncTopic, FeatureId.SyncLight, null, WpfStrings.LightChannelSync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.WriteError("[light-restore] 灯光重放异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>离电自动关灯是否正在生效（设置开 + 当前吃电池）。</summary>
    private static bool IsLightOffOnBatteryNow()
    {
        if (!MechrevoLite.AppConfig.Is("lighting_off_on_battery"))
        {
            return false;
        }
        try
        {
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                != System.Windows.Forms.PowerLineStatus.Online;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RestoreKeyboardAsync(MechrevoHw hw)
    {
        if (!LightingSettingsStore.TryLoad(KeyboardTopic, out LightChannelSettings saved))
        {
            Logger.WriteLine("[light-restore] 键盘：本机没有保存过键盘灯设置 ⇒ 不覆盖固件状态");
            return;
        }
        if (!saved.PowerExplicit)
        {
            Logger.WriteLine("[light-restore] 键盘：没有保存过开关状态 ⇒ 不覆盖固件状态");
            return;
        }
        if (KeyboardChannelPolicy.IsHidKeyboard(hw))
        {
            bool hidOk = WpfBackend.ReapplySavedKeyboardRgb(saved.PowerOn, saved.Effect, saved.Light, saved.BrightnessPct);
            Logger.WriteError("[light-restore] 单键 RGB 按保存设置重放" + (hidOk ? "成功" : "失败（不重试）")
                + "（效果=" + saved.Effect + " 亮度=" + saved.RestoreBrightnessPercent + "%"
                + (saved.BrightnessPct >= 0 ? "·精确值" : "·档位折算")
                + " 电源=" + (saved.PowerOn ? "开" : "关") + "）");
            return;
        }
        // ⭐ 固件重启后「读回来的开关态」不可信（常常恒 false）⇒ 按保存态**无条件重发一次**；
        //   不再像旧版那样 hw.KeyboardPower == saved.PowerOn 就跳过（机主反馈的「点了没反应/效果丢失」就出在这）。
        bool ok;
        if (GcuEcKeyboard.Active)
        {
            ok = GcuEcKeyboard.ApplyFromSettings(saved.Effect, saved.PowerOn, WpfBackend.MapPercentToLevel(saved.RestoreBrightnessPercent));
        }
        else if (EcKeyboardLight.IsChannelAvailable())
        {
            ok = EcKeyboardLight.TrySetPower(saved.PowerOn);
        }
        else
        {
            MechrevoService? service = WpfBackend.Service;
            ok = service != null && await service.SetKeyboardPower(saved.PowerOn).ConfigureAwait(false);
            if (ok && saved.PowerOn && service != null)
            {
                try
                {
                    await service.SetKeyboardEffect(saved.Effect, saved.Light, saved.Speed, "None", (System.Drawing.Color?)null, true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("[light-restore] 键盘效果下发异常（已吞掉）：" + ex.GetType().Name);
                }
            }
        }
        Logger.WriteError("[light-restore] 键盘灯按保存设置重放" + (ok ? "成功" : "失败（不重试）")
            + "（效果=" + saved.Effect + " 亮度=" + saved.Light + " 电源=" + (saved.PowerOn ? "开" : "关") + "）");
    }

    /// <summary>灯带/Logo/铰链/同步：先过滤（不支持 / 用户隐藏 / 无保存 / 未显式），再按通道下发。</summary>
    private static async Task RestoreChannelAsync(MechrevoHw hw, string topic, FeatureId feature, string? uiModeKey, string label)
    {
        BackendChannel channel = FeatureGate.Get(feature, hw).Channel;
        if (channel == BackendChannel.None)
        {
            Logger.WriteLine("[light-restore] " + label + "：能力位自动判定不支持（" + feature + " ⇒ channel=None）⇒ 跳过记忆与重放");
            return;
        }
        if (uiModeKey != null)
        {
            string mode = MechrevoLite.AppConfig.GetString(uiModeKey) ?? "auto";
            if (string.Equals(mode, "hide", StringComparison.OrdinalIgnoreCase))
            {
                Logger.WriteLine("[light-restore] " + label + "：用户在设置里强制隐藏（" + uiModeKey + "=hide）⇒ 跳过记忆与重放");
                return;
            }
        }
        if (!LightingSettingsStore.TryLoad(topic, out LightChannelSettings saved))
        {
            Logger.WriteLine("[light-restore] " + label + "：本机没有保存过该通道 ⇒ 不动固件状态");
            return;
        }
        if (!saved.PowerExplicit)
        {
            Logger.WriteLine("[light-restore] " + label + "：没有保存过开关状态 ⇒ 不动固件状态");
            return;
        }
        bool ok = await ApplyChannelAsync(hw, topic, channel, saved, label).ConfigureAwait(false);
        Logger.WriteError("[light-restore] " + label + " 按保存设置重放" + (ok ? "成功" : "失败（不重试）")
            + "（效果=" + saved.Effect + " 亮度=" + saved.Light + " 电源=" + (saved.PowerOn ? "开" : "关") + "）");
    }

    private static async Task<bool> ApplyChannelAsync(MechrevoHw hw, string topic, BackendChannel channel, LightChannelSettings saved, string label)
    {
        try
        {
            if (hw.IsNucUmiFamily && string.Equals(topic, LightbarTopic, StringComparison.Ordinal))
            {
                // UMI 灯带：本机 EC/固件写 + 回读，先开关再效果（与 BackendProbe 同一顺序）。
                bool powerOk = UmiLightbar.SetPower(saved.PowerOn);
                if (powerOk && saved.PowerOn)
                {
                    UmiLightbar.SetEffect(saved.Effect, System.Drawing.Color.FromArgb(saved.ColorArgb), saved.Light);
                }
                return powerOk;
            }
            if (channel == BackendChannel.LocalHid)
            {
                int colorArgb = saved.ColorArgb;
                if (string.Equals(topic, LightbarTopic, StringComparison.Ordinal))
                {
                    return IteLightingLocal.TryApplyLightbar(saved.Effect, saved.PowerOn, saved.Light, saved.Speed, colorArgb, out _);
                }
                if (string.Equals(topic, LogoTopic, StringComparison.Ordinal))
                {
                    return IteLightingLocal.TryApplyLogo(saved.Effect, saved.PowerOn, saved.Light, saved.Speed, colorArgb, out _);
                }
                if (string.Equals(topic, HingeTopic, StringComparison.Ordinal))
                {
                    return IteLightingLocal.TryApplyHinge(saved.Effect, saved.PowerOn, saved.Light, saved.Speed, colorArgb, out _);
                }
                Logger.WriteLine("[light-restore] " + label + "：本地通道没有同步灯下发入口 ⇒ 跳过");
                return false;
            }
            MechrevoService? service = WpfBackend.Service;
            if (service == null)
            {
                return false;
            }
            bool ok = await service.SetLightPower(topic, saved.PowerOn).ConfigureAwait(false);
            if (!ok || !saved.PowerOn)
            {
                return ok;
            }
            System.Drawing.Color? color = System.Drawing.Color.FromArgb(saved.ColorArgb);
            return await service.SetLightEffect(topic, saved.Effect, saved.Light, saved.Speed, "None", color, true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[light-restore] " + label + " 下发异常：" + ex.GetType().Name + " " + ex.Message);
            return false;
        }
    }
}