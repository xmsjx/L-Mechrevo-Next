using System;
using MechrevoLite;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 屏幕开关（分区控光 / LCD Overdrive）的持久化恢复。
/// <para>这两项在 EC 模式下只有"下发"没有"回读"，重启后硬件保持上电默认值 ⇒ 只能记住上次选择并在启动时重放。
/// GCU 模式由后端自己保存状态，这里不动。</para>
/// </summary>
internal static class DisplayToggleRestore
{
    internal static void RunOnce()
    {
        try
        {
            // 只对"同方 EC 本地后端"重放：星耀走 WMAA、宝龙达走 WMI 邮箱、NUC/UMI 走本地 HID，
            // 那些机型的寄存器位置与同方 EC 不同，不能套同一组 ACPI 命令。
            var backend = WpfBackend.ActiveBackendKind;
            if (backend != MechrevoLite.Hardware.Architecture.BackendKind.TongfangEc)
            {
                Logger.WriteLine("[display-restore] 当前后端=" + backend + " ⇒ 屏幕开关由该后端自行恢复，跳过");
                return;
            }
            if (AppConfig.Get("miniled_on", 0) != 0)
            {
                if (WpfBackend.LocalDimmingChannelAvailable && WpfBackend.ApplyLocalDimming(true))
                {
                    Logger.WriteError("[display-restore] 分区控光已按上次状态开启");
                }
                else
                {
                    Logger.WriteLine("[display-restore] 上次分区控光为开，但本机通道不可用或下发失败（未重试）");
                }
            }
            if (AppConfig.Get("lcd_overdrive_on", 0) != 0)
            {
                if (WpfBackend.LcdOverdriveChannelAvailable && WpfBackend.ApplyLcdOverdrive(true))
                {
                    Logger.WriteError("[display-restore] LCD Overdrive 已按上次状态开启");
                }
                else
                {
                    Logger.WriteLine("[display-restore] 上次 OD 为开，但本机通道不可用或下发失败（未重试）");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteError("[display-restore] 恢复屏幕开关失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }
}
