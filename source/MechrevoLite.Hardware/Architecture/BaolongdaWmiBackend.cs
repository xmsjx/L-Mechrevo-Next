using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>宝龙达 WMI 后端：Logo 走功能号 15 的开关通道；没有灯带通道。</summary>
internal sealed class BaolongdaWmiBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.BaolongdaWmi;

	public string DisplayName => "BaolongdaWMI";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.WmiMailbox, "宝龙达功能号 15 提供 Logo 开关",
			new[] { "wmi:Baolongda.Function15" });
		FeatureDeclaration lightbar = new FeatureDeclaration(FeatureAvailability.Unknown,
			BackendChannel.None, "宝龙达未声明灯带通道，回退旧判据", new string[0]);
		FeatureDeclaration noOemDisplay = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "宝龙达没有 OEM 显示能力通道", new string[0]);
		FeatureDeclaration fanCurve = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.WmiMailbox, "宝龙达软件闭环风扇曲线", new[] { "wmi:Baolongda.FanCurve" });
		FeatureDeclaration fanBoost = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.WmiMailbox, "宝龙达本机强冷通道", new[] { "wmi:Baolongda.FanBoost" });
		FeatureDeclaration chargeLimit = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.WmiMailbox, "宝龙达限充通道", new[] { "wmi:Baolongda.ChargeLimit" });
		FeatureDeclaration performanceMode = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.WmiMailbox, "宝龙达性能档通道", new[] { "wmi:Baolongda.OperatingMode" });
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
				[FeatureId.FanCurve] = fanCurve,
				[FeatureId.FanBoost] = fanBoost,
				[FeatureId.ChargeLimit] = chargeLimit,
				[FeatureId.PerformanceMode] = performanceMode,
				[FeatureId.RefreshRate] = refreshRate
			});
	}
}
