// 风扇曲线恢复默认（后端）：按后端种类还原风扇表/隔离位/固件默认曲线。
using System;
using System.Linq;
using System.Threading.Tasks;
using MechrevoLite.Mode;

using MechrevoLite.Hardware.FanCurve;

namespace MechrevoLite.Hardware
{
	/// <summary>恢复成功时对应的结果种类（界面据此选文案，不再自己判后端）。</summary>
	internal enum FanCurveRestoreKind
	{
		None,
		BaolongdaFirmware,
		NucTable,
		EcTable,
		GcuBuiltin
	}

	internal sealed record FanCurveRestoreResult(
		bool Handled,
		bool Success,
		bool ClearSaved,
		bool ReloadFromHardware,
		FanCurveRestoreKind Kind,
		string Detail,
		byte[]? CpuTemps,
		byte[]? CpuDuties,
		byte[]? GpuTemps,
		byte[]? GpuDuties);

	internal static class FanCurveRestorer
	{
		internal static async Task<FanCurveRestoreResult> RestoreAsync(FanCurveBackendKind kind, MechrevoHw? hw)
		{
			if (hw == null)
			{
				return NotHandled();
			}
			try
			{
				switch (kind)
				{
					case FanCurveBackendKind.Baolongda:
						// 交回固件是否逐项确认由后端给结论：没确认就不清存档、也不报“已恢复默认”。
						bool firmwareDefault = PerfModeService.Instance?.ResetBaolongdaFanToFirmwareDefault() ?? false;
						return new FanCurveRestoreResult(true, firmwareDefault, firmwareDefault, true,
							firmwareDefault ? FanCurveRestoreKind.BaolongdaFirmware : FanCurveRestoreKind.None,
							firmwareDefault ? "" : "交回固件未全部确认（详见 [reset] 日志）", null, null, null, null);
					case FanCurveBackendKind.NucUmi:
						return RestoreEcTable(FanCurveRestoreKind.NucTable, clearSaved: false);
					case FanCurveBackendKind.Ec:
						return RestoreEcTable(FanCurveRestoreKind.EcTable, clearSaved: true);
					case FanCurveBackendKind.Wujie:
						if (GcuEcExperimental.LocalFanCurve)
						{
							return RestoreEcTable(FanCurveRestoreKind.EcTable, clearSaved: true);
						}
						return hw.IsGcuConnected ? await RestoreGcuAsync(hw).ConfigureAwait(false) : NotHandled();
					case FanCurveBackendKind.Gcu:
						return await RestoreGcuAsync(hw).ConfigureAwait(false);
					default:
						return NotHandled();
				}
			}
			catch (Exception ex)
			{
				Logger.WriteLine("[fan] 恢复默认曲线失败：" + ex.GetType().Name + " " + ex.Message);
				return new FanCurveRestoreResult(true, false, false, false, FanCurveRestoreKind.None,
					ex.GetType().Name + " " + ex.Message, null, null, null, null);
			}
		}

		private static FanCurveRestoreResult RestoreEcTable(FanCurveRestoreKind kind, bool clearSaved)
		{
			bool restored = NucFanTable.TryRestoreDefault(out string detail);
			// 还原风扇表后隔离位必须一起清掉（交回固件），否则表是默认值但风扇还在被本程序接管。
			bool isolatedRestore = NucFanTable.TrySetFanIsolated(false, out string isolatedDetail);
			restored &= isolatedRestore;
			detail += " " + isolatedDetail;
			return new FanCurveRestoreResult(true, restored, clearSaved, true, kind, detail, null, null, null, null);
		}

		private static async Task<FanCurveRestoreResult> RestoreGcuAsync(MechrevoHw hw)
		{
			if (!hw.TryGetBuiltInDefaultFanCurve(out byte[] cpuTemps, out byte[] cpuDuties,
					out byte[] gpuTemps, out byte[] gpuDuties))
			{
				return new FanCurveRestoreResult(true, false, false, false, FanCurveRestoreKind.None,
					"", null, null, null, null);
			}
			// 温度轴用默认曲线自己的点位，不在恢复阶段补点；补齐到 16 槽由下发层负责。
			int cpuAxisBefore = CountAxisPoints(hw.CpuCurveUpT);
			int gpuAxisBefore = CountAxisPoints(hw.GpuCurveUpT);
			await hw.SetFanCurve(0, cpuDuties.Select(v => (int)v).ToArray(), cpuTemps).ConfigureAwait(false);
			await hw.SetFanCurve(1, gpuDuties.Select(v => (int)v).ToArray(), gpuTemps).ConfigureAwait(false);
			Logger.WriteLine("[fan] 恢复默认曲线：CPU 轴 " + cpuAxisBefore + " 点 → 默认 " + CountAxisPoints(cpuTemps)
				+ " 点 · GPU 轴 " + gpuAxisBefore + " 点 → 默认 " + CountAxisPoints(gpuTemps) + " 点");
			return new FanCurveRestoreResult(true, true, true, false, FanCurveRestoreKind.GcuBuiltin, "",
				cpuTemps, cpuDuties, gpuTemps, gpuDuties);
		}

		/// <summary>数出温度轴的有效点数（遇到 0 或 0xFF 结束）。</summary>
		private static int CountAxisPoints(byte[] axis)
		{
			if (axis == null)
			{
				return 0;
			}
			int count = 0;
			while (count < axis.Length && axis[count] != 0 && axis[count] != byte.MaxValue)
			{
				count++;
			}
			return count;
		}


		private static FanCurveRestoreResult NotHandled()
			=> new(false, false, false, false, FanCurveRestoreKind.None, "", null, null, null, null);
	}
}
