using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace MechrevoLite.Hardware;

/// <summary>
/// 同方 ITE 灯带 / 铰链 / Logo 的本地 HID 通道。
/// EC 实验模式（GCU 离线）下不经过 GCUService，直接按设备报文下发。
/// 报文长度固定 9 字节（report id 0 + 8 字节负载）。
/// </summary>
internal static class IteLightingLocal
{
    private const ushort IteVid = 0x048D;
    private const ushort LightbarPid = 0x7001;
    private const ushort HingePid = 0x7000;
    private const ushort LightbarUsage = 2;

    private const byte ControlLightbar = 34;
    private const byte ControlHinge = 33;
    private const byte ControlLogo = 35;
    private const byte BlankLightbar = 2;
    private const byte BlankHinge = 1;
    private const byte BlankLogo = 3;

    private static readonly byte[] SpeedMap = { 10, 7, 5, 3, 1 };
    private static readonly byte[] BrightnessMap = { 0, 25, 50, 75, 100 };
    private static readonly object Gate = new();
    private static bool? _hasLightbar;
    private static bool? _hasHinge;
    private static bool? _hasUsbLogo;

    internal static bool HasUsbLightbar => HasUsbDevice(LightbarPid, LightbarUsage);
    internal static bool HasUsbLogo
    {
        get
        {
            lock (Gate)
            {
                return _hasUsbLogo ??= HasUsbLightbar || HasUsbDevice(HingePid, 0);
            }
        }
    }
    internal static bool HasLightbar => _hasLightbar ??= HasUsbLightbar || EcDirectLightbarSupported;
    internal static bool HasHinge => _hasHinge ??= HasUsbDevice(HingePid, 0);

    /// <summary>读取已缓存的 USB Logo 证据；未采集过返回 false，不触发 HID 枚举。</summary>
    internal static bool TryGetUsbLogoCached(out bool value)
    {
        bool? cached = _hasUsbLogo;
        value = cached == true;
        return cached.HasValue;
    }

    /// <summary>读取已缓存的灯带证据；未采集过返回 false，不触发 HID/EC 访问。</summary>
    internal static bool TryGetLightbarCached(out bool value)
    {
        bool? cached = _hasLightbar;
        value = cached == true;
        return cached.HasValue;
    }

    /// <summary>重新枚举 USB Logo 设备并写入缓存。</summary>
    internal static bool RefreshFromHid()
    {
        bool value = HasUsbLightbar || HasUsbDevice(HingePid, 0);
        lock (Gate)
        {
            _hasUsbLogo = value;
        }
        return value;
    }

    /// <summary>无 USB 灯带时，按 OpenRevo 回退到主板 EC 灯带（支持位 1893 bit6 / 1934 bit4）。</summary>
    internal static bool EcDirectLightbarSupported
    {
        get
        {
            try
            {
                bool byte1 = AcpiDriverEc.TryReadByte(1893, out byte support1) && (support1 & 0x40) != 0;
                bool byte3 = AcpiDriverEc.TryReadByte(1934, out byte support3) && (support3 & 0x10) != 0;
                return byte1 || byte3;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static void Invalidate()
    {
        lock (Gate)
        {
            _hasLightbar = null;
            _hasHinge = null;
            _hasUsbLogo = null;
        }
    }

    /// <summary>灯带：topic = HidLightbar/Ctrl。</summary>
    internal static bool TryApplyLightbar(string effect, bool on, int brightness, int speed, int colorArgb, out string detail)
    {
        if (HasUsbDevice(LightbarPid, LightbarUsage))
        {
            return TryApply(LightbarPid, LightbarUsage, ControlLightbar, BlankLightbar, effect, on, brightness, speed, colorArgb, out detail);
        }
        if (EcDirectLightbarSupported)
        {
            detail = "EC direct lightbar effect=" + effect + " light=" + brightness;
            return UmiLightbar.SetEffect(effect, Color.FromArgb(colorArgb), brightness);
        }
        detail = "无 USB 7001/EC 灯带";
        return false;
    }

    /// <summary>铰链：topic = HidLightbar_Hinge/Ctrl。</summary>
    internal static bool TryApplyHinge(string effect, bool on, int brightness, int speed, int colorArgb, out string detail)
        => TryApply(HingePid, 0, ControlHinge, BlankHinge, effect, on, brightness, speed, colorArgb, out detail);

    /// <summary>Logo：USB 7001 的 lettering 通道；没有 7001 时回退 7000。</summary>
    internal static bool TryApplyLogo(string effect, bool on, int brightness, int speed, int colorArgb, out string detail)
    {
        if (HasUsbDevice(LightbarPid, LightbarUsage))
        {
            return TryApply(LightbarPid, LightbarUsage, ControlLogo, BlankLogo,
                effect, on, brightness, speed, colorArgb, out detail);
        }
        if (HasUsbDevice(HingePid, 0))
        {
            return TryApply(HingePid, 0, ControlLogo, BlankLogo,
                effect, on, brightness, speed, colorArgb, out detail);
        }
        detail = "无 USB 7001/7000 Logo 通道";
        return false;
    }

    private static bool TryApply(ushort pid, ushort usage, byte control, byte blank,
        string effect, bool on, int brightness, int speed, int colorArgb, out string detail)
    {
        detail = "";
        int effectId = MapEffect(effect);
        int light = Math.Clamp(brightness, 0, 4);
        int speedIndex = Math.Clamp(speed, 0, 4);
        byte mappedEffect = MapActualEffect(effectId);
        bool circular = effectId == 20 || mappedEffect is 3 or 4 or 14
            || (mappedEffect == 2 && effectId == 20);
        try
        {
            using HidDeviceWin? dev = FindDevice(pid, usage);
            if (dev == null || !dev.Open())
            {
                detail = "未找到 ITE 设备 PID=0x" + pid.ToString("X4");
                return false;
            }
            if (!on || light <= 0)
            {
                byte[] off = { 0, 8, control, 255, 1, 50, 0, 0, 0 };
                bool offOk = dev.SetFeature(off);
                detail = "OFF ⇒ " + (offOk ? "OK" : "FAIL");
                Logger.WriteError("[ite-light] " + detail);
                return offOk;
            }

            byte lightValue = BrightnessMap[light];
            byte speedValue = SpeedMap[speedIndex];
            Color color = Color.FromArgb(colorArgb);
            int r = color.R, g = color.G, b = color.B;
            if (pid == HingePid)
            {
                // 7000 铰链先唤醒 MCU。
                dev.SetFeature(new byte[] { 0, 8, 1, 0, 0, 0, 0, 0, 0 });
            }
            if (!circular)
            {
                byte[] colorPacket = { 0, 20, blank, 1, (byte)r, (byte)g, (byte)b, 0, 0 };
                dev.SetFeature(colorPacket);
                System.Threading.Thread.Sleep(15);
            }
            byte[] packet =
            {
                0, 8, control, mappedEffect, speedValue, lightValue,
                circular ? (byte)8 : (byte)1, (byte)(mappedEffect == 3 ? 1 : 0), 0
            };
            bool ok = dev.SetFeature(packet);
            if (mappedEffect == 1)
            {
                System.Threading.Thread.Sleep(100);
                dev.SetFeature(packet);
            }
            detail = "effect=" + effect + "/" + mappedEffect + " light=" + lightValue
                + " speed=" + speedValue + " circular=" + circular + " ⇒ " + (ok ? "OK" : "FAIL");
            Logger.WriteError("[ite-light] " + detail);
            return ok;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            return false;
        }
    }

    private static bool HasUsbDevice(ushort pid, ushort usage)
    {
        using HidDeviceWin? device = FindDevice(pid, usage);
        return device != null;
    }

    private static HidDeviceWin? FindDevice(ushort pid, ushort usage)
    {
        try
        {
            foreach (HidDeviceWin device in HidDeviceWin.Enumerate(IteVid))
            {
                if (device.ProductID != pid || (usage != 0 && device.Usage != usage))
                {
                    device.Dispose();
                    continue;
                }
                return device;
            }
        }
        catch
        {
        }
        return null;
    }

    private static int MapEffect(string effect)
    {
        return effect switch
        {
            "Single" => 1,
            "Breathing" => 2,
            "Wave" => 3,
            "Impact" => 4,
            "Meteor" or "Raindrop" => 10,
            "ColorMarquee" or "Marquee" => 9,
            "Rainbow" => 20,
            _ => 1
        };
    }

    private static byte MapActualEffect(int effect)
    {
        return effect switch
        {
            20 => 2,
            13 or 4 => 4,
            12 or 6 => 6,
            10 => 6,
            9 => 3,
            14 => 3,
            _ => (byte)Math.Clamp(effect, 0, 255)
        };
    }
}
