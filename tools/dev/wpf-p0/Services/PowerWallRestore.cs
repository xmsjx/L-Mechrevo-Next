using System;
using MechrevoLite;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 自定义档的功耗墙（PL1 / PL2 / fPPT=PL4）持久化与开机重放。
/// <para>自定义档设置本身随档位保存，但开机回档不会重写功耗墙，等于每次重启回到固件默认值。</para>
/// </summary>
internal static class PowerWallRestore
{
	private const string KeyPl1 = "custom_pl1";
	private const string KeyPl2 = "custom_pl2";
	private const string KeyPl4 = "custom_pl4";

	internal static string LastDetail { get; private set; } = "";

	/// <summary>应用成功后记录一份"最近一次下发的功耗墙"。</summary>
	internal static void Save(int pl1, int pl2, int pl4)
	{
		try
		{
			AppConfig.Set(KeyPl1, pl1);
			AppConfig.Set(KeyPl2, pl2);
			AppConfig.Set(KeyPl4, pl4);
			AppConfig.Flush();
			LastDetail = "saved pl1=" + pl1 + " pl2=" + pl2 + " pl4=" + pl4;
			Logger.WriteLine("[power-wall] saved: " + LastDetail);
		}
		catch (Exception ex)
		{
			Logger.WriteError("[power-wall] save failed: " + ex.GetType().Name + " " + ex.Message);
		}
	}

	internal static bool HasSaved()
	{
		try
		{
			return AppConfig.Exists(KeyPl1) || AppConfig.Exists(KeyPl2) || AppConfig.Exists(KeyPl4);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>开机重放：只在 EC 通道可用时写回；不做任何弹窗，结论写日志。</summary>
	internal static bool RunOnce()
	{
		try
		{
			if (!HasSaved())
			{
				LastDetail = "no saved power walls";
				Logger.WriteLine("[power-wall] startup restore skipped: " + LastDetail);
				return false;
			}
			int pl1 = AppConfig.Get(KeyPl1, 0);
			int pl2 = AppConfig.Get(KeyPl2, 0);
			int pl4 = AppConfig.Get(KeyPl4, 0);
			if (pl1 <= 0 && pl2 <= 0 && pl4 <= 0)
			{
				LastDetail = "saved values are empty";
				Logger.WriteLine("[power-wall] startup restore skipped: " + LastDetail);
				return false;
			}
			int pl4ForEc = pl4;
			if (pl4 > 0 && WpfBackend.EcPl4TryRead(out bool doubleUnit) && doubleUnit)
			{
				pl4ForEc = Math.Max(0, pl4 / 2);
			}
			bool ok = WpfBackend.NucPerformanceModeTryApplyCustom(
				pl1 > 0 ? pl1 : null, pl2 > 0 ? pl2 : null, pl4 > 0 ? pl4ForEc : null, null, out string detail);
			LastDetail = "restore " + (ok ? "ok" : "failed") + ": pl1=" + pl1 + " pl2=" + pl2 + " pl4=" + pl4
				+ " (" + detail + ")";
			if (ok)
			{
				Logger.WriteLine("[power-wall] startup restore: " + LastDetail);
			}
			else
			{
				Logger.WriteError("[power-wall] startup restore failed: " + LastDetail);
			}
			return ok;
		}
		catch (Exception ex)
		{
			LastDetail = "restore exception: " + ex.GetType().Name + " " + ex.Message;
			Logger.WriteError("[power-wall] " + LastDetail);
			return false;
		}
	}
}
