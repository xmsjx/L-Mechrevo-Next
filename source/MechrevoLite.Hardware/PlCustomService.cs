using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 自定义档的三个功耗设定值：读写、软件记忆、补写。
/// <para>界面只显示与手势，能否用/写什么/记住什么都在这里判定。</para>
/// </summary>
internal static class PlCustomService
{
	private const string KeyPl1 = "pl_custom_pl1";

	private const string KeyPl2 = "pl_custom_pl2";

	private const string KeyPl4 = "pl_custom_pl4";

	private const string KeyActive = "pl_custom_active";

	/// <summary>补写节流：最多 5 秒一次。</summary>
	private const long ReapplyIntervalMs = 5000;

	/// <summary>连续失败这么多次后本次运行不再补写，避免反复打 EC。</summary>
	private const int MaxReapplyFailures = 3;

	private static long _reapplyAt;

	private static int _reapplyFailures;

	/// <summary>本机是否有可写的功耗设定值（声明表决定）。</summary>
	internal static bool IsSupported => EcPlCustom.InUse;

	/// <summary>声明表是否给出了 PL4 的写入地址：有才允许界面开放 fPPT 行。</summary>
	internal static bool HasPl4 => !string.IsNullOrWhiteSpace(DeclarationRuntime.Param("plCustom", "pl4"));

	/// <summary>自定义档是否处于选中状态（软件记忆）。</summary>
	internal static bool IsActive
	{
		get
		{
			try
			{
				return IsSupported && AppConfig.Is(KeyActive);
			}
			catch
			{
				return false;
			}
		}
	}

	internal static int Minimum => EcPlCustom.Minimum;

	internal static int MaxPl1 => EcPlCustom.MaxPl1;

	internal static int MaxPl2 => EcPlCustom.MaxPl2;

	internal static int MaxPl4 => EcPlCustom.MaxPl4;

	/// <summary>读当前三个设定值；读不到时给出记住的值，返回值表示这次是否真读到。</summary>
	internal static bool TryReadOrRemembered(out int pl1, out int pl2, out int pl4)
	{
		if (IsSupported && EcPlCustom.TryRead(out pl1, out pl2, out pl4))
		{
			return true;
		}
		pl1 = AppConfig.Get(KeyPl1, 45);
		pl2 = AppConfig.Get(KeyPl2, 45);
		pl4 = AppConfig.Get(KeyPl4, 60);
		return false;
	}

	/// <summary>按上限夹好、写入、回读确认，成功即记住并标记为自定义档在选中状态。</summary>
	internal static bool TryApply(int pl1, int pl2, int pl4, out string detail)
	{
		int v1 = Clamp(pl1, Minimum, MaxPl1);
		int v2 = Clamp(pl2, Minimum, MaxPl2);
		int v4 = Clamp(pl4, Minimum, MaxPl4);
		if (!EcPlCustom.TrySet(v1, v2, v4, out detail))
		{
			_reapplyFailures = 0;
			return false;
		}
		Remember(v1, v2, v4);
		return true;
	}

	/// <summary>记住三个值并标记自定义档为选中。</summary>
	internal static void Remember(int pl1, int pl2, int pl4)
	{
		try
		{
			AppConfig.Set(KeyPl1, pl1);
			AppConfig.Set(KeyPl2, pl2);
			AppConfig.Set(KeyPl4, pl4);
			AppConfig.Set(KeyActive, 1);
			AppConfig.Flush();
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[pl-custom] 记忆失败：" + ex.Message);
		}
	}

	/// <summary>离开自定义档（切到别的档位时调用）。</summary>
	internal static void ClearActive()
	{
		try
		{
			AppConfig.Set(KeyActive, 0);
			AppConfig.Flush();
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[pl-custom] 清除选中状态失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 软件记忆补写：自定义档仍在选中状态时，把记住的三个值写回 EC。
	/// <para>EC 里的值已经和记住的一致就不写；返回 true 仅表示这次真的写了。</para>
	/// </summary>
	internal static bool TryReapply(out string detail)
	{
		detail = string.Empty;
		if (!IsActive)
		{
			return false;
		}
		if (_reapplyFailures >= MaxReapplyFailures)
		{
			return false;
		}
		if (Environment.TickCount64 - _reapplyAt < ReapplyIntervalMs)
		{
			return false;
		}
		int want1 = AppConfig.Get(KeyPl1, 0);
		int want2 = AppConfig.Get(KeyPl2, 0);
		int want4 = AppConfig.Get(KeyPl4, 0);
		if (want1 <= 0 || want2 <= 0 || want4 <= 0)
		{
			return false;
		}
		_reapplyAt = Environment.TickCount64;
		if (!EcPlCustom.TryRead(out int now1, out int now2, out int now4))
		{
			return false;
		}
		if (now1 == want1 && now2 == want2 && now4 == want4)
		{
			_reapplyFailures = 0;
			return false;
		}
		if (!EcPlCustom.TrySet(want1, want2, want4, out detail))
		{
			_reapplyFailures++;
			Logger.WriteLine("[pl-custom] 记忆补写失败（第 " + _reapplyFailures + " 次）：" + detail);
			return false;
		}
		_reapplyFailures = 0;
		Logger.WriteLine("[pl-custom] 记忆补写：" + detail);
		return true;
	}

	private static int Clamp(int value, int min, int max)
	{
		if (value < min)
		{
			return min;
		}
		if (value > max)
		{
			return max;
		}
		return value;
	}
}
