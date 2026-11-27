// 同方 EC 风扇表：风扇曲线的保存/应用（后端）。表写入、隔离位、斜坡、温度轴记忆都在这里。
using System;
using System.Collections.Generic;
using System.Linq;
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware
{
	internal static class EcFanCurveApply
	{
		internal static FanCurveApplyOutcome Save(string modeId, int[] cpu, int[] gpu,
			byte[] cpuTemps, byte[] gpuTemps, bool push)
		{
			if (!push)
			{
				RememberTemps(modeId, cpuTemps, gpuTemps);
				PerfModeStore.SaveSettings(modeId, PerfModeStore.LoadSettings(modeId) with
				{
					CpuFanDuty = cpu,
					GpuFanDuty = gpu,
					FanCurveOn = false
				});
				return new FanCurveApplyOutcome(FanCurveApplyStatus.SavedNotPushed);
			}
			// 五点机型（温度轴由固件固定）：只写五个占空比字节，不碰隔离位与斜坡。
			if (EcFanFivePoint.InUse)
			{
				int[] fiveDuties = EcFanFivePoint.Sample(cpuTemps, cpu);
				bool fiveOk = EcFanFivePoint.TrySet(fiveDuties, out string fiveDetail);
				Logger.WriteError("[gcu-ec] 五点风扇" + (fiveOk ? "已写入：" : "写入失败：") + fiveDetail);
				PerfModeSettings fiveBefore = PerfModeStore.LoadSettings(modeId);
				PerfModeStore.SaveSettings(modeId, fiveBefore with
				{
					CpuFanDuty = cpu,
					GpuFanDuty = gpu,
					FanCurveOn = fiveOk ? true : (fiveBefore.FanCurveOn ?? false)
				});
				RememberTemps(modeId, cpuTemps, gpuTemps);
				return new FanCurveApplyOutcome(fiveOk ? FanCurveApplyStatus.Applied : FanCurveApplyStatus.SaveFailed, fiveDetail);
			}
			bool ok = NucFanTable.TryApply(cpuTemps, cpu, gpuTemps, gpu, "应用", out string detail, NucFanTable.GpuCurveSupported);
			// 曲线没写进去就别再动隔离位/斜坡，避免留下半截状态（隔离位已置、表还是旧的）。
			string isoDetail = "隔离位未改（曲线未写入）";
			string rampDetail = "斜坡未改（曲线未写入）";
			bool isolated = false;
			bool ramp = false;
			if (ok)
			{
				isolated = NucFanTable.TrySetFanIsolated(true, out isoDetail);
				PerfModeSettings savedSettings = PerfModeStore.LoadSettings(modeId);
				ramp = NucFanTable.TrySetFanRamp(savedSettings.FanSwitchSpeedOn == true,
					savedSettings.FanSwitchSpeedMs ?? 300, out rampDetail);
			}
			RememberTemps(modeId, cpuTemps, gpuTemps);
			// 表/隔离位/斜坡没全成就不置「已启用」，否则开机自动接管和窗口状态都按已启用走。
			ok = ok && isolated && ramp;
			PerfModeSettings beforeSettings = PerfModeStore.LoadSettings(modeId);
			PerfModeStore.SaveSettings(modeId, beforeSettings with
			{
				CpuFanDuty = cpu,
				GpuFanDuty = gpu,
				FanCurveOn = ok ? true : beforeSettings.FanCurveOn
			});
			detail += " " + isoDetail + " " + rampDetail;
			Logger.WriteError("[gcu-ec] 自定义风扇应用 ok=" + ok + " " + detail);
			return new FanCurveApplyOutcome(
				ok ? FanCurveApplyStatus.Applied : FanCurveApplyStatus.SaveFailed, detail);
		}

		/// <summary>记住该档的 EC 温度轴（开机恢复自定义曲线时按同一条轴重写）。</summary>
		private static void RememberTemps(string modeId, byte[] cpuTemps, byte[] gpuTemps)
		{
			try
			{
				string key = string.IsNullOrWhiteSpace(modeId) ? "active" : modeId;
				if (cpuTemps != null && cpuTemps.Length > 0)
				{
					AppConfig.Set("ec_fan_cputemps_" + key, string.Join(",", cpuTemps));
				}
				if (gpuTemps != null && gpuTemps.Length > 0)
				{
					AppConfig.Set("ec_fan_gputemps_" + key, string.Join(",", gpuTemps));
				}
				AppConfig.Flush();
				PerfModeSettings settings = PerfModeStore.LoadSettings(key);
				PerfModeStore.SaveSettings(key, settings with
				{
					CpuCurveTemps = cpuTemps != null && cpuTemps.Length > 0 ? cpuTemps : settings.CpuCurveTemps,
					GpuCurveTemps = gpuTemps != null && gpuTemps.Length > 0 ? gpuTemps : settings.GpuCurveTemps
				});
			}
			catch (Exception ex)
			{
				Logger.WriteLine("[fan] 记录 EC 温度轴失败（已吞掉）：" + ex.Message);
			}
		}
	}
}
