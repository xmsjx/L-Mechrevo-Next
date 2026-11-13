using System;
using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>统一门控求值：只读事实、声明和探测快照，不触碰硬件。</summary>
internal static class FeatureGate
{
	internal static FeatureDecision Get(FeatureId feature, MechrevoHw? hw)
	{
		return Get(feature, MachineFacts.From(hw), FeatureProbe.From(hw));
	}

	/// <summary>明确支持/不支持时收敛，Unknown 时回退旧判据。</summary>
	internal static bool ResolveSupportOrLegacy(FeatureId feature, MechrevoHw? hw, bool legacySupport)
	{
		try
		{
			return Resolve(Get(feature, hw), legacySupport);
		}
		catch (Exception ex)
		{
			global::Logger.WriteLine("[gate] ResolveSupportOrLegacy failed: " + ex.Message);
		}
		return legacySupport;
	}

	/// <summary>门控结果 → 布尔（纯函数；Unknown 按旧判据，只上报来源一律视为不支持）。</summary>
	internal static bool Resolve(FeatureDecision decision, bool legacySupport)
	{
		if (decision.State == FeatureAvailability.Supported && decision.Channel != BackendChannel.None)
		{
			return true;
		}
		if (decision.State == FeatureAvailability.Unsupported)
		{
			return false;
		}
		if (decision.State == FeatureAvailability.Unknown && decision.Channel == BackendChannel.None
			&& decision.Sources.Length > 0 && IsReportedOnly(decision.Sources))
		{
			return false;
		}
		return legacySupport;
	}

	private static bool IsReportedOnly(string[] sources)
	{
		foreach (string source in sources)
		{
			if (!source.StartsWith("registry:", StringComparison.Ordinal)
				&& !source.StartsWith("gcu:", StringComparison.Ordinal)
				&& !source.StartsWith("bios:", StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	internal static FeatureDecision Get(FeatureId feature, MachineFacts facts, FeatureProbe probe)
	{
		BackendKind backend = facts.Backend == BackendKind.None && facts.DeclaredBackend != BackendKind.None
			? facts.DeclaredBackend
			: facts.Backend;
		if (backend == BackendKind.None)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, BackendKind.None,
				BackendChannel.None, "尚未选定实际后端", Array.Empty<string>());
		}
		IBackendModule? module = BackendModuleRegistry.Resolve(backend);
		if (module == null)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, backend,
				BackendChannel.None, "后端模块未登记", Array.Empty<string>());
		}
		try
		{
			BackendFeatureDeclaration declaration = module.Declare(facts, probe);
			if (!declaration.Features.TryGetValue(feature, out FeatureDeclaration featureDeclaration))
			{
				return new FeatureDecision(feature, FeatureAvailability.Unknown, backend,
					BackendChannel.None, "该功能尚未纳入后端声明", Array.Empty<string>());
			}
			return new FeatureDecision(feature, featureDeclaration.State, backend,
				featureDeclaration.Channel, featureDeclaration.Reason, featureDeclaration.Sources);
		}
		catch (Exception ex)
		{
			return new FeatureDecision(feature, FeatureAvailability.Unknown, backend,
				BackendChannel.None, "后端声明异常: " + ex.Message, Array.Empty<string>());
		}
	}

	/// <summary>离线矩阵自测：只使用合成事实和探测快照，不触碰硬件。</summary>
	internal static string RunSelfTest()
	{
		List<string> lines = new List<string>();
		CheckBackend(lines, "ec-wins-over-mifs",
			MachineFacts.ResolveActualBackend(gcu: false, baolongda: false, nuc: false, xingyao: false, ecMode: true, mifs: true),
			BackendKind.TongfangEc);
		CheckBackend(lines, "nuc-wins-over-ec",
			MachineFacts.ResolveActualBackend(gcu: false, baolongda: false, nuc: true, xingyao: false, ecMode: true, mifs: true),
			BackendKind.NucUmiLocal);
		CheckBackend(lines, "mifs-only",
			MachineFacts.ResolveActualBackend(gcu: false, baolongda: false, nuc: false, xingyao: false, ecMode: false, mifs: true),
			BackendKind.TongfangMifs);
		CheckBackend(lines, "gcu-wins-over-ec",
			MachineFacts.ResolveActualBackend(gcu: true, baolongda: false, nuc: false, xingyao: false, ecMode: true, mifs: true),
			BackendKind.TongfangGcu);
		Check(lines, "gcu-lightbar-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, LightbarCapability: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.Lightbar);
		Check(lines, "gcu-lightbar-no-evidence", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.Lightbar);
		Check(lines, "ec-lightbar-usb", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null, false, false, true),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
		Check(lines, "nuc-lightbar-evidence", Facts(BackendKind.NucUmiLocal),
			new FeatureProbe(false, false, false, null, false, LightbarCapability: true),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
		Check(lines, "nuc-lightbar-unprobed", Facts(BackendKind.NucUmiLocal),
			new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.Lightbar);
		Check(lines, "umi-lightbar-local", Facts(BackendKind.NucUmiLocal, model: "Umi 30"),
			new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
		Check(lines, "gcu-hinge-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, HingeCapability: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.HingeLight);
		Check(lines, "gcu-hinge-no-evidence", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.HingeLight);
		Check(lines, "gcu-sync-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, SyncCapability: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.SyncLight);
		Check(lines, "gcu-keyboard-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, KeyboardCapability: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.KeyboardLight);
		Check(lines, "gcu-keyboard-no-evidence", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.KeyboardLight);
		Check(lines, "ec-keyboard-supported", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, KeyboardCapability: true),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.KeyboardLight);
		Check(lines, "gcu-localdimming-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, LocalDimmingCapability: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.LocalDimming);
		Check(lines, "ec-localdimming-supported", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, EcLocalDimmingSupport: true),
			FeatureAvailability.Supported, BackendChannel.AcpiWmi, FeatureId.LocalDimming);
		Check(lines, "ec-localdimming-disabled", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, EcLocalDimmingSupport: false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.LocalDimming);
		Check(lines, "gcu-lcd-overdrive-disabled", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, LcdOverdriveCapability: true, LcdOverdriveSupport: false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.LcdOverdrive);
		Check(lines, "gcu-lcd-overdrive-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, LcdOverdriveCapability: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.LcdOverdrive);
		Check(lines, "gcu-display-effect-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, DisplayEffectProfile: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.DisplayEffect);
		Check(lines, "gcu-color-calibration-supported", Facts(BackendKind.TongfangGcu),
			new FeatureProbe(true, false, false, null, false, ColorCalibrationCapability: true),
			FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.ColorCalibration);
		Check(lines, "baolongda-no-lightbar", Facts(BackendKind.BaolongdaWmi),
			new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.Lightbar);
		Check(lines, "baolongda-no-oem-calibration", Facts(BackendKind.BaolongdaWmi),
			new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.ColorCalibration);
		// EC 没有 OEM 校色通道，但有本地 ICC（Windows 颜色管理）⇒ 声明 Supported/Local，界面不藏。
		Check(lines, "ec-local-icc-calibration", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.ColorCalibration);
		Check(lines, "ec-local-display-effect", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.DisplayEffect);
		Check(lines, "gcu-supported", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Gcu);
		Check(lines, "gcu-no-evidence", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, false, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "gcu-disconnected", Facts(BackendKind.TongfangGcu), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "ec-supported", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, true),
			FeatureAvailability.Supported, BackendChannel.LocalHid);
		Check(lines, "ec-registry-only", Facts(BackendKind.TongfangEc), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "nuc-unknown", Facts(BackendKind.NucUmiLocal), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "xingyao-unknown", Facts(BackendKind.XingyaoPmc), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "baolongda-supported", Facts(BackendKind.BaolongdaWmi), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.WmiMailbox);
		Check(lines, "no-backend", Facts(BackendKind.None), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.LogoLight);
		Check(lines, "mifs-unsupported", Facts(BackendKind.TongfangMifs), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None);
		Check(lines, "ec-gcu-connected", Facts(BackendKind.TongfangEc), new FeatureProbe(true, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "ec-unprobed", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, null),
			FeatureAvailability.Unknown, BackendChannel.None);
		Check(lines, "ec-fancurve", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.FanCurve);
		Check(lines, "nuc-fancurve", Facts(BackendKind.NucUmiLocal), new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.FanCurve);
		Check(lines, "baolongda-fancurve", Facts(BackendKind.BaolongdaWmi), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.WmiMailbox, FeatureId.FanCurve);
		Check(lines, "mifs-fancurve", Facts(BackendKind.TongfangMifs), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.FanCurve);
		Check(lines, "ec-charge", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.ChargeLimit);
		Check(lines, "baolongda-charge", Facts(BackendKind.BaolongdaWmi), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.WmiMailbox, FeatureId.ChargeLimit);
		Check(lines, "nuc-charge", Facts(BackendKind.NucUmiLocal), new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.ChargeLimit);
		Check(lines, "baolongda-fanboost", Facts(BackendKind.BaolongdaWmi), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Ec, FeatureId.FanBoost);
		Check(lines, "ec-fanboost", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.FanBoost);
		Check(lines, "ec-keyboard-single-zone", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null, EcKeyboardEvidence: true),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.KeyboardLight);
		Check(lines, "ec-keyboard-none", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null, EcKeyboardEvidence: false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.KeyboardLight);
		Check(lines, "ec-keyboard-hid", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null, EcKeyboardEvidence: true, EcKeyboardKind: "HidPerKey"),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.KeyboardLight);
		Check(lines, "ec-logo-none", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.LogoLight);
		Check(lines, "ec-lightbar-none", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null, EcLightbarEvidence: false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.Lightbar);
		Check(lines, "ec-hinge-none", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.HingeLight);
		Check(lines, "ec-sync-none", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.SyncLight);
		// ITE 7001 能力字节：0x0B（IDA/IDB 表）⇒ 灯带 + 铰链 + 新 Logo，无旧 Logo。
		Check(lines, "ite-cap-0b-lightbar", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(0x0B, false)),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
		Check(lines, "ite-cap-0b-hinge", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(0x0B, false)),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.HingeLight);
		Check(lines, "ite-cap-0b-logo", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(0x0B, false)),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.LogoLight);
		Check(lines, "ite-cap-00-lightbar", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(0x00, false)),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.Lightbar);
		// IDX/IDZ 表口径不同：1 = 铰链 + 旧 Logo（无灯带），8 = 什么都没有。
		Check(lines, "ite-cap-01-idx-logo", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(0x01, true)),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.LogoLight);
		Check(lines, "ite-cap-01-idx-lightbar", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(0x01, true)),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.Lightbar);
		Check(lines, "ite-cap-08-idx-logo", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(0x08, true)),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.LogoLight);
		Check(lines, "mifs-fanboost", Facts(BackendKind.TongfangMifs), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.FanBoost);
		Check(lines, "gcu-refresh", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.RefreshRate);
		Check(lines, "undeclared-feature", Facts(BackendKind.TongfangGcu), new FeatureProbe(true, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.KeyboardLight);
		foreach (FeatureId feature in Enum.GetValues<FeatureId>())
		{
			CheckResolve(lines, feature);
		}
		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>逐个功能校验「声明 → 布尔」的六种取值组合。</summary>
	private static void CheckResolve(List<string> lines, FeatureId feature)
	{
		int ok = 0;
		if (Resolve(Decision(feature, FeatureAvailability.Supported, BackendChannel.Local), false))
		{
			ok++;
		}
		if (!Resolve(Decision(feature, FeatureAvailability.Unsupported, BackendChannel.None), true))
		{
			ok++;
		}
		if (Resolve(Decision(feature, FeatureAvailability.Unknown, BackendChannel.None), true))
		{
			ok++;
		}
		if (!Resolve(Decision(feature, FeatureAvailability.Unknown, BackendChannel.None, "gcu:test"), true))
		{
			ok++;
		}
		if (Resolve(Decision(feature, FeatureAvailability.Unknown, BackendChannel.None, "ec:test"), true))
		{
			ok++;
		}
		if (!Resolve(Decision(feature, FeatureAvailability.Supported, BackendChannel.None), false))
		{
			ok++;
		}
		lines.Add((ok == 6 ? "PASS " : "FAIL ") + feature + " 语义矩阵 " + ok + "/6");
	}

	private static FeatureDecision Decision(FeatureId feature, FeatureAvailability state, BackendChannel channel,
		params string[] sources)
	{
		return new FeatureDecision(feature, state, BackendKind.TongfangGcu, channel, "自测合成", sources);
	}

	/// <summary>声明一致性自测：只校验各后端声明表自身是否自洽，不比较旧判据。</summary>
	internal static string RunDeclarationAudit()
	{
		List<string> lines = new List<string>();
		(string Label, FeatureProbe Probe)[] probes =
		{
			("最大能力", ProbeMax()),
			("仅能力位（未连接）", ProbeCapability()),
			("EC 本地证据", ProbeEcEvidence()),
			("GCU 连接 + 仅能力位", ProbeGcuCapability()),
			("无证据", ProbeEmpty())
		};
		for (int i = 0; i < probes.Length; i++)
		{
			int pass = 0;
			int fail = 0;
			List<string> details = new List<string>();
			foreach (IBackendModule module in BackendModuleRegistry.All)
			{
				BackendFeatureDeclaration declaration;
				try
				{
					declaration = module.Declare(Facts(module.Kind), probes[i].Probe);
				}
				catch (Exception ex)
				{
					fail++;
					details.Add("FAIL " + module.Kind + " 声明抛异常 ⇒ " + ex.GetType().Name + " " + ex.Message);
					continue;
				}
				foreach (KeyValuePair<FeatureId, FeatureDeclaration> pair in declaration.Features)
				{
					FeatureDeclaration feature = pair.Value;
					if (feature.State == FeatureAvailability.Supported && feature.Channel == BackendChannel.None)
					{
						fail++;
						details.Add("FAIL " + module.Kind + " " + pair.Key + " 声明可用但未给通道");
						continue;
					}
					if (feature.State == FeatureAvailability.Unsupported && feature.Channel != BackendChannel.None)
					{
						fail++;
						details.Add("FAIL " + module.Kind + " " + pair.Key + " 声明不可用却给了通道 " + feature.Channel);
						continue;
					}
					pass++;
				}
			}
			lines.Add("[" + probes[i].Label + "] PASS=" + pass + " FAIL=" + fail);
			lines.AddRange(details);
		}
		// 核心三项单列：与探针无关，不计入上面各组的 PASS。
		int requiredPass = 0;
		int requiredFail = 0;
		List<string> requiredDetails = new List<string>();
		foreach (IBackendModule module in BackendModuleRegistry.All)
		{
			try
			{
				BackendFeatureDeclaration declaration = module.Declare(Facts(module.Kind), probes[0].Probe);
				foreach (FeatureId required in RequiredFeatures)
				{
					if (declaration.Features.ContainsKey(required))
					{
						requiredPass++;
						continue;
					}
					requiredFail++;
					requiredDetails.Add("FAIL " + module.Kind + " 缺声明 " + required);
				}
			}
			catch (Exception ex)
			{
				requiredFail++;
				requiredDetails.Add("FAIL " + module.Kind + " 声明抛异常（核心三项）⇒ " + ex.GetType().Name + " " + ex.Message);
			}
		}
		lines.Add("[核心三项] PASS=" + requiredPass + " FAIL=" + requiredFail);
		lines.AddRange(requiredDetails);
		return string.Join(Environment.NewLine, lines);
	}

	// 本文件新增的探针全部用具名实参：FeatureProbe 若增删/重排字段，编译器立刻报错，而不是静默错位
	//（RunSelfTest 里的旧断言仍是位置实参）。
	private static FeatureProbe ProbeMax()
	{
		return new FeatureProbe(
			GcuConnected: true, LogoLightCapability: true, LogoLightSeen: true,
			LightbarMbLogoSupport: true, EcLogoEvidence: true, LightbarCapability: true,
			LightbarSeen: true, EcLightbarEvidence: true, HingeCapability: true,
			SyncCapability: true, KeyboardCapability: true, LocalDimmingCapability: true,
			LocalDimmingSeen: true, LcdOverdriveCapability: true, LcdOverdriveSeen: true,
			DisplayEffectProfile: true, ColorCalibrationCapability: true, ColorCalibrationSeen: true,
			LocalDimmingSupport: true, LcdOverdriveSupport: true, EcLocalDimmingSupport: true,
			EcLcdOverdriveSupport: true);
	}

	private static FeatureProbe ProbeCapability()
	{
		return new FeatureProbe(
			GcuConnected: false, LogoLightCapability: true, LogoLightSeen: false,
			LightbarMbLogoSupport: null, EcLogoEvidence: null, LightbarCapability: true,
			LightbarSeen: false, EcLightbarEvidence: null, HingeCapability: true,
			SyncCapability: true, KeyboardCapability: true, LocalDimmingCapability: true,
			LocalDimmingSeen: false, LcdOverdriveCapability: true, LcdOverdriveSeen: false,
			DisplayEffectProfile: true, ColorCalibrationCapability: true, ColorCalibrationSeen: false,
			LocalDimmingSupport: null, LcdOverdriveSupport: null, EcLocalDimmingSupport: null,
			EcLcdOverdriveSupport: null);
	}

	private static FeatureProbe ProbeEcEvidence()
	{
		return new FeatureProbe(
			GcuConnected: false, LogoLightCapability: true, LogoLightSeen: false,
			LightbarMbLogoSupport: true, EcLogoEvidence: true, LightbarCapability: true,
			LightbarSeen: false, EcLightbarEvidence: true, HingeCapability: true,
			SyncCapability: true, KeyboardCapability: true, LocalDimmingCapability: true,
			LocalDimmingSeen: false, LcdOverdriveCapability: true, LcdOverdriveSeen: false,
			DisplayEffectProfile: true, ColorCalibrationCapability: true, ColorCalibrationSeen: false,
			LocalDimmingSupport: null, LcdOverdriveSupport: null, EcLocalDimmingSupport: true,
			EcLcdOverdriveSupport: true);
	}

	private static FeatureProbe ProbeGcuCapability()
	{
		return new FeatureProbe(
			GcuConnected: true, LogoLightCapability: true, LogoLightSeen: false,
			LightbarMbLogoSupport: null, EcLogoEvidence: null, LightbarCapability: true,
			LightbarSeen: false, EcLightbarEvidence: null, HingeCapability: true,
			SyncCapability: true, KeyboardCapability: true, LocalDimmingCapability: true,
			LocalDimmingSeen: false, LcdOverdriveCapability: true, LcdOverdriveSeen: false,
			DisplayEffectProfile: true, ColorCalibrationCapability: true, ColorCalibrationSeen: false,
			LocalDimmingSupport: null, LcdOverdriveSupport: null, EcLocalDimmingSupport: null,
			EcLcdOverdriveSupport: null);
	}

	private static FeatureProbe ProbeEmpty()
	{
		return new FeatureProbe(
			GcuConnected: false, LogoLightCapability: false, LogoLightSeen: false,
			LightbarMbLogoSupport: null, EcLogoEvidence: null, LightbarCapability: false,
			LightbarSeen: false, EcLightbarEvidence: null, HingeCapability: false,
			SyncCapability: false, KeyboardCapability: false, LocalDimmingCapability: false,
			LocalDimmingSeen: false, LcdOverdriveCapability: false, LcdOverdriveSeen: false,
			DisplayEffectProfile: false, ColorCalibrationCapability: false, ColorCalibrationSeen: false,
			LocalDimmingSupport: null, LcdOverdriveSupport: null, EcLocalDimmingSupport: null,
			EcLcdOverdriveSupport: null);
	}

	private static readonly FeatureId[] RequiredFeatures =
	{
		FeatureId.FanCurve,
		FeatureId.FanBoost,
		FeatureId.ChargeLimit
	};

	private static MachineFacts Facts(BackendKind backend, string model = "")
	{
		MachineIdentity identity = MachineIdentity.ForTest(board: "TEST", generation: "Gen40",
			gcuInstalled: backend == BackendKind.TongfangGcu, whitelisted: backend == BackendKind.BaolongdaWmi,
			model: model);
		return new MachineFacts(identity, backend, backend);
	}

	private static void CheckBackend(List<string> lines, string scenario, BackendKind actual, BackendKind expected)
	{
		bool ok = actual == expected;
		lines.Add((ok ? "PASS " : "FAIL ") + scenario + " ⇒ actual=" + actual + " expected=" + expected);
	}

	private static void Check(List<string> lines, string scenario, MachineFacts facts,
		FeatureProbe probe, FeatureAvailability expectedState, BackendChannel expectedChannel,
		FeatureId feature = FeatureId.LogoLight)
	{
		FeatureDecision decision = Get(feature, facts, probe);
		bool ok = decision.State == expectedState && decision.Channel == expectedChannel;
		lines.Add((ok ? "PASS " : "FAIL ") + scenario
			+ " ⇒ state=" + decision.State + " channel=" + decision.Channel
			+ " backend=" + decision.Backend + " reason=" + decision.Reason);
	}
}
