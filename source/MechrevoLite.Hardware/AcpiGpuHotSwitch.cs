using System;
using System.Diagnostics;
using System.Management;
using System.Threading;

namespace MechrevoLite.Hardware;

/// <summary>
/// 免 GCU 的核显省电热切：root\WMI AcpiTest_MULong GetSetULong。
/// cmd=3 addr=3 支持位（85 支持/170 不支持），addr=2 状态位，addr=1 ON/OFF。
/// 写入前停 NVIDIA 显示容器；写入后三重校验，失败回滚。
/// </summary>
internal static class AcpiGpuHotSwitch
{
    private const string WmiClass = "root\\WMI";
    private const string Query = "SELECT * FROM AcpiTest_MULong";
    private const ulong RawSupport = 0x3_0000_0000_03;
    private const ulong RawStatus = 0x3_0000_0000_02;
    private const ulong RawOn = 0x3_0000_0000_01;
    private const ulong RawOff = 0x3_0000_0000_00;
    private const int SupportYes = 85;
    private const int StatusOn = 85;
    private const int StatusOff = 170;

    private static readonly object Gate = new();
    private static bool? _supported;

    internal static bool IsSupported
    {
        get
        {
            lock (Gate)
            {
                if (_supported.HasValue)
                {
                    return _supported.Value;
                }
                _supported = TryReadRaw(RawSupport, out ulong value) && (int)(value & 0xFF) == SupportYes;
                return _supported.Value;
            }
        }
    }

    internal static bool IsPnpFallbackAvailable => FindNvidiaInstance(out string instance, out _) && instance.Length > 0;

    internal static bool CanUseLocalSilence => IsSupported || IsPnpFallbackAvailable;

    internal static bool TryGetState(out bool active, out string detail)
    {
        active = false;
        detail = "";
        if (!IsSupported)
        {
            if (!FindNvidiaInstance(out string instance, out bool enabled))
            {
                detail = "没有 NVIDIA PnP 设备";
                return false;
            }
            active = !enabled;
            detail = "PnP 静默状态=" + (enabled ? "独显启用" : "独显禁用");
            return true;
        }
        if (!TryReadRaw(RawStatus, out ulong value))
        {
            detail = "ACPI 状态读取失败";
            return false;
        }
        int code = (int)(value & 0xFF);
        active = code == StatusOn;
        detail = "ACPI 状态=" + code;
        return code is StatusOn or StatusOff;
    }

    internal static bool TrySetIgpuOnly(bool on, bool stopDisplayContainer, out string detail)
    {
        detail = "";
        if (!IsSupported)
        {
            if (!FindNvidiaInstance(out string pnpInstance, out _))
            {
                detail = "ACPI 不支持且没有 NVIDIA PnP 设备";
                return false;
            }
            if (on)
            {
                StopNvContainers(out _);
                bool disabled = RunPnp("/disable-device", pnpInstance, force: true, out string disableDetail);
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(500);
                    if (FindNvidiaInstance(out _, out bool stillEnabled) && !stillEnabled)
                    {
                        detail = "PnP 静默独显成功 · " + disableDetail;
                        return true;
                    }
                }
                detail = "PnP 禁用未确认 · " + disableDetail;
                return false;
            }
            bool enabledOk = RunPnp("/enable-device", pnpInstance, force: false, out string enableDetail);
            RestoreContainers(new[] { "NVDisplay.ContainerLocalSystem", "NVContainerLocalSystem" });
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(500);
                if (FindNvidiaInstance(out _, out bool nowEnabled) && nowEnabled)
                {
                    detail = "PnP 独显已恢复 · " + enableDetail;
                    return true;
                }
            }
            detail = "PnP 恢复未确认 · " + enableDetail;
            return false;
        }
        string[] services = Array.Empty<string>();
        if (on && stopDisplayContainer)
        {
            services = StopNvContainers(out string stopDetail);
            detail = stopDetail;
        }
        if (!TryWriteRaw(on ? RawOn : RawOff))
        {
            detail += " · ACPI 写入失败";
            if (on) RestoreContainers(services);
            return false;
        }
        bool expected = on;
        bool confirmed = false;
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(500);
            if (TryGetState(out bool active, out _))
            {
                if (active == expected)
                {
                    confirmed = true;
                    break;
                }
            }
            if (i == 6 && on && !NvidiaPresent())
            {
                confirmed = true;
                break;
            }
        }
        if (!on)
        {
            RestoreContainers(services.Length > 0 ? services : new[] { "NVDisplay.ContainerLocalSystem", "NVContainerLocalSystem" });
        }
        if (confirmed)
        {
            detail += on ? " · 核显省电已开启" : " · 独显已恢复";
            return true;
        }
        if (on)
        {
            // 兜底回滚：不能把机器留在半切换状态。
            TryWriteRaw(RawOff);
            RestoreContainers(new[] { "NVDisplay.ContainerLocalSystem", "NVContainerLocalSystem" });
            detail += " · 未确认，已回滚独显";
        }
        return false;
    }

    /// <summary>强制开启前清理占用：只结束本会话、可安全结束的进程。</summary>
    internal static int KillClosableOccupants(out string detail)
    {
        detail = "";
        if (!MechrevoLite.Gpu.NVidia.NvidiaGpuControl.TryGetActiveDgpuApplications(out var applications))
        {
            detail = "占用读取失败";
            return 0;
        }
        int currentPid = Environment.ProcessId;
        int session = Process.GetCurrentProcess().SessionId;
        int killed = 0;
        foreach (var app in MechrevoLite.Gpu.DgpuApplicationSafety.FilterClosable(applications, currentPid, session))
        {
            try
            {
                using Process process = Process.GetProcessById(app.ProcessId);
                process.Kill(entireProcessTree: false);
                killed++;
            }
            catch (Exception ex)
            {
                detail += app.ProcessName + ":" + ex.GetType().Name + " ";
            }
        }
        if (detail.Length == 0)
        {
            detail = "已结束 " + killed + " 个占用进程";
        }
        return killed;
    }


    private const string ForcedMarkerKey = "acpi_igpu_forced_silence";

    /// <summary>强制静默成功后记录标记；异常退出后下次启动据此兜底恢复独显。</summary>
    internal static void MarkForcedSilence()
    {
        try
        {
            MechrevoLite.AppConfig.Set(ForcedMarkerKey, 1);
            MechrevoLite.AppConfig.Flush();
        }
        catch
        {
        }
    }

    internal static void ClearForcedSilence()
    {
        try
        {
            MechrevoLite.AppConfig.Remove(ForcedMarkerKey);
            MechrevoLite.AppConfig.Flush();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 应用启动兜底：上次是“强制静默”且状态仍为 iGPU-only 时，恢复独显。
    /// 正常用户主动开启的核显省电不在此自动撤销。
    /// </summary>
    internal static bool TryRecoverForcedSilenceOnStartup(out string detail)
    {
        detail = "";
        try
        {
            if (MechrevoLite.AppConfig.Get(ForcedMarkerKey, 0) == 0)
            {
                return false;
            }
            if (!TryGetState(out bool active, out string stateDetail))
            {
                ClearForcedSilence();
                detail = "无法读取 ACPI 状态，已清除强制静默标记：" + stateDetail;
                return false;
            }
            if (!active)
            {
                ClearForcedSilence();
                detail = "独显已是启用状态，清除标记";
                return false;
            }
            bool ok = TrySetIgpuOnly(false, stopDisplayContainer: false, out string restoreDetail);
            if (ok)
            {
                ClearForcedSilence();
            }
            detail = "启动兜底恢复独显 ⇒ " + ok + " · " + restoreDetail;
            return ok;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            return false;
        }
    }

    private static bool TryReadRaw(ulong raw, out ulong value)
    {
        value = 0;
        try
        {
            using ManagementObjectSearcher searcher = new(WmiClass, Query);
            foreach (ManagementObject item in searcher.Get())
            {
                using ManagementBaseObject inParams = item.GetMethodParameters("GetSetULong");
                inParams["Data"] = raw;
                using ManagementBaseObject outParams = item.InvokeMethod("GetSetULong", inParams, null);
                object? result = outParams?["Return"];
                if (result == null) continue;
                string text = result.ToString() ?? "";
                value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt64(text.Substring(2), 16)
                    : Convert.ToUInt64(result);
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryWriteRaw(ulong raw)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(WmiClass, Query);
            foreach (ManagementObject item in searcher.Get())
            {
                using ManagementBaseObject inParams = item.GetMethodParameters("GetSetULong");
                inParams["Data"] = raw;
                using ManagementBaseObject outParams = item.InvokeMethod("GetSetULong", inParams, null);
                return outParams != null;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string[] StopNvContainers(out string detail)
    {
        string[] names = { "NVDisplay.ContainerLocalSystem", "NVContainerLocalSystem", "NVDisplay.ContainerUser", "NVContainerUser" };
        var stopped = new System.Collections.Generic.List<string>();
        detail = "";
        foreach (string name in names)
        {
            try
            {
                using Process p = Process.Start(new ProcessStartInfo("net", "stop \"" + name + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })!;
                p.WaitForExit(5000);
                // 已在运行时 net stop 返回非0，按“可能已停”继续。
                stopped.Add(name);
            }
            catch
            {
            }
        }
        detail = "已请求停止 NVIDIA 显示容器";
        return stopped.ToArray();
    }

    private static void RestoreContainers(string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                using Process p = Process.Start(new ProcessStartInfo("net", "start \"" + name + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })!;
                p.WaitForExit(5000);
            }
            catch
            {
            }
        }
    }

    private static bool FindNvidiaInstance(out string instanceId, out bool enabled)
    {
        instanceId = "";
        enabled = false;
        try
        {
            using ManagementObjectSearcher searcher = new("root\\CIMV2",
                "SELECT DeviceID,Status FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\VEN_10DE%'");
            foreach (ManagementObject item in searcher.Get())
            {
                string? id = item["DeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                instanceId = id;
                enabled = string.Equals(item["Status"]?.ToString(), "OK", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool RunPnp(string action, string instanceId, bool force, out string detail)
    {
        detail = "";
        try
        {
            string args = action + " \"" + instanceId + "\"" + (force ? " /force" : "");
            using Process p = Process.Start(new ProcessStartInfo("pnputil.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            detail = "pnputil " + action + " exit=" + p.ExitCode + " " + output.Trim();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            return false;
        }
    }

    private static bool NvidiaPresent()
    {
        try
        {
            using ManagementObjectSearcher searcher = new("root\\CIMV2",
                "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\VEN_10DE%'");
            foreach (ManagementObject item in searcher.Get())
            {
                return true;
            }
        }
        catch
        {
        }
        return false;
    }
}
