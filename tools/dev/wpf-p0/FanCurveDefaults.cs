using System;
using System.Linq;

namespace L_Mechrevo.WpfP0;

/// <summary>自定义档的 16 点默认风扇温度轴与占空比兜底（不依赖 GCU/M4T5）。</summary>
internal static class FanCurveDefaults
{
    // 16 点默认温度轴：0 为保留点，30..100°C 每 5°C 一段。
    internal static readonly byte[] CpuTemperatures =
    {
        0, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100
    };

    internal static readonly int[] CpuDuties =
    {
        0, 20, 25, 30, 38, 45, 50, 55, 60, 65, 72, 78, 85, 92, 100, 100
    };

    internal static readonly byte[] GpuTemperatures =
    {
        0, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100
    };

    internal static readonly int[] GpuDuties =
    {
        0, 0, 20, 25, 30, 38, 45, 50, 55, 60, 70, 78, 85, 92, 100, 100
    };

    internal static byte[] CpuDutyBytes => CpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();

    internal static byte[] GpuDutyBytes => GpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();
}
