// 宝龙达 · 用户风扇曲线的保存/应用入口（后端）。
// 闸门、曲线组装、落盘、闭环记录都在这里；界面只下发意图（哪一档 / 哪些点位 / 是否随档位下发）并展示结果，
// ⛔ 界面不再自己判强冷、不自己组装曲线、不直接写存档。
using System;
using System.Linq;
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware
{
	/// <summary>宝龙达风扇曲线：保存点位并交给目标档（强冷进行中一律拒绝）。</summary>
	internal static class BaolongdaFanCurveApply
	{
		internal static FanCurveApplyOutcome SaveAndApply(MechrevoHw? hw, string modeId, int[] cpu, int[] gpu,
			int[] cpuTemps, int[] gpuTemps, bool push, bool loopOn, bool syncOn)
		{
			if (hw == null)
			{
				return new FanCurveApplyOutcome(FanCurveApplyStatus.SaveFailed, "硬件不可用");
			}
			// 强冷与曲线只能一个生效：强冷进行中不写曲线（界面保留编辑，等用户先关强冷）。
			if (MechrevoHw.FanBoostActive)
			{
				return new FanCurveApplyOutcome(FanCurveApplyStatus.RefusedBoostActive, "强冷进行中", PreserveDirty: true);
			}
			int[] cpuPoints = cpu.Take(BaolongdaFanCurveSettings.CpuPointCount).ToArray();
			int[] gpuPoints = gpu.Take(BaolongdaFanCurveSettings.GpuPointCount).ToArray();
			int maxRpm = Math.Max(1, hw.FanRpmMaxForPercent);
			bool previousEnabled = hw.LoadBaolongdaFanCurve(modeId).Enabled;
			BaolongdaFanCurveSettings curve = new()
			{
				// 关闭“随档位下发”时只保存点位，不破坏已有闭环启用状态。
				Enabled = push ? loopOn : previousEnabled,
				FormatVersion = BaolongdaFanCurveSettings.CurrentFormatVersion,
				SyncEnabled = syncOn,
				HysteresisC = FanCurveEngine.DefaultHysteresisC,
				IntervalMs = BaolongdaFanCurveSettings.DefaultIntervalMs,
				MinRpm = BaolongdaFanCurveSettings.DefaultMinRpm,
				MaxRpm = maxRpm,
				Channel1Temperatures = NormalizeAxis(cpuTemps, BaolongdaFanCurveSettings.DefaultCpuTemperatures()),
				Channel1Rpms = PercentToRpms(cpuPoints, maxRpm, BaolongdaFanCurveSettings.CpuPointCount),
				Channel0Temperatures = NormalizeAxis(gpuTemps, BaolongdaFanCurveSettings.DefaultGpuTemperatures()),
				Channel0Rpms = PercentToRpms(gpuPoints, maxRpm, BaolongdaFanCurveSettings.GpuPointCount)
			};
			bool saved = hw.SaveBaolongdaFanCurve(modeId, curve);
			if (!saved)
			{
				return new FanCurveApplyOutcome(FanCurveApplyStatus.SaveFailed, "存档写入失败");
			}
			PerfModeService.Instance?.RecordFanCurves(modeId, cpuPoints, gpuPoints);
			PerfModeStore.SaveSettings(modeId,
				PerfModeStore.LoadSettings(modeId) with { FanCurveOn = push });
			string? activeModeId = PerfModeService.Instance?.ActiveModeId;
			bool activeMode = string.IsNullOrWhiteSpace(activeModeId)
				|| string.Equals(activeModeId, modeId, StringComparison.Ordinal);
			FanCurveApplyStatus status = !push
				? FanCurveApplyStatus.SavedNotPushed
				: (activeMode ? FanCurveApplyStatus.Applied : FanCurveApplyStatus.SavedInactiveProfile);
			return new FanCurveApplyOutcome(status, "ok");
		}

		/// <summary>温度轴按目标点数截取；缺失或非法项回退到默认轴。</summary>
		private static int[] NormalizeAxis(int[] temps, int[] fallback)
		{
			int[] axis = new int[fallback.Length];
			for (int i = 0; i < axis.Length; i++)
			{
				int value = (temps != null && i < temps.Length) ? temps[i] : 0;
				axis[i] = (value > 0 && value <= 120) ? value : fallback[i];
			}
			return axis;
		}

		/// <summary>占空比转转速；点数不足时沿用最后一点，保证与温度轴等长。</summary>
		private static int[] PercentToRpms(int[] duties, int maxRpm, int count)
		{
			int[] rpms = new int[count];
			for (int i = 0; i < count; i++)
			{
				int duty = (i < duties.Length) ? duties[i] : (duties.Length > 0 ? duties[duties.Length - 1] : 0);
				rpms[i] = (int)Math.Round(Math.Clamp(duty, 0, 100) * maxRpm / 100.0);
			}
			return rpms;
		}
	}
}
