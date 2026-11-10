namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 门控使用的实时探测快照。只保留功能判定需要的最小字段。
/// </summary>
internal sealed record FeatureProbe(
	bool GcuConnected,
	bool LogoLightCapability,
	bool LogoLightSeen,
	bool? LightbarMbLogoSupport,
	bool EcUsbLogo,
	bool EcLightbar)
{
	internal static FeatureProbe From(MechrevoHw? hw)
	{
		if (hw == null)
		{
			return new FeatureProbe(false, false, false, null, false, false);
		}
		bool ec = GcuEcExperimental.Enabled;
		MechrevoDeviceCapabilities caps = hw.Capabilities;
		return new FeatureProbe(
			hw.IsGcuConnected,
			caps != null && caps.LogoLight,
			hw.LogoLightStatusSeen,
			hw.LightbarMbLogoSupport,
			ec && IteLightingLocal.HasUsbLogo,
			ec && IteLightingLocal.HasLightbar);
	}
}
