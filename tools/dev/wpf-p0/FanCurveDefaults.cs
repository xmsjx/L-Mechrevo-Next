using System;
using System.Linq;

namespace L_Mechrevo.WpfP0;

/// <summary>自定义档的 16 点默认风扇温度轴与占空比兜底（不依赖 GCU/M4T5）。</summary>
internal static class FanCurveDefaults
{
    // 测试轴：沿用官方前 10 点（46..80 / 44..78），后 6 点压缩到 100℃。
    internal static readonly byte[] CpuTemperatures =
    {
        46, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 87, 90, 94, 97, 100
    };

    internal static readonly int[] CpuDuties =
    {
        0, 30, 35, 40, 45, 55, 65, 75, 85, 95, 95, 95, 95, 95, 95, 95
    };

    internal static readonly byte[] GpuTemperatures =
    {
        44, 46, 50, 54, 58, 62, 66, 70, 74, 78, 82, 85, 88, 92, 96, 100
    };

    internal static readonly int[] GpuDuties =
    {
        0, 0, 30, 35, 45, 55, 65, 75, 85, 95, 95, 95, 95, 95, 95, 95
    };

    internal static byte[] CpuDutyBytes => CpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();

    internal static byte[] GpuDutyBytes => GpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();
}
