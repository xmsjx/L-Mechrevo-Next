using System;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Gpu;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// ⭐ WPF 侧唯一后端宿主（P0-1/P0-2）：窗口与旧程序集之间的唯一静态桥。
/// <para>旧 WinForms 业务程序集内部仍读 <c>Program.hw/service</c>；本类在赋值时同步写回，
/// 保证 WPF 与旧逻辑共享同一后端实例。</para>
/// <para>⛔ 新 UI 不再直接读写 <c>Program.hw/service</c>；只允许经由本类。</para>
/// </summary>
internal static class WpfBackend
{
    private static MechrevoHw? _hw;
    private static MechrevoService? _service;
    private static AsusACPI? _acpi;
    private static KeyboardRgb? _rgb;

    internal static MechrevoHw? Hw
    {
        get => _hw;
        set
        {
            _hw = value;
            MechrevoLite.Program.hw = value;
        }
    }

    internal static MechrevoService? Service
    {
        get => _service;
        set
        {
            _service = value;
            MechrevoLite.Program.service = value;
        }
    }


    internal static AsusACPI? Acpi
    {
        get => _acpi;
        set
        {
            _acpi = value;
            MechrevoLite.Program.acpi = value;
        }
    }

    internal static KeyboardRgb? Rgb
    {
        get => _rgb;
        set
        {
            _rgb = value;
            MechrevoLite.Program.rgb = value;
        }
    }

    // 兼容旧字段名（WPF 迁移期）；后续可直接用 Acpi/Rgb。
    internal static AsusACPI? acpi
    {
        get => Acpi;
        set => Acpi = value;
    }

    internal static KeyboardRgb? rgb
    {
        get => Rgb;
        set => Rgb = value;
    }

    internal static bool UiAuditMode => MechrevoLite.Program.UiAuditMode;

    internal static string ReleaseVersion => MechrevoLite.Program.ReleaseVersion;

    internal static string ReleaseLabel => MechrevoLite.Program.ReleaseLabel;

    internal static bool IsLightingTemporarilySuspended => MechrevoLite.Program.IsLightingTemporarilySuspended;

    internal static bool LightingIdleSuspended
    {
        get => MechrevoLite.Program.LightingIdleSuspended;
        set => MechrevoLite.Program.LightingIdleSuspended = value;
    }

    internal static void NotifyLightingUserIntent() => MechrevoLite.Program.NotifyLightingUserIntent();

    internal static void MarkKeyboardCustomStatusBaseline() => MechrevoLite.Program.MarkKeyboardCustomStatusBaseline();

    internal static bool IsAttached => _hw != null && _service != null;

    internal static void Attach(MechrevoHw? hw, MechrevoService? service)
    {
        Hw = hw;
        Service = service;
    }

    // ── 星耀 / NUC 专用通道（界面层只经这里调用，⛔ 不直连硬件类型）──

    /// <summary>星耀平台（AMD SMU 系）判定。</summary>
    internal static bool IsXingyaoPlatform => XingyaoPmc2.IsXingyaoPlatform();

    /// <summary>星耀性能档是否适用（按机型事实判定）。</summary>
    internal static bool XingyaoPerformanceModeApplicable(MechrevoHw? hw) => XingyaoPerformanceMode.IsApplicable(hw);

    /// <summary>星耀快捷键（灯光休眠等）是否适用。</summary>
    internal static bool XingyaoQuickSwitchApplicable(MechrevoHw? hw) => XingyaoQuickSwitch.IsApplicable(hw);

    /// <summary>读星耀限充（档位上下限）。</summary>
    internal static bool XingyaoReadChargeLimit(out bool enabled, out int low, out int high, out string detail)
        => XingyaoPmc2.TryReadChargeLimit(out enabled, out low, out high, out detail);

    /// <summary>写星耀限充。</summary>
    internal static bool XingyaoApplyChargeLimit(bool enabled, int low, int high, out string detail)
        => XingyaoPmc2.TryApplyChargeLimit(enabled, low, high, out detail);

    /// <summary>恢复星耀限充原状。</summary>
    internal static bool XingyaoRestoreChargeLimit(out string detail) => XingyaoPmc2.TryRestoreChargeLimit(out detail);

    /// <summary>星耀性能档下发。</summary>
    internal static bool XingyaoApplyVisualMode(int mode, out string detail) => XingyaoPerformanceMode.TryApplyVisualMode(mode, out detail);

    /// <summary>星耀性能档回读。</summary>
    internal static bool XingyaoReadVisualMode(out int mode, out string detail) => XingyaoPerformanceMode.TryReadVisualMode(out mode, out detail);

    /// <summary>星耀键盘休眠超时设置。</summary>
    internal static bool XingyaoSetKeyboardSleepTimeout(bool alwaysOn, out string detail)
        => XingyaoQuickSwitch.TrySetKeyboardSleepTimeout(alwaysOn, out detail);

    /// <summary>NUC 极致狂暴的虚拟档位常量。</summary>
    internal const int NucExtremeVisualMode = NucPerformanceMode.ExtremeVisualMode;

    /// <summary>NUC 性能档下发。</summary>
    internal static bool NucApplyMode(int mode, out string detail) => NucPerformanceMode.TryApply(mode, out detail);

    /// <summary>NUC 性能档回读。</summary>
    internal static bool NucReadMode(out int mode) => NucPerformanceMode.TryReadVisualMode(out mode);

    /// <summary>NUC 极致狂暴是否处于已下发状态。</summary>
    internal static bool NucExtremeActive => NucPerformanceMode.IsExtremeActive;

    /// <summary>清掉 NUC 极致狂暴的本地标记（回读不一致时用）。</summary>
    internal static void NucClearExtremeMarker() => NucPerformanceMode.ClearExtremeMarker();
}
