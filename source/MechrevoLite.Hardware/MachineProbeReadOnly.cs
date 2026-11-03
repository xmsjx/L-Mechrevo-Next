using System;
using System.Management;
using System.Threading;
using Microsoft.Win32;

namespace MechrevoLite.Hardware;

/// <summary>只读机型/通道事实探针。</summary>
internal static class MachineProbeReadOnly
{
    private static int _logged;

    internal static void LogOnce()
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
        {
            return;
        }
        try
        {
            string board = ReadRegistryString(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct");
            string model = ReadRegistryString(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
            bool acpi = AcpiDriverEc.TryReadByte(1963, out byte mode);
            bool nvram = UefiGpuMux.TryRead(out int nvramRaw, out string nvramVariable, out _);
            bool uwacpi = ServiceExists("UWACPIDriver");
            bool gcu = ServiceExists("GCUBridge") || ServiceExists("GCUService") || ServiceExists("AiStoneService");
            bool nvidia = HasNvidiaDgpu();
            bool mulong = TurboSubModeWmi.IsSupported();
            Logger.WriteError("[machine-probe] board=" + board
                + " model=" + model
                + " acpi=" + acpi
                + " nvram=" + (nvram ? nvramVariable + ":0x" + nvramRaw.ToString("X2") : "absent")
                + " uwacpiService=" + uwacpi
                + " gcuService=" + gcu
                + " nvidiaDgpu=" + nvidia
                + " mulongFunctional=" + mulong
                + " ecMode=" + (acpi ? "0x" + mode.ToString("X2") : "unreadable"));
        }
        catch (Exception ex)
        {
            Logger.WriteError("[machine-probe] failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static string ReadRegistryString(string path, string name)
    {
        try
        {
            object? value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + path, name, null);
            return value?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool ServiceExists(string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasNvidiaDgpu()
    {
        try
        {
            using ManagementObjectSearcher searcher = new("root\\CIMV2", "SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject item in searcher.Get())
            {
                string name = item["Name"]?.ToString() ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }
}
