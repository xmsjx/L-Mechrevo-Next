// 风扇曲线读取（后端）：按后端种类给出窗口要显示的曲线点位与温度轴。
using System;
using System.Linq;
using MechrevoLite.Mode;

using MechrevoLite.Hardware.FanCurve;

namespace MechrevoLite.Hardware
{
	internal sealed record FanCurveLoadResult(
		byte[] CpuTemps,
		byte[] CpuDuties,
		byte[] GpuTemps,
		byte[] GpuDuties,
		bool? BaolongdaLoop,
		bool? BaolongdaSync,
		bool PersistIfMissing,
		int[]? CpuSaved,
		int[]? GpuSaved);

	internal static class FanCurveReader
	{
		internal static FanCurveLoadResult Read(FanCurveBackendKind kind, MechrevoHw? hw, string modeId)
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
					&& savedCurve.CpuFanDuty is { Length: >= BaolongdaFanCurveSettings.Legacy10PointCount } legacyCpu
					&& savedCurve.GpuFanDuty is { Length: >= BaolongdaFanCurveSettings.Legacy10PointCount } legacyGpu)
				{
					curve = BaolongdaFanCurveSettings.FromLegacyDuties(legacyCpu, legacyGpu, curve.MaxRpm, curve.Enabled);
				}
				bool pushCurveOn = savedCurve.FanCurveOn ?? true;
				return new FanCurveLoadResult(
					TempsToBytes(curve.Channel1Temperatures),
					RpmsToPercent(curve.Channel1Rpms, curve.MaxRpm),
					TempsToBytes(curve.Channel0Temperatures),
					RpmsToPercent(curve.Channel0Rpms, curve.MaxRpm),
					curve.Enabled && pushCurveOn, curve.SyncEnabled, false, null, null);
			}
			if (hw.WujieSmuApu && FanTableLooksSane(hw.CpuCurveUpT, hw.CpuCurveDuty)
				&& FanTableLooksSane(hw.GpuCurveUpT, hw.GpuCurveDuty))
			{
				// 无界与同方共用同一套 16 点表：温度轴优先用保存值，没有才用内置默认表。
				byte[] wujieAxis = WujieFanEcController.DefaultTemperatures();
				byte[] wujieGpuAxis = WujieFanEcController.DefaultGpuTemperatures();
				// 该档已保存过曲线 ⇒ 优先显示保存值：EC 回读是按温度算出来的实际值，
				// 用它重绘会让刚拖好的点位看起来被改回去了。
				PerfModeSettings savedWujie = PerfModeStore.LoadSettings(modeId);
				if (savedWujie.CpuFanDuty is { Length: >= 2 } savedCpu && savedWujie.GpuFanDuty is { Length: >= 2 } savedGpu)
				{
					byte[] savedCpuTemps = ReadRememberedTemps("wujie_fan_cputemps_", modeId)
						?? ReadRememberedTemps("ec_fan_cputemps_", modeId) ?? wujieAxis;
					byte[] savedGpuTemps = ReadRememberedTemps("wujie_fan_gputemps_", modeId)
						?? ReadRememberedTemps("ec_fan_gputemps_", modeId) ?? wujieGpuAxis;
					return new FanCurveLoadResult(savedCpuTemps,
						savedCpu.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray(),
						savedGpuTemps,
						savedGpu.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray(),
						null, null, false, null, null);
				}
				// 无界的曲线由本程序保存并下发，EC 里不放我们的曲线，所以没有存档时用默认曲线。
				return new FanCurveLoadResult(wujieAxis, FanCurveDefaults.WujieCpuDutyBytes,
					wujieGpuAxis, FanCurveDefaults.WujieGpuDutyBytes, null, null, false, null, null);
			}
			// EC 直连能读回实际生效的表：窗口按 EC 里的表显示，外部改动也能看到。
			// 读不到或表不合法时才回落到存档。
			if ((kind == FanCurveBackendKind.Ec || kind == FanCurveBackendKind.NucUmi)
				&& !hw.IsGcuConnected
				&& NucFanTable.TryReadCurrent(out byte[] ecT, out byte[] ecD, out byte[] ecGt, out byte[] ecGd)
				&& FanTableLooksSane(ecT, ecD) && FanTableLooksSane(ecGt, ecGd))
			{
				return new FanCurveLoadResult(ecT, ecD, ecGt, ecGd, null, null, false, null, null);
			}
			// 每个档位自己的已保存曲线优先：切配置/点应用后窗口要跟着变（EC 直连没有 GCU 回读，只能靠存档）。
			PerfModeSettings saved = PerfModeStore.LoadSettings(modeId);
			int[]? cpuSaved = saved.CpuFanDuty is { Length: >= 2 } cpuDuty ? cpuDuty : null;
			int[]? gpuSaved = saved.GpuFanDuty is { Length: >= 2 } gpuDuty ? gpuDuty : null;
			if (cpuSaved != null && gpuSaved != null)
			{
				byte[] savedCpuTemps = ReadRememberedTemps("ec_fan_cputemps_", modeId) ?? FanCurveDefaults.CpuTemperatures;
				byte[] savedGpuTemps = ReadRememberedTemps("ec_fan_gputemps_", modeId) ?? FanCurveDefaults.GpuTemperatures;
				return new FanCurveLoadResult(savedCpuTemps,
					cpuSaved.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray(),
					savedGpuTemps,
					gpuSaved.Select(v => (byte)Math.Clamp(v, 0, 100)).ToArray(),
					null, null, false, null, null);
			}
			if (modeId.StartsWith("custom", StringComparison.Ordinal)
				|| modeId.StartsWith("fw", StringComparison.Ordinal))
			{
				if (FanTableLooksSane(hw.CpuCurveUpT, hw.CpuCurveDuty)
					&& FanTableLooksSane(hw.GpuCurveUpT, hw.GpuCurveDuty))
				{
					return new FanCurveLoadResult(FanCurveDefaults.CpuTemperatures, hw.CpuCurveDuty,
						FanCurveDefaults.GpuTemperatures, hw.GpuCurveDuty, null, null, false, null, null);
				}
				return new FanCurveLoadResult(FanCurveDefaults.CpuTemperatures, FanCurveDefaults.CpuDutyBytes,
					FanCurveDefaults.GpuTemperatures, FanCurveDefaults.GpuDutyBytes,
					null, null, hw.IsGcuConnected, cpuSaved, gpuSaved);
			}
			if (FanTableLooksSane(hw.CpuCurveUpT, hw.CpuCurveDuty)
				&& FanTableLooksSane(hw.GpuCurveUpT, hw.GpuCurveDuty))
			{
				return new FanCurveLoadResult(FanCurveDefaults.CpuTemperatures, hw.CpuCurveDuty,
					FanCurveDefaults.GpuTemperatures, hw.GpuCurveDuty, null, null, false, null, null);
			}
			if (hw.TryGetBuiltInDefaultFanCurve(out byte[] cpuTemps, out byte[] cpuDuties,
					out byte[] gpuTemps, out byte[] gpuDuties))
			{
				return new FanCurveLoadResult(cpuTemps, cpuDuties, gpuTemps, gpuDuties, null, null, false, null, null);
			}
			return Defaults();
		}

		/// <summary>读保存风扇曲线时记下的温度轴（CSV）；没有或格式不对返回 null。</summary>
		private static byte[]? ReadRememberedTemps(string prefix, string modeId)
		{
			try
			{
				string key = string.IsNullOrWhiteSpace(modeId) ? "active" : modeId;
				string raw = AppConfig.GetString(prefix + key) ?? string.Empty;
				if (raw.Length == 0)
				{
					return null;
				}
				string[] parts = raw.Split(',');
				if (parts.Length < 16)
				{
					return null;
				}
				byte[] temps = new byte[16];
				for (int i = 0; i < temps.Length; i++)
				{
					if (!byte.TryParse(parts[i].Trim(), out temps[i]))
					{
						return null;
					}
				}
				return temps;
			}
			catch
			{
				return null;
			}
		}

		private static FanCurveLoadResult Defaults()
			=> new(FanCurveDefaults.CpuTemperatures, FanCurveDefaults.CpuDutyBytes,
				FanCurveDefaults.GpuTemperatures, FanCurveDefaults.GpuDutyBytes, null, null, false, null, null);

		internal static bool FanTableLooksSane(byte[]? temps, byte[]? duties)
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
}
