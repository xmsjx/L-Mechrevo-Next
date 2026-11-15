using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方 EC 后端：优先本地 USB 证据，其次 GCU 报文，最后能力位。</summary>
internal sealed class TongfangEcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangEc;

	public string DisplayName => "TongfangEC";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo;
		if (probe.GcuConnected)
		{
			logo = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 已连接，EC 路未接管", new string[0]);
		}
		else if (probe.IteCapability is { } iteLogoCap)
		{
			// 有 7001 时以能力字节为准：设备在 ≠ Logo 通道在。
			logo = iteLogoCap.AnyLogo
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
					"ITE 能力字节有 Logo 通道", new[] { "usb:IteCapability" })
				: new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
					"ITE 能力字节无 Logo 通道", new[] { "usb:IteCapability" });
		}
		else if (probe.EcLogoEvidence == true)
		{
			logo = new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
				"本地 EC 有 Logo 证据", new[] { "usb:IteLightingLocal.HasUsbLogo" });
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LogoLightSeen)
			{
				sources.Add("gcu:LogoLightStatusSeen");
			}
			if (probe.LightbarMbLogoSupport == true)
			{
				sources.Add("gcu:LightbarMbLogoSupport");
			}
			if (probe.LogoLightCapability)
			{
				sources.Add("registry:Capabilities.LogoLight");
			}
			if (probe.EcLogoEvidence == false && sources.Count == 0)
			{
				// 本地 USB/EC 已采集过且明确没有 Logo 灯 ⇒ 断言不支持（不再留给旧判据猜）。
				logo = new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
					"本地无 Logo 灯通道", new[] { "usb:IteLightingLocal.HasUsbLogo" });
			}
			else
			{
				logo = sources.Count > 0
					? new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
						"仅有能力位/GCU 报文，未本地确认", sources.ToArray())
					: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
						"本地 EC 证据未采集或无证据", new string[0]);
			}
		}

		FeatureDeclaration lightbar;
		if (probe.GcuConnected)
		{
			lightbar = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 已连接，EC 路未接管", new string[0]);
		}
		else if (probe.IteCapability is { } iteBarCap)
		{
			// 有 7001 时以能力字节为准：设备在 ≠ 灯带通道在。
			lightbar = iteBarCap.Lightbar
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
					"ITE 能力字节有灯带通道", new[] { "usb:IteCapability" })
				: new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
					"ITE 能力字节无灯带通道", new[] { "usb:IteCapability" });
		}
		else if (probe.EcLightbarEvidence == true)
		{
			lightbar = new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
				"本地 EC 有灯带证据", new[] { "usb:IteLightingLocal.HasLightbar" });
		}
		else
		{
			List<string> sources = new List<string>();
			if (probe.LightbarSeen)
			{
				sources.Add("gcu:LightbarStatusSeen");
			}
			if (probe.LightbarCapability)
			{
				sources.Add("registry:Capabilities.Lightbar");
			}
			if (probe.EcLightbarEvidence == false && sources.Count == 0)
			{
				// 本地 USB/EC 已采集过且明确没有灯带 ⇒ 断言不支持。
				lightbar = new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
					"本地无灯带通道", new[] { "usb:IteLightingLocal.HasLightbar" });
			}
			else
			{
				lightbar = sources.Count > 0
					? new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
						"仅有能力位/GCU 报文，未本地确认", sources.ToArray())
					: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
						"本地 EC 灯带证据未采集或无证据", new string[0]);
			}
		}

		FeatureDeclaration keyboard;
		if (probe.EcKeyboardEvidence == true)
		{
			// 键盘灯可能走 USB 单键 HID，也可能走 EC 单区/白光，按探测到的类型如实标注。
			bool hid = probe.EcKeyboardKind == "HidPerKey";
			string reason = probe.EcKeyboardKind switch
			{
				"HidPerKey" => "本地单键 RGB 键盘灯通道",
				"EcSingleZone" => "EC 单区键盘灯通道",
				"EcWhite" => "EC 白光键盘灯通道",
				_ => "本地键盘灯通道"
			};
			keyboard = new FeatureDeclaration(FeatureAvailability.Supported,
				hid ? BackendChannel.LocalHid : BackendChannel.Local,
				reason,
				hid ? new[] { "usb:IteKeyboard" } : new[] { "ec:0x766" });
		}
		else if (probe.EcKeyboardEvidence == false)
		{
			keyboard = new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
				"EC 无可用键盘灯通道", new[] { "ec:0x766" });
		}
		else
		{
			keyboard = probe.KeyboardCapability
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
					"EC 本地键盘通道可用", new[] { "registry:Capabilities.Keyboard" })
				: new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
					"EC 未上报键盘能力", new string[0]);
		}

		FeatureDeclaration hinge = probe.GcuConnected
			? new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 已连接，EC 路未接管", new string[0])
			: probe.IteCapability is { } iteHingeCap
				? iteHingeCap.Hinge
					? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
						"ITE 能力字节有铰链灯通道", new[] { "usb:IteCapability" })
					: new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
						"ITE 能力字节无铰链灯通道", new[] { "usb:IteCapability" })
				: probe.HingeCapability
					? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
						"EC 本地铰链灯通道", new[] { "usb:IteLightingLocal.HasHinge" })
					: new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
						"EC 没有铰链灯通道", new string[0]);
		FeatureDeclaration sync = probe.GcuConnected
			? new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"GCU 已连接，EC 路未接管", new string[0])
			: probe.SyncCapability
				? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.LocalHid,
					"EC 本地同步灯带通道", new[] { "usb:IteLightingLocal.HasHinge" })
				: new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
					"EC 没有同步灯带通道", new string[0]);

		FeatureDeclaration localDimming;
		if (probe.EcLocalDimmingSupport == true)
		{
			localDimming = new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi,
				"EC 本地分区背光通道可用", new[] { "ec:LocalDimmingSupport" });
		}
		else if (probe.EcLocalDimmingSupport == false)
		{
			localDimming = new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
				"EC 明确不支持分区背光", new string[0]);
		}
		else
		{
			localDimming = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"EC 分区背光证据未采集", new string[0]);
		}

		FeatureDeclaration lcdOverdrive;
		if (probe.EcLcdOverdriveSupport == true)
		{
			lcdOverdrive = new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.AcpiWmi,
				"EC 本地 LCD Overdrive 通道可用", new[] { "ec:LcdOverdriveSupport" });
		}
		else if (probe.EcLcdOverdriveSupport == false)
		{
			lcdOverdrive = new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
				"EC 明确不支持 LCD Overdrive", new string[0]);
		}
		else
		{
			lcdOverdrive = new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None,
				"EC LCD Overdrive 证据未采集", new string[0]);
		}

		// 色彩增强有本地 Win32 gamma/矩阵通道：EC 没有 GCU 档位表时也真的能生效，⛔ 不能声明成不可用。
		FeatureDeclaration displayEffect = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "EC 本地软件色彩增强（Win32 gamma/矩阵）", new[] { "win32:gamma" });
		// ⛔ 不能因为"没有 OEM 通道"就把整块屏幕校色从界面藏掉。
		FeatureDeclaration colorCalibration = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "EC 本地 ICC 校色（Windows 颜色管理）", new[] { "win:icc" });

		FeatureDeclaration fanCurve = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "EC 本地风扇曲线", new[] { "ec:FanCurve" });
		FeatureDeclaration fanBoost = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "EC 本地强冷", new[] { "ec:0x751" });
		FeatureDeclaration chargeLimit = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "EC 本地限充", new[] { "ec:ChargeLimit" });
		FeatureDeclaration performanceMode = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "EC 本地性能档", new[] { "ec:OperatingMode" });
		FeatureDeclaration refreshRate = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.Local, "Windows 显示通道", new[] { "win32:EnumDisplaySettings" });

        // 显卡模式三项：能力位上报了就按上报判，没上报保持 Unknown（⛔ 不写 Unsupported）。
        FeatureDeclaration gpuMode = probe.GpuModeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local, "能力位上报显卡模式", new[] { "registry:Capabilities.DgpuDirect" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报显卡模式", new string[0]);
        FeatureDeclaration gpuHotSwap = probe.GpuHotSwapCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local, "能力位上报免重启热切换", new[] { "registry:Capabilities.GpuHotSwap" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报免重启热切换", new string[0]);
        FeatureDeclaration igpuOnly = probe.IgpuOnlyCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local, "能力位上报仅集显输出", new[] { "registry:Capabilities.IgpuOnly" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报仅集显输出", new string[0]);

        // 性能档三项：能力位上报了就按上报判，没上报保持 Unknown（⛔ 不写 Unsupported）。
        FeatureDeclaration perfTuning = probe.PerfTuningCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local, "能力位上报 CPU 调优/风扇设置", new[] { "registry:Capabilities.CpuPerformanceTuning+FanSettings" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报 CPU 调优/风扇设置", new string[0]);
        FeatureDeclaration perfProfile = probe.ProfileAvailableCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local, "能力位上报自定义档", new[] { "registry:Capabilities.ProfileAvailable" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报自定义档", new string[0]);
        FeatureDeclaration turboMode = probe.TurboModeCapability
            ? new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local, "能力位上报涡轮档", new[] { "registry:Capabilities.TurboMode" })
            : new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位未上报涡轮档", new string[0]);
        FeatureDeclaration silentTurbo = probe.SilentTurboAvailability switch
        {
            MechrevoLite.Hardware.FeatureAvailability.Supported => new FeatureDeclaration(FeatureAvailability.Supported, BackendChannel.Local, "能力位三态=支持", new[] { "registry:Capabilities.SilentTurboAvailability" }),
            MechrevoLite.Hardware.FeatureAvailability.Unsupported => new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None, "能力位三态=不支持", new string[0]),
            _ => new FeatureDeclaration(FeatureAvailability.Unknown, BackendChannel.None, "能力位三态未采集", new string[0])
        };
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar,
				[FeatureId.KeyboardLight] = keyboard,
				[FeatureId.HingeLight] = hinge,
				[FeatureId.SyncLight] = sync,
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
        [FeatureId.PerformanceProfile] = perfProfile,
        [FeatureId.TurboMode] = turboMode,
        [FeatureId.SilentTurbo] = silentTurbo,
        [FeatureId.PerfTuning] = perfTuning
			});
	}
}
