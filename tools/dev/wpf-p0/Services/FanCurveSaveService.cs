using System;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Hardware;
using L_Mechrevo.WpfP0.Resources;

namespace L_Mechrevo.WpfP0.Services;

internal sealed record FanCurveSaveResult(bool Success, bool Dirty, string Detail, bool PreserveDirty = false);

/// <summary>风扇曲线保存：界面只下发意图（哪一档、哪些点位、是否随档位下发），
/// 判据与写入在后端对应的 *FanCurveApply 里；这里只把后端结果翻成界面文案。</summary>
internal static class FanCurveSaveService
{
	private enum Mold
	{
		Gcu,
		Wujie,
		Nuc,
		Ec,
		Baolongda
	}

	internal static async Task<FanCurveSaveResult> SaveGcuAsync(string modeId, int[] cpu, int[] gpu, bool push,
		byte[]? cpuTemps = null, byte[]? gpuTemps = null)
	{
		FanCurveApplyOutcome outcome = await GcuFanCurveApply.SaveAsync(FanCurveTarget.Hw, modeId, cpu, gpu, push, cpuTemps, gpuTemps);
		return ToResult(outcome, Mold.Gcu);
	}

	internal static FanCurveSaveResult SaveWujie(object owner, string modeId,
		int[] cpu, int[] gpu, byte[] cpuTemps, byte[] gpuTemps, bool independent)
	{
		return ToResult(WujieFanCurveApply.Save(owner, modeId, cpu, gpu, cpuTemps, gpuTemps, independent), Mold.Wujie);
	}

	/// <summary>该使用者退出无界曲线接管（同一进程内还有别的使用者时不停止）。</summary>
	internal static void StopWujie(object owner, string reason) => WujieFanCurveApply.Stop(owner, reason);

	/// <summary>本机闭环档位缺少存档时补写当前点位。</summary>
	internal static void PersistDutiesIfMissing(string modeId, int[]? cpuSaved, int[]? gpuSaved, int[] cpu, int[] gpu)
		=> FanCurvePersistence.PersistIfMissing(modeId, cpuSaved, gpuSaved, cpu, gpu);

	/// <summary>清掉该档保存的点位。</summary>
	internal static void ClearSavedDuties(string modeId) => FanCurvePersistence.ClearDuties(modeId);

	internal static FanCurveSaveResult SaveNuc(string modeId, int[] cpu, int[] gpu,
		byte[] cpuTemps, byte[] gpuTemps, bool push)
	{
		return ToResult(NucFanCurveApply.Save(modeId, cpu, gpu, cpuTemps, gpuTemps, push), Mold.Nuc);
	}

	internal static FanCurveSaveResult SaveEc(string modeId, int[] cpu, int[] gpu,
		byte[] cpuTemps, byte[] gpuTemps, bool push)
	{
		return ToResult(EcFanCurveApply.Save(modeId, cpu, gpu, cpuTemps, gpuTemps, push), Mold.Ec);
	}

	internal static FanCurveSaveResult SaveBaolongda(string modeId, int[] cpu, int[] gpu,
		int[] cpuTemps, int[] gpuTemps, bool push, bool loopOn, bool syncOn)
	{
		return ToResult(BaolongdaFanCurveApply.SaveAndApply(FanCurveTarget.Hw, modeId, cpu, gpu, cpuTemps, gpuTemps,
			push, loopOn, syncOn), Mold.Baolongda);
	}

	private static FanCurveSaveResult ToResult(FanCurveApplyOutcome outcome, Mold mold)
	{
		// 失败 ⇒ 仍算「有未保存修改」，关窗口要提示、切档不能把编辑冲掉。
		bool dirty = outcome.Status == FanCurveApplyStatus.SaveFailed;
		return new FanCurveSaveResult(outcome.Ok, dirty, Describe(outcome, mold), outcome.PreserveDirty);
	}

	/// <summary>后端结果 ⇒ 界面文案（只做翻译，不判任何硬件事实）。</summary>
	private static string Describe(FanCurveApplyOutcome outcome, Mold mold)
	{
		switch (outcome.Status)
		{
			case FanCurveApplyStatus.SavedNotPushed:
				return mold == Mold.Baolongda
					? WpfStrings.L("宝龙达风扇曲线已保存（未随档位下发）",
						"Baolongda fan curve saved (not pushed with profile)")
					: WpfStrings.FanCurveWindow_xaml_Text_4;
			case FanCurveApplyStatus.RefusedBoostActive:
				return WpfStrings.L("强冷进行中，请先关闭强冷再保存曲线",
					"Fan boost is active; turn it off before saving the fan curve");
			case FanCurveApplyStatus.SavedInactiveProfile:
				return WpfStrings.L("宝龙达风扇曲线已保存，切到该档后应用",
					"Baolongda fan curve saved; applies when this profile is active");
			case FanCurveApplyStatus.Applied:
				return mold switch
				{
					Mold.Gcu => WpfStrings.FanCurveWindow_xaml_Text_5,
					Mold.Wujie => WpfStrings.L("无界 EC 曲线已启动：", "Wujie EC curve started: ") + outcome.Detail,
					Mold.Nuc => WpfStrings.L("NUC/UMI 风扇曲线已写入：", "NUC/UMI fan curve written: ") + outcome.Detail,
					Mold.Ec => WpfStrings.L("风扇曲线已写入 EC：", "Fan curve written to EC: ") + outcome.Detail,
					_ => WpfStrings.L("宝龙达风扇曲线已保存并应用", "Baolongda fan curve saved and applied")
				};
			default:
				return mold switch
				{
					Mold.Gcu => WpfStrings.FanCurveWindow_xaml_Text_6,
					Mold.Wujie => WpfStrings.L("无界 EC 曲线启动失败：", "Failed to start Wujie EC curve: ") + outcome.Detail,
					Mold.Nuc => WpfStrings.L("NUC/UMI 风扇曲线写入失败：", "Failed to write NUC/UMI fan curve: ") + outcome.Detail,
					Mold.Ec => WpfStrings.L("风扇曲线写入失败：", "Failed to write fan curve: ") + outcome.Detail,
					_ => WpfStrings.L("宝龙达风扇曲线保存失败", "Failed to save Baolongda fan curve")
				};
		}
	}
}
