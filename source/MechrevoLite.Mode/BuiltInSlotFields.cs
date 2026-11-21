// 内置档（静音 / 平衡 / 狂暴）的配置里不该留着固件槽字段：
// 40 系及以后内置档恒走内置档下发，功耗 / 温度墙 / 超频这些字段没有下发通道，
// 留在配置里只会把手滑写进去的厂商默认值存下来，还会让内置档被判成“需要固件槽”。
// 30 系固件模式仍按“模式 × 配置”走固件槽，字段必须保留 —— 不能删。
using System;

namespace MechrevoLite.Mode
{
	internal static class BuiltInSlotFields
	{
		/// <summary>内置档 id（自定义档与固件配置档不在此列）。</summary>
		private static readonly string[] BuiltInIds = { "silent", "balanced", "turbo", "silentturbo" };

		internal static bool IsBuiltIn(string? modeId)
		{
			if (string.IsNullOrEmpty(modeId))
			{
				return false;
			}
			foreach (string id in BuiltInIds)
			{
				if (string.Equals(id, modeId, StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>该机型的内置档是否仍要保留固件槽字段（30 系及更早、代次判不出 ⇒ 保留）。</summary>
		internal static bool KeepsSlotFields
		{
			get
			{
				try
				{
					string generation = MechrevoLite.Hardware.MachineIdentity.Collect().GpuGeneration ?? "";
					return !(string.Equals(generation, "Gen40", StringComparison.Ordinal)
						|| string.Equals(generation, "Gen50", StringComparison.Ordinal));
				}
				catch
				{
					return true;
				}
			}
		}

		/// <summary>保存前只用：内置档且本机内置档不走固件槽 ⇒ 需要剔除字段。</summary>
		internal static bool MustStrip(string? modeId) => IsBuiltIn(modeId) && !KeepsSlotFields;

		/// <summary>剔除固件槽字段，保留风扇曲线与界面侧（电源计划 / 刷新率等）字段。</summary>
		internal static PerfModeSettings Strip(PerfModeSettings settings) => settings with
		{
			Pl1 = null,
			Pl2 = null,
			Pl4 = null,
			GpuTgp = null,
			GpuDynamicBoostOn = null,
			GpuDynamicBoost = null,
			TccOn = null,
			TccTarget = null,
			StapmTimeSeconds = null,
			SmuSlowLimit = null,
			SmuSlowTimeSeconds = null,
			SmuFastTimeSeconds = null,
			GpuOverclockOn = null,
			GpuCoreOffset = null,
			GpuMemoryOffset = null,
			FanSwitchSpeedOn = null,
			FanSwitchSpeedMs = null
		};
	}
}
