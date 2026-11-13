using System;
using System.Linq;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Hardware.FanCurve;
using MechrevoLite.Mode;
using L_Mechrevo.WpfP0.Resources;

namespace L_Mechrevo.WpfP0.Services;

internal sealed record FanCurveRestoreOutcome(
	bool Handled,
	bool Success,
	bool ClearSaved,
	bool ReloadFromHardware,
	string Detail,
	byte[]? CpuTemps,
	byte[]? CpuDuties,
	byte[]? GpuTemps,
	byte[]? GpuDuties);

/// <summary>恢复默认风扇曲线的执行适配器；窗口只负责确认框和加载控件。</summary>
internal static class FanCurveRestoreService
{
	internal static async Task<FanCurveRestoreOutcome> RestoreAsync(FanCurveBackendKind kind, MechrevoHw? hw)
	{
		if (hw == null)
		{
			return NotHandled();
		}
		try
		{
			switch (kind)
			{
				case FanCurveBackendKind.Baolongda:
					PerfModeService.Instance?.ResetBaolongdaFanToFirmwareDefault();
					return new FanCurveRestoreOutcome(true, true, true, true,
						WpfStrings.L("已恢复默认：宝龙达软件闭环已关闭，曲线回到固件默认",
							"Restored: Baolongda software loop disabled; curve returned to firmware default"),
						null, null, null, null);
				case FanCurveBackendKind.NucUmi:
					return RestoreNuc(WpfStrings.L("已恢复默认：NUC/UMI 风扇表已还原",
						"Restored: NUC/UMI fan table restored"));
				case FanCurveBackendKind.Ec:
					return RestoreNuc(WpfStrings.L("已恢复默认：同方 EC 风扇表已还原",
						"Restored: Tongfang EC fan table restored"));
				case FanCurveBackendKind.Gcu:
					return await RestoreGcuAsync(hw).ConfigureAwait(false);
				default:
					return NotHandled();
			}
		}
		catch (Exception ex)
		{
			return new FanCurveRestoreOutcome(true, false, false, false,
				ex.GetType().Name + " " + ex.Message, null, null, null, null);
		}
	}

	private static FanCurveRestoreOutcome RestoreNuc(string successText)
	{
		bool restored = NucFanTable.TryRestoreDefault(out string detail);
		bool isolatedRestore = NucFanTable.TrySetFanIsolated(false, out string isolatedDetail);
		restored &= isolatedRestore;
		detail += " " + isolatedDetail;
		return new FanCurveRestoreOutcome(true, restored, false, true,
			restored ? successText : detail, null, null, null, null);
	}

	private static async Task<FanCurveRestoreOutcome> RestoreGcuAsync(MechrevoHw hw)
	{
		if (!hw.TryGetBuiltInDefaultFanCurve(out byte[] cpuTemps, out byte[] cpuDuties,
				out byte[] gpuTemps, out byte[] gpuDuties))
		{
			return new FanCurveRestoreOutcome(true, false, false, false,
				WpfStrings.L("找不到内置默认曲线资源", "Built-in default curve resource not found"),
				null, null, null, null);
		}
		await hw.SetFanCurve(0, cpuDuties.Select(v => (int)v).ToArray()).ConfigureAwait(false);
		await hw.SetFanCurve(1, gpuDuties.Select(v => (int)v).ToArray()).ConfigureAwait(false);
		return new FanCurveRestoreOutcome(true, true, true, false,
			WpfStrings.L("已恢复默认：已写回内置默认风扇曲线", "Restored: built-in default fan curve written"),
			cpuTemps, cpuDuties, gpuTemps, gpuDuties);
	}

	private static FanCurveRestoreOutcome NotHandled()
	{
		return new FanCurveRestoreOutcome(false, false, false, false, "", null, null, null, null);
	}
}
