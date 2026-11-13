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

	// 每个调用点（feature|origin）只保留最后一次对比结果：刷新循环会反复调用，按调用点去重才有意义。
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Seen = new();
	private static string _lastMismatch = "";

	/// <summary>影子对比累计（诊断用：门控与旧判据是否已收敛）。</summary>
	internal static string Describe()
	{
		int agree = 0;
		int disagree = 0;
		int undecided = 0;
		foreach (System.Collections.Generic.KeyValuePair<string, string> pair in Seen)
		{
			switch (pair.Value)
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
		return "调用点=" + Seen.Count + " · 一致=" + agree + " · 不一致=" + disagree + " · 未决=" + undecided
			+ (last.Length == 0 ? " · 最近不一致=(无)" : " · 最近不一致=" + last);
	}

	private static void Record(string key, string agree, string detail)
	{
		Seen[key] = agree;
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
				feature + "@" + origin + " old=" + (oldValue ? "yes" : "no") + " gate=" + (decision.IsSupported ? "yes" : "no"));
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
