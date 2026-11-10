using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 GCU 后端：Logo 由 GCU 状态包或本地能力位证明。</summary>
internal sealed class TongfangGcuBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangGcu;

	public string DisplayName => "TongfangGCU";

	public bool Probe(MachineFacts facts)
	{
		return facts.Backend == Kind || facts.GcuServiceInstalled || facts.TongfangFamily;
	}

	public BackendDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		List<string> sources = new List<string>();
		FeatureState state;
		string reason;
		if (!probe.GcuConnected)
		{
			state = FeatureState.Unknown;
			reason = "GCU 未连接";
		}
		else
		{
			if (probe.LogoLightCapability)
			{
				sources.Add("Capabilities.LogoLight");
			}
			if (probe.LogoLightSeen)
			{
				sources.Add("LogoLightStatusSeen");
			}
			if (probe.LightbarMbLogoSupport == true)
			{
				sources.Add("LightbarMbLogoSupport");
			}
			state = sources.Count > 0 ? FeatureState.Supported : FeatureState.Unsupported;
			reason = sources.Count > 0 ? "GCU 上报 Logo 证据" : "GCU 未上报 Logo 证据";
		}
		FeatureDeclaration logo = new FeatureDeclaration(state, BackendChannel.Gcu, reason, sources.ToArray());
		return new BackendDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
