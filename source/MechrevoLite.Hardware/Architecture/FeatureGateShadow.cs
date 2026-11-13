using System;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 影子门控日志：只记录新门控结果，不改变任何控件可见性。
/// 用于迁移初期对比旧判断。
/// </summary>
internal static class FeatureGateShadow
{
	internal static void LogLogoLight(MechrevoHw? hw, bool oldValue, string origin, string kind = "visibility")
	{
		Log(FeatureId.LogoLight, hw, oldValue, origin, kind);
	}

	internal static void LogLightbar(MechrevoHw? hw, bool oldValue, string origin, string kind = "visibility")
	{
		Log(FeatureId.Lightbar, hw, oldValue, origin, kind);
	}

	internal static void LogHingeLight(MechrevoHw? hw, bool oldValue, string origin, string kind = "visibility")
	{
		Log(FeatureId.HingeLight, hw, oldValue, origin, kind);
	}

	internal static void LogSyncLight(MechrevoHw? hw, bool oldValue, string origin, string kind = "visibility")
	{
		Log(FeatureId.SyncLight, hw, oldValue, origin, kind);
	}

	internal static void LogKeyboardLight(MechrevoHw? hw, bool oldValue, string origin, string kind = "visibility")
	{
		Log(FeatureId.KeyboardLight, hw, oldValue, origin, kind);
	}

	internal static void LogFeature(FeatureId feature, MechrevoHw? hw, bool oldValue, string origin, string kind = "visibility")
	{
		Log(feature, hw, oldValue, origin, kind);
	}

	// 每个调用点（feature|origin）记录最后一次对比结果与“是否曾经不一致”：刷新循环会反复调用，
	// 只按调用点去重才不会把样本刷爆，同时不至于把“先不一致后一致”洗成完全一致。
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Sample> Seen = new();
	private static string _lastMismatch = "";

	private readonly record struct Sample(bool EverDisagreed, string LastAgree, BackendKind Backend);

	/// <summary>影子对比收敛情况（诊断用；只看当前后端，避免旧后端条目混入）。</summary>
	internal static string Describe(MechrevoHw? hw)
	{
		BackendKind current = MachineFacts.From(hw).Backend;
		int agree = 0;
		int disagree = 0;
		int everDisagree = 0;
		int undecided = 0;
		int otherBackend = 0;
		foreach (System.Collections.Generic.KeyValuePair<string, Sample> pair in Seen)
		{
			if (pair.Value.Backend != current)
			{
				otherBackend++;
				continue;
			}
			if (pair.Value.EverDisagreed)
			{
				everDisagree++;
			}
			switch (pair.Value.LastAgree)
			{
				case "yes":
					agree++;
					break;
				case "no":
					disagree++;
					break;
				default:
					undecided++;
					break;
			}
		}
		string last = System.Threading.Volatile.Read(ref _lastMismatch) ?? "";
		return "当前后端=" + current
			+ " · 调用点=" + (agree + disagree + undecided)
			+ " · 当前一致=" + agree
			+ " · 当前不一致=" + disagree
			+ " · 曾经不一致=" + everDisagree
			+ " · 未决=" + undecided
			+ (otherBackend == 0 ? "" : " · 其它后端调用点=" + otherBackend)
			+ (last.Length == 0 ? " · 最近不一致=(无)" : " · 最近不一致=" + last);
	}

	private static void Record(string key, string agree, string detail, BackendKind backend)
	{
		Seen.AddOrUpdate(key,
			_ => new Sample(agree == "no", agree, backend),
			(_, previous) => new Sample(previous.EverDisagreed || agree == "no", agree, backend));
		if (agree == "no")
		{
			System.Threading.Volatile.Write(ref _lastMismatch, detail);
		}
	}

	private static void Log(FeatureId feature, MechrevoHw? hw, bool oldValue, string origin, string kind)
	{
		try
		{
			MachineFacts facts = MachineFacts.From(hw);
			FeatureDecision decision = FeatureGate.Get(feature, facts, FeatureProbe.From(hw));
			string agree;
			if (decision.State == FeatureAvailability.Unknown)
			{
				agree = "na";
			}
			else
			{
				agree = decision.IsSupported == oldValue ? "yes" : "no";
			}
			Record(feature + "|" + origin, agree,
				feature + "@" + origin + " old=" + (oldValue ? "yes" : "no") + " gate=" + (decision.IsSupported ? "yes" : "no"),
				facts.Backend);
			string declaredMatches = facts.Backend == BackendKind.None && facts.DeclaredBackend == BackendKind.None
				? "n/a"
				: (facts.Backend == facts.DeclaredBackend ? "yes" : "no");
			string line = "[gate-shadow] feature=" + feature
				+ " origin=" + origin
				+ " kind=" + kind
				+ " old=" + (oldValue ? "yes" : "no")
				+ " state=" + decision.State
				+ " supported=" + (decision.IsSupported ? "yes" : "no")
				+ " backend=" + decision.Backend
				+ " declared=" + facts.DeclaredBackend
				+ " declaredMatches=" + declaredMatches
				+ " channel=" + decision.Channel
				+ " agree=" + agree
				+ " reason=" + decision.Reason
				+ " sources=" + string.Join(",", decision.Sources);
			global::Logger.WriteLineIfChanged("gate-shadow-" + feature + "-" + origin, line);
		}
		catch (Exception ex)
		{
			global::Logger.WriteLine("[gate-shadow] feature=" + feature + " origin=" + origin
				+ " failed: " + ex.Message);
		}
	}
}
