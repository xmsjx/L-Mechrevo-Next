using System;
using System.Collections.Generic;

namespace MechrevoLite.Hardware;

/// <summary>EC 实验模式下的键盘灯类型。</summary>
internal enum GcuEcKeyboardKind
{
    None,
    HidPerKey,
    HidFourZone,
    EcSingleZone,
    EcWhite
}

/// <summary>
/// 同方 GCU 机型 EC 实验模式（GCU 离线）的键盘灯分流。
/// 判定顺序：HID 单键 → HID 四分区 → EC 单区（0x766 bit2）→ EC 白光 → 无。
/// <para>只有 EC 实验首页启动后（<see cref="MarkActive"/>）才生效；GCU 路由下 <see cref="Active"/> 恒为 false，
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

    /// <summary>EC 实验首页已接管（GCU 离线）。</summary>
    internal static bool Active => _active && GcuEcExperimental.Enabled;

    internal static GcuEcKeyboardKind Kind => _kind;

    internal static bool IsHid => Active && _kind == GcuEcKeyboardKind.HidPerKey;

    internal static bool IsEcSingleZone => Active && _kind == GcuEcKeyboardKind.EcSingleZone;

    internal static bool IsEcWhite => Active && _kind == GcuEcKeyboardKind.EcWhite;

    /// <summary>EC 模式下本程序能控制的键盘（HID 单键 / EC 单区 / EC 白光）。四分区 HID 暂无本地协议。</summary>
    internal static bool HasControllableKeyboard => Active
        && _kind is GcuEcKeyboardKind.HidPerKey or GcuEcKeyboardKind.EcSingleZone or GcuEcKeyboardKind.EcWhite;

    /// <summary>EC 实验首页启动时调用一次：探测键盘类型并打开分流。</summary>
    internal static void MarkActive()
    {
        lock (Gate)
        {
            _kind = Detect(out string detail);
            _active = true;
            Logger.WriteError("[gcu-ec-kbd] 键盘灯分流 ⇒ " + _kind + "（" + detail + "）");
        }
        // EC 路由下没有 GCU 报文可等，灯带/Logo 证据只能本地采集一次，之后门控只读缓存。
        IteLightingLocal.PrimeCaches();
    }

    private static GcuEcKeyboardKind Detect(out string detail)
    {
        bool perKey = false;
        bool fourZone = false;
        List<string> seen = new();
        try
        {
            foreach (HidDeviceWin device in HidDeviceWin.Enumerate(IteVendorId))
            {
                using (device)
                {
                    seen.Add(device.DeviceInfo);
                    if (device.ProductID == FourZoneProductId && device.UsagePage == FourZoneUsagePage)
                    {
                        fourZone = true;
                        continue;
                    }
                    if (device.ProductID is LightbarProductId or HingeProductId or FourZoneProductId)
                    {
                        continue;
                    }
                    // 单键 RGB 仅 ITE 0x6000-0x600B，且排除标准键盘接口。
                    if (device.ProductID < 0x6000 || device.ProductID > 0x600B)
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
            detail = hid;
            return GcuEcKeyboardKind.HidPerKey;
        }
        if (fourZone)
        {
            detail = hid;
            return GcuEcKeyboardKind.HidFourZone;
        }
        if (NucKeyboardLighting.IsSingleZone())
        {
            detail = hid + " · 0x766 bit2=1";
            return GcuEcKeyboardKind.EcSingleZone;
        }
        if (EcKeyboardLight.ReadRegister() >= 0)
        {
            detail = hid + " · 0x766 bit2=0 · 0x78C 可读";
            return GcuEcKeyboardKind.EcWhite;
        }
        detail = hid + " · EC 不可读";
        return GcuEcKeyboardKind.None;
    }

    /// <summary>读 EC 键盘当前电源状态（仅 EC 单区/白光）。</summary>
    internal static bool TryReadPower(out bool on)
    {
        on = false;
        if (IsEcSingleZone)
        {
            if (NucKeyboardLighting.TryReadBrightness(out int level))
            {
                on = level > 0;
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
            int speed = Math.Clamp(settings.Speed, 0, 4);
            if (string.Equals(targetEffect, "Breathing", StringComparison.Ordinal))
            {
                color = BreathingColor(settings.BreathingIndex, color);
            }
            else if (string.Equals(targetEffect, "Manual", StringComparison.Ordinal))
            {
                speed = Math.Clamp((settings.Speed - 2) / 2, 0, 4);
            }
            NucKeyboardLighting.Apply(targetEffect, on, on ? light : 0, speed, color, CycleColors(settings));
            bool ok = NucKeyboardLighting.TryReadBrightness(out int read) && (read > 0) == on;
            Logger.WriteError("[gcu-ec-kbd] EC 单区 effect=" + targetEffect + " on=" + on + " light=" + light
                + " ⇒ " + (ok ? "OK" : "回读不一致"));
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
        if (IsEcWhite)
        {
            int white = Math.Clamp(level, 0, 2);
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
            ? EcKeyboardLight.TryApply(true, Math.Clamp(level1to2, 1, 2))
            : EcKeyboardLight.TrySetPower(false);
        if (AcpiDriverEc.TryReadByte(AddressTriggerByte, out byte trigger))
        {
            AcpiDriverEc.TryWriteByte(AddressTriggerByte, trigger | TriggerLatch);
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
