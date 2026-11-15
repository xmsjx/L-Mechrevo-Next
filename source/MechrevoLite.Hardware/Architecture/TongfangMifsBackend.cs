using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方老 MIFS 后端：当前没有 Logo/灯带下发路径。</summary>
internal sealed class TongfangMifsBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangMifs;

	public string DisplayName => "TongfangMIFS";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "老 MIFS 后端没有 Logo 下发路径", new string[0]);
		// 灯带：本地证据优先（ITE/EC 灯带字节），其次能力位；都没有才 Unknown。
		FeatureDeclaration lightbar = probe.EcLightbarEvidence == true
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "本地灯带证据", new[] { "usb:IteLightingLocal.HasLightbar" })
			: probe.LightbarCapability || probe.LightbarSeen || probe.LightbarMbLogoSupport == true
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位或状态上报灯带", new[] { "registry:Capabilities.Lightbar", "gcu:LightbarStatusSeen" })
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "灯带证据未采集，回退旧判据", new string[0]);
		FeatureDeclaration noOemDisplay = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "老 MIFS 后端没有 OEM 显示能力通道", new string[0]);
		// 色彩增强走 Windows 本地 gamma/矩阵（与后端无关），校色走 Windows 颜色管理的本地 ICC。
		FeatureDeclaration displayEffect = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地软件色彩增强（Win32 gamma/矩阵）", new[] { "win32:gamma" });
		FeatureDeclaration colorCalibration = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地 ICC 校色（Windows 颜色管理）", new[] { "win:icc" });
		FeatureDeclaration unsupported = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "老 MIFS 没有该下发通道", new string[0]);
		FeatureDeclaration perf = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "老 MIFS 内置性能档", new[] { "wmi:MICommonInterface.Mode" });
		FeatureDeclaration refreshRate = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "Windows 显示通道", new[] { "win32:EnumDisplaySettings" });

        // 显卡三项与性能档四项：能力位上报了就按上报判，没上报保持 Unknown（⛔ 不写 Unsupported）。
        FeatureDeclaration gpuMode4 = probe.GpuModeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报显卡模式", new[] { "registry:Capabilities.DgpuDirect" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报显卡模式", new string[0]);
        FeatureDeclaration gpuHotSwap4 = probe.GpuHotSwapCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报免重启热切换", new[] { "registry:Capabilities.GpuHotSwap" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报免重启热切换", new string[0]);
        FeatureDeclaration igpuOnly4 = probe.IgpuOnlyCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报仅集显输出", new[] { "registry:Capabilities.IgpuOnly" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报仅集显输出", new string[0]);
        FeatureDeclaration perfTuning4 = probe.PerfTuningCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报 CPU 调优/风扇设置", new[] { "registry:Capabilities.CpuPerformanceTuning+FanSettings" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报 CPU 调优/风扇设置", new string[0]);
        FeatureDeclaration perfProfile4 = probe.ProfileAvailableCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报自定义档", new[] { "registry:Capabilities.ProfileAvailable" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报自定义档", new string[0]);
        FeatureDeclaration turboMode4 = probe.TurboModeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报涡轮档", new[] { "registry:Capabilities.TurboMode" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报涡轮档", new string[0]);
        FeatureDeclaration silentTurbo4 = probe.SilentTurboAvailability switch
        {
            MechrevoLite.Hardware.FeatureAvailability.Supported => new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位三态=支持", new[] { "registry:Capabilities.SilentTurboAvailability" }),
            MechrevoLite.Hardware.FeatureAvailability.Unsupported => new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None, "能力位三态=不支持", new string[0]),
            _ => new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位三态未采集", new string[0])
        };

        // 键盘/铰链/同步灯：能力位或本地通道上报了才 Supported，没上报保持 Unknown。
        // 键盘灯：本地 USB/EC 证据优先（单键 RGB 常走本地 HID），其次能力位，都没有才 Unknown。
        FeatureDeclaration keyboardLight4 = probe.EcKeyboardEvidence == true
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid, "本地 USB/EC 键盘灯证据", new[] { "usb:IteLightingLocal.HasKeyboard" })
            : probe.KeyboardCapability
                ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.
AcpiWmi
, "能力位上报键盘灯", new[] { "registry:Capabilities.Keyboard" })
                : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "键盘灯证据未采集", new string[0]);
        FeatureDeclaration hingeLight4 = probe.HingeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报铰链灯", new[] { "registry:Capabilities.HingeLight" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报铰链灯", new string[0]);
        FeatureDeclaration syncLight4 = probe.SyncCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi, "能力位上报同步灯", new[] { "registry:Capabilities.SyncLight" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报同步灯", new string[0]);
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar,
				[FeatureId.LocalDimming] = noOemDisplay,
				[FeatureId.LcdOverdrive] = noOemDisplay,
				[FeatureId.DisplayEffect] = displayEffect,
				[FeatureId.ColorCalibration] = colorCalibration,
				[FeatureId.FanCurve] = unsupported,
				[FeatureId.FanBoost] = unsupported,
				[FeatureId.ChargeLimit] = unsupported,
				[FeatureId.PerformanceMode] = perf,
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
			});
	}
}
