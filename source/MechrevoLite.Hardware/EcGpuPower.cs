using System;

namespace MechrevoLite.Hardware;

/// <summary>EC GPU TGP / Dynamic Boost 写入，按固定基频与可调基频两种口径分流。</summary>
internal static class EcGpuPower
{
    private const int AddrDbCtrl = 1859;
    private const int AddrTgpOffset = 1860;
    private const int AddrDbFlag = 1861;
    private const int AddrDbWatts = 1862;

    internal static bool TryResolveDbMaxWatts(out int watts)
    {
        watts = 0;
        if (!AcpiDriverEc.TryReadByte(AddrDbWatts, out byte raw))
        {
            return false;
        }
        watts = raw switch
        {
            0x05 => 5,
            0x0F => 15,
            0x19 => 25,
            _ => 0
        };
        return watts > 0;
    }

    internal static bool TryApply(MechrevoHw hw, int baseTgp, int dbWatts, bool dbOn, out string detail)
    {
        detail = "";
        if (hw == null)
        {
            detail = "hw 为空";
            return false;
        }
        int floor = hw.GpuTgpMinimum > 0 ? hw.GpuTgpMinimum : 0;
        int maxBase = hw.GpuTgpMaximum > 0 ? hw.GpuTgpMaximum : baseTgp;
        int maxDb = TryResolveDbMaxWatts(out int ecDbMax)
            ? ecDbMax
            : hw.GpuDbMaximum > 0 ? hw.GpuDbMaximum : 25;
        if (maxBase <= 0)
        {
            detail = "GPU TGP 范围不可用";
            return false;
        }

        bool fullPower = maxBase - floor <= 25;
        int targetBase = Math.Clamp(baseTgp, Math.Max(5, floor), maxBase);
        int offset = fullPower ? 0 : Math.Clamp(targetBase - floor, 0, 100);
        int targetDb = dbOn ? Math.Clamp(dbWatts, 5, Math.Max(5, maxDb)) : 0;
        byte ctrl = dbOn && targetDb > 0 ? (byte)0x07 : (byte)0x05;

        if (!AcpiDriverEc.TryWriteByte(AddrDbFlag, 0xFF))
        {
            detail = "0x745 握手写入失败";
            return false;
        }
        bool c = AcpiDriverEc.TryWriteByte(AddrDbCtrl, ctrl);
        bool o = AcpiDriverEc.TryWriteByte(AddrTgpOffset, offset);
        bool w = AcpiDriverEc.TryWriteByte(AddrDbWatts, targetDb);
        bool cRead = AcpiDriverEc.TryReadByte(AddrDbCtrl, out byte ctrlBack);
        bool oRead = AcpiDriverEc.TryReadByte(AddrTgpOffset, out byte offsetBack);
        bool wRead = AcpiDriverEc.TryReadByte(AddrDbWatts, out byte wattsBack);
        bool ok = c && o && w && cRead && oRead && wRead && ctrlBack == ctrl && offsetBack == offset && wattsBack == targetDb;
        detail = "GPU " + (fullPower ? "固定基频" : "可调基频")
            + " 基础=" + targetBase + "W（floor=" + floor + " offset=" + offset + "）"
            + " DB=" + targetDb + "W（on=" + dbOn + "）"
            + " ctrl=0x" + ctrl.ToString("X2") + "/0x" + ctrlBack.ToString("X2")
            + " read=" + offsetBack + "/" + wattsBack
            + " ⇒ " + (ok ? "OK" : "FAIL");
        Logger.WriteError("[gpu-power-ec] " + detail);
        return ok;
    }
}
