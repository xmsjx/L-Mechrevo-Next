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
		Check(lines, "ec-no-oem-calibration", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.ColorCalibration);
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
			FeatureAvailability.Supported, BackendChannel.WmiMailbox, FeatureId.FanBoost);
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
		FeatureProbe probe = new FeatureProbe(true, true, true, true, true, true, true, true,
			true, true, true, true, true, true, true, true, true, true, true, true, true, true);
		int pass = 0;
		int fail = 0;
		foreach (IBackendModule module in BackendModuleRegistry.All)
		{
			BackendFeatureDeclaration declaration;
			try
			{
				declaration = module.Declare(Facts(module.Kind), probe);
			}
			catch (Exception ex)
			{
				fail++;
				lines.Add("FAIL " + module.Kind + " 声明抛异常 ⇒ " + ex.GetType().Name + " " + ex.Message);
				continue;
			}
			foreach (KeyValuePair<FeatureId, FeatureDeclaration> pair in declaration.Features)
			{
				FeatureDeclaration feature = pair.Value;
				if (feature.State == FeatureAvailability.Supported && feature.Channel == BackendChannel.None)
				{
					fail++;
					lines.Add("FAIL " + module.Kind + " " + pair.Key + " 声明可用但未给通道");
					continue;
				}
				if (feature.State == FeatureAvailability.Unsupported && feature.Channel != BackendChannel.None)
				{
					fail++;
					lines.Add("FAIL " + module.Kind + " " + pair.Key + " 声明不可用却给了通道 " + feature.Channel);
					continue;
				}
				pass++;
			}
			foreach (FeatureId required in RequiredFeatures)
			{
				if (declaration.Features.ContainsKey(required))
				{
					pass++;
					continue;
				}
				fail++;
				lines.Add("FAIL " + module.Kind + " 缺声明 " + required);
			}
		}
		lines.Add("PASS=" + pass + " FAIL=" + fail);
		return string.Join(Environment.NewLine, lines);
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
