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

		FeatureDeclaration localDimming = probe.GcuConnected && (probe.LocalDimmingCapability || probe.LocalDimmingSeen)
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 上报分区背光能力", Source("registry:Capabilities.LocalDimming", "gcu:LocalDimmingStatusSeen"))
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				probe.GcuConnected ? "GCU 未上报分区背光能力" : "GCU 未连接", new string[0]);
		FeatureDeclaration lcdOverdrive = probe.GcuConnected && (probe.LcdOverdriveCapability || probe.LcdOverdriveSeen)
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 上报 LCD Overdrive 能力", Source("registry:Capabilities.LcdOverdrive", "gcu:LcdOverdriveStatusSeen"))
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				probe.GcuConnected ? "GCU 未上报 LCD Overdrive 能力" : "GCU 未连接", new string[0]);
		FeatureDeclaration displayEffect = probe.GcuConnected && probe.DisplayEffectProfile
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 提供显示效果 Profile", new[] { "gcu:DisplayEffectProfiles" })
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				probe.GcuConnected ? "GCU 未提供显示效果 Profile" : "GCU 未连接", new string[0]);
		FeatureDeclaration colorCalibration = probe.GcuConnected && (probe.ColorCalibrationCapability || probe.ColorCalibrationSeen)
			? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Gcu,
				"GCU 上报校色能力", Source("registry:Capabilities.ColorCalibration", "gcu:ColorCalibrationSeen"))
			: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				probe.GcuConnected ? "GCU 未上报校色能力" : "GCU 未连接", new string[0]);

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
				[FeatureId.ColorCalibration] = colorCalibration
			});
	}

	private static string[] Source(string registry, string gcu)
	{
		return new[] { registry, gcu };
	}
}
