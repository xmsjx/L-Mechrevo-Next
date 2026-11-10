using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>宝龙达 WMI 后端：当前没有独立 Logo 灯通道。</summary>
internal sealed class BaolongdaWmiBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.BaolongdaWmi;

	public string DisplayName => "BaolongdaWMI";

	public bool Probe(MachineFacts facts)
	{
		return facts.Backend == Kind || facts.BaolongdaWhitelisted;
	}

	public BackendDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureState.Unsupported,
			BackendChannel.None, "宝龙达后端没有 Logo 灯通道", new[] { "BackendDeclaration" });
		return new BackendDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
