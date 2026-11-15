using System;
using System.Threading.Tasks;
using MechrevoLite;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 水冷档位的"记住 + 开机重放"：固件不保存上次下发的档位，
/// 开机后必须再收到一次才会工作，这里把用户最后选的档位记下来并在启动时补发。
/// </summary>
internal static class WaterCoolerRestore
{
	internal const string PumpKey = "water_pump_profile";

	internal const string FanKey = "water_fan_profile";

	private static bool _ran;

	/// <summary>保存用户最后成功下发的档位（-1 = 自动）。</summary>
	internal static void Save(int pump, int fan)
	{
		try
		{
			if (pump >= -1)
			{
				AppConfig.Set(PumpKey, pump);
			}
			if (fan >= -1)
			{
				AppConfig.Set(FanKey, fan);
			}
			AppConfig.Flush();
			Logger.WriteLine("[water-cooler] saved: pump=" + pump + " fan=" + fan);
		}
		catch (Exception ex)
		{
			Logger.WriteError("[water-cooler] 保存档位失败：" + ex.Message);
		}
	}

	internal static bool HasSaved()
	{
		try
		{
			return AppConfig.Exists(PumpKey) || AppConfig.Exists(FanKey);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>开机补发一次：依次 pump → fan，每步单独判成败，失败只记日志。</summary>
	internal static async Task RunOnce(WaterCoolerRoutingService? routing)
	{
		if (_ran || routing == null || !HasSaved())
		{
			return;
		}
		if (!routing.IsGcuRoute && !routing.IsBleRoute)
		{
			Logger.WriteLine("[water-cooler] 启动重放跳过：水冷路由不可用");
			return;
		}
		_ran = true;
		int pump = ReadSaved(PumpKey);
		int fan = ReadSaved(FanKey);
		bool pumpOk = pump < -1 || await SafeRun(() => routing.SetPumpAsync(pump)).ConfigureAwait(false);
		bool fanOk = fan < -1 || await SafeRun(() => routing.SetFanAsync(fan)).ConfigureAwait(false);
		Logger.WriteLine("[water-cooler] startup restore: pump=" + pump + " ok=" + pumpOk
			+ " · fan=" + fan + " ok=" + fanOk);
	}

	private static int ReadSaved(string key)
	{
		try
		{
			return AppConfig.Exists(key) ? AppConfig.Get(key, -1) : -1;
		}
		catch
		{
			return -1;
		}
	}

	private static async Task<bool> SafeRun(Func<Task<bool>> action)
	{
		try
		{
			return await action().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Logger.WriteError("[water-cooler] 启动重放失败：" + ex.Message);
			return false;
		}
	}
}
