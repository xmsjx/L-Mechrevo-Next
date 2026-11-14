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

    // ── 机型/能力枚举与零散硬件事实（界面层只经这里读） ──

    internal const FeatureAvailability FeatureSupported = FeatureAvailability.Supported;
    internal const FeatureAvailability FeatureUnsupported = FeatureAvailability.Unsupported;
    internal const FeatureAvailability FeatureUnknown = FeatureAvailability.Unknown;

    /// <summary>是否属同方家族（只读事实）。</summary>
    internal static bool IsTongfangFamily => MachineIdentity.Collect().TongfangFamily;

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

    /// <summary>电源来源标题（功率表命中 / EC 原始码 / 充放功率兜底）；无结论返回 null。</summary>
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
        decimal? rate = PowerStatusService.Read().BatteryRateWatts;
        if (rate.HasValue)
        {
            return string.Format(WpfStrings.DisplayPowerEcWattFormat, rate.Value.ToString("0.0"));
        }
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
    internal static KeyboardRgb CreateKeyboardRgb() => new KeyboardRgb();

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

    /// <summary>按百分比算 0x7A6 模式位（日志与复查用）。</summary>
    internal static int EcChargeModeBits(int percent) => EcChargeLimitOverride.ComputeModeBits(percent);
}

