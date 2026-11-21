// 无界：风扇曲线的保存/应用（后端）。曲线启动、独立双路开关、温度轴记忆都在这里。
using System;
using System.Collections.Generic;
using System.Linq;
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware
{
	internal static class WujieFanCurveApply
	{
		private static WujieFanEcController? _controller;
		private static readonly HashSet<object> Owners = new();

		/// <summary>本程序持有的无界曲线写手（懒创建；最后一个使用者退出时才停）。</summary>
		private static WujieFanEcController Controller => _controller ??= new WujieFanEcController();

		/// <summary>某个使用者退出接管：只有没有别的使用者时才真正停止写手。</summary>
		internal static void Stop(object owner, string reason)
		{
			Owners.Remove(owner);
			if (Owners.Count > 0 || _controller == null)
			{
				return;
			}
			WujieFanEcController controller = _controller;
			_controller = null;
			controller.Stop(reason);
		}

		internal static FanCurveApplyOutcome Save(object owner, string modeId, int[] cpu, int[] gpu,
			byte[] cpuTemps, byte[] gpuTemps, bool independent)
		{
			Owners.Add(owner);
			WujieFanEcController controller = Controller;
			try
			{
				AppConfig.Set("wujie_fan_independent", independent ? 1 : 0);
			}
			catch
			{
			}
			bool ok = controller.Start(cpuTemps, cpu, independent, gpuTemps, gpu, out string detail);
			PerfModeService.Instance?.RecordFanCurves(modeId, cpu, gpu);
			try
			{
				string key = string.IsNullOrWhiteSpace(modeId) ? "active" : modeId;
				AppConfig.Set("wujie_fan_cputemps_" + key, string.Join(",", cpuTemps));
				AppConfig.Set("wujie_fan_gputemps_" + key, string.Join(",", gpuTemps));
			}
			catch
			{
			}
			return new FanCurveApplyOutcome(
				ok ? FanCurveApplyStatus.Applied : FanCurveApplyStatus.SaveFailed, detail ?? "");
		}
	}
}
