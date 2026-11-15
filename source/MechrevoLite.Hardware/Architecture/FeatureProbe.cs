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
	bool SyncCapability = false,
	bool KeyboardCapability = false,
	bool LocalDimmingCapability = false,
	bool LocalDimmingSeen = false,
	bool LcdOverdriveCapability = false,
	bool LcdOverdriveSeen = false,
	bool DisplayEffectProfile = false,
	bool ColorCalibrationCapability = false,
	bool ColorCalibrationSeen = false,
	bool? LocalDimmingSupport = null,
	bool? LcdOverdriveSupport = null,
	bool? EcLocalDimmingSupport = null,
	bool? EcLcdOverdriveSupport = null,
	bool? EcKeyboardEvidence = null,
	IteLightingCapability? IteCapability = null,
	string EcKeyboardKind = "")
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
			hw.SupportsSyncLight,
			hw.SupportsKeyboard,
			caps != null && caps.LocalDimming,
			hw.LocalDimmingSeen,
			caps != null && caps.LcdOverdrive,
			hw.LcdOverdriveSeen,
			hw.DisplayEffectProfiles.Count > 0,
			caps != null && caps.ColorCalibration,
			hw.ColorCalibrationSeen,
			hw.LocalDimmingSupport,
			hw.LcdOverdriveSupport,
			AcpiDisplaySwitch.LocalDimmingSupportCached,
			AcpiDisplaySwitch.LcdOverdriveSupportCached,
			// 键盘类型探测过就给出结论（不看 EC 实验开关）；否则 null = 不表态（回退旧判据）。
			GcuEcKeyboard.KindProbed ? GcuEcKeyboard.ControllableProbed : (bool?)null,
			hw.IteCapability,
			GcuEcKeyboard.KindProbed ? GcuEcKeyboard.Kind.ToString() : "");
	}
}
