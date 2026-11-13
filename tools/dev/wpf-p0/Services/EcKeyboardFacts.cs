using MechrevoLite.Hardware;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// EC 实验模式键盘通道的只读事实（唯一来源）。
/// <para>⭐ 界面层只读这里，⛔ 不直接引用 EC 键盘驱动类型；服务层的实例属性也转发到这里，
/// 保证「静态辅助方法」和「服务实例」两条读取路径拿到的值永远一致。</para>
/// </summary>
internal static class EcKeyboardFacts
{
    /// <summary>EC 实验模式（无 GCU）是否接管键盘灯。</summary>
    internal static bool Active => GcuEcKeyboard.Active;

    /// <summary>EC 模式键盘灯是否为 HID 单键。</summary>
    internal static bool Hid => GcuEcKeyboard.IsHid;

    /// <summary>EC 模式键盘灯是否为白光单色键盘。</summary>
    internal static bool White => GcuEcKeyboard.IsEcWhite;

    /// <summary>EC 模式键盘灯是否为单分区 RGB。</summary>
    internal static bool SingleZone => GcuEcKeyboard.IsEcSingleZone;

    /// <summary>EC 模式是否存在可控制的键盘灯通道。</summary>
    internal static bool Controllable => GcuEcKeyboard.HasControllableKeyboard;

    /// <summary>EC 模式键盘灯种类（日志/状态行展示用）。</summary>
    internal static string Kind => GcuEcKeyboard.Kind.ToString();
}
