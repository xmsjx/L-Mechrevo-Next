using System;

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
}
