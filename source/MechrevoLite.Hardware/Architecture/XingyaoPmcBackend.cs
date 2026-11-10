using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>星耀 PMC 后端：当前没有独立 Logo 灯通道。</summary>
internal sealed class XingyaoPmcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.XingyaoPmc;

	public string DisplayName => "XingyaoPMC";

	public bool Probe(MachineFacts facts)
	{
		return facts.Backend == Kind;
	}

	public BackendDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureState.Unsupported,
			BackendChannel.None, "星耀后端没有独立 Logo 灯通道", new[] { "BackendDeclaration" });
		return new BackendDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
