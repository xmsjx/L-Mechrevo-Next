using MechrevoLite.Hardware.FanCurve;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>界面展示用的默认曲线兜底数据（来自后端常量，界面不直接引硬件命名空间）。</summary>
internal static class FanCurveDefaultView
{
	internal static byte[] CpuTemperatures => FanCurveDefaults.CpuTemperatures;

	internal static int[] CpuDuties => FanCurveDefaults.CpuDuties;

	internal static byte[] GpuTemperatures => FanCurveDefaults.GpuTemperatures;

	internal static int[] GpuDuties => FanCurveDefaults.GpuDuties;

	internal static byte[] CpuDutyBytes => FanCurveDefaults.CpuDutyBytes;

	internal static byte[] GpuDutyBytes => FanCurveDefaults.GpuDutyBytes;

	internal static byte[] WujieCpuTemperatures => FanCurveDefaults.WujieCpuTemperatures;

	internal static byte[] WujieGpuTemperatures => FanCurveDefaults.WujieGpuTemperatures;

	internal static byte[] WujieCpuDutyBytes => FanCurveDefaults.WujieCpuDutyBytes;

	internal static byte[] WujieGpuDutyBytes => FanCurveDefaults.WujieGpuDutyBytes;
}
