using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 GCU 后端：Logo/灯带由 GCU 状态包或已缓存能力位证明。</summary>
internal sealed class TongfangGcuBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangGcu;

	public string DisplayName => "TongfangGCU";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo;
		if (!probe.GcuConnected)
		{
			logo = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LogoLightCapability)
			{
				sources.Add("registry:Capabilities.LogoLight");
			}
			if (probe.LogoLightSeen)
			{
				sources.Add("gcu:LogoLightStatusSeen");
			}
			if (probe.LightbarMbLogoSupport == true)
			{
				sources.Add("gcu:LightbarMbLogoSupport");
			}
			logo = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
					"GCU 上报 Logo 证据", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"GCU 已连接但未上报 Logo 证据", new string[0]);
		}

		FeatureDeclaration lightbar;
		if (!probe.GcuConnected)
		{
			lightbar = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LightbarCapability)
			{
				sources.Add("registry:Capabilities.Lightbar");
			}
			if (probe.LightbarSeen)
			{
				sources.Add("gcu:LightbarStatusSeen");
			}
			lightbar = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
					"GCU 上报灯带证据", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"GCU 已连接但未上报灯带证据", new string[0]);
		}

		FeatureDeclaration hinge = probe.GcuConnected && probe.HingeCapability
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 上报铰链灯能力", new[] { "gcu:HingeSupport", "gcu:LightbarStatusType", "bios:ProjectId" })
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				probe.GcuConnected ? "GCU 未上报铰链灯能力" : "GCU 未连接", new string[0]);
		FeatureDeclaration sync = probe.GcuConnected && probe.SyncCapability
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 上报同步灯带能力", new[] { "gcu:SyncStatusType", "gcu:LightbarStatusType", "bios:ProjectId" })
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				probe.GcuConnected ? "GCU 未上报同步灯带能力" : "GCU 未连接", new string[0]);

		FeatureDeclaration keyboard = probe.GcuConnected && probe.KeyboardCapability
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 键盘通道可用", new[] { "registry:Capabilities.Keyboard" })
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				probe.GcuConnected ? "GCU 未上报键盘能力" : "GCU 未连接", new string[0]);

		FeatureDeclaration localDimming;
		if (!probe.GcuConnected)
		{
			localDimming = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		}
		else if (probe.LocalDimmingSupport == false && !probe.LocalDimmingCapability)
		{
			localDimming = new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
				"GCU 明确不支持分区背光", new string[0]);
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LocalDimmingSupport == true)
			{
				sources.Add("gcu:LocalDimmingSupport");
			}
			if (probe.LocalDimmingCapability && probe.LocalDimmingSupport != false)
			{
				sources.Add("registry:Capabilities.LocalDimming");
			}
			localDimming = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
					"GCU 上报分区背光能力", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"GCU 未上报分区背光能力", new string[0]);
		}

		FeatureDeclaration lcdOverdrive;
		if (!probe.GcuConnected)
		{
			lcdOverdrive = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		}
		else if (probe.LcdOverdriveSupport == false)
		{
			lcdOverdrive = new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
				"GCU 明确不支持 LCD Overdrive", new string[0]);
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LcdOverdriveSupport == true)
			{
				sources.Add("gcu:LcdOverdriveSupport");
			}
			if (probe.LcdOverdriveCapability)
			{
				sources.Add("registry:Capabilities.LcdOverdrive");
			}
			if (probe.LcdOverdriveSeen)
			{
				sources.Add("gcu:LcdOverdriveStatusSeen");
			}
			lcdOverdrive = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
					"GCU 上报 LCD Overdrive 能力", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"GCU 未上报 LCD Overdrive 能力", new string[0]);
		}
		FeatureDeclaration displayEffect = probe.GcuConnected && probe.DisplayEffectProfile
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 提供显示效果 Profile", new[] { "gcu:DisplayEffectProfiles" })
			// 没有 GCU 档位表时走 Windows 本地 gamma/矩阵色彩增强，功能仍然可用。
			: new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local,
				probe.GcuConnected ? "GCU 未提供档位表，走本地软件色彩增强" : "GCU 未连接，走本地软件色彩增强", new[] { "win32:gamma" });
		FeatureDeclaration colorCalibration;
		if (!probe.GcuConnected)
		{
			// 没有 GCU 也还能用 Windows 颜色管理的本地 ICC 校色。
			colorCalibration = new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local,
				"GCU 未连接，走本地 ICC 校色", new[] { "win:icc" });
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.ColorCalibrationCapability)
			{
				sources.Add("registry:Capabilities.ColorCalibration");
			}
			if (probe.ColorCalibrationSeen)
			{
				sources.Add("gcu:ColorCalibrationSeen");
			}
			colorCalibration = sources.Count > 0
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
					"GCU 上报校色能力", sources.ToArray())
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"GCU 未上报校色能力", new string[0]);
		}

		FeatureDeclaration refreshRate = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "Windows 显示通道", new[] { "win32:EnumDisplaySettings" });
		FeatureDeclaration fanCurve = probe.GcuConnected
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 风扇曲线通道", new[] { "gcu:FanCurve" })
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		FeatureDeclaration fanBoost = probe.GcuConnected
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 强冷通道", new[] { "gcu:FanBoost" })
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		FeatureDeclaration chargeLimit = probe.GcuConnected
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 限充通道", new[] { "gcu:ChargeLimit" })
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 未连接", new string[0]);
		FeatureDeclaration performanceMode = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Gcu, "GCU 性能档通道", new[] { "gcu:OperatingMode" });


        // 显卡模式三项：能力位上报了就按上报判，没上报保持 Unknown（⛔ 不写 Unsupported）。
        FeatureDeclaration gpuMode = probe.GpuModeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu, "能力位上报显卡模式", new[] { "registry:Capabilities.DgpuDirect" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报显卡模式", new string[0]);
        FeatureDeclaration gpuHotSwap = probe.GpuHotSwapCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu, "能力位上报免重启热切换", new[] { "registry:Capabilities.GpuHotSwap" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报免重启热切换", new string[0]);
        FeatureDeclaration igpuOnly = probe.IgpuOnlyCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu, "能力位上报仅集显输出", new[] { "registry:Capabilities.IgpuOnly" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报仅集显输出", new string[0]);
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar,
				[FeatureId.HingeLight] = hinge,
				[FeatureId.SyncLight] = sync,
				[FeatureId.KeyboardLight] = keyboard,
				[FeatureId.LocalDimming] = localDimming,
				[FeatureId.LcdOverdrive] = lcdOverdrive,
				[FeatureId.DisplayEffect] = displayEffect,
				[FeatureId.ColorCalibration] = colorCalibration,
				[FeatureId.FanCurve] = fanCurve,
				[FeatureId.FanBoost] = fanBoost,
				[FeatureId.ChargeLimit] = chargeLimit,
				[FeatureId.PerformanceMode] = performanceMode,
				[FeatureId.RefreshRate] = refreshRate,
        [FeatureId.GpuModeSwitch] = gpuMode,
        [FeatureId.GpuHotSwap] = gpuHotSwap,
        [FeatureId.IgpuOnly] = igpuOnly,
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
