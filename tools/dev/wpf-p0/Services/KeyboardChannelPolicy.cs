using MechrevoLite.Hardware;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 键盘灯「走 HID 单键帧流」还是「走后端固件/EC 通道」的判据。
/// <para>⚠️ 原先这段判断写在窗口里（<c>OriginalStyleWindow.IsHidKeyboard</c>），属于
/// UI 直连硬件；收口到服务层后窗口只做一次转发，判据只有这一处。</para>
/// <para>⭐ 判据顺序：EC 实验模式以实际探测为准 → 宝龙达/无界 APU/GCU 分区键盘一律非 HID →
/// HID 不可用或亮度未生效时回落 GCU 固件面板 → 其余按 HID 单键处理。</para>
/// </summary>
internal static class KeyboardChannelPolicy
{
    internal static bool IsHidKeyboard(MechrevoHw hw)
    {
        if (GcuEcKeyboard.Active)
        {
            // EC 实验模式：以实际探测为准，不用注册表 KeyboardType / GCU 回退策略。
            return GcuEcKeyboard.IsHid;
        }
        if (hw == null
            || hw.UsesBaolongda
            || hw.WujieSmuApu
            || KeyboardFirmwareEffects.IsGcuZoneKeyboardPath(hw.Capabilities.KeyboardType))
        {
            return false;
        }
        // 与原版 ShouldUseGcuKeyboardFallback 一致：HID 不可用/亮度未生效时回 GCU 固件面板。
        KeyboardRgb rgb = WpfBackend.rgb;
        if (rgb != null
            && KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
                rgb.ControllerAvailability,
                rgb.IsConnected,
                WpfBackend.Service != null && hw.IsConnected,
                hidBrightnessTookEffect: true))
        {
            return false;
        }
        return true;
    }
}
