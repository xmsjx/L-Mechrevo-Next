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
		try
		{
			MachineFacts facts = MachineFacts.From(hw);
			FeatureDecision decision = FeatureGate.Get(FeatureId.LogoLight, facts, FeatureProbe.From(hw));
			string agree = decision.State == FeatureAvailability.Unknown
				? "na"
				: (decision.IsSupported == oldValue ? "yes" : "no");
			string line = "[gate-shadow] feature=LogoLight"
				+ " origin=" + origin
				+ " kind=" + kind
				+ " old=" + (oldValue ? "yes" : "no")
				+ " state=" + decision.State
				+ " backend=" + decision.Backend
				+ " declared=" + facts.DeclaredBackend
				+ " declaredMatches=" + (facts.Backend == facts.DeclaredBackend ? "yes" : "no")
				+ " channel=" + decision.Channel
				+ " agree=" + agree
				+ " reason=" + decision.Reason
				+ " sources=" + string.Join(",", decision.Sources);
			global::Logger.WriteLineIfChanged("gate-shadow-logo-" + origin, line);
		}
		catch (Exception ex)
		{
			global::Logger.WriteLine("[gate-shadow] feature=LogoLight origin=" + origin
				+ " failed: " + ex.Message);
		}
	}
}
