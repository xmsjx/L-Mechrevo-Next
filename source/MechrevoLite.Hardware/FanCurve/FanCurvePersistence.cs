// 风扇曲线的存档读写（后端）：只有本机闭环的档位需要落 CpuFanDuty/GpuFanDuty。
using System;
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware
{
	internal static class FanCurvePersistence
	{
		/// <summary>该档的风扇曲线是否由本机保存（自定义档 / 固件配置档）。</summary>
		internal static bool IsLocalCurveMode(string modeId)
			=> modeId.StartsWith("custom", StringComparison.Ordinal)
			|| modeId.StartsWith("fw", StringComparison.Ordinal);

		/// <summary>本机闭环档位缺少存档时，把当前点位补写进去（来源是硬件回读的默认曲线）。</summary>
		internal static void PersistIfMissing(string modeId, int[]? cpuSaved, int[]? gpuSaved, int[] cpu, int[] gpu)
		{
			if (!IsLocalCurveMode(modeId) || (cpuSaved != null && gpuSaved != null))
			{
				return;
			}
			try
			{
				ModeSettings.Save(modeId, ModeSettings.Load(modeId) with
				{
					CpuFanDuty = cpuSaved ?? cpu,
					GpuFanDuty = gpuSaved ?? gpu
				});
			}
			catch (Exception ex)
			{
				Logger.WriteLine("[fan] 补写曲线存档失败（已吞掉）：" + ex.Message);
			}
		}

		/// <summary>清掉该档保存的点位（恢复默认后调用）。</summary>
		internal static void ClearDuties(string modeId)
		{
			try
			{
				ModeSettings.Save(modeId, ModeSettings.Load(modeId) with
				{
					CpuFanDuty = null,
					GpuFanDuty = null
				});
			}
			catch (Exception ex)
			{
				Logger.WriteLine("[fan] 清空曲线存档失败（已吞掉）：" + ex.Message);
			}
		}
	}
}
