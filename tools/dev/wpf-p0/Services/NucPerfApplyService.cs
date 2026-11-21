using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>NUC/UMI 自定义档下发：只把档位设置交给后端。</summary>
internal static class NucPerfApplyService
{
	internal static (bool Success, string Detail) ApplyCustom(MechrevoHw? hw, PerfModeSettings edited)
		=> NucPerfApplier.ApplyCustom(hw, edited);

	/// <summary>灵敏度（FanRamp）单独下发：与曲线互不依赖。</summary>
	internal static bool ApplyFanRamp(bool enabled, int speedMs, out string detail)
		=> WpfBackend.NucFanTableTrySetFanRamp(enabled, speedMs, out detail);

	/// <summary>同方风扇表：写曲线，成功才写隔离位。</summary>
	internal static bool ApplyFanCurve(int[] cpu, int[] gpu, out string curveDetail, out string isolatedDetail)
	{
		bool ok = WpfBackend.NucFanTableTryApply(FanCurveDefaultView.CpuTemperatures, cpu,
			FanCurveDefaultView.GpuTemperatures, gpu, out curveDetail);
		bool isolated = WpfBackend.NucFanTableTrySetFanIsolated(true, out isolatedDetail);
		return ok && isolated;
	}

	/// <summary>只下发 CPU 侧自定义功耗（PL1/PL2/PL4/TCC）。</summary>
	internal static bool ApplyCpuCustom(int? pl1, int? pl2, int? pl4, int? tcc, out string detail)
		=> WpfBackend.NucPerformanceModeTryApplyCustom(pl1, pl2, pl4, tcc, out detail);
}
