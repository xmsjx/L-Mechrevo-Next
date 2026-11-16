using System;
using System.Threading;
using System.Threading.Tasks;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace MechrevoLite;

/// <summary>
/// EC 控制模式下的自定义风扇曲线开机恢复。
/// <para>EC 的 16 段风扇表放在 HDRAM 里，重启后回到固件默认，因此需要在开机后按上次保存的点位重写一次。</para>
/// <para>触发条件：走同方 EC 通道（非无界/非宝龙达/非 NUC）、当前档位是自定义档、该档曲线开关未关闭、
/// 且该档保存过占空比点位；温度轴优先用该档保存的温度轴，没有则读 EC 当前表。</para>
/// </summary>
internal static class EcFanCurveAutoApply
{
	private static int _scheduled;

	internal static void Schedule()
	{
		if (!GcuEcMode.IsEcMode)
		{
			return;
		}
		if (Program.hw?.WujieSmuApu == true || Program.hw?.UsesBaolongda == true || Program.hw?.IsNucUmiFamily == true)
		{
			// 这三类各有自己的风扇曲线通道，不走本路径。
			return;
		}
		if (Interlocked.Exchange(ref _scheduled, 1) != 0)
		{
			return;
		}
		_ = Task.Run(async delegate
		{
			try
			{
				// 等档位回读/模式重放稳定（与无界那条一致）。
				await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(continueOnCapturedContext: false);
				ApplyIfNeeded();
			}
			catch (Exception ex)
			{
				Logger.WriteLine("[fan] EC 开机自动曲线失败（已吞掉）：" + ex.GetType().Name + " " + ex.Message);
			}
		});
	}

	private static void ApplyIfNeeded()
	{
		PerfModeService? perf = PerfModeService.Instance;
		PerfModeDefinition? active = perf?.Active;
		if (active == null)
		{
			Logger.WriteLine("[fan] EC 开机自动曲线：当前没有活动档位，跳过");
			return;
		}
		if (!active.IsCustom)
		{
			Logger.WriteLine("[fan] EC 开机自动曲线：当前档位=" + active.Id + "（非自定义），跳过");
			return;
		}
		PerfModeSettings settings = PerfModeStore.LoadSettings(active.Id);
		if (settings.FanCurveOn == false)
		{
			Logger.WriteLine("[fan] EC 开机自动曲线：档位 " + active.Id + " 的曲线下发开关=关，跳过");
			return;
		}
		if (settings.CpuFanDuty == null || settings.CpuFanDuty.Length < 2)
		{
			Logger.WriteLine("[fan] EC 开机自动曲线：档位 " + active.Id + " 没有保存过曲线点位，跳过");
			return;
		}
		int[] cpuDuty = settings.CpuFanDuty;
		int[] gpuDuty = settings.GpuFanDuty ?? settings.CpuFanDuty;
		string key = string.IsNullOrWhiteSpace(active.Id) ? "active" : active.Id;
		byte[]? cpuTemps = ParseTemps(AppConfig.GetString("ec_fan_cputemps_" + key));
		byte[]? gpuTemps = ParseTemps(AppConfig.GetString("ec_fan_gputemps_" + key));
		if (cpuTemps == null || gpuTemps == null)
		{
			// 没存过温度轴：用 EC 当前表的温度轴（重启后是固件默认轴），只在拿到时才继续。
			if (NucFanTable.TryReadCurrent(out byte[] curCpuTemp, out _, out byte[] curGpuTemp, out _))
			{
				cpuTemps ??= curCpuTemp;
				gpuTemps ??= curGpuTemp;
			}
		}
		if (cpuTemps == null || gpuTemps == null)
		{
			Logger.WriteLine("[fan] EC 开机自动曲线：拿不到温度轴（既未保存也读不到当前表），跳过");
			return;
		}
		bool ok = NucFanTable.TryApply(cpuTemps, cpuDuty, gpuTemps, gpuDuty, out string detail);
		bool isolated = NucFanTable.TrySetFanIsolated(true, out string isoDetail);
		bool ramp = NucFanTable.TrySetFanRamp(settings.FanSwitchSpeedOn == true,
			settings.FanSwitchSpeedMs ?? 300, out string rampDetail);
		Logger.WriteLine("[fan] EC 开机自动曲线（档位=" + active.Id + "）⇒ "
			+ (ok && isolated && ramp ? "已应用 · " : "未完全应用 · ")
			+ detail + " " + isoDetail + " " + rampDetail);
	}

	/// <summary>解析保存的温度轴（逗号分隔，最多 16 点，值 0..120）；无效返回 null。</summary>
	private static byte[]? ParseTemps(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		string[] parts = text.Split(',');
		if (parts.Length < 2)
		{
			return null;
		}
		byte[] temps = new byte[Math.Min(16, parts.Length)];
		for (int i = 0; i < temps.Length; i++)
		{
			if (!byte.TryParse(parts[i].Trim(), out temps[i]) || temps[i] > 120)
			{
				return null;
			}
		}
		return temps;
	}
}
