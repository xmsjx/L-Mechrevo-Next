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
			return new FeatureDecision(feature, FeatureState.Unknown, BackendKind.None,
				BackendChannel.None, "尚未选定后端", Array.Empty<string>());
		}
		IBackendModule? module = BackendModuleRegistry.Resolve(facts.Backend);
		if (module == null || !module.Probe(facts))
		{
			return new FeatureDecision(feature, FeatureState.Unknown, facts.Backend,
				BackendChannel.None, "后端模块未就绪", Array.Empty<string>());
		}
		BackendDeclaration declaration = module.Declare(facts, probe);
		if (!declaration.Features.TryGetValue(feature, out FeatureDeclaration featureDeclaration))
		{
			return new FeatureDecision(feature, FeatureState.Unsupported, facts.Backend,
				BackendChannel.None, "后端未声明该功能", Array.Empty<string>());
		}
		return new FeatureDecision(feature, featureDeclaration.State, facts.Backend,
			featureDeclaration.Channel, featureDeclaration.Reason, featureDeclaration.Sources);
	}

	internal static Dictionary<FeatureId, FeatureDeclaration> One(
		FeatureId feature, FeatureDeclaration declaration)
	{
		return new Dictionary<FeatureId, FeatureDeclaration> { [feature] = declaration };
	}
}
