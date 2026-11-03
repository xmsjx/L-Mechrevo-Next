using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MechrevoLite.Hardware;

/// <summary>EC GPU TGP / Dynamic Boost 写入，按固定基频与可调基频两种口径分流。</summary>
internal static class EcGpuPower
{
    private const int AddrDbCtrl = 1859;
    private const int AddrTgpOffset = 1860;
    private const int AddrDbFlag = 1861;
    private const int AddrDbWatts = 1862;

    internal readonly struct Capabilities
    {
        internal int FloorWatts { get; init; }
        internal int BaseMaxWatts { get; init; }
        internal int DbMaxWatts { get; init; }
        internal bool FullPower { get; init; }
    }

    /// <summary>按显卡物理功耗上限计算基频/DB 上限，不使用 EC 运行时 DB setpoint 冒充最大值。</summary>
    internal static bool TryGetCapabilities(out Capabilities caps, out string detail)
        => TryGetCapabilities(null, out caps, out detail);

    internal static bool TryGetCapabilities(MechrevoHw? hw, out Capabilities caps, out string detail)
    {
        caps = default;
        detail = "";
        int minW = 0;
        int maxW = 0;
        bool hasNvml = global::NvmlHelper.TryGetPowerLimitBounds(out minW, out maxW, out _) && maxW > 0;
        int defW = 0;
        if (hasNvml)
        {
            global::NvmlHelper.TryGetDefaultPowerLimitWatts(out defW);
        }
        if ((!hasNvml || maxW <= 0) && TryQuerySmiLimits(out int smiDefault, out int smiMax))
        {
            hasNvml = true;
            maxW = smiMax;
            defW = smiDefault;
            minW = smiDefault;
        }
        if ((!hasNvml || maxW <= 0) && hw != null)
        {
            maxW = hw.GpuTgpMaximum;
            minW = hw.GpuTgpMinimum;
        }
        if (maxW <= 0)
        {
            detail = "GPU 物理功耗上限不可用";
            return false;
        }
        int hardwareDbCapFallback = maxW <= 115 ? 15 : 25;
        if (defW <= 0)
        {
            defW = Math.Max(5, minW > 0 ? minW : maxW - hardwareDbCapFallback);
        }
        int delta = maxW - defW;
        int hardwareDbCap = hardwareDbCapFallback;
        if (delta > 0 && delta <= 25)
        {
            caps = new Capabilities
            {
                FloorWatts = defW,
                BaseMaxWatts = defW,
                DbMaxWatts = Math.Min(hardwareDbCap, delta),
                FullPower = true
            };
        }
        else if (delta > 25)
        {
            int dbMax = Math.Min(hardwareDbCap, delta);
            int baseMax = Math.Max(defW, maxW - dbMax);
            caps = new Capabilities
            {
                FloorWatts = defW,
                BaseMaxWatts = baseMax,
                DbMaxWatts = dbMax,
                FullPower = false
            };
        }
        else
        {
            int dbMax = hardwareDbCap;
            int baseMax = Math.Max(5, maxW - dbMax);
            caps = new Capabilities
            {
                FloorWatts = baseMax,
                BaseMaxWatts = baseMax,
                DbMaxWatts = dbMax,
                FullPower = true
            };
        }
        detail = "default=" + defW + " max=" + maxW
            + " floor=" + caps.FloorWatts + " baseMax=" + caps.BaseMaxWatts
            + " dbMax=" + caps.DbMaxWatts + " full=" + caps.FullPower;
        return true;
    }

    internal static bool TryApply(MechrevoHw hw, int baseTgp, int dbWatts, bool dbOn, out string detail)
    {
        detail = "";
        if (hw == null)
        {
            detail = "hw 为空";
            return false;
        }
        if (!TryGetCapabilities(hw, out Capabilities caps, out string capDetail))
        {
            detail = capDetail;
            Logger.WriteError("[gpu-power-ec] 跳过：base=" + baseTgp + " db=" + dbWatts
                + " on=" + dbOn + " · " + capDetail);
            return false;
        }
        int targetBase = Math.Clamp(baseTgp, caps.FloorWatts, caps.BaseMaxWatts);
        int offset = caps.FullPower ? 0 : Math.Clamp(targetBase - caps.FloorWatts, 0, 100);
        int targetDb = dbOn ? Math.Clamp(dbWatts, 5, caps.DbMaxWatts) : 0;
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
        detail = "GPU " + (caps.FullPower ? "固定基频" : "可调基频")
            + " 基础=" + targetBase + "W（floor=" + caps.FloorWatts + " offset=" + offset + "）"
            + " DB=" + targetDb + "W（max=" + caps.DbMaxWatts + " on=" + dbOn + "）"
            + " ctrl=0x" + ctrl.ToString("X2") + "/0x" + ctrlBack.ToString("X2")
            + " read=" + offsetBack + "/" + wattsBack
            + " ⇒ " + (ok ? "OK" : "FAIL");
        Logger.WriteError("[gpu-power-ec] " + detail);
        return ok;
    }

    private static bool TryQuerySmiLimits(out int defaultW, out int maxW)
    {
        defaultW = 0;
        maxW = 0;
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (!File.Exists(exe))
            {
                exe = "nvidia-smi.exe";
            }
            ProcessStartInfo psi = new(exe, "--query-gpu=power.default_limit,power.max_limit --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("start failed");
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2500) || process.ExitCode != 0)
            {
                try { process.Kill(); } catch { }
                return false;
            }
            string[] parts = output.Split(',');
            if (parts.Length < 2
                || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double defaultValue)
                || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double maxValue))
            {
                return false;
            }
            defaultW = (int)Math.Round(defaultValue);
            maxW = (int)Math.Round(maxValue);
            return defaultW > 0 && maxW > 0;
        }
        catch
        {
            return false;
        }
    }
}
