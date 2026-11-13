using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using L_Mechrevo.WpfP0.Resources;

namespace L_Mechrevo.WpfP0.Services;

internal sealed record FanCurveSaveResult(bool Success, bool Dirty, string Detail);

/// <summary>GCU 风扇曲线保存执行适配器。</summary>
internal static class FanCurveSaveService
{
	internal static async Task<FanCurveSaveResult> SaveGcuAsync(MechrevoHw hw, string modeId,
		int[] cpu, int[] gpu, bool push)
	{
		if (!push)
		{
			PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
			PerfModeService.Instance?.RecordFanCurveEnabled(modeId, false);
			return new FanCurveSaveResult(true, false, WpfStrings.FanCurveWindow_xaml_Text_4);
		}
		PerfModeService.Instance?.RecordFanCurveEnabled(modeId, true);
		await hw.SetFanCurve(0, cpu).ConfigureAwait(true);
		await hw.SetFanCurve(1, gpu).ConfigureAwait(true);
		await hw.Publish("Fan/Control", new Dictionary<string, object>
		{
			["Action"] = "GET_FAN_SPEED_CURVE_SETTING"
		}).ConfigureAwait(true);
		await Task.Delay(700).ConfigureAwait(true);
		bool confirmed = CurveMatches(cpu, hw.CpuCurveDuty, hw.CpuCurveUpT)
			&& CurveMatches(gpu, hw.GpuCurveDuty, hw.GpuCurveUpT);
		if (!confirmed)
		{
			return new FanCurveSaveResult(false, true, WpfStrings.FanCurveWindow_xaml_Text_6);
		}
		if (modeId.StartsWith("fw", StringComparison.Ordinal)
			|| modeId.StartsWith("custom", StringComparison.Ordinal))
		{
			PerfModeSettings settings = PerfModeStore.LoadSettings(modeId) with
			{
				CpuFanDuty = cpu,
				GpuFanDuty = gpu,
				FanCurveOn = true
			};
			PerfModeStore.SaveSettings(modeId, settings);
		}
		else
		{
			PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
			PerfModeService.Instance?.RecordFanCurveEnabled(modeId, true);
		}
		return new FanCurveSaveResult(true, false, WpfStrings.FanCurveWindow_xaml_Text_5);
	}

	internal static FanCurveSaveResult SaveNuc(string modeId, int[] cpu, int[] gpu,
		byte[] cpuTemps, byte[] gpuTemps, bool push)
	{
		if (!push)
		{
			PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
			PerfModeService.Instance?.RecordFanCurveEnabled(modeId, false);
			return new FanCurveSaveResult(true, false, WpfStrings.FanCurveWindow_xaml_Text_4);
		}
		bool ok = NucFanTable.TryApply(cpuTemps, cpu, gpuTemps, gpu, out string detail);
		bool rampOk = NucFanTable.TrySetFanRamp(true, 300, out string rampDetail);
		bool isolatedOk = NucFanTable.TrySetFanIsolated(true, out string isolatedDetail);
		ok = ok && rampOk && isolatedOk;
		detail += " " + rampDetail + " " + isolatedDetail;
		Logger.WriteLine("[nuc-fan] 自定义风扇应用 ok=" + ok + " detail=" + detail);
		PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
		PerfModeService.Instance?.RecordFanCurveEnabled(modeId, true);
		return new FanCurveSaveResult(ok, false,
			ok ? WpfStrings.L("NUC/UMI 风扇曲线已写入：", "NUC/UMI fan curve written: ") + detail
				: WpfStrings.L("NUC/UMI 风扇曲线写入失败：", "Failed to write NUC/UMI fan curve: ") + detail);
	}

	internal static FanCurveSaveResult SaveEc(string modeId, int[] cpu, int[] gpu,
		byte[] cpuTemps, byte[] gpuTemps, bool push)
	{
		if (!push)
		{
			PerfModeStore.SaveSettings(modeId, PerfModeStore.LoadSettings(modeId) with
			{
				CpuFanDuty = cpu,
				GpuFanDuty = gpu,
				FanCurveOn = false
			});
			return new FanCurveSaveResult(true, false, WpfStrings.FanCurveWindow_xaml_Text_4);
		}
		bool ok = NucFanTable.TryApply(cpuTemps, cpu, gpuTemps, gpu, out string detail);
		bool isolated = NucFanTable.TrySetFanIsolated(true, out string isoDetail);
		PerfModeSettings savedSettings = PerfModeStore.LoadSettings(modeId);
		bool ramp = NucFanTable.TrySetFanRamp(savedSettings.FanSwitchSpeedOn == true,
			savedSettings.FanSwitchSpeedMs ?? 300, out string rampDetail);
		PerfModeStore.SaveSettings(modeId, PerfModeStore.LoadSettings(modeId) with
		{
			CpuFanDuty = cpu,
			GpuFanDuty = gpu,
			FanCurveOn = true
		});
		ok = ok && isolated && ramp;
		detail += " " + isoDetail + " " + rampDetail;
		Logger.WriteError("[gcu-ec] 自定义风扇应用 ok=" + ok + " " + detail);
		return new FanCurveSaveResult(ok, false,
			ok ? WpfStrings.L("风扇曲线已写入 EC：", "Fan curve written to EC: ") + detail
				: WpfStrings.L("风扇曲线写入失败：", "Failed to write fan curve: ") + detail);
	}

	internal static FanCurveSaveResult SaveBaolongda(MechrevoHw hw, string modeId, int[] cpu, int[] gpu,
		int[] cpuTemps, int[] gpuTemps, bool push, bool loopOn, bool syncOn)
	{
		if (MechrevoHw.FanBoostActive)
		{
			return new FanCurveSaveResult(false, true,
				WpfStrings.L("强冷进行中，请先关闭强冷再保存曲线",
					"Fan boost is active; turn it off before saving the fan curve"));
		}
		int[] cpuPoints = cpu.Take(BaolongdaFanCurveSettings.PointCount).ToArray();
		int[] gpuPoints = gpu.Take(BaolongdaFanCurveSettings.PointCount).ToArray();
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
			Channel1Temperatures = cpuTemps.Take(BaolongdaFanCurveSettings.PointCount).ToArray(),
			Channel1Rpms = PercentToRpms(cpuPoints, maxRpm),
			Channel0Temperatures = gpuTemps.Take(BaolongdaFanCurveSettings.PointCount).ToArray(),
			Channel0Rpms = PercentToRpms(gpuPoints, maxRpm)
		};
		bool ok = hw.SaveBaolongdaFanCurve(modeId, curve);
		if (ok)
		{
			PerfModeService.Instance?.RecordFanCurves(modeId, cpuPoints, gpuPoints);
			PerfModeStore.SaveSettings(modeId,
				PerfModeStore.LoadSettings(modeId) with { FanCurveOn = push });
		}
		string? activeModeId = PerfModeService.Instance?.ActiveModeId;
		bool activeMode = string.IsNullOrWhiteSpace(activeModeId)
			|| string.Equals(activeModeId, modeId, StringComparison.Ordinal);
		string okText = !push
			? WpfStrings.L("宝龙达风扇曲线已保存（未随档位下发）", "Baolongda fan curve saved (not pushed with profile)")
			: (activeMode
				? WpfStrings.L("宝龙达风扇曲线已保存并应用", "Baolongda fan curve saved and applied")
				: WpfStrings.L("宝龙达风扇曲线已保存，切到该档后应用", "Baolongda fan curve saved; applies when this profile is active"));
		return new FanCurveSaveResult(ok, false,
			ok ? okText : WpfStrings.L("宝龙达风扇曲线保存失败", "Failed to save Baolongda fan curve"));
	}

	private static int[] PercentToRpms(int[] duties, int maxRpm)
	{
		int[] rpms = new int[16];
		for (int i = 0; i < rpms.Length && i < duties.Length; i++)
		{
			rpms[i] = (int)Math.Round(duties[i] * maxRpm / 100.0);
		}
		return rpms;
	}

	private static bool CurveMatches(int[] request, byte[] actual, byte[] temps)
	{
		int valid = Array.IndexOf(temps, (byte)255);
		valid = valid < 2 ? Math.Min(11, Math.Max(2, request.Length)) : valid;
		for (int i = 0; i < valid; i++)
		{
			if (i < actual.Length && actual[i] != 255 && Math.Clamp((int)actual[i], 0, 100) != request[i])
			{
				return false;
			}
		}
		return true;
	}
}
