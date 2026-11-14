using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>星耀 PMC 后端：当前没有独立 Logo/灯带通道。</summary>
internal sealed class XingyaoPmcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.XingyaoPmc;

	public string DisplayName => "XingyaoPMC";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Unknown,
			BackendChannel.None, "星耀后端没有独立 Logo 灯通道", new string[0]);
		FeatureDeclaration lightbar = new FeatureDeclaration(FeatureAvailability.Unknown,
			BackendChannel.None, "星耀后端没有独立灯带通道", new string[0]);
		FeatureDeclaration noOemDisplay = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "星耀后端没有 OEM 显示能力通道", new string[0]);
		// 色彩增强走 Windows 本地 gamma/矩阵（与后端无关），校色走 Windows 颜色管理的本地 ICC。
		FeatureDeclaration displayEffect = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地软件色彩增强（Win32 gamma/矩阵）", new[] { "win32:gamma" });
		FeatureDeclaration colorCalibration = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地 ICC 校色（Windows 颜色管理）", new[] { "win:icc" });
		FeatureDeclaration unsupported = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "星耀没有该下发通道", new string[0]);
		FeatureDeclaration perf = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "星耀本机性能档", new[] { "wmi:Xingyao.Mode" });
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
				[FeatureId.FanCurve] = unsupported,
				[FeatureId.FanBoost] = new FeatureDeclaration(FeatureAvailability.Unknown,
					BackendChannel.None, "星耀未声明强冷通道，回退旧判据", new string[0]),
				[FeatureId.ChargeLimit] = unsupported,
				[FeatureId.PerformanceMode] = perf,
				[FeatureId.RefreshRate] = refreshRate,
        [FeatureId.GpuModeSwitch] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "显卡模式切换：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.GpuHotSwap] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "免重启热切换：本后端未声明，交由门控回退旧判据", new string[0]),
        [FeatureId.IgpuOnly] = new FeatureDeclaration(FeatureAvailability.Unknown,
            BackendChannel.None, "仅集显输出：本后端未声明，交由门控回退旧判据", new string[0])
			});
	}
}
