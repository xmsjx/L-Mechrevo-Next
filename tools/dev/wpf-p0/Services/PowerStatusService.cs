using System;
using MechrevoLite;
using MechrevoLite.Hardware;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>电源/电池元数据快照（WPF 与迷你窗共用；⛔ 不含 UI 文案）。</summary>
internal sealed record PowerStatusSnapshot(
    PowerInputSample? Input,
    decimal? BatteryRateWatts,
    int Percent,
    bool AcOnline,
    bool Charging,
    bool LocalSamplingUnavailable);

/// <summary>
/// ⭐ 电源/电池静态采样的唯一 WPF 服务入口（P0-3 的补充）。
/// <para>旧 <c>HardwareControl</c> 的采样 API 仍属业务程序集；窗口不再直接散读静态字段。</para>
/// </summary>
internal static class PowerStatusService
{
    /// <summary>供电方式补刷的节流时间戳（采样器自身 5 秒窗口，这里只防重复派任务）。</summary>
    private static long _lastPowerInputKick;

    internal static PowerStatusSnapshot Read(bool sample = false)
    {
        try
        {
            if (sample)
            {
                // CPU/GPU 功耗、显存频率等属于本地采样链，不能只刷新电源输入/电池。
                HardwareControl.SampleLocalPower();
            }
            else
            {
                // 电池充放功率、供电方式（适配器瓦数）都不在常驻采样泵里（那条链刻意跳过辅助项），
                // 界面每次取快照时补一次：电池功率自带 5 秒节流；供电方式放后台跑，避免那 250ms 的等待卡住 UI。
                try
                {
                    HardwareControl.RefreshBatteryRate();
                }
                catch
                {
                }
                if (Environment.TickCount64 - Volatile.Read(ref _lastPowerInputKick) >= 4000)
                {
                    Interlocked.Exchange(ref _lastPowerInputKick, Environment.TickCount64);
                    try
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                HardwareControl.RefreshPowerInput();
                            }
                            catch
                            {
                            }
                        });
                    }
                    catch
                    {
                    }
                }
            }
            int percent = HardwareControl.GetBatteryChargePercentage();
            if (percent <= 0)
            {
                try
                {
                    percent = (int)Math.Round(System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent * 100);
                }
                catch
                {
                }
            }
            return new PowerStatusSnapshot(
                HardwareControl.PowerInput,
                HardwareControl.batteryRate,
                percent,
                System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online,
                System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging),
                HardwareControl.LocalPowerSamplingUnavailable);
        }
        catch
        {
            return new PowerStatusSnapshot(null, null, -1, false, false, true);
        }
    }

    internal static void ReadOverlay()
    {
        try { HardwareControl.ReadSensorsOverlay(); } catch { }
    }

    internal static void SampleLocalPower()
    {
        try { HardwareControl.SampleLocalPower(); } catch { }
    }

    internal static string ReadPowerWallText()
    {
        try
        {
            if (HardwareControl.LocalPowerSamplingUnavailable)
            {
                return PowerWallVerifier.UnavailableText;
            }
            PowerWallVerdict verdict = HardwareControl.powerWall.Evaluate(DateTime.Now);
            return verdict is PowerWallVerdict.Enforced or PowerWallVerdict.NotEnforced
                ? HardwareControl.powerWall.Describe(DateTime.Now)
                : "";
        }
        catch
        {
            return "";
        }
    }
}
