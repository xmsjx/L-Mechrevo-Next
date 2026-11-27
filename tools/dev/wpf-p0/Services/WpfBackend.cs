using System;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Display;
using MechrevoLite.Hardware.Architecture;
using MechrevoLite.Gpu;
using L_Mechrevo.WpfP0.Resources;

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
    private static KeyboardRgb? _rgb;

    internal static MechrevoHw? Hw
    {
        // 本类可能从未显式赋值（旧程序集直接写 Program.hw）⇒ 回落到全局后端实例，避免门面读到 null。
        get => _hw ?? MechrevoLite.Program.hw;
        set
        {
            _hw = value;
            MechrevoLite.Program.hw = value;
        MechrevoLite.WujieFanCurveAutoApply.Hook();
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


    internal static KeyboardRgb? Rgb
    {
        get => _rgb;
        set
        {
            _rgb = value;
            MechrevoLite.Program.rgb = value;
        }
    }

    // 兼容键盘灯控制器的字段访问。
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

    /// <summary>当前实际运行的硬件后端（同方 GCU / 同方 EC / 宝龙达 / NUC-UMI / 星耀 / 老 MIFS）。</summary>
    internal static MechrevoLite.Hardware.Architecture.BackendKind ActiveBackendKind =>
        MechrevoLite.Hardware.Architecture.MachineFacts.From(HwOrProgram).Backend;

    /// <summary>EC 模式的判定来源（诊断展示用）。</summary>
    internal static string EcModeReason => GcuEcMode.Reason;

    /// <summary>是否提供「GCU 模式 / EC 模式」切换（门控在后端求值，界面只读这里的结论）。</summary>
    internal static bool ControlModeSwitchSupported => HwOrProgram?.SupportsControlModeSwitch == true;

    /// <summary>控制模式切换门控的判定依据（诊断展示用）。</summary>
    internal static string ControlModeSwitchReason => HwOrProgram?.ControlModeSwitchReason ?? "未探测到后端";

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

    // ── 机型/能力枚举与零散硬件事实（界面层只经这里读） ──

    internal const FeatureAvailability FeatureSupported = FeatureAvailability.Supported;
    internal const FeatureAvailability FeatureUnsupported = FeatureAvailability.Unsupported;
    internal const FeatureAvailability FeatureUnknown = FeatureAvailability.Unknown;

    /// <summary>是否属同方家族（只读事实）。</summary>
    internal static bool IsTongfangFamily => MachineIdentity.Collect().TongfangFamily;

    // ── GCU 预热窗口（首次安装本程序 / 首次装载 GCU / 从 EC 切回 GCU 后的 5 分钟） ──
    /// <summary>启动时按机型事实打开预热窗口（界面层不直接碰硬件类型）。</summary>
    internal static void BeginGcuWarmupOnStartup()
    {
        MechrevoLite.Hardware.GcuWarmup.BeginIfFirstRun();
        MechrevoLite.Hardware.GcuWarmup.BeginIfFirstGcuInstall();
    }

    /// <summary>是否处于 GCU 预热窗口内。</summary>
    internal static bool GcuWarmupActive => MechrevoLite.Hardware.GcuWarmup.IsActive;

    /// <summary>预热窗口的触发原因。</summary>
    internal static string GcuWarmupReason => MechrevoLite.Hardware.GcuWarmup.Reason;

    /// <summary>预热窗口剩余秒数。</summary>
    internal static int GcuWarmupRemainingSeconds => MechrevoLite.Hardware.GcuWarmup.RemainingSeconds;

    /// <summary>当前 GCU 是否真的连上（预热豁免只适用于「连上但功能未就绪」）。</summary>
    internal static bool GcuConnectedNow => Hw?.IsGcuConnected == true;

    // 机型身份（判据都在硬件层，界面只读结论；hw 为 null 时一律 false，与原有 `Hw?.X == true` 等价）
    internal static bool MachineIsBaolongda => Hw?.UsesBaolongda == true;
    internal static bool MachineIsBaolongda30 => Hw?.IsBaolongda30Series == true;
    internal static bool MachineIsNucUmiFamily => Hw?.IsNucUmiFamily == true;
    internal static bool MachineGcuConnected => Hw?.IsGcuConnected == true;
    internal static bool MachineIsWujieSmuApu => Hw?.WujieSmuApu == true;
    internal static bool MachineIsSmuCustomApu => Hw?.SmuCustomApu == true;

    /// <summary>自定义档在固件里没有槽位（无界 / 星耀 APU / 宝龙达）：档位记忆只能由软件承担。</summary>
    internal static bool MachineCustomModeIsLocalOnly => Hw?.CustomModeIsLocalOnly == true;
    internal static bool MachineIsIntelNuc => Hw?.IsIntelNucMachine == true;
    internal static bool MachineIsUmi => Hw?.IsUmiMachine == true;
    internal static bool MachineConnected => Hw?.IsConnected == true;

    // 只读事实（可空语义与 `hw?.X` 完全一致，别改成默认值）
    internal static MechrevoLite.Gpu.DgpuGenerationKind? DgpuGeneration => Hw?.DgpuGeneration;

    // 传感器与只读数据（缺省值沿用界面对 -1 的既有约定，不是新语义）
    internal static int CpuTemp => Hw?.CpuTemp ?? -1;
    internal static int GpuTemp => Hw?.GpuTemp ?? -1;
    internal static int CpuFanRpm => Hw?.CpuFanRpm ?? -1;
    internal static int GpuFanRpm => Hw?.GpuFanRpm ?? -1;
    internal static int BatteryPercent => Hw?.BatteryPercent ?? -1;
    internal static int RamUsage => Hw?.RamUsage ?? -1;
    internal static double RamUsedGb => Hw?.RamUsedGb ?? -1;
    internal static int VramUsedMb => Hw?.VramUsedMb ?? -1;
    internal static int CurrentHz => Hw?.CurrentHz ?? -1;
    internal static int Pl4Minimum => Hw?.Pl4Minimum ?? -1;
    internal static int Pl4Maximum => Hw?.Pl4Maximum ?? -1;
    internal static bool Pl1Adjustable => Hw?.Pl1Adjustable == true;
    internal static bool Pl2Adjustable => Hw?.Pl2Adjustable == true;
    internal static bool Pl4Adjustable => Hw?.Pl4Adjustable == true;
    // ⭐ 没有回读通道时（LocalDimmingSeen == false，如苍龙 GCU 不回该字段）用本机记忆显示，
    //    否则硬件/记忆都是"开"、开关却显示关 ⚠️。
    internal static bool LocalDimmingOn
    {
        get
        {
            try
            {
                // 记忆优先：这两个开关不少同方机型只有下发没有回读，或回读易失；
                // 按回读显示会和用户上次的选择相反（固件开机回默认值）。
                int saved = MechrevoLite.AppConfig.Get(MechrevoLite.Hardware.MechrevoService.LocalDimmingSavedKey, -1);
                if (saved >= 0)
                {
                    return saved != 0;
                }
                return Hw?.LocalDimmingSeen == true && Hw.LocalDimming;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>LCD Overdrive 开关状态：与分区控光同一条规则（记忆优先，没有记忆才看回读）。</summary>
    internal static bool LcdOverdriveOn
    {
        get
        {
            try
            {
                int saved = MechrevoLite.AppConfig.Get(MechrevoLite.Hardware.MechrevoService.LcdOverdriveSavedKey, -1);
                if (saved >= 0)
                {
                    return saved != 0;
                }
                return Hw?.LcdOverdriveSeen == true && Hw.LcdOverdrive;
            }
            catch
            {
                return false;
            }
        }
    }
    internal static bool SupportsLocalDimming => Hw?.SupportsLocalDimming == true;
    internal static bool SupportsGpuHotSwap => Hw?.SupportsGpuHotSwap == true;

    // 自定义档调参范围与当前值（界面只取值，判据仍由硬件层给）
    internal static int Pl1Minimum => Hw?.Pl1Minimum ?? -1;
    internal static int Pl1Maximum => Hw?.Pl1Maximum ?? -1;
    internal static int Pl2Minimum => Hw?.Pl2Minimum ?? -1;
    internal static int Pl2Maximum => Hw?.Pl2Maximum ?? -1;
    internal static int TccMinimum => Hw?.TccMinimum ?? -1;
    internal static int TccMaximum => Hw?.TccMaximum ?? -1;
    internal static int GpuTgpMinimum => Hw?.GpuTgpMinimum ?? -1;
    internal static int GpuTgpMaximum => Hw?.GpuTgpMaximum ?? -1;
    internal static int GpuDbMinimum => Hw?.GpuDbMinimum ?? -1;
    internal static int GpuDbMaximum => Hw?.GpuDbMaximum ?? -1;
    internal static int GpuCoreOffsetUserMinimum => Hw?.GpuCoreOffsetUserMinimum ?? -1;
    internal static int GpuCoreOffsetUserMaximum => Hw?.GpuCoreOffsetUserMaximum ?? -1;
    internal static int GpuMemoryOffsetUserMinimum => Hw?.GpuMemoryOffsetUserMinimum ?? -1;
    internal static int GpuMemoryOffsetUserMaximum => Hw?.GpuMemoryOffsetUserMaximum ?? -1;
    internal static int FanSwitchSpeedMinimum => Hw?.FanSwitchSpeedMinimum ?? -1;
    internal static int FanSwitchSpeedMaximum => Hw?.FanSwitchSpeedMaximum ?? -1;
    internal static int Pl1 => Hw?.Pl1 ?? -1;
    internal static int Pl2 => Hw?.Pl2 ?? -1;
    internal static int Pl4 => Hw?.Pl4 ?? -1;
    internal static int TccTarget => Hw?.TccTarget ?? -1;
    internal static int GpuTgp => Hw?.GpuTgp ?? -1;
    internal static int GpuDb => Hw?.GpuDb ?? -1;
    internal static int FanSwitchSpeed => Hw?.FanSwitchSpeed ?? -1;
    internal static int EffectiveGpuCoreClockOffset => Hw?.EffectiveGpuCoreClockOffset ?? -1;
    internal static int EffectiveGpuMemoryClockOffset => Hw?.EffectiveGpuMemoryClockOffset ?? -1;

    internal static bool TccSwitch => Hw?.TccSwitch == true;
    internal static bool GpuDbSwitch => Hw?.GpuDbSwitch == true;
    internal static bool FanSwitchSpeedEnabled => Hw?.FanSwitchSpeedEnabled == true;
    internal static bool FanBoost => Hw?.FanBoost == true;
    internal static bool TccAdjustable => Hw?.TccAdjustable == true;
    internal static bool GpuDynamicBoostAdjustable => Hw?.GpuDynamicBoostAdjustable == true;
    internal static bool GpuTgpAdjustable => Hw?.GpuTgpAdjustable == true;
    internal static bool SupportsFanSwitchSpeed => Hw?.SupportsFanSwitchSpeed == true;
    internal static bool SupportsGpuOverclock => Hw?.SupportsGpuOverclock == true;
    internal static bool HwGpuOverclockEnabled => Hw?.GpuOverclockEnabled == true;
    internal static bool BaolongdaPl1Unset => Hw?.BaolongdaPl1Unset == true;
    internal static bool BaolongdaPl2Unset => Hw?.BaolongdaPl2Unset == true;
    internal static bool BaolongdaTempWallUnset => Hw?.BaolongdaTempWallUnset == true;
    internal static int IgpuOnlySaveMode => Hw?.IgpuOnlySaveMode ?? -1;
    internal static int GpuMode => Hw?.GpuMode ?? -1;

    // 核显省电开关状态写进硬件对象（写入动作仍在硬件层）
    internal static bool SetAcpiIgpuOnlyState(bool on)
    {
        var hw = Hw;
        if (hw == null)
        {
            return false;
        }
        hw.SetAcpiIgpuOnlyState(on);
        return true;
    }

    // 可空事实：界面用 `?? 兜底` 的写法必须保留可空，不能压成 bool
    internal static bool? SupportsDgpuDirectIfKnown => Hw?.SupportsDgpuDirect;
    internal static bool? CanOfferIgpuOnlyIfKnown => Hw?.CanOfferIgpuOnly;
    // 快捷开关：界面只下发意图，字典由硬件层持有
    internal static bool QuickSwitchesAvailable => Hw?.QuickSwitches != null;

    internal static bool TrySetQuickSwitch(string key, bool value)
    {
        var switches = Hw?.QuickSwitches;
        if (switches == null)
        {
            return false;
        }
        switches[key] = value;
        return true;
    }

    internal static bool TryGetQuickSwitch(string key, out bool value)
    {
        var switches = Hw?.QuickSwitches;
        value = false;
        return switches != null && switches.TryGetValue(key, out value);
    }


    internal static void ResetDgpuOverclockForSleep()
    {
        try { Hw?.ResetDirectGpuOverclockForSleep(); } catch { }
    }
    internal static bool DgpuEverPresent => Hw?.DgpuEverPresent == true;

    // ── ACPIDriver（EC 通道）可用性与自愈 ──

    // ── 屏幕开关（分区控光 / LCD Overdrive）：EC 模式没有回读通道，只能持久化上次状态 ──
    internal static bool LocalDimmingChannelAvailable => MechrevoLite.Hardware.AcpiDisplaySwitch.QueryLocalDimmingSupport();

    internal static bool LcdOverdriveChannelAvailable => MechrevoLite.Hardware.AcpiDisplaySwitch.QueryLcdOverdriveSupport();

    /// <summary>分区控光通道的本地区分结果：true=有、false=本地明确无、null=读不到（未知）。</summary>
    internal static bool? LocalDimmingChannelState => MechrevoLite.Hardware.AcpiDisplaySwitch.LocalDimmingSupportCached;

    /// <summary>Overdrive 通道的本地区分结果：true=有、false=本地明确无、null=读不到（未知）。</summary>
    internal static bool? LcdOverdriveChannelState => MechrevoLite.Hardware.AcpiDisplaySwitch.LcdOverdriveSupportCached;

    /// <summary>
    /// 分区控光下发（服务层门面）：GCU 在线走后端命令，否则走本地 EC/ACPI 通道。
    /// <para>⭐ 界面与启动重放共用这一条，避免两处各写一套通道判据（重复代码）。</para>
    /// </summary>
    internal static async Task<bool> SwitchLocalDimmingAsync(bool on)
    {
        try
        {
            MechrevoService? service = Service;
            // ⭐ 按【声明的通道】选路，而不是按"GCU 连没连"：
            //   苍龙这类机器 GCU 是连着的，但分区控光是 EC 本地 ACPI 通道（GCU 压根不上报该字段）
            //   ⇒ 原来一律发 MQTT 等于发给了不支持它的后端。
            BackendChannel channel = Hw == null
                ? BackendChannel.None
                : FeatureGate.Get(FeatureId.LocalDimming, Hw).Channel;
            if (channel == BackendChannel.Gcu && service != null)
            {
                return await service.SwitchLocalDimming(on).ConfigureAwait(false);
            }
            if (channel == BackendChannel.AcpiWmi)
            {
                return ApplyLocalDimming(on);
            }
            // 通道未声明（Unknown/None）：保留旧行为 —— GCU 在线走后端，否则走本地 EC。
            if (service != null && Hw?.IsGcuConnected == true)
            {
                return await service.SwitchLocalDimming(on).ConfigureAwait(false);
            }
            return MechrevoLite.Hardware.GcuEcMode.IsEcMode && ApplyLocalDimming(on);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 单键 RGB 开机重放：按本机保存的灯光设置重新起一次灯效。
    /// <para>⭐ 固件重启会回到自己的默认灯效（与用户上次选择无关）⇒ 只能由本机记住并重放。</para>
    /// <para>⛔ 用户从未设置过灯光时调用方不应调用本方法（不要覆盖官方控制台的状态）。</para>
    /// </summary>
    internal static bool ReapplySavedKeyboardRgb()
    {
        LightChannelSettings saved = LightingSettingsStore.Load("Keyboard/Ctrl", "Single");
        return ReapplySavedKeyboardRgb(saved.PowerOn, saved.Effect, saved.Light, saved.BrightnessPct);
    }

    /// <summary>当前是不是 HID 单键键盘（界面据此决定亮度是连续 0..100 还是档位）。</summary>
    internal static bool IsHidKeyboardNow() => KeyboardChannelPolicy.IsHidKeyboard(Hw);

    /// <summary>HID 键盘当前亮度百分比（0..100；没有设备 = -1）。</summary>
    internal static int CurrentHidBrightness() => rgb?.Brightness ?? -1;

    /// <summary>百分比 ⇒ 固件/EC 只认的 1..4 档（复用既有映射，⛔ 不另立判据）。</summary>
    internal static int MapPercentToLevel(int percent) => KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(percent);

    /// <summary>
    /// 单键 RGB 开机重放（显式保存态）。
    /// <para>⭐ 2026-11-19（机主：键盘灯没有重放）：开关/亮度/效果一律以 keyboard.cfg 为准；
    /// ⛔ 不再靠 rgb.cfg 的 KbPowerOn 猜开关 —— 本机根本没有 rgb.cfg，旧写法把「开灯」重放成了「关灯」。</para>
    /// </summary>
    internal static bool ReapplySavedKeyboardRgb(bool powerOn, string? effect, int light, int brightnessPct = -1)
    {
        try
        {
            // 开机重放要能真的发帧 ⇒ 用 EnsurePerKeyRgb（顺带补一次连接），不是只读的 Rgb 快照。
            KeyboardRgb? kb = EnsurePerKeyRgb();
            if (kb == null)
            {
                return false;
            }
            kb.KbPowerOn = powerOn;
            if (!powerOn)
            {
                kb.StopCurrentEffect();
                kb.KbPowerSyncFromHardware(false);
                return true;
            }
            kb.StartMode(ResolveSavedKeyboardHidMode(effect, kb.ActiveMode));
            kb.StartMode(ResolveSavedKeyboardHidMode(effect, kb.ActiveMode));
            // ⭐ 2026-11-19（机主）：HID 键盘亮度是连续的 0..100 —— 有精确值就按原值还原，
            //   老存档没有这个字段才退回档位×25（与旧版行为一致）。
            int percent = brightnessPct >= 0 ? Math.Clamp(brightnessPct, 0, 100) : Math.Clamp(light, 0, 4) * 25;
            if (percent > 0)
            {
                kb.Brightness = percent;
            }
            return kb.ApplyBrightnessToDevice();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[light-restore] 单键 RGB 重放异常：" + ex.GetType().Name + " " + ex.Message);
            return false;
        }
    }

    /// <summary>存档里的效果 id：HID 单键机存的是 "hid:N" ⇒ 直接就是帧流的模式号。</summary>
    private static int ResolveSavedKeyboardHidMode(string? effect, int fallback)
    {
        if (!string.IsNullOrWhiteSpace(effect)
            && effect.StartsWith("hid:", StringComparison.Ordinal)
            && int.TryParse(effect.AsSpan(4), out int mode)
            && mode > 0)
        {
            return mode;
        }
        return fallback;
    }

    /// <summary>
    /// LCD Overdrive 下发（服务层门面）：与分区控光同一条规则 —— 按声明的通道选路
    /// （Gcu ⇒ 服务层 MQTT；AcpiWmi ⇒ 本地 EC/ACPI），⛔ 不再按"GCU 连没连"猜通道。
    /// </summary>
    internal static async Task<bool> SwitchLcdOverdriveAsync(bool on)
    {
        try
        {
            MechrevoService? service = Service;
            BackendChannel channel = Hw == null
                ? BackendChannel.None
                : FeatureGate.Get(FeatureId.LcdOverdrive, Hw).Channel;
            if (channel == BackendChannel.Gcu && service != null)
            {
                return await service.SwitchLcdOverdrive(on).ConfigureAwait(false);
            }
            if (channel == BackendChannel.AcpiWmi)
            {
                return ApplyLcdOverdrive(on);
            }
            if (service != null && Hw?.IsGcuConnected == true)
            {
                return await service.SwitchLcdOverdrive(on).ConfigureAwait(false);
            }
            return MechrevoLite.Hardware.GcuEcMode.IsEcMode && ApplyLcdOverdrive(on);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本机这一轮是否成功下发过"开"：这两个开关没有回读通道，用下发结果代替回读。</summary>
    internal static bool LocalDimmingAppliedOn { get; private set; }

    /// <summary>开机屏幕开关重放是否已有结论：用来区分"还没跑"和"跑了但没生效"（界面提示用）。</summary>
    internal static bool DisplayToggleReplayDone { get; private set; }

    internal static void MarkDisplayToggleReplayDone() => DisplayToggleReplayDone = true;

    internal static bool ApplyLocalDimming(bool on)
    {
        try
        {
            if (on)
            {
                // ⭐ EC/ACPI 这两个开关只在【状态变化】时真正生效：固件已经是"开"时再写"开"等于没写。
                //   用户看到的就是"按钮开了却没效果，关了再开才行" ⇒ 开之前先关一次制造边沿。
                //   ⛔ 这次关不发意图（不把用户记忆改掉），关失败也无所谓，继续开。
                MechrevoLite.Hardware.AcpiDisplaySwitch.TrySetLocalDimming(false, rememberIntent: false);
                System.Threading.Thread.Sleep(120);
            }
            if (!MechrevoLite.Hardware.AcpiDisplaySwitch.TrySetLocalDimming(on))
            {
                return false;
            }
            HwOrProgram?.ApplyLocalDimmingState(on);
            LocalDimmingAppliedOn = on;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本机这一轮是否成功下发过 OD 的"开"（同分区控光：没有回读通道，用下发结果代替）。</summary>
    internal static bool LcdOverdriveAppliedOn { get; private set; }

    internal static bool ApplyLcdOverdrive(bool on)
    {
        try
        {
            if (on)
            {
                // ⭐ 与分区控光同一条规则：固件只在状态变化时真正生效 ⇒ 开之前先关一次制造边沿。
                MechrevoLite.Hardware.AcpiDisplaySwitch.TrySetLcdOverdrive(false, rememberIntent: false);
                System.Threading.Thread.Sleep(120);
            }
            if (!MechrevoLite.Hardware.AcpiDisplaySwitch.TrySetLcdOverdrive(on))
            {
                return false;
            }
            HwOrProgram?.ApplyLcdOverdriveState(on);
            LcdOverdriveAppliedOn = on;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>确保自定义档服务已创建（物理键改绑「轮换自定义」与自定义档页都依赖它）。</summary>
    internal static bool EnsurePerfModeService()
    {
        try
        {
            if (MechrevoLite.Mode.PerfModeService.Instance != null)
            {
                return true;
            }
            MechrevoHw? hw = HwOrProgram;
            if (hw == null)
            {
                return false;
            }
            MechrevoLite.Mode.PerfModeService.EnsureCreated(hw);
            Logger.WriteLine("[perfmode] 自定义档服务已创建");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[perfmode] 自定义档服务创建失败：" + ex.GetType().Name + " " + ex.Message);
            return false;
        }
    }
    /// <summary>ACPIDriver 设备是否可用（EC 通道的前提）。</summary>
    internal static bool AcpiDriverAvailable => MechrevoLite.Hardware.AcpiDriverInstaller.IsDeviceAvailable();

    /// <summary>装机目录带着驱动文件时，后台尝试把驱动装上（失败只记日志，不弹窗）。</summary>
    internal static void EnsureAcpiDriverInBackground()
    {
        MechrevoLite.Hardware.AcpiDriverInstaller.EnsureInstalled(force: false, out _);
    }

    /// <summary>诊断用：ACPIDriver 状态一行。</summary>
    internal static string AcpiDriverDetail => MechrevoLite.Hardware.AcpiDriverInstaller.Describe();

    /// <summary>GCU 服务登记在、但可执行载荷缺失（登记残留，写入无人应答）。</summary>
    internal static bool GcuPayloadMissing => !MachineIdentity.Collect().GcuPayloadPresent;

    /// <summary>就绪判定的可读说明（缺什么一眼可见）。</summary>
    internal static string GcuPayloadDetail => MachineIdentity.Collect().GcuPayloadSource;

    /// <summary>本地控制端口是否可连（只探测，不发数据）。</summary>
    internal static bool GcuControlPortReachable => GcuPayloadProbe.IsPortReachable("127.0.0.1", 13688);

    /// <summary>限充状态回读是否可读（通道探测用）。</summary>
    internal static bool ChargeLimitChannelReadable() => BatteryLimitWriter.TryReadState(out _, out _, out _, out _);

    /// <summary>运行时机型是否允许可写通道。</summary>
    internal static bool RuntimeModelSupported => RuntimeModelSupport.Current().IsSupported;

    /// <summary>分区背光本机探测。</summary>
    internal static bool QueryLocalDimmingSupport() => AcpiDisplaySwitch.QueryLocalDimmingSupport();

    /// <summary>LCD Overdrive 本机探测。</summary>
    internal static bool QueryLcdOverdriveSupport() => AcpiDisplaySwitch.QueryLcdOverdriveSupport();

    /// <summary>静音狂暴（Turbo 子模式）当前是否激活。</summary>
    internal static bool SilentTurboActive => MechrevoService.IsSilentTurboActive;

    /// <summary>限充判定结论后缀（" · 提示"）。</summary>
    internal static string ChargeLimitVerdictSuffix => EcChargeLimit.Verdict switch
    {
        ChargeLimitVerdict.Verified => " · " + EcChargeLimit.VerifiedNotice,
        ChargeLimitVerdict.Ineffective => " · " + EcChargeLimit.IneffectiveNotice,
        _ => " · " + EcChargeLimit.PendingNotice
    };

    /// <summary>EC-only 路由声明（只读，供诊断包解释当前机型）。</summary>
    internal static string EcRouteDeclaration()
    {
        try
        {
            BackendRouteDecision route = BackendRouter.DecideAndLog();
            return "backend=" + route.Backend + " · rule=#" + route.RuleIndex + " " + route.Rule
                + " · machine=" + route.Identity.Summary;
        }
        catch (Exception ex)
        {
            return "路由声明读取失败：" + ex.Message;
        }
    }

    /// <summary>显卡数据库控制是否可用。</summary>
    internal static bool GpuDbAvailable => GpuDbControl.Supported();

    /// <summary>按视觉档同步显卡数据库。</summary>
    internal static void GpuDbSyncForVisualMode(int visualMode, string reason) => GpuDbControl.SyncForVisualMode(visualMode, reason);

    /// <summary>GCU 变体标识。</summary>
    internal static string GcuVariant => GcuIdentity.Variant;

    /// <summary>NUC 风扇表恢复默认。</summary>
    internal static bool NucFanRestoreDefault(out string detail) => NucFanTable.TryRestoreDefault(out detail);

    /// <summary>读 NUC Fn 锁状态。</summary>
    internal static bool NucReadFnLock(out bool on) => NucQuickSwitches.TryReadFnLock(out on);

    /// <summary>同步 NUC 快捷开关。</summary>
    internal static void NucSyncQuickSwitches(MechrevoHw? hw) => NucQuickSwitches.Sync(hw);

    /// <summary>静音狂暴的 WMI 子模式是否支持。</summary>
    internal static bool TurboSubModeSupported() => TurboSubModeWmi.IsSupported();

    /// <summary>UEFI 显卡复用变量是否存在。</summary>
    internal static bool UefiMuxVariablePresent => UefiGpuMux.HasMuxVariable;

    /// <summary>写 UEFI 冷切换档位。</summary>
    internal static bool UefiWriteColdMode(int visualMode, out string detail) => UefiGpuMux.TryWriteColdMode(visualMode, out detail);

    /// <summary>NUC 电池模式按百分比设置。</summary>
    internal static bool NucSetBatteryPercent(int percent, out string detail) => NucBatteryMode.TrySetPercent(percent, out detail);

    /// <summary>EC 限充覆盖（无智能提示）设置。</summary>
    internal static bool EcSetChargeLimitOverride(int percent, out string detail) => EcChargeLimitOverride.TrySetNoSmartHint(percent, out detail);

    /// <summary>星耀风扇转速读取。</summary>
    internal static bool XingyaoReadFanRpm(out int main, out int secondary) => XingyaoFanReader.TryReadFanRpm(out main, out secondary);

    /// <summary>灯光开关显示状态（通道电源 + 临时挂起）。</summary>
    internal static bool LightSwitchOn(bool channelPowerOn, bool temporarilySuspended) => LightingState.IsLightSwitchOn(channelPowerOn, temporarilySuspended);

    /// <summary>读 EC 键盘亮度档。</summary>
    internal static bool ReadEcKeyboardBrightness(out int level) => KeyboardEcBrightness.TryReadCurrentLevel(out level);

    /// <summary>灯带去重修复开关。</summary>
    internal static bool LightbarDedupEnabled => LightCompatibilitySwitches.LightbarDedupEnabled;

    /// <summary>NUC 单区键盘判定。</summary>
    internal static bool IsNucSingleZoneKeyboard() => NucKeyboardLighting.IsSingleZone();

    /// <summary>停止无界 EC 风扇自定义档。</summary>
    internal static void StopWujieFanActive(string reason) => WujieFanEcController.StopActive(reason);

    /// <summary>开机显卡 OC 保护（无服务时直接返回）。</summary>
    internal static Task<bool> ApplyGpuOcGuardAsync() =>
        Service is { } svc ? GpuOcShutdownGuard.ApplyIfUncleanShutdownAsync(svc) : Task.FromResult(false);

    /// <summary>宝龙达退出自定义档：停用曲线并交回固件（失败只记日志，不抛）。</summary>
    internal static void RestoreBaolongdaFanOnExit()
    {
        try
        {
            if (Hw is not { UsesBaolongda: true } bld)
            {
                return;
            }
            BaolongdaFanCurveSettings? curve = bld.LoadBaolongdaFanCurve();
            if (curve != null)
            {
                bld.SaveBaolongdaFanCurve(MechrevoHw.CurrentFanCurveModeId, curve.WithEnabled(false));
            }
            bld.RestoreBaolongdaFanFirmwareControl();
            Logger.WriteError("[fan] 宝龙达退出自定义档 ⇒ 曲线已停用并交回固件");
        }
        catch (Exception ex)
        {
            Logger.WriteError("[fan] 宝龙达退出自定义档失败（已吞掉）：" + ex.Message);
        }
    }

    /// <summary>电源来源标题（功率表命中 / EC 原始码）；无结论返回 null。</summary>
    internal static string? ResolvePowerSourceHeadline(bool acOnline)
    {
        PowerInputSample? sample = PowerStatusService.Read().Input;
        PowerInputKind kind = PowerInputDecoder.Reconcile(sample, acOnline);
        if (sample != null)
        {
            string text = SettingsText.PowerSourceHeadline(kind, sample.AdapterWatts);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        int raw = sample?.BiosInfo3Raw ?? -1;
        if (raw >= 0)
        {
            return string.Format(WpfStrings.DisplayPowerEcHexFormat, raw.ToString("X2"));
        }
        // ⛔ 不再用电池充放功率兜底适配器功率：那是「充电功率」，填进适配器位会写成
        //    「外接电源（EC 30.4W）」这种误导文案。适配器瓦数没解出来就只显示「外接电源」。
        return null;
    }

    // ── EC 本地灯光 / ITE 设备事实（小窗也要用） ──

    /// <summary>EC 本地灯带可用。</summary>
    internal static bool LocalLightbarSupported => GcuEcExperimental.LocalLightbar;

    /// <summary>EC 本地 Logo 灯可用。</summary>
    internal static bool LocalLogoSupported => GcuEcExperimental.LocalLogoLight;

    /// <summary>EC 本地铰链灯可用。</summary>
    internal static bool LocalHingeSupported => GcuEcExperimental.LocalHingeLight;

    /// <summary>ITE 设备有灯带通道。</summary>
    internal static bool IteHasLightbar => IteLightingLocal.HasLightbar;

    /// <summary>ITE 设备有 USB Logo 通道。</summary>
    internal static bool IteHasUsbLogo => IteLightingLocal.HasUsbLogo;

    /// <summary>ITE 设备有铰链灯通道。</summary>
    internal static bool IteHasHinge => IteLightingLocal.HasHinge;

    /// <summary>NUC/UMI 是否支持走能力位的显卡模式切换（按传入事实判定）。</summary>
    internal static bool NucGpuModeSupported(MechrevoHw? hw) => NucUmiCapabilityMap.IsSupported(hw, NucUmiOperation.GpuMode);

    // ── EC 键盘事实 / 本地软件调色 / HID 亮度换算（小窗用） ──

    /// <summary>EC 键盘灯通道是否启用。</summary>
    internal static bool EcKeyboardActiveNow => EcKeyboardFacts.Active;

    /// <summary>EC 键盘灯是否可控。</summary>
    internal static bool EcKeyboardControllableNow => EcKeyboardFacts.Controllable;

    /// <summary>EC 键盘是否为 HID 单键键盘。</summary>
    internal static bool EcKeyboardIsHidNow => EcKeyboardFacts.Hid;

    /// <summary>EC 键盘是否为白光键盘。</summary>
    internal static bool EcKeyboardIsWhiteNow => EcKeyboardFacts.White;

    /// <summary>色彩增强默认档位（无后端档位表时用本地预设）。</summary>
    internal static DisplayEffectProfile? DefaultColorEffectProfile(string mode) =>
        SoftwareColorEnhancement.DefaultProfile(mode);

    /// <summary>按参数应用本地软件色彩增强。</summary>
    internal static void ApplyLocalColorProfile(string mode, int brightness, int colorTemp,
        float red, float green, float blue, float vibrant, float contrast, float gamma) =>
        SoftwareColorEnhancement.ApplyProfile(mode, brightness, colorTemp, red, green, blue, vibrant, contrast, gamma);

    /// <summary>按档位名应用本地软件色彩增强。</summary>
    internal static void ApplyLocalColorEffect(string mode) => SoftwareColorEnhancement.Apply(mode);

    /// <summary>HID 软件亮度（%）换算成硬件档位。</summary>
    internal static int MapHidBrightnessToLevel(int percent) => KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(percent);

    /// <summary>呼吸灯配色（1..4 编号）。</summary>
    internal static int BreathingPaletteColor(int index) => LightChannelSettings.BreathingPalette[index];

    /// <summary>新建一个 HID 键盘实例（未连接）。</summary>
    internal static KeyboardRgb CreatePerKeyRgb() => new KeyboardRgb();

    /// <summary>系统唤醒后重连 HID 键盘并按原模式重放灯效（休眠后句柄失效）。</summary>
    internal static bool ReconnectHidKeyboardForResume()
    {
        try
        {
            KeyboardRgb? kb = rgb;
            return kb != null && kb.ReconnectForResumeReplay();
        }
        catch
        {
            return false;
        }
    }
    /// <summary>复查并重写一次 EC 限充档位（GCU 会把自己的档位写回，覆盖用户选择）。</summary>
    internal static bool EcReassertChargeMode(int mode, out string detail) =>
        EcChargeLimitOverride.TryReassertMode(mode, 3000, out detail);

    /// <summary>把用户选的三档同步给后端（GCU 会按自己的值再写回 EC，必须先让它认这个档）。</summary>
    /// <summary>电池三档与后端对齐的结果：本机没有 GCU 通道时不算失败。</summary>
    internal enum BatteryAlignResult
    {
        /// <summary>GCU 通道存在且已确认。</summary>
        Aligned,

        /// <summary>本机没有 GCU 通道（EC 本地路由），档位由 EC 写入，无需对齐。</summary>
        NotApplicable,

        /// <summary>GCU 通道存在但发布失败。</summary>
        Failed
    }

    internal static async Task<BatteryAlignResult> AlignBackendBatteryProtectionAsync(int mode)
    {
        MechrevoHw? hw = Hw;
        if (hw == null)
        {
            return BatteryAlignResult.NotApplicable;
        }
        // 没有 GCU 通道时不要去 publish（否则每次点档位都记一条"MQTT 未连接"的假失败）。
        if (!hw.IsConnected)
        {
            Logger.WriteLine("[battery-gcu] 档位对齐跳过：本机无 GCU 通道（EC 本地路由），档位已由 EC 写入 · mode=" + mode);
            return BatteryAlignResult.NotApplicable;
        }
        try
        {
            bool ok = await hw.SetBatteryProtection(mode);
            Logger.WriteError("[battery-gcu] 后端档位同步 mode=" + mode + " ⇒ " + ok
                + " · 后端上报=" + hw.BatteryProtection);
            return ok ? BatteryAlignResult.Aligned : BatteryAlignResult.Failed;
        }
        catch (Exception ex)
        {
            Logger.WriteError("[battery-gcu] 后端档位同步失败：" + ex.Message);
            return BatteryAlignResult.Failed;
        }
    }

    /// <summary>
    /// 用户选完档位后盯一段时间：EC 档位位被后端写回就再写一次。
    /// <para>⭐ 只盯 attempts×intervalMs，不无限刷写；每次结果都写日志便于定位。</para>
    /// </summary>
    internal static void StartChargeModeWatch(int mode, int attempts = 10, int intervalMs = 3000)
    {
        _ = Task.Run(async () =>
        {
            int rewrites = 0;
            for (int i = 0; i < attempts; i++)
            {
                await Task.Delay(intervalMs).ConfigureAwait(false);
                bool kept = EcChargeLimitOverride.TryReassertMode(mode, 0, out string detail);
                if (!kept)
                {
                    rewrites++;
                    Logger.WriteError("[battery-gcu] 第 " + (i + 1) + " 次复查被写回 ⇒ 已重写：" + detail);
                }
            }
            Logger.WriteError("[battery-gcu] 档位监视结束 mode=" + mode + " · 重写次数=" + rewrites);
        });
    }

    /// <summary>按百分比算 0x7A6 模式位（日志与复查用）。</summary>
    internal static int EcChargeModeBits(int percent) => EcChargeLimitOverride.ComputeModeBits(percent);

    /// <summary>GPU 功耗可调范围（界面只读这三项）。</summary>
    internal readonly record struct GpuTgpRange(int FloorWatts, int BaseMaxWatts, int DbMaxWatts);

    internal static bool IsAmdCpu() => CpuVendor.IsAmd;

    internal static bool IsIntelCpu() => CpuVendor.IsIntel;

    internal static bool UsesAmdPowerFields() => Hw?.UsesAmdPowerFields == true;

    internal static bool LocalPowerConfig() => GcuEcExperimental.LocalPowerConfig;

    /// <summary>启动恢复用：按当前后端建一个水冷路由实例（不注册到窗口，用完即弃）。</summary>
    internal static WaterCoolerRoutingService? CreateWaterCoolerRouting()
    {
        try
        {
            MechrevoHw? hw = Hw;
            return hw == null ? null : new WaterCoolerRoutingService(hw, Service);
        }
        catch (Exception ex)
        {
            Logger.WriteError("[water-cooler] 建路由实例失败：" + ex.Message);
            return null;
        }
    }

    internal static bool SmuReady() => SmuAvailability.IsReady;

    internal static bool TongfangFamily() => MachineIdentity.Collect().TongfangFamily;

    internal static string BoardProduct() => BoardWhitelist.ReadBoardProduct();

    /// <summary>SMU 自定义功耗的滑块边界（PL 最小值/最大值、温度墙最小/最大值）。</summary>
    internal static (int PlMin, int PlMax, int TccMin, int TccMax) SmuLimits() =>
        (WujieSmuGate.PlMinWatts, WujieSmuGate.PlMaxWatts, WujieSmuGate.TccMinCelsius, WujieSmuGate.TccMaxCelsius);

    internal static bool EcPl4Supported() => EcPl4Unit.IsSupported;

    internal static bool EcPl4TryRead(out bool doubleUnit) => EcPl4Unit.TryRead(out doubleUnit, out _);

    internal static bool EcPl4TryRead(out bool doubleUnit, out byte raw) => EcPl4Unit.TryRead(out doubleUnit, out raw);

    internal static bool EcPl4TryReadWatts(out int watts, out byte raw, out bool doubleUnit) =>
        EcPl4Unit.TryReadPl4(out watts, out raw, out doubleUnit);

    internal static bool EcPl4TrySetDouble(bool doubleUnit, out string detail) =>
        EcPl4Unit.TrySetDouble(doubleUnit, out detail);

    internal static void ClearPl4UnitOverride() => HwOrProgram?.ClearPl4UnitOverride();

    /// <summary>GPU TGP/动态加速的可调范围（读不到返回 false，界面保持原范围）。</summary>
    internal static bool TryGetGpuTgpRange(out GpuTgpRange range) => TryGetGpuTgpRange(Hw, out range);

    internal static bool TryGetGpuTgpRange(MechrevoHw? hw, out GpuTgpRange range)
    {
        range = default;
        if (!EcGpuPower.TryGetCapabilities(hw, out EcGpuPower.Capabilities caps, out _))
        {
            return false;
        }
        range = new GpuTgpRange(caps.FloorWatts, caps.BaseMaxWatts, caps.DbMaxWatts);
        return true;
    }

    internal static bool EcGpuPowerTryApply(int baseTgp, int dbWatts, bool dbOn, out string detail)
    {
        detail = "未提供硬件上下文";
        MechrevoHw? hw = Hw;
        return hw != null && EcGpuPower.TryApply(hw, baseTgp, dbWatts, dbOn, out detail);
    }

    internal static bool NucFanTableTrySetFanRamp(bool enabled, int speedMs, out string detail) =>
        NucFanTable.TrySetFanRamp(enabled, speedMs, out detail);

    internal static bool NucFanTableTryApply(byte[] cpuTemps, int[] cpuDuties, byte[] gpuTemps, int[] gpuDuties, out string detail) =>
        NucFanTable.TryApply(cpuTemps, cpuDuties, gpuTemps, gpuDuties, out detail);

    /// <summary>无界 EC 表自己的温度轴（不是同方那套 46..80）。</summary>
    internal static byte[] WujieFanDefaultTemperatures() => WujieFanEcController.DefaultTemperatures();

    internal static bool NucFanTableTrySetFanIsolated(bool enabled, out string detail) =>
        NucFanTable.TrySetFanIsolated(enabled, out detail);

    internal static bool NucPerformanceModeTryApplyCustom(int? pl1, int? pl2, int? pl4, int? tcc, out string detail) =>
        NucPerformanceMode.TryApplyCustom(pl1, pl2, pl4, tcc, out detail);

    internal static bool GcuTryApplyMode(int visualMode, out string detail) =>
        GcuEcExperimental.TryApplyMode(visualMode, out detail);

    /// <summary>EC 模式是否在用（与 EcModeActive 同源，取后端判据）。</summary>
    internal static bool GcuActive => GcuEcExperimental.Enabled;

    /// <summary>该机型是否支持「模式 × 配置」绑定。</summary>
    internal static bool ModeProfileBindingSupported => Hw?.SupportsModeProfileConfig ?? false;

    internal static bool GcuDirectMode => GcuEcExperimental.DirectMode;

    internal static bool GcuTryReadVisualMode(out int visualMode) =>
        GcuEcExperimental.TryReadVisualMode(out visualMode);

    internal static bool GcuTrySetSilentTurbo(bool silent, out string detail) =>
        GcuEcExperimental.TrySetSilentTurbo(silent, out detail);

    internal static bool GcuSelfWroteRecently(int ms) => GcuEcExperimental.SelfWroteRecently(ms);

    internal static bool GcuServiceProcessRunning() => GcuEcExperimental.GcuServiceProcessRunning();

    internal static void GcuLogCurrentState(string reason) => GcuEcExperimental.LogCurrentState(reason);

    internal static bool GcuTryEnableHotkeyNotify() => GcuEcExperimental.TryEnableHotkeyNotify();

    /// <summary>读工厂 Turbo 的 PL1/PL2（EC 0x7A7/0x7A8 语义，单位 W）。</summary>
    internal static bool TryReadTurboPl1Pl2(out int pl1, out int pl2)
    {
        pl1 = 0;
        pl2 = 0;
        if (!AcpiDriverEc.TryReadByte(1959, out byte pl1Raw)
            || !AcpiDriverEc.TryReadByte(1960, out byte pl2Raw))
        {
            return false;
        }
        pl1 = pl1Raw;
        pl2 = pl2Raw;
        return true;
    }

    /// <summary>读工厂 Turbo 的 PL4/fPPT 原始值（单位由 EcPl4Unit 决定）。</summary>
    internal static bool TryReadTurboPl4(out byte raw) => AcpiDriverEc.TryReadByte(1961, out raw);

    /// <summary>自定义功耗前先落模式字节；实现在硬件层。</summary>
    internal static bool TrySetCustomModeGate()
        => MechrevoLite.Hardware.GcuEcExperimental.TrySetCustomModeGate();

    internal static List<(string Guid, string Name)> WinPowerPlanPlans() => WinPowerPlan.GetPlans();

    internal static bool WinPowerPlanSetActive(string guid) => WinPowerPlan.SetActivePlan(guid);

    /// <summary>EC 键盘背光当前状态（是否点亮 + 档位）。</summary>
    internal static bool EcKeyboardTryGet(out bool on, out int level) => EcKeyboardLight.TryGet(out on, out level);

    /// <summary>键盘是否为单区 RGB（只有轮播有速度概念）。</summary>
    internal static bool IsSingleZoneRgbKeyboard()
    {
        MechrevoHw? hw = Hw;
        return hw != null && KeyboardFirmwareEffects.IsSingleZoneRgb(hw.Capabilities.KeyboardType);
    }

    /// <summary>UMI/NUC 灯带走本地 ACPI 通道（与 GCU 服务通道不同）。</summary>
    internal static bool NucLightbarSetPower(bool on) => UmiLightbar.SetPower(on);

    internal static bool NucLightbarSetEffect(string effect, System.Drawing.Color color, int level) =>
        UmiLightbar.SetEffect(effect, color, level);

    /// <summary>本地软件色彩增强：按 9 个参数下发（无 GCU 档位表时的回退通道）。</summary>
    internal static void ApplyColorProfile(string name, int brightness, int colorTemp,
        float red, float green, float blue, float vibrant, float contrast, float gamma) =>
        SoftwareColorEnhancement.ApplyProfile(name, brightness, colorTemp, red, green, blue, vibrant, contrast, gamma);

    internal static bool LocalFanCurve() => GcuEcExperimental.LocalFanCurve;

    /// <summary>风扇曲线能力：门控优先，未声明回退旧判据。</summary>
    internal static bool ResolveFanCurveSupport(bool legacySupported) =>
        FeatureGate.ResolveSupportOrLegacy(FeatureId.FanCurve, Hw, legacySupported);


    /// <summary>键盘灯能力：门控声明优先，未声明回退调用方给的旧判据。</summary>
    internal static bool KeyboardLightSupported(bool legacySupported) =>
        FeatureGate.ResolveSupportOrLegacy(FeatureId.KeyboardLight, HwOrProgram, legacySupported);

    /// <summary>灯带能力：门控声明优先，未声明回退调用方给的旧判据。</summary>
    internal static bool LightbarSupported(bool legacySupported) =>
        FeatureGate.ResolveSupportOrLegacy(FeatureId.Lightbar, HwOrProgram, legacySupported);
    internal static Task<(bool Ok, string Detail)> SetEcModeAsync(bool ecMode) => GcuEcMode.SetModeAsync(ecMode);

    /// <summary>
    /// 把本地通道证据接进门控：后端声明 Unknown 时，本地 HID/EC 通道可用即视为支持。
    /// <para>⚠️ 只在**确实探到**时返回 true；探不到一律 null（未知），交给旧判据，避免误否。</para>
    /// <summary>
    private static bool KeyboardLightLocalEvidence() => IteLightingLocal.HasLightbar || EcKeyboardActiveNow;
    /// <summary>崩溃记录去重：签名、窗口判定、轮转判定（界面层不直接引用硬件层类型）。</summary>
    internal static string CrashSignature(string source, string exceptionType, string firstFrame) =>
        CrashLogPolicy.Signature(source, exceptionType, firstFrame);

    internal static string CrashFirstFrame(string? stack) => CrashLogPolicy.FirstFrame(stack);

    internal static bool CrashShouldWriteFull(DateTime nowUtc, DateTime? lastUtc) =>
        CrashLogPolicy.ShouldWriteFull(nowUtc, lastUtc);

    internal static bool CrashShouldRotate(long bytes) => CrashLogPolicy.ShouldRotate(bytes);

    /// <summary>风扇环形填充比例：以占空比为准，未知返回 null（交给调用方退回转速比例）。</summary>
    internal static double? FanRingFractionFromDuty(int dutyPercent) => FanRingFraction.FromDuty(dutyPercent);


    /// <summary>按特征名读门控；legacy 用"能力位名字"给出，由桥内部取值（界面不直接读能力位）。</summary>
    /// <summary>读一个能力位（全局实例）。界面层不直接读能力位，统一从这里取。</summary>
    internal static bool CapabilityFlag(string capability)
    {
        return CapabilityFlag(HwOrProgram?.Capabilities, capability);
    }

    /// <summary>读一个能力位（调用方给的实例）。</summary>

    internal static bool CapabilityFlag(MechrevoDeviceCapabilities? caps, string capability)
    {
        if (caps == null)
        {
            return false;
        }
        return capability switch
        {
            "ColorCalibration" => caps.ColorCalibration,
            "LocalDimming" => caps.LocalDimming,
            "LiquidCooling" => caps.LiquidCooling,
            "DgpuDirect" => caps.DgpuDirect,
            "IgpuOnly" => caps.IgpuOnly,
            "GpuHotSwap" => caps.GpuHotSwap,
            "Keyboard" => caps.Keyboard,
            "Lightbar" => caps.Lightbar,
            "LogoLight" => caps.LogoLight,
            "FanSettings" => caps.FanSettings,
            "CpuPerformanceTuning" => caps.CpuPerformanceTuning,
            "ProfileAvailable" => caps.ProfileAvailable,
            "TurboMode" => caps.TurboMode,
            "OverclockSettings" => caps.OverclockSettings == true,
            _ => false
        };
    }

    /// <summary>校色等级（缺省按 1 处理）。</summary>
    internal static int ColorCalibrationLevel()
    {
        try
        {
            return HwOrProgram?.Capabilities?.ColorCalibrationLevel ?? 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>静音↔狂暴三态（调用方给的实例）：1=支持 · 0=不支持 · 2=未知。</summary>
    internal static int SilentTurboStateOf(MechrevoDeviceCapabilities? caps)
    {
        if (caps == null)
        {
            return 2;
        }
        return caps.SilentTurboAvailability == FeatureSupported ? 1
            : caps.SilentTurboAvailability == FeatureUnsupported ? 0 : 2;
    }

    internal static bool SupportsFeatureWithCapability(string feature, string capability)
    {
        bool legacy = false;
        try
        {
            MechrevoDeviceCapabilities? caps = HwOrProgram?.Capabilities;
            if (caps != null)
            {
                legacy = capability switch
                {
                    "CpuPerformanceTuning" => caps.CpuPerformanceTuning,
                    "FanSettings" => caps.FanSettings,
                    "ProfileAvailable" => caps.ProfileAvailable,
                    "TurboMode" => caps.TurboMode,
                    "SilentTurboAvailability" => caps.SilentTurboAvailability == FeatureSupported,
                    "DgpuDirect" => caps.DgpuDirect,
                    "GpuHotSwap" => caps.GpuHotSwap,
                    "IgpuOnly" => caps.IgpuOnly,
                    "ColorCalibration" => caps.ColorCalibration,
                    "LocalDimming" => caps.LocalDimming,
                    "LiquidCooling" => caps.LiquidCooling,
                    "Keyboard" => caps.Keyboard,
                    "Lightbar" => caps.Lightbar,
                    "LogoLight" => caps.LogoLight,
                    "OverclockSettings" => caps.OverclockSettings == true,
                    _ => false
                };
            }
        }
        catch
        {
        }
        return SupportsFeature(feature, legacy);
    }

    /// <summary>静音↔狂暴三态：1=支持 · 0=不支持 · 2=未知（供界面选择分支，不读能力位）。</summary>
    internal static int SilentTurboState
    {
        get
        {
            try
            {
                MechrevoDeviceCapabilities? caps = HwOrProgram?.Capabilities;
                if (caps == null)
                {
                    return 2;
                }
                return caps.SilentTurboAvailability == FeatureSupported ? 1
                    : caps.SilentTurboAvailability == FeatureUnsupported ? 0 : 2;
            }
            catch
            {
                return 2;
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> GateLogged = new();

    /// <summary>每个特征只记一次判定，便于解释"为什么这个区块显示/隐藏"。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> GateStateLogged = new();

    /// <summary>三态读取也留一行：只认门控的位置出问题时，诊断包里要有线索。</summary>
    private static void LogGateState(string feature,
        MechrevoLite.Hardware.Architecture.FeatureId id,
        MechrevoLite.Hardware.Architecture.FeatureDecision decision)
    {
        if (!GateStateLogged.TryAdd(feature + "|state", 0))
        {
            return;
        }
        try
        {
            Logger.WriteLine("[gate] f=" + feature + " backend=" + decision.Backend
                + " availability=" + decision.State + " channel=" + decision.Channel
                + " → " + (decision.State == MechrevoLite.Hardware.FeatureAvailability.Supported ? "supported" : "hidden")
                + " · 只认门控 · " + decision.Reason);
        }
        catch
        {
        }
    }

    private static void LogGateOnce(string feature, MechrevoHw hw, MechrevoLite.Hardware.Architecture.FeatureId id,
        bool effective, bool legacy)
    {
        if (!GateLogged.TryAdd(feature, 0))
        {
            return;
        }
        try
        {
            MechrevoLite.Hardware.Architecture.FeatureDecision decision =
                MechrevoLite.Hardware.Architecture.FeatureGate.Get(id, hw);
            Logger.WriteLine("[gate] f=" + feature + " backend=" + decision.Backend
                + " availability=" + decision.State + " channel=" + decision.Channel
                + " → " + (effective ? "supported" : "hidden") + " · legacy=" + legacy
                + " · " + decision.Reason);
        }
        catch
        {
        }
    }

    /// <summary>按特征名读门控结论；后端未就绪或名字未知时退回调用方给的 legacy 判定。</summary>
    /// <summary>门控三态：1=Supported · 0=Unsupported · 2=Unknown（含名字未知、后端未就绪）。</summary>
    internal static int FeatureState(string feature)
    {
        try
        {
            MechrevoHw? hw = HwOrProgram;
            MechrevoLite.Hardware.Architecture.FeatureId? id = MapFeature(feature);
            if (hw == null || id == null)
            {
                return 2;
            }
            MechrevoLite.Hardware.Architecture.FeatureDecision decision =
                MechrevoLite.Hardware.Architecture.FeatureGate.Get(id.Value, hw);
            LogGateState(feature, id.Value, decision);
            return decision.State switch
            {
                MechrevoLite.Hardware.FeatureAvailability.Supported => 1,
                MechrevoLite.Hardware.FeatureAvailability.Unsupported => 0,
                _ => 2
            };
        }
        catch
        {
            return 2;
        }
    }

    /// <summary>门控优先、只在 Unknown 时用调用方的硬件事实。</summary>
    internal static bool FeatureOrHardware(string feature, bool hardwareValue)
    {
        int state = FeatureState(feature);
        return state == 2 ? hardwareValue : state == 1;
    }

    /// <summary>特征名 → FeatureId（界面只传字符串名，不引用硬件层类型）。</summary>
    private static MechrevoLite.Hardware.Architecture.FeatureId? MapFeature(string feature)
    {
        const string N = "MechrevoLite.Hardware.Architecture.FeatureId.";
        return feature switch
        {
            "LogoLight" => MechrevoLite.Hardware.Architecture.FeatureId.LogoLight,
            "Lightbar" => MechrevoLite.Hardware.Architecture.FeatureId.Lightbar,
            "KeyboardLight" => MechrevoLite.Hardware.Architecture.FeatureId.KeyboardLight,
            "HingeLight" => MechrevoLite.Hardware.Architecture.FeatureId.HingeLight,
            "SyncLight" => MechrevoLite.Hardware.Architecture.FeatureId.SyncLight,
            "LocalDimming" => MechrevoLite.Hardware.Architecture.FeatureId.LocalDimming,
            "LcdOverdrive" => MechrevoLite.Hardware.Architecture.FeatureId.LcdOverdrive,
            "DisplayEffect" => MechrevoLite.Hardware.Architecture.FeatureId.DisplayEffect,
            "ColorCalibration" => MechrevoLite.Hardware.Architecture.FeatureId.ColorCalibration,
            "FanCurve" => MechrevoLite.Hardware.Architecture.FeatureId.FanCurve,
            "FanBoost" => MechrevoLite.Hardware.Architecture.FeatureId.FanBoost,
            "ChargeLimit" => MechrevoLite.Hardware.Architecture.FeatureId.ChargeLimit,
            "PerformanceMode" => MechrevoLite.Hardware.Architecture.FeatureId.PerformanceMode,
            "RefreshRate" => MechrevoLite.Hardware.Architecture.FeatureId.RefreshRate,
            "GpuModeSwitch" => MechrevoLite.Hardware.Architecture.FeatureId.GpuModeSwitch,
            "GpuHotSwap" => MechrevoLite.Hardware.Architecture.FeatureId.GpuHotSwap,
            "IgpuOnly" => MechrevoLite.Hardware.Architecture.FeatureId.IgpuOnly,
            "PerformanceProfile" => MechrevoLite.Hardware.Architecture.FeatureId.PerformanceProfile,
            "TurboMode" => MechrevoLite.Hardware.Architecture.FeatureId.TurboMode,
            "SilentTurbo" => MechrevoLite.Hardware.Architecture.FeatureId.SilentTurbo,
            "PerfTuning" => MechrevoLite.Hardware.Architecture.FeatureId.PerfTuning,
            _ => (MechrevoLite.Hardware.Architecture.FeatureId?)null
        };
    }

    internal static bool SupportsFeature(string feature, bool legacy)
    {
        try
        {
            MechrevoHw? hw = HwOrProgram;
            if (hw == null)
            {
                return legacy;
            }
            MechrevoLite.Hardware.Architecture.FeatureId? id = MapFeature(feature);
            if (id == null)
            {
                // 名字写错会静默回退 legacy，这里留一行便于发现（同一名字只记一次）。
                Logger.WriteErrorIfChanged("gate-unknown-" + feature, "[gate] 未映射的特征名：" + feature);
                return legacy;
            }
            bool supported = MechrevoLite.Hardware.Architecture.FeatureGate.ResolveSupportOrLegacy(id.Value, hw, legacy);
            LogGateOnce(feature, hw, id.Value, supported, legacy);
            return supported;
        }
        catch
        {
            return legacy;
        }
    }

    /// <summary>是否有摄像头设备（有缓存读缓存，否则探测一次并缓存）。</summary>
    internal static bool HasCamera
    {
        get
        {
            try
            {
                return MechrevoLite.Hardware.CameraPnpControl.HasCameraCached
                    || MechrevoLite.Hardware.CameraPnpControl.HasCamera;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>后台预热摄像头设备探测，避免首次打开快捷开关列表时现枚举。</summary>
    internal static bool WarmupCameraProbe()
    {
        try
        {
            return MechrevoLite.Hardware.CameraPnpControl.ProbeHasCamera();
        }
        catch
        {
            return false;
        }
    }
    internal static void AttachLocalChannelEvidence()
    {
        FeatureGate.LocalChannelEvidenceProvider = feature =>
        {
            try
            {
                return feature switch
                {
                    FeatureId.Lightbar => IteHasLightbar || LocalLightbarSupported ? true : null,
                    FeatureId.LogoLight => IteHasUsbLogo || LocalLogoSupported ? true : null,
                    FeatureId.KeyboardLight => (EcKeyboardActiveNow || KeyboardLightLocalEvidence()) ? true : null,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        };
    }

    internal static int PowerInputKindBarrel => (int)PowerInputKind.Barrel;

    internal static int PowerInputKindTypeC => (int)PowerInputKind.TypeC;

    internal static int PowerInputKindBarrelAndTypeC => (int)PowerInputKind.BarrelAndTypeC;

    internal static int PowerInputKindExternalUnknown => (int)PowerInputKind.ExternalUnknown;

    /// <summary>供电输入类别 + 适配器瓦数 + BIOS 原始字节（-1 = 读不到）。</summary>
    internal static (int Kind, bool HasSample, int Watts, int BiosRaw) DescribePowerInput(bool acOnline)
    {
        PowerStatusSnapshot power = PowerStatusService.Read();
        PowerInputSample? sample = power.Input;
        PowerInputKind kind = PowerInputDecoder.Reconcile(sample, acOnline);
        return ((int)kind, sample != null, sample?.AdapterWatts ?? -1, sample?.BiosInfo3Raw ?? -1);
    }

    /// <summary>当前后端硬件对象（后端桥 + 老程序集兜底），界面只当不透明句柄用。</summary>
    internal static MechrevoHw? HwOrProgram => Hw ?? MechrevoLite.Program.hw;

    internal static bool IsGcuServiceInstalled => MechrevoHw.IsGcuServiceInstalled;

    /// <summary>键盘档位上限（宝龙达有效上限与常规 4 档的取舍由 Core 决定）。</summary>
    internal static int KeyboardBrightnessMax() => MechrevoHw.EffectiveKeyboardBrightnessMax();

    /// <summary>EC 键盘通道的判据与下发（与 GCU 通道相互独立）。</summary>
    internal static bool EcKbdActive => GcuEcKeyboard.Active;

    internal static bool EcKbdIsSingleZone => GcuEcKeyboard.IsEcSingleZone;

    internal static bool EcKbdTryReadPower(out bool on) => GcuEcKeyboard.TryReadPower(out on);

    internal static bool EcKbdIsWhite => GcuEcKeyboard.IsEcWhite;

    /// <summary>该机型是否显示风扇占空比（读不到的机型隐藏，不显示假 0%）。</summary>
    internal static bool FanDutyVisible() => !MechrevoLite.Hardware.DeclarationRuntime.Flag("fanTelemetry", "hideDuty");

    /// <summary>该机型是否显示风扇转速。</summary>
    internal static bool FanRpmVisible() => !MechrevoLite.Hardware.DeclarationRuntime.Flag("fanTelemetry", "hideRpm");

    /// <summary>该机型是否可以改风扇曲线（判定在后端能力，界面只读结果）。</summary>
    internal static bool FanCurveSupported()
    {
        MechrevoHw? hw = HwOrProgram;
        return hw != null && MechrevoLite.Mode.PerfModeBackend.CapabilitiesFrom(hw).SupportsFanCurve;
    }

    /// <summary>该机型是否隐藏亮度调节（只有开/关）。</summary>
    internal static bool EcKbdBrightnessHidden => MechrevoLite.Hardware.EcKeyboardLight.BrightnessHidden;

    /// <summary>亮度键由固件自管（声明 hotkey=0）：界面只显示真实档位，不回写 EC。</summary>
    internal static bool EcKbdHotkeyByFirmware => MechrevoLite.Hardware.EcKeyboardLight.HotkeyByFirmware;

    internal static bool EcKbdIsHid => GcuEcKeyboard.IsHid;

    internal static bool EcKbdControllable => GcuEcKeyboard.HasControllableKeyboard;

    internal static bool EcKbdApplyWhite(bool on, int level) => GcuEcKeyboard.ApplyWhite(on, level);

    internal static bool NucKbdTryReadBrightness(out int level) => NucKeyboardLighting.TryReadBrightness(out level);

    /// <summary>触发官方迎宾灯效（仅 GCU 键盘支持，无服务时返回 false）。</summary>
    internal static Task<bool> TriggerWelcomeAsync(string trigger)
    {
        MechrevoService? service = Service;
        return service == null ? Task.FromResult(false) : service.TriggerWelcomeAsync(trigger);
    }

    /// <summary>灯光通道设置的读写（界面不再直接触碰存档层）。</summary>
    internal static LightChannelSettings LoadLightChannelSettings(string topic, string defaultEffect) =>
        LightingSettingsStore.Load(topic, defaultEffect);

    internal static void SaveLightChannelSettings(string topic, LightChannelSettings settings) =>
        LightingSettingsStore.Save(topic, settings);

    internal static void SaveLightChannelPower(string topic, bool powerOn, string trigger) =>
        LightingSettingsStore.SavePower(topic, powerOn, trigger);

    internal static bool NucKbdIsSingleZone() => NucKeyboardLighting.IsSingleZone();

    /// <summary>单区软件轮播的档位上限（1 = 最慢、N = 最快）。</summary>
    internal static int NucKbdSpeedLevels => NucKeyboardLighting.SpeedLevels;

    internal static void NucKbdApply(string effect, bool on, int brightness, int speed, int colorArgb, int[] cycleColors) =>
        NucKeyboardLighting.Apply(effect, on, brightness, speed, colorArgb, cycleColors);

    /// <summary>风扇读数新鲜度阈值。</summary>
    internal static TimeSpan FanInfoMaxAgeMs => MechrevoHw.FanInfoMaximumAge;

    /// <summary>离线后端对象（GCU 停掉时撑起 EC-only 首页）。</summary>
    internal static MechrevoHw CreateOfflineHw() => new();

    internal static void AttachOfflineHw(MechrevoHw hw) => MechrevoLite.HardwareControl.AttachMechrevoHw(hw);

    /// <summary>单键 RGB 帧流对象：已存在就直接复用，否则按配置新建（不连接）。</summary>
    internal static KeyboardRgb? EnsurePerKeyRgbConfig()
    {
        if (_rgb != null)
        {
            return _rgb;
        }
        try
        {
            KeyboardRgb created = new();
            created.LoadConfig();
            _rgb = created;
            return created;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>单键 RGB 帧流对象：已存在但未连接时补一次连接。</summary>
    internal static KeyboardRgb? EnsurePerKeyRgb()
    {
        if (_rgb != null)
        {
            if (!_rgb.IsConnected)
            {
                try
                {
                    _rgb.Connect();
                }
                catch
                {
                }
            }
            return _rgb;
        }
        try
        {
            KeyboardRgb created = new();
            created.LoadConfig();
            created.Connect();
            _rgb = created;
            return created;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>无原厂回读的机型靠全局按键状态判断"是否有键入"。</summary>
    internal static void SetPerKeyKeyDownProvider(Func<int, bool>? provider) => KeyboardRgb.BuiltInKeyDownProvider = provider;

    /// <summary>GPU 超频开关（GCU 侧持久状态）。</summary>
    internal static bool GpuOverclockEnabled
    {
        get => MechrevoService.GpuOverclockEnabled;
        set => MechrevoService.GpuOverclockEnabled = value;
    }

    /// <summary>显卡超频开关的"用户意图"记忆（硬件层落盘 gpu_oc；只在狂暴档生效）。</summary>
    internal static bool GpuOverclockIntentOn => MechrevoService.GpuOcIntentOn;

    /// <summary>档位意图（存储层记忆的用户选择；界面只读它做重放判据，不直接碰档位存储）。</summary>
    internal static string? PerfBootIntentModeId => MechrevoLite.Mode.PerfModeStore.BootIntentId;

    internal static MechrevoLite.Mode.PerfModeDefinition? PerfFindMode(string? id)
        => MechrevoLite.Mode.PerfModeService.Instance?.Find(id);

    internal static bool IsGcu30Variant => MechrevoService.IsGcu30;

    /// <summary>模式切换结果的三种取值（界面用整数比较，不再引用后端的枚举类型）。</summary>
    internal static int ModeResultOk => (int)MechrevoService.ModeSwitchResult.Ok;

    internal static int ModeResultFailed => (int)MechrevoService.ModeSwitchResult.Failed;

    internal static int ModeResultSuperseded => (int)MechrevoService.ModeSwitchResult.Superseded;

    /// <summary>挂硬件状态事件（灯效状态、性能模式），窗口只收回调。</summary>
    internal static void HookHwStateChanged(Action<string> onLightingState, Action<int> onMode)
    {
        MechrevoHw? hw = HwOrProgram;
        if (hw == null)
        {
            return;
        }
        hw.StateChanged += onLightingState;
        hw.ModeChanged += onMode;
    }

    internal static void UnhookHwStateChanged(object? hw, Action<string> onLightingState, Action<int> onMode)
    {
        if (hw is not MechrevoHw typed)
        {
            return;
        }
        try
        {
            typed.StateChanged -= onLightingState;
            typed.ModeChanged -= onMode;
        }
        catch
        {
        }
    }

    // ── 充电门控（界面只读状态 + 下发释放意图）─────────────────────────────
    /// <summary>充电门控是否开启。</summary>
    internal static bool ChargeGateEnabled => MechrevoLite.Hardware.ChargeGatePin.Enabled;
    /// <summary>充电门控的配置键（界面写开关用）。</summary>
    internal static string ChargeGateConfigKey => MechrevoLite.Hardware.ChargeGatePin.ConfigKey;
    /// <summary>立即释放充电门控。</summary>
    internal static void ChargeGateReleaseNow() => MechrevoLite.Hardware.ChargeGatePin.TryReleaseNow();

    // ── 限充恢复存档（界面只交数值，落盘与判据在后端）─────────────────────
    /// <summary>保存限充恢复配置。</summary>
    internal static void SaveBatteryLimitRestore(bool enabled, int low, int high) =>
        MechrevoLite.Hardware.BatteryLimitRestore.SaveConfig(enabled, low, high);
    /// <summary>限充「进行中」模式值。</summary>
    internal const int BatteryLimitModeActive = MechrevoLite.Hardware.BatteryLimitWriter.ModeActive;

    // ── 电源事件策略（唤醒恢复去重）──────────────────────────────────────
    /// <summary>WM_POWERBROADCAST 消息号。</summary>
    internal const long WmPowerBroadcast = MechrevoLite.Hardware.PowerBroadcastPolicy.WmPowerBroadcast;
    /// <summary>是否唤醒类消息。</summary>
    internal static bool IsResumeMessage(long msg, long wParam) =>
        MechrevoLite.Hardware.PowerBroadcastPolicy.IsResume(msg, wParam);
    /// <summary>唤醒恢复是否该处理（去重窗口）。</summary>
    internal static bool ShouldHandleResume(long nowTick, long lastHandledTick, long dedupeMs) =>
        MechrevoLite.Hardware.PowerBroadcastPolicy.ShouldHandle(nowTick, lastHandledTick, dedupeMs);

    // ── 退出/启动侧只读记录 ─────────────────────────────────────────────
    /// <summary>记录一次只读机型探查结论。</summary>
    internal static void LogMachineProbeOnce() => MechrevoLite.Hardware.MachineProbeReadOnly.LogOnce();
    /// <summary>标记 CPU 偏移干净退出。</summary>
    internal static void MarkCpuCoCleanExit(string reason) =>
        MechrevoLite.Hardware.CpuCoAutoApplyGuard.MarkCleanExit(reason);
    /// <summary>标记 GPU OC 干净退出。</summary>
    internal static void MarkGpuOcCleanExit(string reason) =>
        MechrevoLite.Hardware.GpuOcShutdownGuard.MarkCleanExit(reason);

    // ── 平台事实 ────────────────────────────────────────────────────────
    /// <summary>CPU 是否缺少可用核显（设置页判据）。</summary>
    internal static bool CpuLacksIgpuSilicon => MechrevoLite.Hardware.CpuVendor.LacksIgpuSilicon;
    /// <summary>PawnIO 安装状态。</summary>
    internal static (bool Installed, string Version) PawnIoStatus()
    {
        MechrevoLite.Hardware.PawnIOInstaller.Status status = MechrevoLite.Hardware.PawnIOInstaller.GetStatus();
        return (status.Installed, status.Version);
    }
    /// <summary>运行 PawnIO 安装程序。</summary>
    internal static Task<int> PawnIoInstallAsync() => MechrevoLite.Hardware.PawnIOInstaller.RunInstallerAsync();

}

