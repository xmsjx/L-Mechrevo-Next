using System;
using System.Linq;

namespace MechrevoLite.Hardware.FanCurve;

/// <summary>自定义档的 16 点默认风扇温度轴与占空比兜底（不依赖 GCU）。</summary>
internal static class FanCurveDefaults
{
	// CPU 16 点全部可用；GPU 只有 13 个可写占空比槽（其后是 Max1/Max2/TableCtrl），用 255 结束。
	internal static readonly byte[] CpuTemperatures =
	{
		30, 40, 45, 50, 55, 60, 64, 68, 72, 75, 78, 81, 85, 90, 95, 100
	};

	internal static readonly int[] CpuDuties =
	{
		25, 26, 30, 34, 40, 46, 52, 58, 65, 72, 78, 82, 86, 90, 95, 100
	};

	internal static readonly byte[] GpuTemperatures =
	{
		30, 40, 45, 50, 54, 58, 62, 66, 70, 74, 78, 82, 87, 255, 255, 255
	};

	internal static readonly int[] GpuDuties =
	{
		25, 26, 30, 34, 40, 46, 52, 58, 64, 72, 80, 84, 88, 88, 88, 88
	};

	// 无界（WUJIE）：控制器把 <45℃ 的温度轴点抬到 45℃，且 <45℃ 直接交还 EC ⇒ 默认轴从 45℃ 起，
	// 温度与占空比同序对齐，避免出现拖了不生效的死点。
	internal static readonly byte[] WujieCpuTemperatures =
	{
		45, 50, 55, 60, 64, 68, 72, 75, 78, 81, 85, 90, 95, 100, 255, 255
	};

	internal static readonly int[] WujieCpuDuties =
	{
		30, 34, 40, 46, 52, 58, 65, 72, 78, 82, 86, 90, 95, 100, 100, 100
	};

	internal static readonly byte[] WujieGpuTemperatures =
	{
		45, 50, 54, 58, 62, 66, 70, 74, 78, 82, 87, 255, 255, 255, 255, 255
	};

	internal static readonly int[] WujieGpuDuties =
	{
		30, 34, 40, 46, 52, 58, 64, 72, 80, 84, 88, 88, 88, 88, 88, 88
	};

	internal static byte[] CpuDutyBytes => CpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();

	internal static byte[] WujieCpuDutyBytes => WujieCpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();

	internal static byte[] WujieGpuDutyBytes => WujieGpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();

	internal static byte[] GpuDutyBytes => GpuDuties.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray();
}
