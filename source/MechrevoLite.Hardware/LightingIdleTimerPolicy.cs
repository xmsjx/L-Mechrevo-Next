namespace MechrevoLite.Hardware;

/// <summary>键盘灯自动关闭时间的同步动作。</summary>
internal enum IdleTimerAction
{
	/// <summary>保持现状，不动硬件。</summary>
	Nothing,

	/// <summary>把本地的秒数推给后端。</summary>
	PushLocal,

	/// <summary>后端当前值更长，采用后端的值并回写本地。</summary>
	AdoptGcu
}

/// <summary>
/// 键盘灯自动关闭时间（空闲关灯）的同步策略。
/// <para>⭐ 背景：后端（GCU）的该字段按秒计数，且有自己的默认值（常见 10 分钟）。
/// 若启动时无条件把本地值推下去，会把"10 分钟"悄悄改成"10 秒"，表现为灯效很快就停了。
/// 所以：只有用户显式改过才推送；启动/刷新时只在"本地比后端更长"时推送，否则以后端为准。</para>
/// </summary>
internal static class LightingIdleTimerPolicy
{
	/// <summary>按当前事实决定同步动作。</summary>
	/// <param name="localSeconds">本地配置的秒数（0 = 关闭）。</param>
	/// <param name="backendMinutes">后端上报的分钟数（-1 = 还没收到）。</param>
	/// <param name="explicitChange">本次是否是用户刚改设置触发的同步。</param>
	internal static IdleTimerAction Decide(int localSeconds, int backendMinutes, bool explicitChange)
	{
		if (explicitChange)
		{
			return IdleTimerAction.PushLocal;
		}
		if (localSeconds <= 0)
		{
			// 本地是"关闭"：不主动改后端，避免把后端原有的计时直接抹掉。
			return IdleTimerAction.Nothing;
		}
		if (backendMinutes < 0)
		{
			// 还没读到后端值：以本地为准推一次（否则用户设置永远不生效）。
			return IdleTimerAction.PushLocal;
		}
		int backendSeconds = backendMinutes * 60;
		if (backendSeconds == localSeconds)
		{
			return IdleTimerAction.Nothing;
		}
		return localSeconds > backendSeconds ? IdleTimerAction.PushLocal : IdleTimerAction.AdoptGcu;
	}

	/// <summary>界面档位表对应的秒数（0 = 关闭）。</summary>
	internal static readonly int[] KnownIdleSeconds =
	{
		0, 10, 15, 20, 30, 45, 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800
	};

	/// <summary>该秒数是否是档位表里的合法取值。</summary>
	internal static bool IsKnown(int seconds)
	{
		if (seconds <= 0)
		{
			return true;
		}
		foreach (int option in KnownIdleSeconds)
		{
			if (option == seconds)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>把非法值归一到不超过它的最大合法档（没有就归零）。</summary>
	internal static int Snap(int seconds)
	{
		int best = 0;
		foreach (int option in KnownIdleSeconds)
		{
			if (option > 0 && option <= seconds && option > best)
			{
				best = option;
			}
		}
		return best;
	}
}
