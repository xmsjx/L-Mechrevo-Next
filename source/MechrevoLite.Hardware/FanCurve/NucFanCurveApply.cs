// NUC/UMI：风扇曲线写入 EC 表（后端）。表写入、风扇切换斜坡、隔离位一起处理。
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware
{
	internal static class NucFanCurveApply
	{
		internal static FanCurveApplyOutcome Save(string modeId, int[] cpu, int[] gpu,
			byte[] cpuTemps, byte[] gpuTemps, bool push)
		{
			if (!push)
			{
				PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
				PerfModeService.Instance?.RecordFanCurveEnabled(modeId, false);
				return new FanCurveApplyOutcome(FanCurveApplyStatus.SavedNotPushed);
			}
			bool ok = NucFanTable.TryApply(cpuTemps, cpu, gpuTemps, gpu, out string detail);
			// 曲线没写进去就别再动斜坡/隔离位，避免留下半截状态。
			string rampDetail = "斜坡未改（曲线未写入）";
			string isolatedDetail = "隔离位未改（曲线未写入）";
			bool rampOk = false;
			bool isolatedOk = false;
			if (ok)
			{
				rampOk = NucFanTable.TrySetFanRamp(true, 300, out rampDetail);
				isolatedOk = NucFanTable.TrySetFanIsolated(true, out isolatedDetail);
			}
			ok = ok && rampOk && isolatedOk;
			detail += " " + rampDetail + " " + isolatedDetail;
			Logger.WriteLine("[nuc-fan] 自定义风扇应用 ok=" + ok + " detail=" + detail);
			PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
			if (ok)
			{
				PerfModeService.Instance?.RecordFanCurveEnabled(modeId, true);
			}
			return new FanCurveApplyOutcome(
				ok ? FanCurveApplyStatus.Applied : FanCurveApplyStatus.SaveFailed, detail);
		}
	}
}
