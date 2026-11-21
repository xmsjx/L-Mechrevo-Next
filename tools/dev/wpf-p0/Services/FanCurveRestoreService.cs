using System.Threading.Tasks;
using MechrevoLite.Hardware;
using MechrevoLite.Hardware.FanCurve;
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

/// <summary>恢复默认风扇曲线：后端还原风扇表/隔离位/固件默认，界面只翻译结果。</summary>
internal static class FanCurveRestoreService
{
	internal static async Task<FanCurveRestoreOutcome> RestoreAsync()
	{
		FanCurveRestoreResult result = await FanCurveRestorer
			.RestoreAsync(FanCurveTarget.Kind, FanCurveTarget.Hw).ConfigureAwait(true);
		return new FanCurveRestoreOutcome(result.Handled, result.Success, result.ClearSaved,
			result.ReloadFromHardware, Describe(result), result.CpuTemps, result.CpuDuties,
			result.GpuTemps, result.GpuDuties);
	}

	/// <summary>后端结果 ⇒ 界面文案（只翻译，不判硬件）。</summary>
	private static string Describe(FanCurveRestoreResult result)
	{
		if (!result.Success)
		{
			return result.Kind == FanCurveRestoreKind.None && result.Handled
				? (result.Detail.Length > 0
					? result.Detail
					: WpfStrings.L("找不到内置默认曲线资源", "Built-in default curve resource not found"))
				: result.Detail;
		}
		return result.Kind switch
		{
			FanCurveRestoreKind.BaolongdaFirmware => WpfStrings.L("已恢复默认：宝龙达软件闭环已关闭，曲线回到固件默认",
				"Restored: Baolongda software loop disabled; curve returned to firmware default"),
			FanCurveRestoreKind.NucTable => WpfStrings.L("已恢复默认：NUC/UMI 风扇表已还原",
				"Restored: NUC/UMI fan table restored"),
			FanCurveRestoreKind.EcTable => WpfStrings.L("已恢复默认：同方 EC 风扇表已还原",
				"Restored: Tongfang EC fan table restored"),
			FanCurveRestoreKind.GcuBuiltin => WpfStrings.L("已恢复默认：已写回内置默认风扇曲线",
				"Restored: built-in default fan curve written"),
			_ => result.Detail
		};
	}
}
