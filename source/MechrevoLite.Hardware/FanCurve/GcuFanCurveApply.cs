// 同方 GCU 路由：风扇曲线的保存/应用（后端）。曲线下发、回读确认、档位记录都在这里。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware
{
	internal static class GcuFanCurveApply
	{
		internal static async Task<FanCurveApplyOutcome> SaveAsync(MechrevoHw? hw, string modeId,
			int[] cpu, int[] gpu, bool push, byte[]? cpuTemps = null, byte[]? gpuTemps = null)
		{
			if (hw == null)
			{
				return new FanCurveApplyOutcome(FanCurveApplyStatus.SaveFailed, "硬件不可用");
			}
			if (!push)
			{
				PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
				PerfModeService.Instance?.RecordFanCurveEnabled(modeId, false);
				return new FanCurveApplyOutcome(FanCurveApplyStatus.SavedNotPushed);
			}
			PerfModeService.Instance?.RecordFanCurveEnabled(modeId, true);
			// 固件按非递减执行：曲线里后一点低于前一点时，实际值会被抬成前一点的值。
			// 下发前按同一口径规整，下发的才是真正生效的那条，回读也才对得上。
			int[] cpuApply = NormalizeMonotonic(cpu);
			int[] gpuApply = NormalizeMonotonic(gpu);
			if (!SameDuties(cpuApply, cpu) || !SameDuties(gpuApply, gpu))
			{
				Logger.WriteLine("[fan] 曲线下发前按固件口径规整为非递减：CPU=" + string.Join(",", cpuApply)
					+ " GPU=" + string.Join(",", gpuApply));
			}
			await hw.SetFanCurve(0, cpuApply, cpuTemps).ConfigureAwait(false);
			await hw.SetFanCurve(1, gpuApply, gpuTemps).ConfigureAwait(false);
			await hw.Publish("Fan/Control", new Dictionary<string, object>
			{
				["Action"] = "GET_FAN_SPEED_CURVE_SETTING"
			}).ConfigureAwait(false);
			await Task.Delay(700).ConfigureAwait(false);
			// 温度轴尝试：报文里的温度字段是否被接受，只能靠回读判断。
			if (cpuTemps != null)
			{
				Logger.WriteLine("[fan] GCU 温度轴下发=" + string.Join(",", cpuTemps)
					+ " 回读=" + string.Join(",", hw.CpuCurveUpT)
					+ (TempsMatch(cpuTemps, hw.CpuCurveUpT) ? "（已接受）" : "（未接受）"));
			}
			bool cpuOk = CurveMatches(cpuApply, hw.CpuCurveDuty, hw.CpuCurveUpT, out string cpuDiff);
			bool gpuOk = CurveMatches(gpuApply, hw.GpuCurveDuty, hw.GpuCurveUpT, out string gpuDiff);
			if (!cpuOk || !gpuOk)
			{
				string diff = cpuOk ? gpuDiff : cpuDiff;
				// 回读对不上时把两侧点位都记下来：只报“未确认”没法定位是哪个点、是口径还是真没写进去。
				Logger.WriteError("[fan] 曲线回读不一致：" + diff
					+ " · 下发 CPU=" + string.Join(",", cpu) + " GPU=" + string.Join(",", gpu)
					+ " · 回读 CPU=" + string.Join(",", hw.CpuCurveDuty) + " GPU=" + string.Join(",", hw.GpuCurveDuty)
					+ " · 回读温度 CPU=" + string.Join(",", hw.CpuCurveUpT) + " GPU=" + string.Join(",", hw.GpuCurveUpT));
				// 下发后回读对不上：不落档，界面保留用户编辑。
				return new FanCurveApplyOutcome(FanCurveApplyStatus.SaveFailed, "曲线回读未确认（" + diff + "）", PreserveDirty: true);
			}
			if (modeId.StartsWith("fw", StringComparison.Ordinal)
				|| modeId.StartsWith("custom", StringComparison.Ordinal))
			{
				PerfModeSettings settings = PerfModeStore.LoadSettings(modeId) with
				{
					CpuFanDuty = cpuApply,
					GpuFanDuty = gpuApply,
					FanCurveOn = true
				};
				PerfModeStore.SaveSettings(modeId, settings);
			}
			else
			{
				PerfModeService.Instance?.RecordFanCurves(modeId, cpuApply, gpuApply);
				PerfModeService.Instance?.RecordFanCurveEnabled(modeId, true);
			}
			return new FanCurveApplyOutcome(FanCurveApplyStatus.Applied);
		}

		/// <summary>固件口径：占空比沿点位非递减，下降点会被抬到前一点的值。</summary>
		private static int[] NormalizeMonotonic(int[] duties)
		{
			int[] result = (int[])duties.Clone();
			for (int i = 1; i < result.Length; i++)
			{
				if (result[i] < result[i - 1])
				{
					result[i] = result[i - 1];
				}
			}
			return result;
		}

		private static bool SameDuties(int[] a, int[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}
			return true;
		}
		private static bool TempsMatch(byte[] requested, byte[] actual)
		{
			if (actual == null)
			{
				return false;
			}
			int n = Math.Min(requested.Length, actual.Length);
			for (int i = 0; i < n; i++)
			{
				if (requested[i] == byte.MaxValue || requested[i] == 0)
				{
					continue;
				}
				if (requested[i] != actual[i])
				{
					return false;
				}
			}
			return true;
		}

		private static bool CurveMatches(int[] request, byte[] actual, byte[] temps, out string diff)
		{
			diff = "";
			// 有效点数：表里 255 是“禁用”，0 也不是有效温度点（第 1 槽除外）⇒ 两者都当填充，不参与比对；
			// 否则回读的填充位会被当成有效点，曲线写进去了也永远判“未确认”。
			int valid = Math.Min(request.Length, Math.Min(actual.Length, temps.Length));
			for (int i = 1; i < valid; i++)
			{
				if (temps[i] == 255 || temps[i] == 0)
				{
					valid = i;
					break;
				}
			}
			if (valid < 2)
			{
				valid = Math.Min(2, request.Length);
			}
			for (int i = 0; i < valid; i++)
			{
				int got = Math.Clamp((int)actual[i], 0, 100);
				if (actual[i] != 255 && got != request[i])
				{
					diff = "第 " + (i + 1) + " 点 期望 " + request[i] + "% 实际 " + actual[i] + "%（UpT=" + temps[i] + "℃，共 " + valid + " 个有效点）";
					return false;
				}
			}
			return true;
		}
	}
}
