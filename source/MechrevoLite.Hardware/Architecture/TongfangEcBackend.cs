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
		else if (probe.EcLogoEvidence)
		{
			sources.Add("ec:IteLightingLocal.HasUsbLogo");
			state = FeatureAvailability.Supported;
		}
		else
		{
			state = FeatureAvailability.Unknown;
		}
		string reason = state switch
		{
			FeatureAvailability.Supported => "本地 EC 有 Logo 证据",
			FeatureAvailability.Unknown => probe.GcuConnected ? "GCU 已连接，EC 路未接管" : "本地 EC 证据未采集或无证据",
			_ => "本地 EC 无 Logo 证据"
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
