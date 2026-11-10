using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 EC 后端：优先本地 USB 证据，其次 GCU 报文，最后能力位。</summary>
internal sealed class TongfangEcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangEc;

	public string DisplayName => "TongfangEC";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo;
		if (probe.GcuConnected)
		{
			logo = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 已连接，EC 路未接管", new string[0]);
		}
		else if (probe.EcLogoEvidence == true)
		{
			logo = new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
				"本地 EC 有 Logo 证据", new[] { "usb:IteLightingLocal.HasUsbLogo" });
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LogoLightSeen)
			{
				sources.Add("gcu:LogoLightStatusSeen");
			}
			if (probe.LightbarMbLogoSupport == true)
			{
				sources.Add("gcu:LightbarMbLogoSupport");
			}
			if (probe.LogoLightCapability)
			{
				sources.Add("registry:Capabilities.LogoLight");
			}
			logo = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.None,
					"仅有能力位/GCU 报文，未本地确认", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"本地 EC 证据未采集或无证据", new string[0]);
		}

		FeatureDeclaration lightbar;
		if (probe.GcuConnected)
		{
			lightbar = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 已连接，EC 路未接管", new string[0]);
		}
		else if (probe.EcLightbarEvidence == true)
		{
			lightbar = new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
				"本地 EC 有灯带证据", new[] { "usb:IteLightingLocal.HasLightbar" });
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LightbarSeen)
			{
				sources.Add("gcu:LightbarStatusSeen");
			}
			if (probe.LightbarCapability)
			{
				sources.Add("registry:Capabilities.Lightbar");
			}
			lightbar = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.None,
					"仅有能力位/GCU 报文，未本地确认", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"本地 EC 灯带证据未采集或无证据", new string[0]);
		}

		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar
			});
	}
}
