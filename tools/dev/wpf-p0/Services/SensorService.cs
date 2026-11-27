using MechrevoLite;
using MechrevoLite.Hardware;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 传感器读取（两窗共用同一份快照口径）。
/// <para>本服务只做「后端快照 → 显示快照」的映射：⛔ 不读 NVML / nvidia-smi / EC，
/// ⛔ 不触发采样（采样由后端的 2.5 s 常驻采样泵负责，见 HardwareControl.CurrentLocalSnapshot）。</para>
/// </summary>
internal interface ISensorService
{
    /// <summary>读一次传感器快照；未连接/异常 ⇒ <see cref="SensorSnapshot.Empty" />。</summary>
    SensorSnapshot Read();
}

/// <inheritdoc />
internal sealed class SensorService : ISensorService
{
    private readonly BackendAccessor _backend;

    internal SensorService(BackendAccessor backend)
    {
        _backend = backend;
    }

    private static int Prefer(int backend, int local)
    {
        // 30/50 GCU 某些遥测字段为 0/未就绪；旧版直接读 LHM/NVML 本地值，
        // 这里在 backend 值为空时回退到本地采样，避免整页显示 --。
        return backend > 0 ? backend : local;
    }

    /// <summary>
    /// 取一次传感器快照。
    /// <para>本地值一律来自后端快照，界面线程不碰任何硬件通道 —— 原先在界面线程上同步读
    /// EC 风扇寄存器 / NVML / nvidia-smi，会与后台采样泵争同一条串行通路，窗口在前台时每 2 s 卡一次。</para>
    /// </summary>
    public SensorSnapshot Read()
    {
        MechrevoHw? hw = _backend.Hw;
        bool connected = hw != null && hw.IsConnected;
        try
        {
            HardwareControl.LocalSensorSnapshot snap = HardwareControl.CurrentLocalSnapshot();
            bool preferLocalTemp = hw?.SmuCustomApu == true || hw?.WujieSmuApu == true
                || hw?.IsNucUmiFamily == true
                || hw?.IsYaoshiMachine == true;
            int cpuTemp = preferLocalTemp && snap.CpuTemp > 0
                ? snap.CpuTemp
                : Prefer(connected ? hw!.CpuTemp : -1, snap.CpuTemp);
            int gpuTemp = preferLocalTemp && snap.GpuTemp > 0
                ? snap.GpuTemp
                : Prefer(connected ? hw!.GpuTemp : -1, snap.GpuTemp);
            int cpuFanRpm = Prefer(connected ? hw!.CpuFanRpm : -1, snap.CpuFanRpm);
            int gpuFanRpm = Prefer(connected ? hw!.GpuFanRpm : -1, snap.GpuFanRpm);
            int cpuDutyPercent = Prefer(connected ? hw!.CpuFanDuty : -1, snap.CpuFanDuty ?? 0);
            int gpuDutyPercent = Prefer(connected ? hw!.GpuFanDuty : -1, snap.GpuFanDuty ?? 0);
            return new SensorSnapshot(
                HardwareAvailable: true,
                CpuTempC: cpuTemp,
                GpuTempC: gpuTemp,
                CpuFanRpm: cpuFanRpm,
                GpuFanRpm: gpuFanRpm,
                CpuUsagePercent: Prefer(connected ? hw!.CpuUsage : -1, snap.CpuUsage ?? 0),
                GpuUsagePercent: Prefer(connected ? hw!.GpuUsage : -1, snap.GpuUsage ?? 0),
                CpuFanDutyPercent: cpuDutyPercent,
                GpuFanDutyPercent: gpuDutyPercent,
                CpuFrequencyMhz: Prefer(connected ? hw!.CpuFrequency : -1, snap.CpuFrequency),
                GpuCoreFrequencyMhz: Prefer(connected ? hw!.GpuCoreFreq : -1, snap.GpuCoreFreq),
                FanRpmMaxForPercent: connected ? hw!.FanRpmPercentDisplayMax : 6000,
                CpuPowerWatts: snap.CpuPower,
                GpuPowerWatts: snap.GpuPower,
                GpuMemoryClockMhz: snap.GpuMemoryClockMhz > 0 ? snap.GpuMemoryClockMhz : null,
                GpuVramUsagePercent: snap.VramUsage ?? -1);
        }
        catch
        {
            return SensorSnapshot.Empty;
        }
    }
}
