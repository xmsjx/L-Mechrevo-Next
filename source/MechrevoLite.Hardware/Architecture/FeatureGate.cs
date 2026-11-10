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

	/// <summary>明确支持/不支持时收敛，Unknown 时回退旧判据。</summary>
	internal static bool ResolveSupportOrLegacy(FeatureId feature, MechrevoHw? hw, bool legacySupport)
	{
		try
		{
			FeatureDecision decision = Get(feature, hw);
			if (decision.State == FeatureAvailability.Supported)
			{
				return true;
			}
			if (decision.State == FeatureAvailability.Unsupported)
			{
				return false;
			}
		}
		catch (Exception ex)
		{
			global::Logger.WriteLine("[gate] ResolveSupportOrLegacy failed: " + ex.Message);
		}
		return legacySupport;
	}

	internal static FeatureDecision Get(FeatureId feature, MachineFacts facts, FeatureProbe probe)
	{
		BackendKind backend = facts.Backend == BackendKind.None && facts.DeclaredBackend != BackendKind.None
			? facts.DeclaredBackend
			: facts.Backend;
		if (backend == BackendKind.None)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, BackendKind.None,
				BackendChannel.None, "尚未选定实际后端", Array.Empty<string>());
		}
		IBackendModule? module = BackendModuleRegistry.Resolve(backend);
		if (module == null)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, backend,
				BackendChannel.None, "后端模块未登记", Array.Empty<string>());
		}
		try
		{
			BackendFeatureDeclaration declaration = module.Declare(facts, probe);
			if (!declaration.Features.TryGetValue(feature, out FeatureDeclaration featureDeclaration))
			{
				return new FeatureDecision(feature, FeatureAvailability.Unknown, backend,
					BackendChannel.None, "该功能尚未纳入后端声明", Array.Empty<string>());
			}
			return new FeatureDecision(feature, featureDeclaration.State, backend,
				featureDeclaration.Channel, featureDeclaration.Reason, featureDeclaration.Sources);
		}
		catch (Exception ex)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, backend,
				BackendChannel.None, "后端声明异常: " + ex.Message, Array.Empty<string>());
		}
	}

	/// <summary>离线矩阵自测：只使用合成事实和探测快照，不触碰硬件。</summary>
	internal static string RunSelfTest()
	{
		List<string> lines = new List<string>();
		CheckBackend(lines, "ec-wins-over-mifs",
			MachineFacts.ResolveActualBackend(gcu: false, baolongda: false, nuc: false, xingyao: false, ecMode: true, mifs: true),
			BackendKind.TongfangEc);
		CheckBackend(lines, "nuc-wins-over-ec",
			MachineFacts.ResolveActualBackend(gcu: false, baolongda: false, nuc: true, xingyao: false, ecMode: true, mifs: true),
			BackendKind.NucUmiLocal);
		CheckBackend(lines, "mifs-only",
			MachineFacts.ResolveActualBackend(gcu: false, baolongda: false, nuc: false, xingyao: false, ecMode: false, mifs: true),
			BackendKind.TongfangMifs);
		CheckBackend(lines, "gcu-wins-over-ec",
			MachineFacts.ResolveActualBackend(gcu: true, baolongda: false, nuc: false, xingyao: false, ecMode: true, mifs: true),
			BackendKind.TongfangGcu);
		Check(lines, "gcu-supported", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Gcu);
		Check(lines, "gcu-no-evidence", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, false, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "gcu-disconnected", Facts(BackendKind.TongfangGcu), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "ec-supported", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, true),
			FeatureAvailability.Supported, BackendChannel.LocalHid);
		Check(lines, "ec-registry-only", Facts(BackendKind.TongfangEc), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.None);
		Check(lines, "nuc-unknown", Facts(BackendKind.NucUmiLocal), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "xingyao-unknown", Facts(BackendKind.XingyaoPmc), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "baolongda-supported", Facts(BackendKind.BaolongdaWmi), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.WmiMailbox);
		Check(lines, "no-backend", Facts(BackendKind.None), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.LogoLight);
		Check(lines, "mifs-unsupported", Facts(BackendKind.TongfangMifs), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None);
		Check(lines, "ec-gcu-connected", Facts(BackendKind.TongfangEc), new FeatureProbe(true, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "ec-unprobed", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, null),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "undeclared-feature", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.Lightbar);
		return string.Join(Environment.NewLine, lines);
	}

	private static MachineFacts Facts(BackendKind backend)
	{
		MachineIdentity identity = MachineIdentity.ForTest(board: "TEST", generation: "Gen40",
			gcuInstalled: backend == BackendKind.TongfangGcu, whitelisted: backend == BackendKind.BaolongdaWmi);
		return new MachineFacts(identity, backend, backend);
	}

	private static void CheckBackend(List<string> lines, string scenario, BackendKind actual, BackendKind expected)
	{
		bool ok = actual == expected;
		lines.Add((ok ? "PASS " : "FAIL ") + scenario + " ⇒ actual=" + actual + " expected=" + expected);
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
