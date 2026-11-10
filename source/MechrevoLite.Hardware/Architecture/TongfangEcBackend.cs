using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 EC 后端：优先本地 USB 证据，其次 GCU 报文，最后能力位。</summary>
internal sealed class TongfangEcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangEc;

	public string DisplayName => "TongfangEC";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		List<string> sources = new List<string>();
		FeatureAvailability state;
		BackendChannel channel;
		if (probe.GcuConnected)
		{
			state = FeatureAvailability.Unknown;
			channel = BackendChannel.None;
		}
		else if (probe.EcLogoEvidence)
		{
			sources.Add("usb:IteLightingLocal.HasUsbLogo");
			state = FeatureAvailability.Supported;
			channel = BackendChannel.LocalHid;
		}
		else if (probe.LogoLightSeen)
		{
			sources.Add("gcu:LogoLightStatusSeen");
			state = FeatureAvailability.Supported;
			channel = BackendChannel.None;
		}
		else if (probe.LightbarMbLogoSupport == true)
		{
			sources.Add("gcu:LightbarMbLogoSupport");
			state = FeatureAvailability.Supported;
			channel = BackendChannel.None;
		}
		else if (probe.LogoLightCapability)
		{
			sources.Add("registry:Capabilities.LogoLight");
			state = FeatureAvailability.Supported;
			channel = BackendChannel.None;
		}
		else
		{
			state = FeatureAvailability.Unknown;
			channel = BackendChannel.None;
		}
		string reason = state switch
		{
			FeatureAvailability.Supported => channel == BackendChannel.None
				? "仅有能力位/GCU 报文，未本地确认"
				: "本地 EC 有 Logo 证据",
			FeatureAvailability.Unknown => probe.GcuConnected ? "GCU 已连接，EC 路未接管" : "本地 EC 证据未采集或无证据",
			_ => "本地 EC 无 Logo 证据"
		};
		FeatureDeclaration logo = new FeatureDeclaration(state, channel, reason, sources.ToArray());
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
