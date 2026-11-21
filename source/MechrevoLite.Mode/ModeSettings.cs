// 档位设置的读写入口：界面只调用这里，不直接碰存储。
using System;
using System.Collections.Generic;

namespace MechrevoLite.Mode
{
	internal static class ModeSettings
	{
		internal static PerfModeSettings Load(string modeId) => PerfModeStore.LoadSettings(modeId);

		/// <summary>保存档位设置。内置档（40 系及以后）先剔除没有下发通道的固件槽字段。</summary>
		internal static void Save(string modeId, PerfModeSettings settings)
		{
			if (BuiltInSlotFields.MustStrip(modeId))
			{
				settings = BuiltInSlotFields.Strip(settings);
			}
			PerfModeStore.SaveSettings(modeId, settings);
		}

		/// <summary>清理旧配置里内置档残留的固件槽字段（只清一次；风扇曲线不动）。</summary>
		internal static void StripBuiltInSlotFieldsOnce()
		{
			try
			{
				if (AppConfig.Get("perf_builtin_strip_v1", 0) == 1)
				{
					return;
				}
				foreach (string id in new[] { "silent", "balanced", "turbo", "silentturbo" })
				{
					if (!BuiltInSlotFields.MustStrip(id))
					{
						continue;
					}
					PerfModeSettings before = PerfModeStore.LoadSettings(id);
					PerfModeSettings after = BuiltInSlotFields.Strip(before);
					if (before == after)
					{
						continue;
					}
					PerfModeStore.SaveSettings(id, after);
					Logger.WriteLine("[perfmode] 内置档清理固件槽字段：" + id + "（风扇曲线保留）");
				}
				AppConfig.Set("perf_builtin_strip_v1", 1);
				AppConfig.Flush();
			}
			catch (Exception ex)
			{
				Logger.WriteLine("[perfmode] 内置档固件槽字段清理失败：" + ex.Message);
			}
		}

		/// <summary>保存固件配置档（只写 fwXpY，不动内置档）。</summary>
		internal static void SaveFirmwareProfile(int visualMode, int profileIndex, PerfModeSettings settings)
		{
			PerfModeStore.SaveSettings("fw" + visualMode + "p" + profileIndex, settings);
			ModeProfileConfig.Set(visualMode, profileIndex);
		}

		/// <summary>「模式 × 配置」可绑定的配置数量（不支持时为 1）。</summary>
		internal static int ProfileCount => ModeProfileConfig.Count;

		/// <summary>该视觉模式当前绑定的配置号。</summary>
		internal static int GetProfile(int visualMode) => ModeProfileConfig.Get(visualMode);

		/// <summary>记住该视觉模式选中的配置号（设置页下拉框用）。</summary>
		internal static void SetProfile(int visualMode, int profileIndex)
			=> ModeProfileConfig.Set(visualMode, profileIndex);

		/// <summary>全部档位定义（内置 + 自定义 + 固件配置档）。</summary>
		internal static IReadOnlyList<PerfModeDefinition> Modes => PerfModeStore.Load();

		/// <summary>当前实际生效的档位 id。</summary>
		internal static string? ActiveModeId
		{
			get => PerfModeStore.ActiveModeId;
			set => PerfModeStore.ActiveModeId = value;
		}

		internal static void Reset(string modeId) => PerfModeStore.ResetSettings(modeId);

		internal static void SaveName(string modeId, string? name) => PerfModeStore.SaveName(modeId, name);

		internal static PerfModeDefinition? AddCustom(string? seedFromModeId = null)
			=> PerfModeStore.AddCustom(seedFromModeId);

		internal static bool RemoveCustom(string modeId) => PerfModeStore.RemoveCustom(modeId);

		internal static void SaveSlots(IReadOnlyList<FirmwareSlotState> slots) => PerfModeStore.SaveSlots(slots);
	}
}
