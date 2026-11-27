using System;
using System.Collections.Generic;
using System.Globalization;
using MechrevoLite.Display;
using MechrevoLite.Hardware;

namespace MechrevoLite.Mode;

/// <summary>
/// 系统侧四项（电源计划 / Windows 电源模式 / CPU 睿频模式 / 屏幕刷新率）的"用户原值"快照。
/// <para>规则：</para>
/// <para>① 只在**第一次**要改写某一项之前记录一次，之后不再覆盖（避免追着用户的改动跑）；</para>
/// <para>② 切模式时先把上一个模式改动过的项恢复成快照，再下发新模式的配置；</para>
/// <para>③ 前后两个模式都没改过这些项（全「不改变」）⇒ 一律不动；</para>
/// <para>④ 关闭开关后完全不参与，等于旧行为（只改不恢复）。</para>
/// </summary>
public static class ModeSystemSnapshot
{
	public enum Field
	{
		PowerPlan,
		PowerOverlay,
		CpuBoost,
		RefreshHz,
		ProcessorMaxFreq
	}

	private const string EnabledKey = "syssnap_enabled";
	private const string OwnedKey = "syssnap_owned";

	/// <summary>是否启用"切模式恢复系统设置"（默认启用，关闭时写 0）。</summary>
	public static bool Enabled
	{
		get { return AppConfig.IsNotFalse(EnabledKey); }
		set
		{
			if (value)
			{
				AppConfig.Remove(EnabledKey);
			}
			else
			{
				AppConfig.Set(EnabledKey, 0);
			}
		}
	}

	/// <summary>恢复顺序：覆盖层会把电源计划压成"平衡"，所以计划放在最后一步恢复。</summary>
	private static readonly Field[] RestoreOrder = new Field[5]
	{
		Field.PowerOverlay,
		Field.CpuBoost,
		Field.ProcessorMaxFreq,
		Field.RefreshHz,
		Field.PowerPlan
	};

	internal static string KeyOf(Field field)
	{
		return field switch
		{
			Field.PowerPlan => "syssnap_plan", 
			Field.PowerOverlay => "syssnap_overlay", 
			Field.CpuBoost => "syssnap_boost", 
			Field.ProcessorMaxFreq => "syssnap_freq", 
			_ => "syssnap_hz", 
		};
	}

	public static string NameOf(Field field)
	{
		return field switch
		{
			Field.PowerPlan => "电源计划", 
			Field.PowerOverlay => "Windows 电源模式", 
			Field.CpuBoost => "CPU 睿频模式", 
			Field.ProcessorMaxFreq => "处理器最大频率", 
			_ => "屏幕刷新率", 
		};
	}

	/// <summary>读当前 Windows 值（用来记快照/做界面显示）。</summary>
	public static bool TryReadCurrent(Field field, out string text)
	{
		text = "";
		switch (field)
		{
		case Field.PowerPlan:
		{
			string activePlan = WinPowerPlan.GetActivePlan();
			if (string.IsNullOrWhiteSpace(activePlan))
			{
				return false;
			}
			text = activePlan;
			return true;
		}
		case Field.PowerOverlay:
		{
			int? overlayIndex = PowerNative.GetOverlayIndex();
			if (!overlayIndex.HasValue)
			{
				return false;
			}
			text = overlayIndex.Value.ToString(CultureInfo.InvariantCulture);
			return true;
		}
		case Field.CpuBoost:
		{
			int ac;
			int dc;
			if (!WinPowerPlan.TryGetBoost(out ac, out dc))
			{
				return false;
			}
			text = ac.ToString(CultureInfo.InvariantCulture);
			return true;
		}
		case Field.ProcessorMaxFreq:
		{
			if (!PowerNative.TryGetProcessorMaxFreq(out int freqAc, out int freqDc))
			{
				return false;
			}
			text = freqAc.ToString(CultureInfo.InvariantCulture) + "," + freqDc.ToString(CultureInfo.InvariantCulture);
			return true;
		}
		default:
		{
			int refreshRate = ScreenNative.GetRefreshRate(ScreenNative.FindLaptopScreen(log: false));
			if (refreshRate <= 0)
			{
				return false;
			}
			text = refreshRate.ToString(CultureInfo.InvariantCulture);
			return true;
		}
		}
	}

	/// <summary>快照值（没有记录过返回 null）。</summary>
	public static string? Get(Field field)
	{
		return AppConfig.GetString(KeyOf(field), null);
	}

	/// <summary>第一次要改这一项时把当时的 Windows 值记下来；已有快照不覆盖。</summary>
	internal static void CaptureIfMissing(Field field)
	{
		if (Get(field) != null || !TryReadCurrent(field, out string text))
		{
			return;
		}
		AppConfig.Set(KeyOf(field), text);
		Logger.WriteLine("[syssnap] 记录基线：" + NameOf(field) + " = " + text);
	}

	/// <summary>以当前系统状态整体重记四项基线（界面按钮）。返回成功记录的项数。</summary>
	public static int RecaptureAll()
	{
		int num = 0;
		Field[] restoreOrder = RestoreOrder;
		foreach (Field field in restoreOrder)
		{
			if (!TryReadCurrent(field, out string text))
			{
				continue;
			}
			AppConfig.Set(KeyOf(field), text);
			num++;
		}
		Logger.WriteLine("[syssnap] 用户重记基线：" + num + "/4 项");
		return num;
	}

	/// <summary>只清基准记忆与归属，不动"自动恢复"开关（开关由界面单独控制）。</summary>
	public static void ClearBaseline()
	{
		Field[] restoreOrder = RestoreOrder;
		foreach (Field field in restoreOrder)
		{
			AppConfig.Remove(KeyOf(field));
		}
		AppConfig.Remove(OwnedKey);
		Logger.WriteLine("[syssnap] 已清理基准记忆（自动恢复开关未变）");
	}

	/// <summary>清除基线并关闭自动恢复。</summary>
	public static void Clear()
	{
		ClearBaseline();
		AppConfig.Set(EnabledKey, 0);
	}

	/// <summary>上一个模式改动过的项（逗号分隔的 Field 名）。</summary>
	private static HashSet<Field> LoadOwned()
	{
		HashSet<Field> hashSet = new HashSet<Field>();
		string text = AppConfig.GetString(OwnedKey, null);
		if (string.IsNullOrWhiteSpace(text))
		{
			return hashSet;
		}
		string[] array = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string value in array)
		{
			if (Enum.TryParse<Field>(value, ignoreCase: true, out var result))
			{
				hashSet.Add(result);
			}
		}
		if (ApuGuardOwnsBoost())
		{
			hashSet.Remove(Field.CpuBoost);
		}
		if (OverlayOwnedOutside())
		{
			hashSet.Remove(Field.PowerOverlay);
		}
		return hashSet;
	}

	/// <summary>下发成功后才登记"当前模式改过哪些系统项"；失败不登记，下次切换就不会去恢复没动过的项。</summary>
	internal static void CommitOwned(string modeId, HashSet<Field>? owned)
	{
		if (owned == null)
		{
			return;
		}
		SaveOwned(owned);
	}

	/// <summary>「高性能电源」开关开着时，Windows 电源模式由它统一决定（GCU 由 GCU 下发、EC 由 LM 按档位下发），
	/// 快照不登记也不恢复这一项 —— 否则两边都会去写同一个值，谁后到谁赢。</summary>
	private static bool OverlayOwnedOutside()
	{
		try
		{
			return AppConfig.Get("bd_power_overlay", 0) != 0;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>APU 防缩缸开着时，处理器性能提升模式由它独占（优先于基准），快照不登记也不恢复这一项。</summary>
	private static bool ApuGuardOwnsBoost()
	{
		try
		{
			return AppConfig.Get("apuboost", 0) == 1;
		}
		catch
		{
			return false;
		}
	}

	private static void SaveOwned(HashSet<Field> owned)
	{
		if (owned.Count == 0)
		{
			AppConfig.Remove(OwnedKey);
			return;
		}
		AppConfig.Set(OwnedKey, string.Join(",", owned));
	}

	/// <summary>这批步骤会不会真的切档（只有切档才做恢复）。</summary>
	private static bool HasSwitch(IReadOnlyList<PerfApplyStep> steps)
	{
		for (int i = 0; i < steps.Count; i++)
		{
			PerfApplyStepKind kind = steps[i].Kind;
			if (kind == PerfApplyStepKind.SwitchBuiltIn || kind == PerfApplyStepKind.SwitchFirmwareSlot)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>这批步骤将要改动哪些系统项（写覆盖层时会把电源计划压成平衡，所以连带计划）。</summary>
	/// <summary>这一步本身会改哪些系统项（登记用；覆盖层不连带电源计划）。</summary>
	internal static IEnumerable<Field> FieldsOfStep(PerfApplyStep step)
	{
		switch (step.Kind)
		{
		case PerfApplyStepKind.ApplyPowerPlan:
			yield return Field.PowerPlan;
			break;
		case PerfApplyStepKind.ApplyPowerOverlay:
			yield return Field.PowerOverlay;
			break;
		case PerfApplyStepKind.ApplyCpuBoost:
			yield return Field.CpuBoost;
			break;
		case PerfApplyStepKind.ApplyProcessorMaxFreq:
			yield return Field.ProcessorMaxFreq;
			break;
		case PerfApplyStepKind.ApplyRefreshRate:
			yield return Field.RefreshHz;
			break;
		}
	}

	private static HashSet<Field> DeclaredFields(IReadOnlyList<PerfApplyStep> steps)
	{
		HashSet<Field> hashSet = new HashSet<Field>();
		for (int i = 0; i < steps.Count; i++)
		{
			switch (steps[i].Kind)
			{
			case PerfApplyStepKind.ApplyPowerPlan:
				hashSet.Add(Field.PowerPlan);
				break;
			case PerfApplyStepKind.ApplyPowerOverlay:
				hashSet.Add(Field.PowerOverlay);
				hashSet.Add(Field.PowerPlan);
				break;
			case PerfApplyStepKind.ApplyCpuBoost:
				hashSet.Add(Field.CpuBoost);
				break;
			case PerfApplyStepKind.ApplyProcessorMaxFreq:
				hashSet.Add(Field.ProcessorMaxFreq);
				break;
			case PerfApplyStepKind.ApplyRefreshRate:
				hashSet.Add(Field.RefreshHz);
				break;
			}
		}
		if (ApuGuardOwnsBoost())
		{
			hashSet.Remove(Field.CpuBoost);
		}
		if (OverlayOwnedOutside())
		{
			hashSet.Remove(Field.PowerOverlay);
		}
		return hashSet;
	}

	private static bool TryBuildRestoreStep(Field field, out PerfApplyStep step)
	{
		step = null;
		string? text = Get(field);
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		switch (field)
		{
		case Field.PowerPlan:
			step = new PerfApplyStep(PerfApplyStepKind.ApplyPowerPlan, null, text, 0, IsCpuCurve: false, null, PerfApplyVerify.PowerPlan);
			return true;
		case Field.PowerOverlay:
		{
			if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result < 0 || result > 2)
			{
				return false;
			}
			step = new PerfApplyStep(PerfApplyStepKind.ApplyPowerOverlay, null, null, result, IsCpuCurve: false, null, PerfApplyVerify.PowerOverlay);
			return true;
		}
		case Field.CpuBoost:
		{
			if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result2))
			{
				return false;
			}
			step = new PerfApplyStep(PerfApplyStepKind.ApplyCpuBoost, null, null, result2, IsCpuCurve: false, null, PerfApplyVerify.CpuBoost);
			return true;
		}
		case Field.ProcessorMaxFreq:
		{
			string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
			if (parts.Length != 2
				|| !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int freqAc)
				|| !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int freqDc))
			{
				return false;
			}
			step = new PerfApplyStep(PerfApplyStepKind.ApplyProcessorMaxFreq, null, null, freqAc, IsCpuCurve: false, new int[1] { freqDc }, PerfApplyVerify.ProcessorMaxFreq);
			return true;
		}
		default:
		{
			if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result3) || result3 <= 0)
			{
				return false;
			}
			step = new PerfApplyStep(PerfApplyStepKind.ApplyRefreshRate, null, null, result3, IsCpuCurve: false, null, PerfApplyVerify.RefreshRate);
			return true;
		}
		}
	}

	/// <summary>
	/// 切档时清理上一个模式的系统项自定义：**只有上一个模式真的改过系统项**（离开它）才恢复快照；
	/// 上一个模式没改过（全「不改变」）⇒ 什么都不做，不会因为"进入一个不带系统项的模式"而触发清理。
	/// <para>不是切档（同模式内重应用 / 漂移修正）时原样返回。</para>
	/// </summary>
	/// <param name="ownAfter">本次模式将要改动的系统项；下发成功后再 <see cref="CommitOwned" />，失败不登记。</param>

	/// <param name="forceRestore">调用方只带恢复步（EC 直连点内置档）时置 true：没有切档步也照常恢复。</param>
	internal static IReadOnlyList<PerfApplyStep> PrepareForSwitch(string modeId, IReadOnlyList<PerfApplyStep> steps, out HashSet<Field>? ownAfter, bool forceRestore = false)
	{
		ownAfter = null;
		// 判"同档重应用"必须用**当前已生效档位**（ActiveModeId，切档成功后才更新）：
		// 内置档切换不经过本方法，用"上次处理过的档位"会一直停在旧值，
		// 结果回到自定义档被误判成"同档重应用"⇒ 归属永远登记不上、也就永不恢复。
		string activeModeId = PerfModeStore.ActiveModeId ?? "";
		bool sameModeAgain = !string.IsNullOrEmpty(activeModeId)
			&& string.Equals(activeModeId, modeId, StringComparison.Ordinal);
		bool declaresBoost = false;
		for (int i = 0; i < steps.Count; i++)
		{
			if (steps[i].Kind == PerfApplyStepKind.ApplyCpuBoost)
			{
				declaresBoost = true;
				break;
			}
		}
		bool declaresOverlay = false;
		for (int j = 0; j < steps.Count; j++)
		{
			if (steps[j].Kind == PerfApplyStepKind.ApplyPowerOverlay)
			{
				declaresOverlay = true;
				break;
			}
		}
		if (declaresOverlay && OverlayOwnedOutside())
		{
			Logger.WriteErrorIfChanged("syssnap-overlay-gcu", "[syssnap] 高性能电源开关已开 ⇒ Windows 电源模式由它统一决定（GCU/按档位下发），快照不登记也不恢复这一项");
		}
		if (declaresBoost && ApuGuardOwnsBoost())
		{
			Logger.WriteErrorIfChanged("syssnap-apuboost", "[syssnap] APU 防缩缸开启 ⇒ 处理器性能提升模式由它独占（优先于基准），快照不登记也不恢复这一项");
		}
		if (!Enabled || (!forceRestore && !HasSwitch(steps)) || sameModeAgain)
		{
			// 没参与恢复时留一行可见记录：否则"改了系统项、切档却没恢复"在默认日志级别下查不到原因。
			Logger.WriteErrorIfChanged("syssnap-skip:" + modeId + ":" + activeModeId, "[syssnap] 本次未参与切档恢复：开关="
				+ (Enabled ? "开" : "关") + " · 有切档步=" + (HasSwitch(steps) ? "是" : "否")
				+ " · 同档重应用=" + (sameModeAgain ? "是" : "否") + "（当前档=" + activeModeId + "，本次=" + modeId + "）"
				+ " · 已登记归属=" + LoadOwned().Count + " 项");
			return steps;
		}
		HashSet<Field> hashSet = DeclaredFields(steps);
		HashSet<Field> hashSet2 = LoadOwned();
		foreach (Field item in hashSet)
		{
			CaptureIfMissing(item);
		}
		// 本次真的恢复掉的项（恢复完就回到基线，不再算"我们写的"）。
		HashSet<Field> restoredNow = new HashSet<Field>();
		List<PerfApplyStep> list = new List<PerfApplyStep>(4);
		List<string> list2 = new List<string>(4);
		Field[] restoreOrder = RestoreOrder;
		foreach (Field field in restoreOrder)
		{
			if (hashSet2.Contains(field) && TryBuildRestoreStep(field, out PerfApplyStep step))
			{
				list.Add(step);
				list2.Add(NameOf(field));
				restoredNow.Add(field);
			}
		}
		// ⭐ 归属要"并"不要"换"：上一个模式写过、这次没恢复也没重写的项仍然是我们写的，
		//   丢掉归属就再也恢复不回去（例如切到不带该项目的模式后，上限会一直留着）。
		ownAfter = new HashSet<Field>(hashSet);
		foreach (Field carried in hashSet2)
		{
			if (!restoredNow.Contains(carried))
			{
				ownAfter.Add(carried);
			}
		}
		if (list.Count == 0)
		{
			// 上一个模式没改过系统项 ⇒ 不清理（也不因为新模式有没有系统项而动作）。
			Logger.WriteLine("[syssnap] 上一个模式无系统项自定义 ⇒ 不恢复（本次将改："
				+ (hashSet.Count == 0 ? "无" : string.Join("、", hashSet)) + "）");
			return steps;
		}
		Logger.WriteErrorIfChanged("syssnap-restore", "[syssnap] 离开带系统项的模式 ⇒ 恢复基线：" + string.Join("、", list2)
			+ "（本次将改：" + (hashSet.Count == 0 ? "无" : string.Join("、", hashSet)) + "）");
		List<PerfApplyStep> list3 = new List<PerfApplyStep>(list.Count + steps.Count);
		list3.AddRange(list);
		list3.AddRange(steps);
		return list3;
	}
}
