namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 门控只读事实快照。直接组合现有身份记录，不复制字段、不重新采集。
/// </summary>
internal sealed record MachineFacts(
	MachineIdentity? Identity,
	BackendKind Backend,
	BackendKind DeclaredBackend)
{
	/// <summary>
	/// 从当前路由结果与实际硬件状态生成；不触发机型采集。
	/// <para>实际后端 = 同一张规则表 + 运行期可用性事实的求值结果（不是另一条优先级链）；
	/// 未路由过（启动极早期）时才退回旧的连接推导。</para>
	/// </summary>
	internal static MachineFacts From(MechrevoHw? hw)
	{
		BackendRouteDecision? decision = BackendRouter.LastDecision;
		MachineIdentity? identity = decision?.Identity;
		BackendKind actual;
		if (identity != null)
		{
			BackendRouter.RouteAvailability availability = BackendRouter.RouteAvailability.From(hw);
			BackendRouteDecision routed = BackendRouter.Decide(identity, availability);
			actual = ToBackendKind(routed.Backend);
			Logger.WriteLineIfChanged("route-actual", "[强化机型分流] 实际后端=" + routed.Backend
				+ " | rule=#" + routed.RuleIndex + " " + routed.Rule
				+ " | 可用性：GCU=" + Bool(availability.GcuReachable)
				+ " NUC=" + Bool(availability.NucLocalReady)
				+ " 星耀=" + Bool(availability.XingyaoLocalReady)
				+ " EC直连=" + Bool(availability.EcLocalAvailable));
		}
		else
		{
			actual = ResolveActualBackend(hw);
		}
		return new MachineFacts(identity, actual, ResolveDeclaredBackend(decision));
	}

	/// <summary>路由枚举 → 后端类别（唯一映射，避免两处各写一份）。</summary>
	internal static string Bool(bool value) => value ? "是" : "否";
	internal static BackendKind ToBackendKind(MechrevoBackendKind kind)
	{
		return kind switch
		{
			MechrevoBackendKind.Gcu => BackendKind.TongfangGcu,
			MechrevoBackendKind.Baolongda => BackendKind.BaolongdaWmi,
			MechrevoBackendKind.LegacyMifs => BackendKind.TongfangMifs,
			MechrevoBackendKind.NucUmiLocal => BackendKind.NucUmiLocal,
			MechrevoBackendKind.XingyaoPmc => BackendKind.XingyaoPmc,
			MechrevoBackendKind.TongfangEc => BackendKind.TongfangEc,
			_ => BackendKind.None
		};
	}

	/// <summary>连接推导（只在"还没路由过"时兜底用；新逻辑走规则表）。</summary>
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
