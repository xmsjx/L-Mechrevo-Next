using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>NUC/UMI 本地后端：当前没有独立 Logo 灯通道。</summary>
internal sealed class NucUmiLocalBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.NucUmiLocal;

	public string DisplayName => "NucUmiLocal";

	public bool Probe(MachineFacts facts)
	{
		return facts.Backend == Kind;
	}

	public BackendDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureState.Unsupported,
			BackendChannel.None, "NUC/UMI 没有独立 Logo 灯通道", new[] { "BackendDeclaration" });
		return new BackendDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
