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
            Logger.WriteLine("[acpi-display] EC MiniLED 支持=" + supported + " raw=0x" + raw.ToString("X2"));
            return supported;
        }
        // 读不到（驱动未装/通道暂不可用）不写缓存：否则"读不到"会被固化成"不支持"，
        // 等通道恢复后界面还是灰的，必须重启才认。
        bool fallback = MechrevoLite.AppConfig.Get("ec_miniled_supported", 0) != 0;
        Logger.WriteLineIfChanged("acpi-display-miniled", "[acpi-display] MiniLED 通道暂不可读 ⇒ 按上次记录=" + fallback);
        return fallback;
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
            Logger.WriteLine("[acpi-display] EC OD 支持=" + supported + " raw=0x" + value.ToString("X"));
            return supported;
        }
        // 同上：通道不可读时不写缓存，等能读时再判。
        bool fallback = MechrevoLite.AppConfig.Get("ec_od_supported", 0) != 0;
        Logger.WriteLineIfChanged("acpi-display-od", "[acpi-display] OD 通道暂不可读 ⇒ 按上次记录=" + fallback);
        return fallback;
    }

    /// <summary>
    /// 分区控光下发（本地 EC/ACPI 通道）。成功即把选择记在本机 —— 这类开关没有回读通道，
    /// 固件开机回默认值，只能靠本机记忆 + 开机重放（<c>DisplayToggleRestore</c>）。
    /// </summary>
    internal static bool TrySetLocalDimming(bool on, bool rememberIntent = true)
    {
        bool ok = TryInvoke(on ? LocalDimmingOn : LocalDimmingOff, "LocalDimming=" + on);
        if (ok && rememberIntent)
        {
            MechrevoService.SaveDisplayToggleIntent(MechrevoService.LocalDimmingSavedKey, on);
        }
        return ok;
    }

    /// <summary>LCD Overdrive 下发（同分区控光：成功即记本机意图，供开机重放）。</summary>
    internal static bool TrySetLcdOverdrive(bool on, bool rememberIntent = true)
    {
        bool ok = TryInvoke(on ? LcdOverdriveOn : LcdOverdriveOff, "LcdOverdrive=" + on);
        if (ok && rememberIntent)
        {
            MechrevoService.SaveDisplayToggleIntent(MechrevoService.LcdOverdriveSavedKey, on);
        }
        return ok;
    }

    /// <summary>查询一路 ULong：失败计数与暂停交给共享阶梯退避（ReadFailureBackoff），本类不再自己算。</summary>
    private static bool TryRead(ulong data, out ulong value)
    {
        ulong? result = null;
        bool ok = ReadFailureBackoff.TryRun("acpi-mulong", "查询 0x" + data.ToString("X") + " 失败",
            () =>
            {
                result = QueryMulong(data);
                return result.HasValue;
            });
        value = result ?? 0;
        return ok;
    }

    /// <summary>真正的 WMI 查询；失败返回 null，异常交给退避层记账（不在这里逐次打日志）。</summary>
    private static ulong? QueryMulong(ulong data)
    {
        try
        {
            using ManagementObjectSearcher searcher = new("root\\WMI", "SELECT * FROM AcpiTest_MULong");
            foreach (ManagementObject item in searcher.Get())
            {
                using ManagementBaseObject inParams = item.GetMethodParameters("GetSetULong");
                inParams["Data"] = data;
                using ManagementBaseObject outParams = item.InvokeMethod("GetSetULong", inParams, null);
                object? ret = outParams?["Return"];
                if (ret == null)
                {
                    return null;
                }
                string text = ret.ToString() ?? "";
                return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt64(text.Substring(2), 16)
                    : Convert.ToUInt64(ret);
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>下发一路 ULong：同样走共享阶梯退避。</summary>
    private static bool TryInvoke(ulong data, string what)
        => ReadFailureBackoff.TryRun("acpi-mulong", what + " 失败", () => InvokeMulong(data, what));

    /// <summary>真正的 WMI 下发；异常交给退避层记账。</summary>
    private static bool InvokeMulong(ulong data, string what)
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
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
