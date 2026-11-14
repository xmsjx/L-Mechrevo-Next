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

    // ── 同方 EC 通道（无 GCU 时由本地 EC 接管；界面层只经这里问） ──

    /// <summary>当前是否由本地 EC 接管（EC 模式）。</summary>
    internal static bool EcModeActive => GcuEcMode.IsEcMode;

    /// <summary>EC 模式的判定来源（诊断展示用）。</summary>
    internal static string EcModeReason => GcuEcMode.Reason;

    /// <summary>标记 EC 通道已启用。</summary>
    internal static void EcMarkActive() => GcuEcMode.MarkActive();

    /// <summary>本机限充通道是否可用。</summary>
    internal static bool EcChargeAvailable => EcChargeLimit.IsAvailableOnThisMachine();

    /// <summary>限充是否被代际规则判为危险（10/16/20/30 系）。</summary>
    internal static bool EcChargeLegacyBlocked => EcChargeLimit.IsLegacyGenerationBlocked();

    /// <summary>限充通道探测是否可用。</summary>
    internal static bool EcChargeProbeChannelAvailable => EcChargeLimit.ProbeChannelAvailable();

    // ── 核显直连/静音（ACPI 本机通道） ──

    /// <summary>本机是否支持核显直连切换。</summary>
    internal static bool IgpuSwitchSupported => AcpiGpuHotSwitch.IsSupported;

    /// <summary>本机是否能走本地静音通道。</summary>
    internal static bool IgpuLocalSilenceAvailable => AcpiGpuHotSwitch.CanUseLocalSilence;

    /// <summary>结束占用核显的设备（可关闭的进程/容器）。</summary>
    internal static int IgpuKillClosableOccupants(out string detail) => AcpiGpuHotSwitch.KillClosableOccupants(out detail);

    /// <summary>切换核显直连。</summary>
    internal static bool IgpuTrySetOnly(bool on, bool stopDisplayContainer, out string detail)
        => AcpiGpuHotSwitch.TrySetIgpuOnly(on, stopDisplayContainer, out detail);

    /// <summary>标记"强制静音"状态。</summary>
    internal static void IgpuMarkForcedSilence() => AcpiGpuHotSwitch.MarkForcedSilence();

    /// <summary>清除"强制静音"状态。</summary>
    internal static void IgpuClearForcedSilence() => AcpiGpuHotSwitch.ClearForcedSilence();

    /// <summary>开机恢复被强制静音的显卡状态。</summary>
    internal static bool IgpuRecoverForcedSilenceOnStartup(out string detail)
        => AcpiGpuHotSwitch.TryRecoverForcedSilenceOnStartup(out detail);
}
