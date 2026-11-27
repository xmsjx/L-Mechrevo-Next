using System;
using System.Collections.Generic;
using System.Text;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>统一门控求值：只读事实、声明和探测快照，不触碰硬件。</summary>
/// <summary>
/// 门控结论与界面的契约：
/// <para>Supported ⇒ 显示；Unsupported ⇒ 隐藏；Unknown ⇒ <b>回退旧判据</b>，不得直接隐藏。</para>
/// <para>要把某个特征改成"Unknown 也隐藏"，必须同时满足两条：</para>
/// <para>① 六个后端在"确有该硬件"的机型都能给出 Supported（声明覆盖 + 证据齐全）；</para>
/// <para>② 连续两版诊断包里 [gate] 结论与界面实际表现一致。</para>
/// </summary>
internal static class FeatureGate
{
    /// <summary>本地通道证据提供者（Lightbar/KeyboardLight/LogoLight）：true=本地探测可用，null=不知道。</summary>
    internal static Func<FeatureId, bool?>? LocalChannelEvidenceProvider { get; set; }
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
            FeatureDecision decided = new FeatureDecision(feature, featureDeclaration.State, backend,
				featureDeclaration.Channel, featureDeclaration.Reason, featureDeclaration.Sources);
            // 后端声明 Unknown 时，先看本地通道探测（同一功能可能由本地 HID/EC 通道提供）。
            if (decided.State == FeatureAvailability.Unknown)
            {
                bool? local = LocalChannelEvidenceProvider?.Invoke(feature);
                if (local == true)
                {
                    return new FeatureDecision(feature, FeatureAvailability.Supported, backend,
                        BackendChannel.LocalHid, "本地通道探测可用", new[] { "local:probe" });
                }
            }
            return decided;
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
		// 色彩增强与校色都是 Windows 本地通道（gamma 矩阵 / 颜色管理 ICC），与后端无关 ⇒ 各后端都声明本地可用。
		Check(lines, "baolongda-local-display-effect", Facts(BackendKind.BaolongdaWmi),
			new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.DisplayEffect);
		Check(lines, "nuc-local-display-effect", Facts(BackendKind.NucUmiLocal),
			new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Supported, BackendChannel.Local, FeatureId.DisplayEffect);
		Check(lines, "ec-lightbar-usb", Facts(BackendKind.TongfangEc),
			new FeatureProbe(false, false, false, null, null, false, false, true),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
		Check(lines, "nuc-lightbar-evidence", Facts(BackendKind.NucUmiLocal),
			new FeatureProbe(false, false, false, null, false, LightbarCapability: true),
			FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
        // 本地 USB 灯带证据明确为 false 且没有任何能力位来源 ⇒ 断言不支持（不是"未采集"）。
        // Logo 与灯带的"有/无"两侧都要有断言，才算把该特征的正反结论钉住。
        Check(lines, "ec-logo-none-evidence", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.LogoLight);
        Check(lines, "ec-logo-usb-evidence", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, true, IteCapability: new IteLightingCapability(0x04, false)),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.LogoLight);
        Check(lines, "ec-logo-lightbar-only", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, true),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.LogoLight);
        Check(lines, "gcu-logo-lightbar-only", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(false, false, false, null, true),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.LogoLight);
        Check(lines, "ec-lightbar-usb-evidence", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false, false, false, true),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
        Check(lines, "ec-lightbar-none-evidence", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false, false, false, false),
            FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.Lightbar);
        // 性能档与显卡三项在 EC 路统一先声明 Unknown（界面回退旧判据）。
        Check(lines, "ec-perf-tuning-unknown", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.PerfTuning);
        Check(lines, "ec-silent-turbo-unknown", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.SilentTurbo);
        // GCU 路按能力位判：上报了就 Supported/Gcu，没上报保持 Unknown。
        Check(lines, "gcu-gpu-mode-supported", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, false, null, false, GpuModeCapability: true),
            FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.GpuModeSwitch);
        Check(lines, "gcu-gpu-mode-no-evidence", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.GpuModeSwitch);

        // 性能档三项同样按能力位判：上报了就 Supported，没上报保持 Unknown。
        Check(lines, "gcu-perf-tuning-supported", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, false, null, false, PerfTuningCapability: true),
            FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.PerfTuning);
        Check(lines, "gcu-perf-tuning-no-evidence", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.PerfTuning);
        Check(lines, "gcu-turbo-mode-supported", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, false, null, false, TurboModeCapability: true),
            FeatureAvailability.Supported, BackendChannel.Gcu, FeatureId.TurboMode);
        Check(lines, "gcu-silent-turbo-unsupported", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, false, null, false, SilentTurboAvailability: FeatureAvailability.Unsupported),
            FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.SilentTurbo);

        // 四个"能力位驱动"的后端：真 ⇒ Supported(各自通道)，假 ⇒ Unknown。
        Check(lines, "baolongda-gpu-mode-supported", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false, GpuModeCapability: true),
            FeatureAvailability.Supported, BackendChannel.WmiMailbox, FeatureId.GpuModeSwitch);
        Check(lines, "baolongda-gpu-mode-no-evidence", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.GpuModeSwitch);
        Check(lines, "baolongda-perf-tuning-supported", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false, PerfTuningCapability: true),
            FeatureAvailability.Supported, BackendChannel.WmiMailbox, FeatureId.PerfTuning);
        Check(lines, "nuc-gpu-mode-supported", Facts(BackendKind.NucUmiLocal),
            new FeatureProbe(false, false, false, null, false, GpuModeCapability: true),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.GpuModeSwitch);
        Check(lines, "nuc-perf-tuning-supported", Facts(BackendKind.NucUmiLocal),
            new FeatureProbe(false, false, false, null, false, PerfTuningCapability: true),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.PerfTuning);
        Check(lines, "xingyao-perf-tuning-supported", Facts(BackendKind.XingyaoPmc),
            new FeatureProbe(false, false, false, null, false, PerfTuningCapability: true),
            FeatureAvailability.Supported, BackendChannel.AcpiWmi, FeatureId.PerfTuning);
        Check(lines, "mifs-perf-tuning-supported", Facts(BackendKind.TongfangMifs),
            new FeatureProbe(false, false, false, null, false, PerfTuningCapability: true),
            FeatureAvailability.Supported, BackendChannel.AcpiWmi, FeatureId.PerfTuning);
        Check(lines, "mifs-perf-tuning-no-evidence", Facts(BackendKind.TongfangMifs),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.PerfTuning);

        // 键盘灯：本地 USB/EC 证据优先，其次能力位；这是"只认门控"试点能否成立的关键。
        Check(lines, "gcu-keyboard-local-evidence", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, false, null, false, EcKeyboardEvidence: true),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.KeyboardLight);
        Check(lines, "baolongda-keyboard-local-evidence", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false, EcKeyboardEvidence: true),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.KeyboardLight);
        Check(lines, "mifs-keyboard-no-evidence", Facts(BackendKind.TongfangMifs),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.KeyboardLight);

        // 灯带 / Logo：本地证据优先，其次能力位 —— 这是"六后端都能判"的前提。
        Check(lines, "baolongda-lightbar-local-evidence", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false, EcLightbarEvidence: true),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
        Check(lines, "baolongda-lightbar-no-evidence", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.Lightbar);
        Check(lines, "nuc-lightbar-capability", Facts(BackendKind.NucUmiLocal),
            new FeatureProbe(true, false, false, null, false, LightbarCapability: true),
            FeatureAvailability.Supported, BackendChannel.LocalHid, FeatureId.Lightbar);
        Check(lines, "xingyao-logo-capability", Facts(BackendKind.XingyaoPmc),
            new FeatureProbe(false, false, true, null, false),
            FeatureAvailability.Supported, BackendChannel.AcpiWmi, FeatureId.LogoLight);
        Check(lines, "mifs-lightbar-capability", Facts(BackendKind.TongfangMifs),
            new FeatureProbe(false, false, false, null, false, LightbarCapability: true),
            FeatureAvailability.Supported, BackendChannel.AcpiWmi, FeatureId.Lightbar);

        // Logo 两种来源要在结论里能区分：独立 Logo 与"与灯带同一路"。
        CheckReason(lines, "baolongda-logo-mb-lightbar", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, true, false, true, false),
            FeatureAvailability.Supported, FeatureId.LogoLight, "与灯带");
        CheckReason(lines, "gcu-logo-standalone", Facts(BackendKind.TongfangGcu),
            new FeatureProbe(true, false, true, null, false),
            FeatureAvailability.Supported, FeatureId.LogoLight, "独立 Logo");

        // 同方两类 Logo：USB lettering/旧通道 vs A 面 EC Logo（MBALogo）。
        CheckReason(lines, "tongfang-logo-usb-lettering", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false, IteCapability: new IteLightingCapability(8, false)),
            FeatureAvailability.Supported, FeatureId.LogoLight, "独立 USB Logo（字母形");
        CheckReason(lines, "tongfang-logo-ec-mba", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false, MbaLogoSupport: true),
            FeatureAvailability.Supported, FeatureId.LogoLight, "A 面 EC Logo");
        Check(lines, "ec-gpu-mode-unknown", Facts(BackendKind.TongfangEc),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unknown, BackendChannel.None, FeatureId.GpuModeSwitch);
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
		Check(lines, "baolongda-tail-lightbar-same-as-logo", Facts(BackendKind.BaolongdaWmi),
			new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None, FeatureId.Lightbar);
		Check(lines, "baolongda-no-localdimming", Facts(BackendKind.BaolongdaWmi),
			new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.LocalDimming);
        // 宝龙达机型没有联网校色通道 ⇒ 声明 Unsupported（其它四路为 Supported/Local）。
        Check(lines, "baolongda-none-calibration", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.ColorCalibration);
        // 联网校色：星耀 / UMI / 同方 / 无界四路都有，宝龙达没有。
        Check(lines, "baolongda-no-calibration", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Unsupported, BackendChannel.None, FeatureId.ColorCalibration);
        Check(lines, "nuc-calibration-local", Facts(BackendKind.NucUmiLocal),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Supported, BackendChannel.Local, FeatureId.ColorCalibration);
        Check(lines, "xingyao-calibration-local", Facts(BackendKind.XingyaoPmc),
            new FeatureProbe(false, false, false, null, false),
            FeatureAvailability.Supported, BackendChannel.Local, FeatureId.ColorCalibration);
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
		Check(lines, "ec-supported", Facts(BackendKind.TongfangEc), new FeatureProbe(false, false, false, null, true, IteCapability: new IteLightingCapability(0x04, false)),
			FeatureAvailability.Supported, BackendChannel.LocalHid);
		Check(lines, "ec-registry-only", Facts(BackendKind.TongfangEc), new FeatureProbe(false, true, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
        Check(lines, "nuc-unknown", Facts(BackendKind.NucUmiLocal), new FeatureProbe(false, false, false, null, false),
			FeatureAvailability.Unknown, BackendChannel.None);
        Check(lines, "xingyao-unknown", Facts(BackendKind.XingyaoPmc), new FeatureProbe(false, false, false, null, false),
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
        // 本地通道证据：声明 Unknown 时由本地探测补位；声明 Unsupported 时不许被补位。
        LocalChannelEvidenceProvider = f => f == FeatureId.Lightbar ? true : null;
        Check(lines, "local-evidence-fills-unknown", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, true, false, null, false), FeatureAvailability.Supported,
            BackendChannel.LocalHid, FeatureId.Lightbar);
        Check(lines, "local-evidence-not-for-unsupported", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, true, false, null, false), FeatureAvailability.Unsupported,
            BackendChannel.None, FeatureId.LocalDimming);
        LocalChannelEvidenceProvider = null;
        Check(lines, "no-local-evidence-stays-unknown", Facts(BackendKind.BaolongdaWmi),
            new FeatureProbe(false, true, false, null, false), FeatureAvailability.Unknown,
            BackendChannel.None, FeatureId.Lightbar);
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

    /// <summary>只读导出：特征 × 各后端的声明矩阵（核对用，不参与运行判定）。</summary>
    /// <summary>声明覆盖率：每个特征在每个后端都必须有声明条目（缺项会被判"尚未纳入"）。</summary>
    internal static string RunCoverageCheck()
    {
        StringBuilder sb = new StringBuilder();
        int total = 0;
        int missing = 0;
        foreach (FeatureId feature in Enum.GetValues<FeatureId>())
        {
            foreach (IBackendModule module in BackendModuleRegistry.All)
            {
                total++;
                FeatureDecision decision = Get(feature, Facts(module.Kind), ProbeEmpty());
                if (decision.Reason.Contains("尚未纳入后端声明", StringComparison.Ordinal))
                {
                    missing++;
                    sb.AppendLine("缺声明：" + module.Kind + " " + feature);
                }
            }
        }
        string head = "声明覆盖=" + (total - missing) + "/" + total
            + (missing == 0 ? " ✅" : " ❌") + Environment.NewLine;
        return head + sb.ToString();
    }

    internal static string RunDeclarationMatrix()
    {
        (string Label, FeatureProbe Probe)[] probes =
        {
            ("最大能力", ProbeMax()),
            ("仅能力位（未连接）", ProbeCapability()),
            ("EC 本地证据", ProbeEcEvidence()),
            ("GCU 连接 + 仅能力位", ProbeGcuCapability()),
            ("无证据", ProbeEmpty())
        };
        StringBuilder sb = new StringBuilder();
        for (int p = 0; p < probes.Length; p++)
        {
            string label = probes[p].Label;
            FeatureProbe probe = probes[p].Probe;
            List<string> names = new List<string>();
            List<BackendFeatureDeclaration> declarations = new List<BackendFeatureDeclaration>();
            foreach (IBackendModule module in BackendModuleRegistry.All)
            {
                names.Add(module.DisplayName);
                try
                {
                    declarations.Add(module.Declare(Facts(module.Kind), probe));
                }
                catch
                {
                    declarations.Add(new BackendFeatureDeclaration(module.Kind, new Dictionary<FeatureId, FeatureDeclaration>()));
                }
            }
            sb.AppendLine("## " + label);
            sb.Append("| 特征 |");
            for (int n = 0; n < names.Count; n++)
            {
                sb.Append(" " + names[n] + " |");
            }
            sb.AppendLine();
            sb.Append("|---|");
            for (int n = 0; n < names.Count; n++)
            {
                sb.Append("---|");
            }
            sb.AppendLine();
            foreach (FeatureId feature in Enum.GetValues<FeatureId>())
            {
                sb.Append("| " + feature + " |");
                for (int n = 0; n < declarations.Count; n++)
                {
                    string cell = "?";
                    if (declarations[n].Features.TryGetValue(feature, out FeatureDeclaration? d))
                    {
                        if (d.State == FeatureAvailability.Supported)
                        {
                            cell = "✓" + d.Channel;
                        }
                        else if (d.State == FeatureAvailability.Unsupported)
                        {
                            cell = "✗";
                        }
                    }
                    sb.Append(" " + cell + " |");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

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

    /// <summary>状态与原因同时校验：用于区分"独立 Logo"与"与灯带同一路"这类同状态不同来源。</summary>
    private static void CheckReason(List<string> lines, string scenario, MachineFacts facts,
        FeatureProbe probe, FeatureAvailability expectedState, FeatureId feature, string reasonContains)
    {
        FeatureDecision decision = Get(feature, facts, probe);
        bool ok = decision.State == expectedState
            && decision.Reason.Contains(reasonContains, StringComparison.Ordinal);
        lines.Add((ok ? "PASS " : "FAIL ") + scenario
            + " ⇒ state=" + decision.State + " backend=" + decision.Backend
            + " reason=" + decision.Reason);
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
