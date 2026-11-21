using MechrevoLite.Hardware;
using MechrevoLite.Hardware.FanCurve;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>曲线读取结果（界面视图模型，字段与后端一致）。</summary>
internal sealed record FanCurveLoadOutcome(
	byte[] CpuTemps,
	byte[] CpuDuties,
	byte[] GpuTemps,
	byte[] GpuDuties,
	bool? BaolongdaLoop,
	bool? BaolongdaSync,
	bool PersistIfMissing,
	int[]? CpuSaved,
	int[]? GpuSaved);

/// <summary>风扇曲线读取：转给后端 FanCurveReader，界面不判任何硬件事实。</summary>
internal static class FanCurveLoadService
{
	internal static FanCurveLoadOutcome Load(string modeId)
	{
		FanCurveLoadResult result = FanCurveReader.Read(FanCurveTarget.Kind, FanCurveTarget.Hw, modeId);
		return new FanCurveLoadOutcome(result.CpuTemps, result.CpuDuties, result.GpuTemps, result.GpuDuties,
			result.BaolongdaLoop, result.BaolongdaSync, result.PersistIfMissing, result.CpuSaved, result.GpuSaved);
	}
}
