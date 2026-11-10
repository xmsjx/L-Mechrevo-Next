using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 GCU 后端：Logo 由 GCU 状态包或已缓存能力位证明。</summary>
internal sealed class TongfangGcuBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangGcu;

	public string DisplayName => "TongfangGCU";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		List<string> sources = new List<string>();
		FeatureAvailability state;
		if (!probe.GcuConnected)
		{
			state = FeatureAvailability.Unknown;
		}
		else
		{
			if (probe.LogoLightCapability)
			{
				sources.Add("profile:Capabilities.LogoLight");
			}
			if (probe.LogoLightSeen)
			{
				sources.Add("live:LogoLightStatusSeen");
			}
			if (probe.LightbarMbLogoSupport == true)
			{
				sources.Add("live:LightbarMbLogoSupport");
			}
			state = sources.Count > 0 ? FeatureAvailability.Supported : FeatureAvailability.Unsupported;
		}
		string reason = state switch
		{
			FeatureAvailability.Supported => "GCU 上报 Logo 证据",
			FeatureAvailability.Unsupported => "GCU 未上报 Logo 证据",
			_ => "GCU 未连接"
		};
		FeatureDeclaration logo = new FeatureDeclaration(
			state,
			state == FeatureAvailability.Supported ? BackendChannel.Gcu : BackendChannel.None,
			reason,
			sources.ToArray());
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
