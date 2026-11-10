using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 EC 后端：GCU 不在时由本地 EC/能力位接管。</summary>
internal sealed class TongfangEcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangEc;

	public string DisplayName => "TongfangEC";

	public bool Probe(MachineFacts facts)
	{
		return facts.Backend == Kind;
	}

	public BackendDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		List<string> sources = new List<string>();
		FeatureState state;
		string reason;
		if (probe.GcuConnected)
		{
			state = FeatureState.Unknown;
			reason = "GCU 已连接，EC 路未接管";
		}
		else
		{
			if (probe.LogoLightCapability)
			{
				sources.Add("Capabilities.LogoLight");
			}
			if (probe.EcUsbLogo)
			{
				sources.Add("IteLightingLocal.HasUsbLogo");
			}
			state = sources.Count > 0 ? FeatureState.Supported : FeatureState.Unsupported;
			reason = sources.Count > 0 ? "本地 EC 提供 Logo 证据" : "本地 EC 无 Logo 证据";
		}
		FeatureDeclaration logo = new FeatureDeclaration(state, BackendChannel.Ec, reason, sources.ToArray());
		return new BackendDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
