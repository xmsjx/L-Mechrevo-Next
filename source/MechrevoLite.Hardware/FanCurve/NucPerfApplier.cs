// NUC/UMI 自定义档：功耗、GPU、风扇开关一次下发（后端）。
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware
{
	internal static class NucPerfApplier
	{
		internal static (bool Success, string Detail) ApplyCustom(MechrevoHw? hw, PerfModeSettings edited)
		{
			if (hw == null)
			{
				return (false, "硬件不可用");
			}
			bool gpuOk = NucGpuPower.TryApply(
				edited.GpuTgp ?? 0,
				edited.GpuDynamicBoostOn == true,
				edited.GpuDynamicBoost ?? 0,
				edited.GpuOverclockOn == true ? edited.GpuCoreOffset : null,
				edited.GpuOverclockOn == true ? edited.GpuMemoryOffset : null,
				out string gpuDetail,
				force: true);
			bool ok = NucPerformanceMode.TryApplyCustom(
				edited.Pl1, edited.Pl2, edited.Pl4,
				edited.TccOn == true ? edited.TccTarget : null,
				out string detail);
			bool rampOk = NucFanTable.TrySetFanRamp(
				edited.FanSwitchSpeedOn == true, edited.FanSwitchSpeedMs ?? 300, out string rampDetail);
			bool curveOn = edited.FanCurveOn != false
				&& (edited.CpuFanDuty != null || edited.GpuFanDuty != null);
			bool isolatedOk = NucFanTable.TrySetFanIsolated(curveOn, out string isolatedDetail);
			ok = ok && rampOk && isolatedOk && gpuOk;
			detail += " " + rampDetail + " " + isolatedDetail + " " + gpuDetail;
			return (ok, detail);
		}
	}
}
