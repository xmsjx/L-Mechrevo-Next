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
		FeatureDeclaration lightbar = (probe.LightbarCapability || probe.LightbarSeen
			|| facts.Identity?.IsIntelNuc == true)
			? new FeatureDeclaration(FeatureAvailability.Supported,
				BackendChannel.LocalHid, "NUC/UMI 灯带走本地 HID", new[] { "usb:IteLightingLocal.HasLightbar" })
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
