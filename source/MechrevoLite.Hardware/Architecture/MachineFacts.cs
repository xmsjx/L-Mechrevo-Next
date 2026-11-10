using System;
using MechrevoLite.Gpu;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 门控只读事实快照。只描述机器是什么，不描述功能是否可用。
/// </summary>
internal sealed record MachineFacts(
	string Board,
	string Model,
	bool BaolongdaWhitelisted,
	bool GcuServiceInstalled,
	bool TongfangFamily,
	bool IsUmi,
	bool IsIntelNuc,
	bool WujieApu,
	string GpuGeneration,
	BackendKind Backend)
{
	/// <summary>从当前路由结果与硬件实例生成；不触发新的机型采集。</summary>
	internal static MachineFacts From(MechrevoHw? hw)
	{
		BackendRouteDecision? decision = BackendRouter.LastDecision;
		MachineIdentity? identity = decision?.Identity;
		string board = identity?.Board ?? string.Empty;
		string model = identity?.Model ?? string.Empty;
		bool whitelisted = identity?.BaolongdaWhitelisted
			?? (hw?.UsesBaolongda == true);
		bool gcuInstalled = identity?.GcuServiceInstalled
			?? MechrevoHw.IsGcuServiceInstalled;
		bool tongfangFamily = identity?.TongfangFamily
			?? (hw?.IsTongfangFamily == true);
		bool isUmi = identity?.IsUmi
			?? (hw?.IsUmiMachine == true);
		bool isNuc = identity?.IsIntelNuc
			?? (hw?.IsIntelNucMachine == true);
		bool wujieApu = identity?.WujieApu
			?? (hw?.WujieSmuApu == true);
		string generation = identity?.GpuGeneration
			?? hw?.DgpuGeneration.ToString()
			?? DgpuGenerationKind.Unknown.ToString();
		return new MachineFacts(board, model, whitelisted, gcuInstalled, tongfangFamily,
			isUmi, isNuc, wujieApu, generation, ResolveBackend(hw, decision));
	}

	private static BackendKind ResolveBackend(MechrevoHw? hw, BackendRouteDecision? decision)
	{
		if (decision != null)
		{
			switch (decision.Backend)
			{
				case MechrevoBackendKind.Baolongda:
					return BackendKind.BaolongdaWmi;
				case MechrevoBackendKind.LegacyMifs:
					return BackendKind.TongfangMifs;
				case MechrevoBackendKind.Gcu:
					return hw != null && GcuEcMode.IsEcMode && !hw.IsGcuConnected
						? BackendKind.TongfangEc
						: BackendKind.TongfangGcu;
			}
		}
		if (hw == null)
		{
			return BackendKind.None;
		}
		if (hw.UsesBaolongda)
		{
			return BackendKind.BaolongdaWmi;
		}
		if (XingyaoPmc2.IsXingyaoPlatform() && !hw.IsGcuConnected)
		{
			return BackendKind.XingyaoPmc;
		}
		if (hw.IsIntelNucMachine && !hw.IsGcuConnected)
		{
			return BackendKind.NucUmiLocal;
		}
		if (hw.IsGcuConnected)
		{
			return BackendKind.TongfangGcu;
		}
		return GcuEcMode.IsEcMode ? BackendKind.TongfangEc : BackendKind.None;
	}
}
