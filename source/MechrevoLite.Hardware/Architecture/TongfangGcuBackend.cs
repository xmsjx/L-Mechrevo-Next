using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 GCU 后端：Logo/灯带由 GCU 状态包或已缓存能力位证明。</summary>
internal sealed class TongfangGcuBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangGcu;

	public string DisplayName => "TongfangGCU";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo;
		if (!probe.GcuConnected)
		{
			logo = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LogoLightCapability)
			{
				sources.Add("registry:Capabilities.LogoLight");
			}
			if (probe.LogoLightSeen)
			{
				sources.Add("gcu:LogoLightStatusSeen");
			}
			if (probe.LightbarMbLogoSupport == true)
			{
				sources.Add("gcu:LightbarMbLogoSupport");
			}
			logo = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
					"GCU 上报 Logo 证据", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"GCU 已连接但未上报 Logo 证据", new string[0]);
		}

		FeatureDeclaration lightbar;
		if (!probe.GcuConnected)
		{
			lightbar = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LightbarCapability)
			{
				sources.Add("registry:Capabilities.Lightbar");
			}
			if (probe.LightbarSeen)
			{
				sources.Add("gcu:LightbarStatusSeen");
			}
			lightbar = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
					"GCU 上报灯带证据", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"GCU 已连接但未上报灯带证据", new string[0]);
		}

		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar
			});
	}
}
