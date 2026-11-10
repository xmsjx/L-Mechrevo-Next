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
		if (hw.IsGcuConnected)
		{
			return BackendKind.TongfangGcu;
		}
		if (hw.UsesBaolongda)
		{
			return BackendKind.BaolongdaWmi;
		}
		if (hw.UsesLegacyMifs)
		{
			return BackendKind.TongfangMifs;
		}
		if (hw.NucLocalReady)
		{
			return BackendKind.NucUmiLocal;
		}
		if (hw.XingyaoLocalReady)
		{
			return BackendKind.XingyaoPmc;
		}
		return GcuEcMode.IsEcMode ? BackendKind.TongfangEc : BackendKind.None;
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
