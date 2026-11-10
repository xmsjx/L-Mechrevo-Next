namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 门控使用的实时探测快照。只读已缓存的硬件字段，不触发 HID/EC/WMI 访问。
/// </summary>
internal sealed record FeatureProbe(
	bool GcuConnected,
	bool LogoLightCapability,
	bool LogoLightSeen,
	bool? LightbarMbLogoSupport,
	bool? EcLogoEvidence,
	bool LightbarCapability = false,
	bool LightbarSeen = false,
	bool? EcLightbarEvidence = null,
	bool HingeCapability = false,
	bool SyncCapability = false)
{
	internal static FeatureProbe From(MechrevoHw? hw)
	{
		if (hw == null)
		{
			return new FeatureProbe(false, false, false, null, null);
		}
		MechrevoDeviceCapabilities caps = hw.Capabilities;
		return new FeatureProbe(
			hw.IsGcuConnected,
			caps != null && caps.LogoLight,
			hw.LogoLightStatusSeen,
			hw.LightbarMbLogoSupport,
			hw.EcLogoEvidence,
			caps != null && (caps.Lightbar || caps.RgbLightbar),
			hw.LightbarStatusSeen,
			IteLightingLocal.TryGetLightbarCached(out bool lightbarCached) ? lightbarCached : (bool?)null,
			hw.SupportsHingeLight,
			hw.SupportsSyncLight);
	}
}
