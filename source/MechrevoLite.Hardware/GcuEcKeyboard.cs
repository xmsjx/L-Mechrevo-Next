using System;
using System.Collections.Generic;

namespace MechrevoLite.Hardware;

/// <summary>EC 模式下的键盘灯类型。</summary>
internal enum GcuEcKeyboardKind
{
    None,
    HidPerKey,
    HidFourZone,
    EcSingleZone,
    EcWhite
}

/// <summary>
/// 同方 GCU 机型 EC 模式（GCU 离线）的键盘灯分流。
/// 判定顺序：HID 单键 → HID 四分区 → EC 单区（0x766 bit2）→ EC 白光 → 无。
/// <para>只有 EC 首页启动后（<see cref="MarkActive"/>）才生效；GCU 路由下 <see cref="Active"/> 恒为 false，
/// 所有调用方都先判 Active，GCU 的键盘灯逻辑不受影响。</para>
/// <para>HID 单键与 GCU 模式共用 KeyboardRgb 路径（本来就不经过 GCU）。</para>
/// </summary>
internal static class GcuEcKeyboard
{
    private const ushort IteVendorId = 0x048D;
    private const ushort FourZoneProductId = 0xCE00;
    private const ushort FourZoneUsagePage = 0xFF12;
    private const ushort LightbarProductId = 0x7001;
    private const ushort HingeProductId = 0x7000;
    private const int AddressTriggerByte = 1895; // 0x767
    private const byte TriggerLatch = 0x20;
    private const string Topic = "Keyboard/Ctrl";

    private static readonly object Gate = new();
    private static volatile bool _active;
    private static GcuEcKeyboardKind _kind = GcuEcKeyboardKind.None;

    /// <summary>EC 首页已接管（GCU 离线）。</summary>
    internal static bool Active => _active && GcuEcExperimental.Enabled;

    internal static GcuEcKeyboardKind Kind => _kind;

    internal static bool IsHid => Active && _kind == GcuEcKeyboardKind.HidPerKey;

    internal static bool IsEcSingleZone => Active && _kind == GcuEcKeyboardKind.EcSingleZone;

    internal static bool IsEcWhite => Active && _kind == GcuEcKeyboardKind.EcWhite;

    /// <summary>EC 模式下本程序能控制的键盘（HID 单键 / EC 单区 / EC 白光）。四分区 HID 暂无本地协议。</summary>
    internal static bool HasControllableKeyboard => Active
        && _kind is GcuEcKeyboardKind.HidPerKey or GcuEcKeyboardKind.EcSingleZone or GcuEcKeyboardKind.EcWhite;

    /// <summary>键盘类型已探测过（不看 EC 模式开关）⇒ 门控可据此给出确定结论。</summary>
    internal static bool KindProbed => _active;

    /// <summary>已探测且本程序能控制键盘灯（不看 EC 模式开关）。</summary>
    internal static bool ControllableProbed => _active
        && _kind is GcuEcKeyboardKind.HidPerKey or GcuEcKeyboardKind.EcSingleZone or GcuEcKeyboardKind.EcWhite;

    /// <summary>EC 首页启动时调用一次：探测键盘类型并打开分流。</summary>
    internal static void MarkActive()
    {
        lock (Gate)
        {
            _kind = Detect(out string detail);
            _active = true;
            Logger.WriteError("[gcu-ec-kbd] 键盘灯分流 ⇒ " + _kind + "（" + detail + "）");
        }
        // ⭐ EC 模式下顺手把「GCU 又起来了」守护挂上：这个入口每次启动必跑，比 UI 启动分支可靠。
        try { GcuEcMode.EnsureGuardIfEcMode(); } catch { }
        // EC 路由下没有 GCU 报文可等，灯带/Logo 证据只能本地采集一次，之后门控只读缓存。
        IteLightingLocal.PrimeCaches();
    }

    /// <summary>
    /// 能力位回报的键盘类型是否表明"不是单分区键盘"（例如 MEZone_* 单键系列）。
    /// 未知（0 / 读不到）一律返回 false，保持旧分流不动。
    /// </summary>
    private static bool CapabilitySaysNotSingleZone()
    {
        try
        {
            MechrevoHw? hw = MechrevoLite.Program.hw;
            if (hw == null)
            {
                return false;
            }
            int type = hw.CustomizeKeyboardType > 0 ? hw.CustomizeKeyboardType : (hw.Capabilities?.KeyboardType ?? 0);
            if (type <= 0)
            {
                return false;
            }
            return !KeyboardFirmwareEffects.IsSingleZoneRgb(type);
        }
        catch
        {
            return false;
        }
    }

    private static GcuEcKeyboardKind Detect(out string detail)
    {
        bool perKey = false;
        bool fourZone = false;
        List<string> seen = new();
        int kbType = 0;
        try { MechrevoHw? kbdHw = MechrevoLite.Program.hw; kbType = kbdHw == null ? 0 : (kbdHw.CustomizeKeyboardType > 0 ? kbdHw.CustomizeKeyboardType : (kbdHw.Capabilities?.KeyboardType ?? 0)); } catch { }
        string typeText = " · 键盘类型=" + kbType;
        try
        {
            foreach (HidDeviceWin device in HidDeviceWin.Enumerate(IteVendorId))
            {
                using (device)
                {
                    seen.Add(device.DeviceInfo);
                    if (device.ProductID == FourZoneProductId)
                    {
                        if (device.UsagePage == FourZoneUsagePage)
                        {
                            fourZone = true;
                            continue;
                        }
                        // 同一 PID 的 0xFF03 集合是键盘控制通道：能力位表明"不是单分区键盘"时按单键处理，
                        // 与 KeyboardRgb 的评分口径一致；能力位未知时保持旧行为，不改变现有分流。
                        if (device.UsagePage == 0xFF03 && CapabilitySaysNotSingleZone())
                        {
                            perKey = true;
                        }
                        continue;
                    }
                    if (device.ProductID is LightbarProductId or HingeProductId)
                    {
                        continue;
                    }
                    // 单键 RGB 仅 ITE 0x6000-0x600B，且排除标准键盘接口。
                    if (!HidKeyboardScoring.IsPerKeyKeyboard(device.ProductID))
                    {
                        continue;
                    }
                    if (device.UsagePage == 0x0001 && device.Usage == 0x0006)
                    {
                        continue;
                    }
                    // 与 KeyboardRgb.ResolveDevice 同一评分口径。
                    if (KeyboardRgb.ScoreCandidate(device.ProductID, device.UsagePage, device.InterfaceNumber,
                            device.OutputReportLength, device.FeatureReportLength) > 0)
                    {
                        perKey = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            seen.Add("HID 枚举异常 " + ex.GetType().Name);
        }
        string hid = seen.Count == 0 ? "无 ITE HID" : string.Join("; ", seen);
        if (perKey)
        {
            detail = hid + typeText;
            return GcuEcKeyboardKind.HidPerKey;
        }
        if (fourZone)
        {
            detail = hid + typeText;
            return GcuEcKeyboardKind.HidFourZone;
        }
        // ⭐ 同方收敛：EC 单区只认「0x766 bit2=1 **且** 能力位没表明“不是单分区键盘”」。
        // 能力位说是单键/其它分区机型时，即使这一位可读也不走 EC 单区 ⇒ 软件轮播只在真·单分区键盘上跑。
        bool ecSingleBit = NucKeyboardLighting.IsSingleZone();
        bool capabilityNotSingle = ecSingleBit && CapabilitySaysNotSingleZone();
        if (ecSingleBit && !capabilityNotSingle)
        {
            detail = hid + typeText + " · 0x766 bit2=1";
            return GcuEcKeyboardKind.EcSingleZone;
        }
        if (EcKeyboardLight.ReadRegister() >= 0)
        {
            detail = hid + typeText
                + (capabilityNotSingle ? " · 0x766 bit2=1 但能力位表明非单分区 ⇒ 不按 EC 单区处理" : " · 0x766 bit2=0")
                + " · 0x78C 可读";
            return GcuEcKeyboardKind.EcWhite;
        }
        detail = hid + typeText + " · EC 不可读";
        return GcuEcKeyboardKind.None;
    }

    /// <summary>读 EC 键盘当前电源状态（仅 EC 单区/白光）。</summary>
    internal static bool TryReadPower(out bool on)
    {
        on = false;
        if (IsEcSingleZone)
        {
            // 亮度字节和颜色通道一起判：只看亮度字节时，灯亮着也可能读到 0 ⇒ 开关显示成关。
            if (NucKeyboardLighting.TryReadState(out bool onNow, out _))
            {
                on = onNow;
                return true;
            }
            return false;
        }
        if (IsEcWhite && EcKeyboardLight.TryGet(out bool whiteOn, out _))
        {
            on = whiteOn;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 按已保存的键盘灯设置下发（EC 单区/白光）。
    /// <paramref name="lightOverride"/>：0~4 档；白光按 ≥3 亮、其余暗。
    /// </summary>
    internal static bool ApplyFromSettings(string? effect, bool on, int? lightOverride = null)
    {
        if (!Active)
        {
            return false;
        }
        LightChannelSettings settings = LightingSettingsStore.Load(Topic, string.IsNullOrEmpty(effect) ? "Single" : effect);
        string targetEffect = string.IsNullOrEmpty(effect) ? (settings.Effect ?? "Single") : effect;
        if (IsOffEffect(targetEffect))
        {
            on = false;
        }
        int light = Math.Clamp(lightOverride ?? settings.Light, 0, 4);
        if (on && light <= 0)
        {
            light = 1;
        }
        if (IsEcSingleZone)
        {
            int color = settings.EffectiveColorArgb();
            // 单区轮播的速度档是 1~8（界面上 1/8 .. 8/8，数值越大越快）。
            int speed = Math.Clamp(settings.Speed, 1, NucKeyboardLighting.SpeedLevels);
            if (string.Equals(targetEffect, "Breathing", StringComparison.Ordinal))
            {
                color = BreathingColor(settings.BreathingIndex, color);
            }
            else if (string.Equals(targetEffect, "Manual", StringComparison.Ordinal))
            {
                // 旧存档可能还是“每键时间 2~10”，超出档位上限的折回档位；新存档直接就是档位。
                speed = settings.Speed > NucKeyboardLighting.SpeedLevels
                    ? Math.Clamp((settings.Speed - 2) / 2 + 1, 1, NucKeyboardLighting.SpeedLevels)
                    : speed;
            }
            NucKeyboardLighting.Apply(targetEffect, on, on ? light : 0, speed, color, CycleColors(settings));
            bool ok = NucKeyboardLighting.TryReadBrightness(out int read) && (read > 0) == on;
            Logger.WriteError("[gcu-ec-kbd] EC 单区 effect=" + targetEffect + " on=" + on + " light=" + light
                + " speed=" + speed + " ⇒ " + (ok ? "OK" : "回读不一致"));
            return ok;
        }
        if (IsEcWhite)
        {
            return ApplyWhite(on, light >= 3 ? 2 : 1);
        }
        return false;
    }

    /// <summary>Fn 亮度键：EC 单区 0~4 档；EC 白光 0~2 档。</summary>
    internal static bool ApplyHotkeyBrightness(int level)
    {
        // 声明 hotkey=0 的机型：亮度键交给固件自己处理，软件不介入（避免覆盖固件已写好的亮度）。
        if (string.Equals(DeclarationRuntime.Param("keyboardBacklight", "hotkey")?.Trim(), "0", StringComparison.Ordinal))
        {
            Logger.WriteErrorIfChanged("kbd-hotkey-skip", "[kbd-ec] 亮度键跳过（该机型由固件自管）→ 上报档位 " + level);
            return true;
        }
        if (IsEcWhite)
        {
            // 亮度键是 0~4 档，白光只有关/暗/亮三态：0 => 关，1~2 => 暗，3~4 => 亮。
            // （原来直接夹到 0~2，导致 2/4 被当成"亮"，中间那一档看起来没反应。）
            int white = (level <= 0) ? 0 : ((level >= 3) ? 2 : 1);
            bool ok = ApplyWhite(white > 0, Math.Max(1, white));
            if (ok && white > 0)
            {
                SaveLight(white >= 2 ? 4 : 2);
            }
            return ok;
        }
        if (IsEcSingleZone)
        {
            int clamped = Math.Clamp(level, 0, 4);
            if (clamped > 0)
            {
                SaveLight(clamped);
            }
            return ApplyFromSettings(null, clamped > 0, clamped);
        }
        return false;
    }

    /// <summary>EC 白光：沿用 0x78C 的已验证写法，再置 0x767 bit5 锁存。</summary>
    internal static bool ApplyWhite(bool on, int level1to2)
    {
        if (!IsEcWhite)
        {
            return false;
        }
        bool ok = on
            ? EcKeyboardLight.TryApply(true, Math.Clamp(level1to2, 1, EcKeyboardLight.MaxBrightnessLevel))
            : EcKeyboardLight.TrySetPower(false);
        if (EcKeyboardLight.LatchEnabled)
        {
            EcTriggerByte.TryApply(EcTriggerByte.Latch, 0);
        }
        Logger.WriteError("[gcu-ec-kbd] EC 白光 on=" + on + " level=" + level1to2 + " ⇒ " + (ok ? "OK" : "FAIL"));
        return ok;
    }

    private static void SaveLight(int light)
    {
        try
        {
            LightChannelSettings current = LightingSettingsStore.Load(Topic, "Single");
            LightingSettingsStore.Save(Topic, current with { Light = light });
        }
        catch
        {
        }
    }

    private static bool IsOffEffect(string effect)
    {
        return string.Equals(effect, "Off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effect, "None", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effect, "关闭", StringComparison.Ordinal);
    }

    private static int BreathingColor(int index, int fallback)
    {
        return index switch
        {
            1 => unchecked((int)0xFFFFFFFF),
            2 => unchecked((int)0xFFFF0000),
            3 => unchecked((int)0xFF00FF00),
            4 => unchecked((int)0xFF0000FF),
            _ => fallback
        };
    }

    private static int[] CycleColors(LightChannelSettings settings)
    {
        int[] indexes = settings.ManualIndexes is { Length: >= 6 } manual
            ? manual
            : LightChannelSettings.DefaultManualIndexes;
        int[] colors = new int[indexes.Length];
        for (int i = 0; i < indexes.Length; i++)
        {
            int paletteIndex = Math.Clamp(indexes[i], 1, LightChannelSettings.ManualPalette.Length);
            colors[i] = LightChannelSettings.ManualPalette[paletteIndex - 1];
        }
        return colors;
    }
}
