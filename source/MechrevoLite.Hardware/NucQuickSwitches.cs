using System;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MechrevoLite.Hardware;

// 机型适配参考：OpenRevo (https://github.com/faintonce/open-revo, GPL-3.0)。

/// <summary>NUC/UMI 高频快捷开关：Fn 锁、摄像头、Win 键锁，直接走 ACPI EC。</summary>
internal static class NucQuickSwitches
{
    private const int FnLockAddress = 1870;      // 0x74E bit4
    private const int CameraCutoffAddress = 1831; // 0x727 bit3
    private const int WinKeyStatusAddress = 1896; // 0x768 bit0
    private const int TriggerAddress = 1895;      // 0x767 bit0
    private const int FnLockBit = 0x10;
    private const int CameraCutoffBit = 0x08;
    private const int WinKeyBit = 0x01;
    private const int UsbChargeLevelBit = 0x10;   // 0x767 bit4 关机 USB 给电（常驻电平）
    private const int AcRecoveryAddress = 1830; // 0x726 bit3
    private const int AcRecoveryBit = 0x08;
    private const int CopilotAddress = 1832; // 0x728 bit2
    private const int CopilotBit = 0x04;
    private const int FanBoostAddress = 1873; // 0x751
    private const int FanBoostBit = 0x40;

    /// <summary>声明表指定「关机 USB 给电」走本地 EC 通道（未声明 ⇒ 沿用原有通道）。</summary>
    internal static bool UsesEcUsbCharge => DeclarationRuntime.Flag("usbCharge", "ec");

    /// <summary>
    /// 关机 USB 给电的口径状态：这一位重启就丢，所以「想不想开」只能由软件记忆（持久化配置）回答；
    /// EC 里的 bit4 只是这次开机会话里有没有真的落下，用来确认和回读，不能当唯一来源。
    /// </summary>
    internal static bool TryGetUsbChargeMemory(out bool on)
    {
        on = AppConfig.Get("usb_charge", 0) != 0;
        return true;
    }

    /// <summary>读回 bit4 电平（确认用）；读不到返回 false。</summary>
    internal static bool TryReadUsbCharging(out bool on)
    {
        on = false;
        if (!EcTriggerByte.TryRead(out byte value))
        {
            return false;
        }
        on = (value & UsbChargeLevelBit) != 0;
        return true;
    }

    /// <summary>
    /// 开机按保存的配置把关机 USB 给电重下发一次。
    /// 这一位是常驻电平、开机后会被清掉，不重下发就会「上次设的开、这次开机就没了」。
    /// 同方与 NUC/UMI 都走这一位；GCU 在管这条设置的机型交给 GCU，这里不动。
    /// </summary>
    internal static bool ApplyPersistedUsbCharge(MechrevoHw hw)
    {
        if (!string.IsNullOrWhiteSpace(GcuIdentity.Variant))
        {
            return false;
        }
        if (AppConfig.Get("usb_charge", 0) == 0)
        {
            // 保存的是关：不动硬件，避免把固件自己的默认值改掉。
            return false;
        }
        bool ok = TrySetUsbChargingLevel(true, out bool confirmed);
        if (confirmed)
        {
            hw.QuickSwitches["usb"] = true;
        }
        Logger.WriteLine($"[usb] 开机重下发关机 USB 给电 ⇒ 写={ok} 确认={confirmed}");
        return confirmed;
    }

    /// <summary>
    /// 电平写 bit4：写目标值后回读确认，状态可以直接读出来。
    /// 写这一位有可能被固件顺带清掉来电自启（1830 bit3），按写前的值恢复。
    /// </summary>
    internal static bool TrySetUsbChargingLevel(bool on, out bool confirmed)
    {
        confirmed = false;
        bool acBefore = TryRead(AcRecoveryAddress, out int acOld) && (acOld & AcRecoveryBit) != 0;
        // 走统一入口，只改 bit4，键盘灯锁存/风扇表/彩虹位原样保留。
        bool ok = on
            ? EcTriggerByte.TryApply(UsbChargeLevelBit, 0, ownUsbPulse: true)
            : EcTriggerByte.TryApply(0, UsbChargeLevelBit, ownUsbPulse: true);
        if (!ok)
        {
            return false;
        }
        if (TryRead(AcRecoveryAddress, out int acAfter) && (acAfter & AcRecoveryBit) == 0 && acBefore)
        {
            TryWrite(AcRecoveryAddress, acAfter | AcRecoveryBit);
        }
        if (EcTriggerByte.TryRead(out byte after))
        {
            confirmed = ((after & UsbChargeLevelBit) != 0) == on;
            return confirmed;
        }
        return false;
    }

    internal static bool TryReadFnLock(out bool on)
    {
        on = false;
        if (!TryRead(FnLockAddress, out int current))
        {
            return false;
        }
        on = (current & FnLockBit) != 0;
        return true;
    }

    internal static bool TrySetFnLock(bool on, out bool confirmed)
    {
        confirmed = false;
        if (DeclarationRuntime.FeatureBlocked("quickSwitch", "ec"))
        {
            return false;
        }
        if (!TryRead(FnLockAddress, out int current))
        {
            return false;
        }
        int target = on ? (current | FnLockBit) : (current & ~FnLockBit);
        if (!TryWrite(FnLockAddress, target))
        {
            return false;
        }
        confirmed = TryRead(FnLockAddress, out int after) && ((after & FnLockBit) != 0) == on;
        if (confirmed)
        {
            // 这一位重启会被固件清掉，用户想开/想关只能软件记忆。
            AppConfig.Set("fn_lock", on ? 1 : 0);
        }
        return confirmed;
    }

    /// <summary>Fn 锁的持久意图（EC 里那一位重启就丢，这里才是真相）。</summary>
    internal static bool TryGetFnLockMemory(out bool on)
    {
        on = AppConfig.Get("fn_lock", 0) != 0;
        return true;
    }

    /// <summary>
    /// 开机按保存的配置把 Fn 锁重下发一次；保存的是关就完全不动硬件，避免改掉固件默认值。
    /// </summary>
    internal static bool ApplyPersistedFnLock(MechrevoHw hw)
    {
        if (AppConfig.Get("fn_lock", 0) == 0)
        {
            return false;
        }
        if (!TrySetFnLock(on: true, out bool confirmed))
        {
            return false;
        }
        if (confirmed)
        {
            hw.QuickSwitches["fnkey"] = true;
        }
        Logger.WriteLine($"[fnkey] 开机重下发 Fn 锁 ⇒ 确认={confirmed}");
        return confirmed;
    }

    /// <summary>on=true 表示摄像头启用；EC bit3=1 表示物理断电。</summary>
    internal static bool TrySetCamera(bool on, out bool confirmed)
    {
        confirmed = false;
        if (!TryRead(CameraCutoffAddress, out int current))
        {
            return false;
        }
        int target = on ? (current & ~CameraCutoffBit) : (current | CameraCutoffBit);
        if (!TryWrite(CameraCutoffAddress, target))
        {
            return false;
        }
        confirmed = TryRead(CameraCutoffAddress, out int after) && ((after & CameraCutoffBit) == 0) == on;
        return confirmed;
    }

    internal static bool TryReadWinKeyLock(out bool on)
    {
        on = false;
        if (!TryRead(WinKeyStatusAddress, out int status))
        {
            return false;
        }
        on = (status & WinKeyBit) != 0;
        return true;
    }

    internal static bool TrySetWinKeyLock(bool on, out bool confirmed)
    {
        confirmed = false;
        if (DeclarationRuntime.FeatureBlocked("quickSwitch", "ec"))
        {
            return false;
        }
        if (!TryRead(WinKeyStatusAddress, out int status))
        {
            return false;
        }
        if (((status & WinKeyBit) != 0) == on)
        {
            confirmed = true;
            return true;
        }
        // bit0 是快捷开关的翻转位：走统一入口，只改这一位，其余位原样保留。
        if (!EcTriggerByte.TryApply(EcTriggerByte.QuickSwitchLatch, 0, ownQuickSwitch: true))
        {
            return false;
        }
        // 触发位写入成功即视为已下发；状态位可能滞后，不再阻塞 600ms 等待。
        confirmed = TryRead(WinKeyStatusAddress, out int now) && ((now & WinKeyBit) != 0) == on;
        return true;
    }

    internal static bool TrySetTouchpad(bool on, out bool confirmed)
    {
        confirmed = false;
        // ① 合成 Win+Ctrl+F24（由 Windows 的 Precision Touchpad 状态机执行开关）→ ② Windows 设置同款 API。
        // ⛔ 触摸板开关不写 EC：0x7A6 bit3 不是开关位（开机就是 0x29，触摸板照样能用），
        // 读改写它会改掉固件刚写好的位，Fn 组合会被判成别的功能（实测表现为麦克风静音）。
        bool known = ReadPtpRegistry(out bool ptpNow);
        if (known && ptpNow == on)
        {
            confirmed = true;
            Logger.WriteLine($"[touchpad] 已是目标状态 PTP={on} · 不下发");
            return true;
        }
        if (known)
        {
            SendTouchpadHotkey();
            for (int waited = 80; waited <= 800; waited += 80)
            {
                Thread.Sleep(80);
                if (ReadPtpRegistry(out bool ptpAfter) && ptpAfter == on)
                {
                    confirmed = true;
                    Logger.WriteLine($"[touchpad] Win+Ctrl+F24 ⇒ PTP={on}（{waited}ms）· 不写 EC");
                    return true;
                }
            }
            Logger.WriteLine("[touchpad] Win+Ctrl+F24 未生效（PTP 未翻转）⇒ 退回 Windows 设置 API");
        }
        WritePtpRegistry(on);
        bool systemOk = TrySystemTouchpad(on, out string systemDetail);
        if (systemOk)
        {
            confirmed = ReadPtpRegistry(out bool ptpOn) ? ptpOn == on : true;
            Logger.WriteLine($"[touchpad] Windows API PTP={confirmed}（{systemDetail}）· 不写 EC");
            return confirmed;
        }
        // ⛔ 不再写 EC 兜底：触摸板没有 EC 开关位，写下去只会串掉固件位。
        Logger.WriteError($"[touchpad] 两路都不可用（{systemDetail}）· 不写 EC，放弃本次下发");
        return false;
    }

    /// <summary>
    /// 合成 <c>Win + Ctrl + F24</c>。
    /// <para>官方控制台 <c>DeviceSwitchItem.TriggerTouchpad()</c> 就是发这个组合（同方机器通用），
    /// 触摸板开关由 Windows 处理，结果会写回 <c>PTP Status\Enabled</c>，我们据此回读确认。</para>
    /// <para>⚠️ 只调 Windows 公开 API，⛔ 不碰 EC、⛔ 不碰厂商寄存器。</para>
    /// </summary>
    private static void SendTouchpadHotkey()
    {
        const byte VkLWin = 0x5B;
        const byte VkLCtrl = 0xA2;
        const byte VkF24 = 0x87;
        const uint KeyUp = 0x0002;
        keybd_event(VkLWin, 0, 0, UIntPtr.Zero);
        keybd_event(VkLCtrl, 0, 0, UIntPtr.Zero);
        keybd_event(VkF24, 0, 0, UIntPtr.Zero);
        keybd_event(VkF24, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLCtrl, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLWin, 0, KeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    internal static bool TrySetAcRecovery(bool on, out bool confirmed)
    {
        confirmed = false;
        if (DeclarationRuntime.FeatureBlocked("acRecovery", "ec"))
        {
            return false;
        }
        if (!TryRead(AcRecoveryAddress, out int current))
        {
            return false;
        }
        int target = on ? (current | AcRecoveryBit) : (current & ~AcRecoveryBit);
        if (!TryWrite(AcRecoveryAddress, target))
        {
            return false;
        }
        confirmed = TryRead(AcRecoveryAddress, out int after) && ((after & AcRecoveryBit) != 0) == on;
        return confirmed;
    }

    /// <summary>Copilot 键锁：读改写 0x728 bit2，只动这一位。</summary>
    internal static bool TrySetCopilot(bool on, out bool confirmed)
    {
        confirmed = false;
        if (!TryRead(CopilotAddress, out int current))
        {
            return false;
        }
        int target = on ? (current | CopilotBit) : (current & ~CopilotBit);
        if (!TryWrite(CopilotAddress, target))
        {
            return false;
        }
        confirmed = TryRead(CopilotAddress, out int after) && ((after & CopilotBit) != 0) == on;
        return confirmed;
    }

    internal static bool TrySetFanBoost(bool on, out bool confirmed)
    {
        confirmed = false;
        // 声明 blocked=1 的机型没有独立强冷位（0x751 那一位是档位字节），必须整条拒绝，否则会顺手改档位。
        if (DeclarationRuntime.Flag("fanBoost", "blocked"))
        {
            Logger.WriteErrorIfChanged("fan-boost-blocked", "[fan-boost] 该机型没有独立强冷位，拒绝写入（未碰 0x751）");
            return false;
        }
        if (DeclarationRuntime.FeatureBlocked("fanBoost", "ec", "gcu"))
        {
            return false;
        }
        // NUC/UMI 30 官方 SetEcFanBoost：开写 0x40，关写 0x00；
        // 保留同字节其它位，避免踩到 Q-Key/其它 clamp 状态。
        if (!TryRead(FanBoostAddress, out int current))
        {
            return false;
        }
        int target = on ? (current | 0x40) : (current & ~0x40);
        if (!TryWrite(FanBoostAddress, target))
        {
            return false;
        }
        confirmed = TryRead(FanBoostAddress, out int after) && ((after & 0x40) != 0) == on;
        return confirmed;
    }

    private const string SystemTouchpadTool = @"C:\Windows\System32\SystemSettingsAdminFlows.exe";

    private static bool TrySystemTouchpad(bool on, out string detail)
    {
        detail = "";
        try
        {
            if (!File.Exists(SystemTouchpadTool))
            {
                detail = "SystemSettingsAdminFlows 不存在";
                return false;
            }
            ProcessStartInfo psi = new(SystemTouchpadTool, "EnableTouchpad " + (on ? "1" : "0"))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process? process = Process.Start(psi))
            {
                if (process == null)
                {
                    detail = "启动失败";
                    return false;
                }
                process.WaitForExit(2000);
            }
            if (on)
            {
                // 从设备管理器 PnP 禁用状态恢复；只做 enable，不做 disable（禁用由 Windows 输入子系统负责）。
                foreach (string deviceId in new[] { @"HID\UNIW0001&Col01", @"HID\UNIW0001&Col02" })
                {
                    ProcessStartInfo pnputil = new("pnputil", "/enable-device /deviceid \"" + deviceId + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using Process? p = Process.Start(pnputil);
                    p?.WaitForExit(1500);
                }
            }
            detail = "已调用 Windows EnableTouchpad";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name;
            return false;
        }
    }

    private static void WritePtpRegistry(bool on)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status", writable: true);
            key?.SetValue("Enabled", on ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    private static bool ReadPtpRegistry(out bool on)
    {
        on = true;
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status");
            object? value = key?.GetValue("Enabled");
            if (value is int i)
            {
                on = i != 0;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    internal static void Sync(MechrevoHw hw)
    {
        if (TryRead(FnLockAddress, out int fn))
        {
            hw.QuickSwitches["fnkey"] = (fn & FnLockBit) != 0;
        }
        if (CameraPnpControl.TryGetEnabled(out bool cameraEnabled, out _))
        {
            hw.QuickSwitches["webcam"] = cameraEnabled;
        }
        else if (TryRead(CameraCutoffAddress, out int cam))
        {
            hw.QuickSwitches["webcam"] = (cam & CameraCutoffBit) == 0;
        }
        if (TryRead(WinKeyStatusAddress, out int win))
        {
            hw.QuickSwitches["winkey"] = (win & WinKeyBit) != 0;
        }
        // 触摸板状态只认 Windows PTP；0x7A6 bit3 不是触摸板开关位（见 TrySetTouchpad 注释）。
        if (ReadPtpRegistry(out bool ptpEnabled))
        {
            hw.QuickSwitches["touchpad"] = ptpEnabled;
        }
        if (TryRead(AcRecoveryAddress, out int ac))
        {
            hw.QuickSwitches["acrecovery"] = (ac & AcRecoveryBit) != 0;
        }
        if (TryRead(FanBoostAddress, out int fb))
        {
            hw.QuickSwitches["fanboost"] = (fb & FanBoostBit) != 0;
        }
        // USB 供电没有常驻状态位，只能回读上次成功下发的持久化值。
        hw.QuickSwitches["usb"] = AppConfig.Get("usb_charge", 0) != 0;
    }

    private static bool TryRead(int address, out int value)
    {
        if (AcpiDriverEc.TryReadByte(address, out byte b))
        {
            value = b;
            return true;
        }
        if (EcPortIo.TryReadByteStable(address, out int legacy, out _))
        {
            value = legacy;
            return true;
        }
        value = -1;
        return false;
    }

    private static bool TryWrite(int address, int value)
    {
        return AcpiDriverEc.TryWriteByte(address, value) || EcPortIo.TryWriteByte(address, value);
    }
}
