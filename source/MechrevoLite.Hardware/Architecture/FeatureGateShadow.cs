using System;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 影子门控日志：只记录新门控结果，不改变任何控件可见性。
/// 用于迁移初期对比旧判断。
/// </summary>
internal static class FeatureGateShadow
{
	internal static void LogLogoLight(MechrevoHw? hw, bool oldVisible, string origin)
	{
		try
		{
			FeatureDecision decision = FeatureGate.Get(FeatureId.LogoLight, hw);
			string line = "[gate-shadow] feature=LogoLight"
				+ " origin=" + origin
				+ " old=" + (oldVisible ? "visible" : "hidden")
				+ " state=" + decision.State
				+ " backend=" + decision.Backend
				+ " channel=" + decision.Channel
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
