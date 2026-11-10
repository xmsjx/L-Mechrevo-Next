using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 EC 后端：GCU 不在时由本地 EC/能力位接管。</summary>
internal sealed class TongfangEcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangEc;

	public string DisplayName => "TongfangEC";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		List<string> sources = new List<string>();
		FeatureAvailability state;
		if (probe.GcuConnected)
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
				sources.Add("profile:LightbarMbLogoSupport");
			}
			state = sources.Count > 0 ? FeatureAvailability.Supported : FeatureAvailability.Unsupported;
		}
		string reason = state switch
		{
			FeatureAvailability.Supported => "本地通道有 Logo 声明",
			FeatureAvailability.Unsupported => "本地通道无 Logo 声明",
			_ => "GCU 已连接，EC 路未接管"
		};
		FeatureDeclaration logo = new FeatureDeclaration(
			state,
			state == FeatureAvailability.Supported ? BackendChannel.Ec : BackendChannel.None,
			reason,
			sources.ToArray());
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
