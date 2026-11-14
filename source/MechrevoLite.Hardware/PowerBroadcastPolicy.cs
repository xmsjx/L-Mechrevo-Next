namespace MechrevoLite.Hardware;

/// <summary>
/// 系统电源广播（WM_POWERBROADCAST）的判定策略。
/// <para>⭐ 必须先判消息号再取 wParam：其他消息的 wParam 可能超出 int 范围，
/// 先转换会抛 OverflowException（曾导致窗口每条消息都记一次崩溃）。</para>
/// </summary>
internal static class PowerBroadcastPolicy
{
	internal const int WmPowerBroadcast = 0x0218;

	internal const long PbtApmResumeSuspend = 0x0007;

	internal const long PbtApmResumeAutomatic = 0x0012;

	/// <summary>该消息是不是"系统已唤醒"（含自动唤醒与挂起唤醒）。</summary>
	/// <param name="msg">窗口消息号。</param>
	/// <param name="wParam">消息的 wParam（只在消息号匹配时才读取）。</param>
	internal static bool IsResume(long msg, long wParam)
	{
		if (msg != WmPowerBroadcast)
		{
			return false;
		}
		return wParam == PbtApmResumeAutomatic || wParam == PbtApmResumeSuspend;
	}

	/// <summary>双通道触发去重：距上次处理不足 <paramref name="dedupeMs"/> 就跳过。</summary>
	internal static bool ShouldHandle(long nowTick, long lastHandledTick, long dedupeMs)
	{
		return nowTick - lastHandledTick >= dedupeMs;
	}
}
