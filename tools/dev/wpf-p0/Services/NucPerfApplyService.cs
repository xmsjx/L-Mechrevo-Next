using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>NUC/UMI 自定义档功耗/GPU/风扇执行。</summary>
internal static class NucPerfApplyService
{
	internal static (bool Success, string Detail) ApplyCustom(MechrevoHw? hw, PerfModeSettings edited)
	{
		if (hw == null)
		{
			return (false, "硬件不可用");
		}
		string gpuDetail = "";
		bool gpuOk = NucGpuPower.TryApply(
			edited.GpuTgp ?? 0,
			edited.GpuDynamicBoostOn == true,
			edited.GpuDynamicBoost ?? 0,
			edited.GpuOverclockOn == true ? edited.GpuCoreOffset : null,
			edited.GpuOverclockOn == true ? edited.GpuMemoryOffset : null,
			out gpuDetail,
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
