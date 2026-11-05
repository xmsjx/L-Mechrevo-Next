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

    internal static bool HasLightbar => _hasLightbar ??= FindDevice(LightbarPid, LightbarUsage) != null;
    internal static bool HasHinge => _hasHinge ??= FindDevice(HingePid, 0) != null;

    internal static void Invalidate()
    {
        lock (Gate)
        {
            _hasLightbar = null;
            _hasHinge = null;
        }
    }

    /// <summary>灯带：topic = HidLightbar/Ctrl。</summary>
    internal static bool TryApplyLightbar(string effect, bool on, int brightness, int speed, int colorArgb, out string detail)
        => TryApply(LightbarPid, LightbarUsage, ControlLightbar, BlankLightbar, effect, on, brightness, speed, colorArgb, out detail);

    /// <summary>铰链：topic = HidLightbar_Hinge/Ctrl。</summary>
    internal static bool TryApplyHinge(string effect, bool on, int brightness, int speed, int colorArgb, out string detail)
        => TryApply(HingePid, 0, ControlHinge, BlankHinge, effect, on, brightness, speed, colorArgb, out detail);

    /// <summary>Logo：USB 7001 的 lettering 通道。</summary>
    internal static bool TryApplyLogo(string effect, bool on, int brightness, int speed, int colorArgb, out string detail)
        => TryApply(LightbarPid, LightbarUsage, ControlLogo, BlankLogo, effect, on, brightness, speed, colorArgb, out detail);

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
            }
            byte[] packet =
            {
                0, 8, control, mappedEffect, speedValue, lightValue,
                circular ? (byte)8 : (byte)1, (byte)(mappedEffect == 3 ? 1 : 0), 0
            };
            bool ok = dev.SetFeature(packet);
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
