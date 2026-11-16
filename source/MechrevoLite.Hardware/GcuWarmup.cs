using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// GCU 预热窗口：首次安装本程序、首次装载 GCU、以及从 EC 模式切回 GCU 模式之后的一段时间内，
/// GCU 服务端通常还没起好（端口未监听、能力位/指示灯档位表尚未上报），
/// 这段窗口里「连不上 / 功能拿不到 / 下发失败」属正常现象，不应作为故障上报或据此切走后端。
/// <para>窗口长度 5 分钟，跨进程重启保留（落配置），到点自动失效。</para>
/// </summary>
public static class GcuWarmup
{
	private const string UntilKey = "gcu_warmup_until_utc";
	private const string ReasonKey = "gcu_warmup_reason";
	private const string FirstRunKey = "gcu_warmup_first_run_done";
	private const string GcuSeenKey = "gcu_warmup_gcu_seen";

	private static readonly object Gate = new();
	private static long _untilUtcTicks = -1;
	private static string _reason = "";

	/// <summary>窗口长度：5 分钟。</summary>
	public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

	/// <summary>当前是否处于预热窗口内。</summary>
	public static bool IsActive
	{
		get
		{
			EnsureLoaded();
			long until = System.Threading.Volatile.Read(ref _untilUtcTicks);
			return until > 0 && DateTime.UtcNow.Ticks < until;
		}
	}

	/// <summary>窗口的触发原因（用于日志/诊断），未激活时为空串。</summary>
	public static string Reason
	{
		get
		{
			EnsureLoaded();
			return IsActive ? _reason : "";
		}
	}

	/// <summary>距离窗口结束还剩多少秒（未激活为 0）。</summary>
	public static int RemainingSeconds
	{
		get
		{
			EnsureLoaded();
			long until = System.Threading.Volatile.Read(ref _untilUtcTicks);
			long left = until - DateTime.UtcNow.Ticks;
			return left <= 0 ? 0 : (int)Math.Ceiling(left / (double)TimeSpan.TicksPerSecond);
		}
	}

	/// <summary>开一个预热窗口（从此刻起 5 分钟，已有更晚的窗口则不缩短）。</summary>
	public static void Begin(string reason)
	{
		try
		{
			lock (Gate)
			{
				DateTime until = DateTime.UtcNow.Add(Window);
				EnsureLoadedNoLock();
				if (_untilUtcTicks >= until.Ticks)
				{
					return;
				}
				_untilUtcTicks = until.Ticks;
				_reason = reason ?? "";
				AppConfig.Set(UntilKey, _untilUtcTicks.ToString());
				AppConfig.Set(ReasonKey, _reason);
				AppConfig.Flush();
			}
			Logger.WriteLine("[gcu-warmup] 进入预热窗口：" + reason + " · 剩余 " + RemainingSeconds + " 秒"
				+ "（窗口内不把 GCU 未就绪当故障，也不据此切后端）");
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[gcu-warmup] 设置预热窗口失败（已吞掉）：" + ex.Message);
		}
	}

	/// <summary>首次运行本程序（装好后第一次启动）⇒ 开窗口。</summary>
	public static void BeginIfFirstRun()
	{
		try
		{
			if (AppConfig.Is(FirstRunKey))
			{
				return;
			}
			// 只有这台机器确实装了 GCU 才需要预热窗口；没装的机型（宝龙达/无 GCU）不进入。
			if (!GcuInstalled())
			{
				return;
			}
			AppConfig.Set(FirstRunKey, 1);
			AppConfig.Flush();
			Begin("首次安装本程序");
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[gcu-warmup] 首次运行标记失败（已吞掉）：" + ex.Message);
		}
	}

	/// <summary>首次检测到 GCU 已装载（装好载荷后第一次启动）⇒ 开窗口。</summary>
	public static void BeginIfFirstGcuInstall()
	{
		try
		{
			if (AppConfig.Is(GcuSeenKey))
			{
				return;
			}
			string variant = "";
			try
			{
				variant = GcuIdentity.Variant;
			}
			catch
			{
			}
			bool installed = !string.IsNullOrWhiteSpace(variant);
			if (!installed)
			{
				// 还没有 GCU：不打标记，等它真的装上再判定。
				return;
			}
			AppConfig.Set(GcuSeenKey, 1);
			AppConfig.Flush();
			Begin("首次装载 GCU");
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[gcu-warmup] 首次装载 GCU 标记失败（已吞掉）：" + ex.Message);
		}
	}

	/// <summary>从 EC 模式切回 GCU 模式 ⇒ 开窗口（GCU 服务刚被拉起，端口/能力都要等）。</summary>
	public static void BeginAfterEcSwitchBack()
	{
		Begin("从 EC 模式切回 GCU");
	}

	/// <summary>清掉窗口（测试/诊断用）</summary>
	internal static void ClearForTest()
	{
		lock (Gate)
		{
			_untilUtcTicks = 0;
			_reason = "";
		}
	}

	private static bool GcuInstalled()
	{
		try
		{
			return !string.IsNullOrWhiteSpace(GcuIdentity.Variant);
		}
		catch
		{
			return false;
		}
	}

	private static void EnsureLoaded()
	{
		if (System.Threading.Volatile.Read(ref _untilUtcTicks) >= 0)
		{
			return;
		}
		lock (Gate)
		{
			EnsureLoadedNoLock();
		}
	}

	private static void EnsureLoadedNoLock()
	{
		if (_untilUtcTicks >= 0)
		{
			return;
		}
		long until = 0;
		string reason = "";
		try
		{
			string text = AppConfig.GetString(UntilKey) ?? "";
			if (long.TryParse(text, out long parsed))
			{
				until = parsed;
			}
			reason = AppConfig.GetString(ReasonKey) ?? "";
		}
		catch
		{
		}
		_reason = reason;
		_untilUtcTicks = until;
	}
}
