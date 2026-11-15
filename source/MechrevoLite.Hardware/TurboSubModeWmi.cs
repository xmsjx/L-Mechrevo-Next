using System;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace MechrevoLite.Hardware;

/// <summary>静音狂暴子模式：EC 1832 bit0 + WMI ULong 命令。</summary>
internal static class TurboSubModeWmi
{
    private const int SubModeAddress = 1832;
    private const ulong CmdSupport = 0x5_0000_0008UL;
    private const ulong CmdSilent = 0x5_0000_0009UL;
    private const ulong CmdExtreme = 0x5_0000_000AUL;
    private const byte SupportMarker = 0x55;
    private static readonly object Gate = new();
    private static ManagementObject? _instance;
    private static long _checkedAt;
    private static bool _supported;

    internal static bool TryReadSilent(out bool silent)
    {
        silent = false;
        if (!AcpiDriverEc.TryReadByte(SubModeAddress, out byte value))
        {
            return false;
        }
        silent = (value & 0x01) != 0;
        return true;
    }

    internal static bool IsSupported()
    {
        lock (Gate)
        {
            if (_supported)
            {
                return true;
            }
            if (_checkedAt != 0 && Environment.TickCount64 - _checkedAt < 5000)
            {
                return false;
            }
            _checkedAt = Environment.TickCount64;
            if (!TryGetInstance(out ManagementObject instance))
            {
                return false;
            }
            if (!InvokeRaw(instance, CmdSupport, out ulong value))
            {
                return false;
            }
            _supported = (byte)(value & 0xFF) == SupportMarker;
            return _supported;
        }
    }

    internal static readonly object ApplyGate = new();

    internal static bool TryApply(bool silent)
    {
        if (!IsSupported())
        {
            return false;
        }
        // 1832 是读-改-写：界面开关、性能键、档位监视可能同时进来，必须同一把锁串行。
        lock (ApplyGate)
        {
            return ApplyUnlocked(silent);
        }
    }

    /// <summary>静音狂暴读-改-写全序列的唯一入口（调用方须持 ApplyGate）。</summary>
    internal static bool ApplyUnlocked(bool silent)
    {
        if (!AcpiDriverEc.TryReadByte(SubModeAddress, out byte previous))
        {
            return false;
        }
        byte target = (byte)(silent ? (previous | 0x01) : (previous & ~0x01));
        if (target != previous && !AcpiDriverEc.TryWriteByte(SubModeAddress, target))
        {
            return false;
        }
        if (!TryGetInstance(out ManagementObject instance)
            || !InvokeRaw(instance, silent ? CmdSilent : CmdExtreme, out _))
        {
            AcpiDriverEc.TryWriteByte(SubModeAddress, previous);
            return false;
        }
        bool readOk = AcpiDriverEc.TryReadByte(SubModeAddress, out byte readback);
        Logger.WriteErrorIfChanged("silent-turbo-apply", "[silent-turbo] 1832 0x" + previous.ToString("X2") + "→0x" + target.ToString("X2") + " · 回读" + (readOk ? "0x" + readback.ToString("X2") : "失败") + " · silent=" + silent);
        return readOk && (readback & 0x01) == (silent ? 0x01 : 0x00);
    }
    private static bool TryGetInstance(out ManagementObject instance)
    {
        instance = null!;
        try
        {
            if (_instance != null)
            {
                instance = _instance;
                return true;
            }
            using ManagementObjectSearcher searcher = new("root\\WMI", "SELECT * FROM AcpiTest_MULong");
            foreach (ManagementObject item in searcher.Get())
            {
                _instance = item;
                instance = item;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool InvokeRaw(ManagementObject instance, ulong rawValue, out ulong value)
    {
        value = 0;
        try
        {
            using ManagementBaseObject inParams = instance.GetMethodParameters("GetSetULong");
            inParams["Data"] = rawValue;
            using ManagementBaseObject outParams = instance.InvokeMethod("GetSetULong", inParams, null);
            object? result = outParams?["Return"];
            if (result == null)
            {
                return false;
            }
            string text = result.ToString() ?? "";
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = Convert.ToUInt64(text.Substring(2), 16);
            }
            else
            {
                value = Convert.ToUInt64(result);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
