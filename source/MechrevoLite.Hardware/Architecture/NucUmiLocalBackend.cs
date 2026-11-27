using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>NUC/UMI 本地后端：没有独立 Logo 通道，灯带走本地 HID。</summary>
internal sealed class NucUmiLocalBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.NucUmiLocal;

	public string DisplayName => "NucUmiLocal";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		// Logo：本地证据 ⇒ 独立 USB Logo；MBlogoSupport ⇒ 与灯带同一路；能力位/状态上报 ⇒ 独立 Logo。
		FeatureDeclaration logo = probe.EcLogoEvidence == true
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "本地独立 Logo 证据", new[] { "usb:IteLightingLocal.HasUsbLogo" })
			: probe.LightbarMbLogoSupport == true
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "与灯带同一路的 Logo", new[] { "gcu:MBlogoSupport" })
				: probe.LogoLightCapability || probe.LogoLightSeen
					? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "独立 Logo（能力位或状态上报）", new[] { "registry:Capabilities.LogoLight", "gcu:LogoLightStatusSeen" })
					: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "Logo 证据未采集", new string[0]);
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
		FeatureDeclaration noOemDisplay = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "NUC/UMI 没有 OEM 显示能力通道", new string[0]);
		// 色彩增强走 Windows 本地 gamma/矩阵（与后端无关），校色走 Windows 颜色管理的本地 ICC。
		FeatureDeclaration displayEffect = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地软件色彩增强（Win32 gamma/矩阵）", new[] { "win32:gamma" });
		FeatureDeclaration colorCalibration = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地 ICC 校色（Windows 颜色管理）", new[] { "win:icc" });
		FeatureDeclaration fanCurve = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "NUC/UMI 本地风扇表", new[] { "ec:FanTable" });
		FeatureDeclaration fanBoost = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "NUC/UMI 本地强冷", new[] { "ec:0x751" });
		FeatureDeclaration chargeLimit = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "NUC/UMI 本地限充", new[] { "acpi:NucBatteryMode" });
		FeatureDeclaration performanceMode = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "NUC/UMI 本地性能档", new[] { "acpi:NucPerformanceMode" });
		FeatureDeclaration refreshRate = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "Windows 显示通道", new[] { "win32:EnumDisplaySettings" });

        // 显卡三项与性能档四项：能力位上报了就按上报判，没上报保持 Unknown（⛔ 不写 Unsupported）。
        FeatureDeclaration gpuMode4 = probe.GpuModeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报显卡模式", new[] { "registry:Capabilities.DgpuDirect" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报显卡模式", new string[0]);
        FeatureDeclaration gpuHotSwap4 = probe.GpuHotSwapCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报免重启热切换", new[] { "registry:Capabilities.GpuHotSwap" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报免重启热切换", new string[0]);
        FeatureDeclaration igpuOnly4 = probe.IgpuOnlyCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报仅集显输出", new[] { "registry:Capabilities.IgpuOnly" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报仅集显输出", new string[0]);
        FeatureDeclaration perfTuning4 = probe.PerfTuningCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报 CPU 调优/风扇设置", new[] { "registry:Capabilities.CpuPerformanceTuning+FanSettings" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报 CPU 调优/风扇设置", new string[0]);
        FeatureDeclaration perfProfile4 = probe.ProfileAvailableCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报自定义档", new[] { "registry:Capabilities.ProfileAvailable" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报自定义档", new string[0]);
        FeatureDeclaration turboMode4 = probe.TurboModeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报涡轮档", new[] { "registry:Capabilities.TurboMode" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报涡轮档", new string[0]);
        FeatureDeclaration silentTurbo4 = probe.SilentTurboAvailability switch
        {
            MechrevoLite.Hardware.FeatureAvailability.Supported => new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位三态=支持", new[] { "registry:Capabilities.SilentTurboAvailability" }),
            MechrevoLite.Hardware.FeatureAvailability.Unsupported => new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None, "能力位三态=不支持", new string[0]),
            _ => new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位三态未采集", new string[0])
        };

        // 键盘/铰链/同步灯：能力位或本地通道上报了才 Supported，没上报保持 Unknown。
        // 键盘灯：本地 USB/EC 证据优先（单键 RGB 常走本地 HID），其次能力位，都没有才 Unknown。
        FeatureDeclaration keyboardLight4 = probe.EcKeyboardEvidence == true
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "本地 USB/EC 键盘灯证据", new[] { "usb:IteLightingLocal.HasKeyboard" })
            : probe.KeyboardCapability
                ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.
LocalHid
, "能力位上报键盘灯", new[] { "registry:Capabilities.Keyboard" })
                : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "键盘灯证据未采集", new string[0]);
        FeatureDeclaration hingeLight4 = probe.HingeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报铰链灯", new[] { "registry:Capabilities.HingeLight" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报铰链灯", new string[0]);
        FeatureDeclaration syncLight4 = probe.SyncCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "能力位上报同步灯", new[] { "registry:Capabilities.SyncLight" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报同步灯", new string[0]);
		return DeclarationGate.Wrap(new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar,
				[FeatureId.LocalDimming] = noOemDisplay,
				[FeatureId.LcdOverdrive] = noOemDisplay,
				[FeatureId.DisplayEffect] = displayEffect,
				[FeatureId.ColorCalibration] = colorCalibration,
				[FeatureId.FanCurve] = fanCurve,
				[FeatureId.FanBoost] = fanBoost,
				[FeatureId.ChargeLimit] = chargeLimit,
				[FeatureId.PerformanceMode] = performanceMode,
				[FeatureId.RefreshRate] = refreshRate,
        [FeatureId.GpuModeSwitch] = gpuMode4,
        [FeatureId.GpuHotSwap] = gpuHotSwap4,
        [FeatureId.IgpuOnly] = igpuOnly4,
        [FeatureId.PerformanceProfile] = perfProfile4,
        [FeatureId.TurboMode] = turboMode4,
        [FeatureId.SilentTurbo] = silentTurbo4,
        [FeatureId.PerfTuning] = perfTuning4,
        [FeatureId.KeyboardLight] = keyboardLight4,
        [FeatureId.HingeLight] = hingeLight4,
        [FeatureId.SyncLight] = syncLight4
			}));
	}
}
