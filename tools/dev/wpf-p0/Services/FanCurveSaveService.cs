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
