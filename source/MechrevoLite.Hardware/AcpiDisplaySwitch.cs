using System;
using System.Management;

namespace MechrevoLite.Hardware;

/// <summary>屏幕分区控光 / LCD Overdrive 的 ACPI WMI 直写通道。</summary>
internal static class AcpiDisplaySwitch
{
    private const ulong LocalDimmingOn = 5497558138894UL;  // 0x5000000000E
    private const ulong LocalDimmingOff = 5497558138893UL; // 0x5000000000D
    private const ulong LcdOverdriveOn = 5497558138904UL;  // 0x50000000018
    private const ulong LcdOverdriveOff = 5497558138903UL; // 0x50000000017
    private const ulong LcdOverdriveSupportQuery = 5497558138902UL; // 0x50000000016
    private const int LocalDimmingSupportAddress = 3407; // 0x0D4F bit0

    private static bool? _localDimmingSupport;
    private static bool? _lcdOverdriveSupport;

    internal static bool? LocalDimmingSupportCached => _localDimmingSupport;
    internal static bool? LcdOverdriveSupportCached => _lcdOverdriveSupport;

    /// <summary>EC 路能力：读 0x0D4F bit0，结果写入软件配置。</summary>
    internal static bool QueryLocalDimmingSupport()
    {
        if (_localDimmingSupport.HasValue)
        {
            return _localDimmingSupport.Value;
        }
        bool ok = AcpiDriverEc.TryReadByte(LocalDimmingSupportAddress, out byte raw);
        bool supported = ok && raw != 0 && raw != 0xFF && (raw & 0x01) != 0;
        if (ok)
        {
            _localDimmingSupport = supported;
            MechrevoLite.AppConfig.Set("ec_miniled_supported", supported ? 1 : 0);
            MechrevoLite.AppConfig.Flush();
        }
        else
        {
            _localDimmingSupport = MechrevoLite.AppConfig.Get("ec_miniled_supported", 0) != 0;
        }
        Logger.WriteLine("[acpi-display] EC MiniLED 支持=" + _localDimmingSupport + " raw=0x" + raw.ToString("X2"));
        return _localDimmingSupport.Value;
    }

    /// <summary>EC 路能力：查询 GetSetULong 0x50000000016，返回 0x55 为支持 OD。</summary>
    internal static bool QueryLcdOverdriveSupport()
    {
        if (_lcdOverdriveSupport.HasValue)
        {
            return _lcdOverdriveSupport.Value;
        }
        bool ok = TryRead(LcdOverdriveSupportQuery, out ulong value);
        bool supported = ok && (byte)value == 0x55;
        if (ok)
        {
            _lcdOverdriveSupport = supported;
            MechrevoLite.AppConfig.Set("ec_od_supported", supported ? 1 : 0);
            MechrevoLite.AppConfig.Flush();
        }
        else
        {
            _lcdOverdriveSupport = MechrevoLite.AppConfig.Get("ec_od_supported", 0) != 0;
        }
        Logger.WriteLine("[acpi-display] EC OD 支持=" + _lcdOverdriveSupport + " raw=0x" + value.ToString("X"));
        return _lcdOverdriveSupport.Value;
    }

    internal static bool TrySetLocalDimming(bool on)
        => TryInvoke(on ? LocalDimmingOn : LocalDimmingOff, "LocalDimming=" + on);

    internal static bool TrySetLcdOverdrive(bool on)
        => TryInvoke(on ? LcdOverdriveOn : LcdOverdriveOff, "LcdOverdrive=" + on);

    private static bool TryRead(ulong data, out ulong value)
    {
        value = 0;
        try
        {
            using ManagementObjectSearcher searcher = new("root\\WMI", "SELECT * FROM AcpiTest_MULong");
            foreach (ManagementObject item in searcher.Get())
            {
                using ManagementBaseObject inParams = item.GetMethodParameters("GetSetULong");
                inParams["Data"] = data;
                using ManagementBaseObject outParams = item.InvokeMethod("GetSetULong", inParams, null);
                object? result = outParams?["Return"];
                if (result == null)
                {
                    return false;
                }
                string text = result.ToString() ?? "";
                value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt64(text.Substring(2), 16)
                    : Convert.ToUInt64(result);
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[acpi-display] 查询 0x" + data.ToString("X") + " 失败：" + ex.GetType().Name + " " + ex.Message);
        }
        return false;
    }

    private static bool TryInvoke(ulong data, string what)
    {
        try
        {
            using ManagementObjectSearcher searcher = new("root\\WMI", "SELECT * FROM AcpiTest_MULong");
            foreach (ManagementObject item in searcher.Get())
            {
                using ManagementBaseObject inParams = item.GetMethodParameters("GetSetULong");
                inParams["Data"] = data;
                using ManagementBaseObject outParams = item.InvokeMethod("GetSetULong", inParams, null);
                bool ok = outParams != null;
                Logger.WriteLine("[acpi-display] " + what + " data=0x" + data.ToString("X") + " ⇒ " + ok);
                return ok;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[acpi-display] " + what + " 失败：" + ex.GetType().Name + " " + ex.Message);
        }
        return false;
    }
}
