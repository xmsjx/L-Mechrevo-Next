namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 门控只读事实快照。直接组合现有身份记录，不复制字段、不重新采集。
/// </summary>
internal sealed record MachineFacts(
	MachineIdentity? Identity,
	BackendKind Backend,
	BackendKind DeclaredBackend)
{
	/// <summary>从当前路由结果与实际硬件状态生成；不触发机型采集。</summary>
	internal static MachineFacts From(MechrevoHw? hw)
	{
		BackendRouteDecision? decision = BackendRouter.LastDecision;
		return new MachineFacts(decision?.Identity, ResolveActualBackend(hw), ResolveDeclaredBackend(decision));
	}

	private static BackendKind ResolveActualBackend(MechrevoHw? hw)
	{
		if (hw == null)
		{
			return BackendKind.None;
		}
		return ResolveActualBackend(hw.IsGcuConnected, hw.UsesBaolongda,
			hw.NucLocalReady, hw.XingyaoLocalReady, GcuEcMode.IsEcMode, hw.UsesLegacyMifs);
	}

	/// <summary>实际后端优先级：GCU → 宝龙达 → NUC/UMI → 星耀 → EC → 老 MIFS。</summary>
	internal static BackendKind ResolveActualBackend(bool gcu, bool baolongda,
		bool nuc, bool xingyao, bool ecMode, bool mifs)
	{
		if (gcu)
		{
			return BackendKind.TongfangGcu;
		}
		if (baolongda)
		{
			return BackendKind.BaolongdaWmi;
		}
		if (nuc)
		{
			return BackendKind.NucUmiLocal;
		}
		if (xingyao)
		{
			return BackendKind.XingyaoPmc;
		}
		if (ecMode)
		{
			return BackendKind.TongfangEc;
		}
		if (mifs)
		{
			return BackendKind.TongfangMifs;
		}
		return BackendKind.None;
	}

	private static BackendKind ResolveDeclaredBackend(BackendRouteDecision? decision)
	{
		if (decision == null)
		{
			return BackendKind.None;
		}
		return decision.Backend switch
		{
			MechrevoBackendKind.Baolongda => BackendKind.BaolongdaWmi,
			MechrevoBackendKind.LegacyMifs => BackendKind.TongfangMifs,
			MechrevoBackendKind.Gcu => BackendKind.TongfangGcu,
			_ => BackendKind.None
		};
	}
}
