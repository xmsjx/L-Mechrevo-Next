using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>NUC/UMI 本地后端：没有独立 Logo 通道，灯带走本地 HID。</summary>
internal sealed class NucUmiLocalBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.NucUmiLocal;

	public string DisplayName => "NucUmiLocal";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Unknown,
			BackendChannel.None, "NUC/UMI 没有独立 Logo 灯通道", new string[0]);
		List<string> sources = new List<string>();
		if (probe.LightbarCapability)
		{
			sources.Add("registry:Capabilities.Lightbar");
		}
		if (probe.LightbarSeen)
		{
			sources.Add("gcu:LightbarStatusSeen");
		}
		if (facts.Identity?.IsIntelNuc == true || facts.Identity?.IsUmi == true)
		{
			sources.Add("identity:NucUmiLocal");
		}
		FeatureDeclaration lightbar = sources.Count > 0
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
				"NUC/UMI 灯带走本地 HID", sources.ToArray())
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"NUC/UMI 灯带证据未采集，回退旧判据", new string[0]);
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar
			});
	}
}
