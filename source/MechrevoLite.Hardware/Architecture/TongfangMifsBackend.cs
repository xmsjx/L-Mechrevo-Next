using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方老 MIFS 后端：当前没有 Logo 下发路径。</summary>
internal sealed class TongfangMifsBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangMifs;

	public string DisplayName => "TongfangMIFS";

	public bool Probe(MachineFacts facts)
	{
		return facts.Backend == Kind;
	}

	public BackendDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureState.Unsupported,
			BackendChannel.None, "老 MIFS 后端没有 Logo 下发路径", new[] { "BackendDeclaration" });
		return new BackendDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
