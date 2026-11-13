using System.Collections.Generic;
using MechrevoLite.Hardware;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 灯光能力/灯效表的只读来源（静态辅助方法用）。
/// <para>⭐ 界面层的静态辅助（灯效列表、OSD 判断）与服务层实例属性都读这里，
/// 保证两条读取路径同值；⛔ 界面层不直接引用固件灯效类型。</para>
/// </summary>
internal static class LightingFacts
{
    /// <summary>固件键盘面板可见灯效（按键盘类型裁剪）。</summary>
    internal static IReadOnlyList<(string Id, string Label)> VisibleFirmwareKeyboardEffects(int keyboardType)
    {
        return KeyboardFirmwareEffects.Visible(keyboardType);
    }

    /// <summary>单区固件键盘灯效常量（1 常亮 / 2 硬件彩虹 / 3 硬件呼吸 / 4 软件六色轮播）。</summary>
    internal static IReadOnlyList<(string Id, string Label)> SingleZoneFirmwareKeyboardEffects =>
        KeyboardFirmwareEffects.SingleZone;
}
