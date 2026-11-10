using System;
using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>统一门控求值：只读事实、声明和探测快照，不触碰硬件。</summary>
internal static class FeatureGate
{
	internal static FeatureDecision Get(FeatureId feature, MechrevoHw? hw)
	{
		return Get(feature, MachineFacts.From(hw), FeatureProbe.From(hw));
	}

	internal static FeatureDecision Get(FeatureId feature, MachineFacts facts, FeatureProbe probe)
	{
		if (facts.Backend == BackendKind.None)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, BackendKind.None,
				BackendChannel.None, "尚未选定实际后端", Array.Empty<string>());
		}
		IBackendModule? module = BackendModuleRegistry.Resolve(facts.Backend);
		if (module == null)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, facts.Backend,
				BackendChannel.None, "后端模块未登记", Array.Empty<string>());
		}
		try
		{
			BackendFeatureDeclaration declaration = module.Declare(facts, probe);
			if (!declaration.Features.TryGetValue(feature, out FeatureDeclaration featureDeclaration))
			{
				return new FeatureDecision(feature, FeatureAvailability.Unknown, facts.Backend,
					BackendChannel.None, "该功能尚未纳入后端声明", Array.Empty<string>());
			}
			return new FeatureDecision(feature, featureDeclaration.State, facts.Backend,
				featureDeclaration.Channel, featureDeclaration.Reason, featureDeclaration.Sources);
		}
		catch (Exception ex)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, facts.Backend,
				BackendChannel.None, "后端声明异常: " + ex.Message, Array.Empty<string>());
		}
	}

	/// <summary>离线矩阵自测：只使用合成事实和探测快照，不触碰硬件。</summary>
	internal static string RunSelfTest()
	{
		List<string> lines = new List<string>();
		Check(lines, "gcu-supported", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, true, false, null),
			FeatureAvailability.Supported, BackendChannel.Gcu);
		Check(lines, "gcu-no-evidence", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, false, false, null),
			FeatureAvailability.Unsupported, BackendChannel.None);
		Check(lines, "gcu-disconnected", Facts(BackendKind.TongfangGcu), new FeatureProbe(false, true, false, null),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "ec-supported", Facts(BackendKind.TongfangEc), new FeatureProbe(false, true, false, null),
			FeatureAvailability.Supported, BackendChannel.Ec);
		Check(lines, "baolongda-unsupported", Facts(BackendKind.BaolongdaWmi), new FeatureProbe(false, true, false, null),
			FeatureAvailability.Unsupported, BackendChannel.None);
		Check(lines, "no-backend", Facts(BackendKind.None), new FeatureProbe(false, true, false, null),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.LogoLight);
		Check(lines, "undeclared-feature", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, true, false, null),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.Lightbar);
		return string.Join(Environment.NewLine, lines);
	}

	private static MachineFacts Facts(BackendKind backend)
	{
		MachineIdentity identity = MachineIdentity.ForTest(board: "TEST", generation: "Gen40",
			gcuInstalled: backend == BackendKind.TongfangGcu, whitelisted: backend == BackendKind.BaolongdaWmi);
		return new MachineFacts(identity, backend, backend);
	}

	private static void Check(List<string> lines, string scenario, MachineFacts facts,
		FeatureProbe probe, FeatureAvailability expectedState, BackendChannel expectedChannel,
		FeatureId feature = FeatureId.LogoLight)
	{
		FeatureDecision decision = Get(feature, facts, probe);
		bool ok = decision.State == expectedState && decision.Channel == expectedChannel;
		lines.Add((ok ? "PASS " : "FAIL ") + scenario
			+ " ⇒ state=" + decision.State + " channel=" + decision.Channel
			+ " backend=" + decision.Backend + " reason=" + decision.Reason);
	}
}
