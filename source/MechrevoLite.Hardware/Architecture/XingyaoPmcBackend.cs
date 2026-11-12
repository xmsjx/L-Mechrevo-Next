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
				[FeatureId.ColorCalibration] = noOemDisplay,
				[FeatureId.FanCurve] = unsupported,
				[FeatureId.FanBoost] = unsupported,
				[FeatureId.ChargeLimit] = unsupported,
				[FeatureId.PerformanceMode] = perf,
				[FeatureId.RefreshRate] = refreshRate
			});
	}
}
