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
		return new BackendFeatureDeclaration(Kind,
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
        [FeatureId.GpuModeSwitch] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "显卡模式切换：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.GpuHotSwap] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "免重启热切换：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.IgpuOnly] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "仅集显输出：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.PerformanceProfile] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "自定义档：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.TurboMode] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "涡轮档：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.SilentTurbo] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "静音↔狂暴：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.PerfTuning] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "CPU 调优/风扇设置：本后端未声明，交由门控回退旧判据", new string[0])
			});
	}
}
