using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// OpenRevo HID 效果编号（100+）到 LM 内置软件效果的映射。
/// 只做 UI 编号翻译，不改变 KeyboardRgb 的帧协议。
/// </summary>
internal static class HidStreamerEffects
{
    /// <summary>100+ 的 streamer 效果列表，追加在原有 HID 效果之后。</summary>
    internal static readonly (int Mode, string Name)[] Extra =
    {
        (100, "呼吸 Streamer"),
        (101, "波浪 Streamer"),
        (102, "涟漪 Streamer"),
        (103, "流沙 Streamer"),
        (104, "电流 Streamer"),
        (105, "色轮 Streamer"),
        (106, "彩虹 Streamer"),
        (107, "矩阵 Streamer"),
        (108, "闪电 Streamer"),
        (109, "火焰 Streamer"),
        (110, "雨滴 Streamer"),
        (111, "星光 Streamer"),
        (112, "双向电流 Streamer"),
        (113, "单色常亮 Streamer")
    };

    /// <summary>UI 选项编号转本地模式：基础列表已经是 0..9，只有 100+ Streamer 需要翻译。</summary>
    internal static int ToLocalModeFromSelection(int effect)
        => effect >= 100 ? ToLocalMode(effect) : effect;

    /// <summary>把 OpenRevo/硬件效果编号转成 KeyboardRgb 的 0..9 本地模式。</summary>
    internal static int ToLocalMode(int effect)
    {
        return effect switch
        {
            // OpenRevo BetterRGB streamer 100+
            100 => 0,   // Breath
            101 => 1,   // Wave
            102 => 3,   // Ripple → 按键扩散
            103 => 1,   // Quicksand → 波浪
            104 => 3,   // Current → 扩散
            105 => 4,   // Wheel
            106 => 4,   // Rainbow → Wheel
            107 => 8,   // Matrix
            108 => 5,   // Lightning
            109 => 6,   // Flame
            110 => 7,   // Rain
            111 => 2,   // Sparkle
            112 => 3,   // Touch Current → 扩散
            113 => 9,   // Static

            // OpenRevo 硬件 MCU 1..21
            1 => 9,     // 单色常亮
            2 => 0,     // 呼吸
            3 => 1,     // 波浪
            4 => 3,     // 按键微光
            5 => 4,     // 彩虹相环
            6 => 3,     // 涟漪
            7 => 2,     // 闪烁
            9 => 1,     // 跑马 → 波浪
            10 => 7,    // 雨滴
            12 => 3,    // 闪烁跳变
            13 => 4,    // 混合流光
            14 => 1,    // 极光 → 波浪
            17 => 2,    // 烟花 → 星光
            20 => 4,    // 彩虹呼吸 → 色轮
            21 => 3,    // 电竞高亮 → 按键微光
            _ => effect
        };
    }

    internal static bool HasColor(int localMode) => localMode is 0 or 3 or 9;

    internal static bool HasSpeed(int localMode) => localMode is 0 or 1 or 2 or 4 or 5 or 6 or 7 or 8;
}
