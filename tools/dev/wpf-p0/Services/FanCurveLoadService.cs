using System;
using System.Linq;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Hardware.FanCurve;
using MechrevoLite.Mode;

namespace L_Mechrevo.WpfP0.Services;

internal sealed record FanCurveLoadOutcome(
	byte[] CpuTemps,
	byte[] CpuDuties,
	byte[] GpuTemps,
	byte[] GpuDuties,
	bool? BaolongdaLoop,
	bool? BaolongdaSync,
	bool PersistIfMissing,
	int[]? CpuSaved,
	int[]? GpuSaved);

/// <summary>风扇曲线读取适配器；窗口只负责把结果加载到控件。</summary>
internal static class FanCurveLoadService
{
	internal static FanCurveLoadOutcome Load(FanCurveBackendKind kind, MechrevoHw? hw, string modeId)
	{
		if (hw == null)
		{
			return Defaults();
		}
		if (kind == FanCurveBackendKind.Baolongda)
		{
			BaolongdaFanCurveSettings curve = hw.LoadBaolongdaFanCurve(modeId);
			PerfModeSettings savedCurve = PerfModeStore.LoadSettings(modeId);
			if (!BaolongdaFanCurveStore.ExistsForMode(modeId)
				&& savedCurve.CpuFanDuty is { Length: >= BaolongdaFanCurveSettings.PointCount } legacyCpu
				&& savedCurve.GpuFanDuty is { Length: >= BaolongdaFanCurveSettings.PointCount } legacyGpu)
			{
				curve = BaolongdaFanCurveSettings.FromLegacyDuties(legacyCpu, legacyGpu, curve.MaxRpm, curve.Enabled);
			}
			bool pushCurveOn = savedCurve.FanCurveOn ?? true;
			return new FanCurveLoadOutcome(
				TempsToBytes(curve.Channel1Temperatures),
				RpmsToPercent(curve.Channel1Rpms, curve.MaxRpm),
				TempsToBytes(curve.Channel0Temperatures),
				RpmsToPercent(curve.Channel0Rpms, curve.MaxRpm),
				curve.Enabled && pushCurveOn, curve.SyncEnabled, false, null, null);
		}
		if (hw.WujieSmuApu && FanTableLooksSane(hw.CpuCurveUpT, hw.CpuCurveDuty)
			&& FanTableLooksSane(hw.GpuCurveUpT, hw.GpuCurveDuty))
		{
			return new FanCurveLoadOutcome(FanCurveDefaults.CpuTemperatures, hw.CpuCurveDuty,
				FanCurveDefaults.GpuTemperatures, hw.GpuCurveDuty, null, null, false, null, null);
		}
		if (!hw.IsGcuConnected
			&& NucFanTable.TryReadCurrent(out byte[] ecCpuT, out byte[] ecCpuD, out byte[] ecGpuT, out byte[] ecGpuD)
			&& FanTableLooksSane(ecCpuT, ecCpuD) && FanTableLooksSane(ecGpuT, ecGpuD))
		{
			return new FanCurveLoadOutcome(ecCpuT, ecCpuD, ecGpuT, ecGpuD, null, null, false, null, null);
		}
		PerfModeSettings saved = PerfModeStore.LoadSettings(modeId);
		int[]? cpuSaved = saved.CpuFanDuty is { Length: >= 2 } cpuDuty ? cpuDuty : null;
		int[]? gpuSaved = saved.GpuFanDuty is { Length: >= 2 } gpuDuty ? gpuDuty : null;
		if (cpuSaved != null && gpuSaved != null)
		{
			return new FanCurveLoadOutcome(FanCurveDefaults.CpuTemperatures,
				cpuSaved.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray(),
				FanCurveDefaults.GpuTemperatures,
				gpuSaved.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray(),
				null, null, false, null, null);
		}
		if (modeId.StartsWith("custom", StringComparison.Ordinal)
			|| modeId.StartsWith("fw", StringComparison.Ordinal))
		{
			// 优先用当前固件/EC 实际读取到的 16 点表；读不到才退回默认 30..100°C 轴。
			if (FanTableLooksSane(hw.CpuCurveUpT, hw.CpuCurveDuty)
				&& FanTableLooksSane(hw.GpuCurveUpT, hw.GpuCurveDuty))
			{
				return new FanCurveLoadOutcome(FanCurveDefaults.CpuTemperatures, hw.CpuCurveDuty,
					FanCurveDefaults.GpuTemperatures, hw.GpuCurveDuty, null, null, false, null, null);
			}
			return new FanCurveLoadOutcome(FanCurveDefaults.CpuTemperatures, FanCurveDefaults.CpuDutyBytes,
				FanCurveDefaults.GpuTemperatures, FanCurveDefaults.GpuDutyBytes,
				null, null, hw.IsGcuConnected, cpuSaved, gpuSaved);
		}
		if (FanTableLooksSane(hw.CpuCurveUpT, hw.CpuCurveDuty)
			&& FanTableLooksSane(hw.GpuCurveUpT, hw.GpuCurveDuty))
		{
			return new FanCurveLoadOutcome(FanCurveDefaults.CpuTemperatures, hw.CpuCurveDuty,
				FanCurveDefaults.GpuTemperatures, hw.GpuCurveDuty, null, null, false, null, null);
		}
		if (hw.TryGetBuiltInDefaultFanCurve(out byte[] cpuTemps, out byte[] cpuDuties,
				out byte[] gpuTemps, out byte[] gpuDuties))
		{
			return new FanCurveLoadOutcome(cpuTemps, cpuDuties, gpuTemps, gpuDuties, null, null, false, null, null);
		}
		return Defaults();
	}

	private static FanCurveLoadOutcome Defaults()
	{
		return new FanCurveLoadOutcome(FanCurveDefaults.CpuTemperatures, FanCurveDefaults.CpuDutyBytes,
			FanCurveDefaults.GpuTemperatures, FanCurveDefaults.GpuDutyBytes, null, null, false, null, null);
	}

	private static bool FanTableLooksSane(byte[] temps, byte[] duties)
	{
		if (temps == null || duties == null || temps.Length < 16 || duties.Length < 16)
		{
			return false;
		}
		bool ended = false;
		int previousTemp = -1;
		int previousDuty = -1;
		bool hasActive = false;
		for (int i = 0; i < 16; i++)
		{
			int temp = temps[i];
			if (temp == 255)
			{
				ended = true;
				continue;
			}
			if (ended || temp < 0 || temp > 120 || temp < previousTemp)
			{
				return false;
			}
			previousTemp = temp;
			int duty = duties[i];
			if (duty < 0 || duty > 100 || duty < previousDuty)
			{
				return false;
			}
			previousDuty = duty;
			if (i > 0 && temp > 0 && duty > 0)
			{
				hasActive = true;
			}
		}
		return hasActive;
	}

	private static byte[] TempsToBytes(int[] temps)
	{
		byte[] bytes = new byte[16];
		for (int i = 0; i < bytes.Length && i < temps.Length; i++)
		{
			bytes[i] = (byte)Math.Clamp(temps[i], 0, 255);
		}
		return bytes;
	}

	private static byte[] RpmsToPercent(int[] rpms, int maxRpm)
	{
		byte[] duties = new byte[16];
		int max = Math.Max(1, maxRpm);
		for (int i = 0; i < duties.Length && i < rpms.Length; i++)
		{
			duties[i] = (byte)Math.Clamp((int)Math.Round(rpms[i] * 100.0 / max), 0, 100);
		}
		return duties;
	}
}
