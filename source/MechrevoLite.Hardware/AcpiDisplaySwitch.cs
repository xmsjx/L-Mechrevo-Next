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

    internal static bool TrySetLocalDimming(bool on)
        => TryInvoke(on ? LocalDimmingOn : LocalDimmingOff, "LocalDimming=" + on);

    internal static bool TrySetLcdOverdrive(bool on)
        => TryInvoke(on ? LcdOverdriveOn : LcdOverdriveOff, "LcdOverdrive=" + on);

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
