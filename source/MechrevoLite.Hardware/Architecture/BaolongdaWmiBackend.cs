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
			BackendChannel.None, "尾部灯带与 LOGO 灯同一路（功能号 15），本后端无独立灯带通道；本地通道由灯效服务判定",
			new string[0]);
		FeatureDeclaration localDimming = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "协议没有分区背光功能号", new string[0]);
		FeatureDeclaration lcdOverdrive = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "协议没有 LCD Overdrive 功能号", new string[0]);
		// 色彩增强走 Windows 本地 gamma/矩阵（与后端无关），校色走 Windows 颜色管理的本地 ICC。
		FeatureDeclaration displayEffect = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地软件色彩增强（Win32 gamma/矩阵）", new[] { "win32:gamma" });
		FeatureDeclaration colorCalibration = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "本地 ICC 校色（Windows 颜色管理）", new[] { "win:icc" });
		FeatureDeclaration fanCurve = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.WmiMailbox, "宝龙达软件闭环风扇曲线", new[] { "wmi:Baolongda.FanCurve" });
		FeatureDeclaration fanBoost = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Ec, "宝龙达本地 EC 强冷（目标 + 掩码）", new[] { "ec:FanTargets+0xB20" });
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
				[FeatureId.LocalDimming] = localDimming,
				[FeatureId.LcdOverdrive] = lcdOverdrive,
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
            BackendChannel.None, "仅集显输出：本后端未声明，交由门控回退旧判据", new string[0])
			});
	}
}
