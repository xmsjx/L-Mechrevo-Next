using System.Collections.Generic;
using PawnIO;

namespace MechrevoLite.Hardware;

/// <summary>
/// 分核负压（Curve Optimizer）可写的取值区间：按 CPU 代号取，不同世代上限不同。
/// <para>界面、写入门控与参数编码共用这一份定义，避免三处各写一个数字。</para>
/// </summary>
internal static class CpuCoRange
{
	/// <summary>未知/非 AMD 时的保守下限。</summary>
	internal const int DefaultMinimum = -30;

	/// <summary>未知/非 AMD 时的保守上限。</summary>
	internal const int DefaultMaximum = 0;

	/// <summary>较新世代（Zen4 / Zen5）的区间。</summary>
	internal const int NewerMinimum = -50;

	internal const int NewerMaximum = 30;

	/// <summary>按代号解析本机可写区间。</summary>
	internal static (int Minimum, int Maximum) ForCode(CpuCodeName codeName)
	{
		switch (codeName)
		{
			case CpuCodeName.Raphael:
			case CpuCodeName.DragonRange:
			case CpuCodeName.Rembrandt:
			case CpuCodeName.Phoenix:
			case CpuCodeName.Phoenix2:
			case CpuCodeName.HawkPoint:
			case CpuCodeName.StrixPoint:
			case CpuCodeName.StrixHalo:
			case CpuCodeName.Mendocino:
			case CpuCodeName.StormPeak:
			case CpuCodeName.GraniteRidge:
			case CpuCodeName.Genoa:
			case CpuCodeName.Bergamo:
			case CpuCodeName.Turin:
			case CpuCodeName.TurinD:
			case CpuCodeName.KrackanPoint:
			case CpuCodeName.KrackanPoint2:
			case CpuCodeName.ShimadaPeak:
				return (NewerMinimum, NewerMaximum);
			default:
				// Zen3 及更早：上限为 -30（该世代实际接受的下限），正值不开放。
				return (DefaultMinimum, DefaultMaximum);
		}
	}

	/// <summary>
	/// 从"请求值 vs 回读值"里识别 SMU 自己夹住的下限：某个核请求更低、回读却停在 X，
	/// 且没有任何核失败 ⇒ 判定本机真实下限是这些回读值里最大的那个。返回 null 表示没有证据。
	/// </summary>
	internal static int? DetectClampedMinimum(IReadOnlyList<int> requested, IReadOnlyList<int> readback, int codeMinimum)
	{
		if (requested.Count != readback.Count || requested.Count == 0)
		{
			return null;
		}
		int clamped = int.MinValue;
		for (int i = 0; i < requested.Count; i++)
		{
			if (requested[i] < codeMinimum || readback[i] <= requested[i])
			{
				continue;
			}
			int candidate = readback[i];
			if (candidate > clamped)
			{
				clamped = candidate;
			}
		}
		if (clamped == int.MinValue || clamped <= codeMinimum)
		{
			return null;
		}
		return clamped;
	}

	internal static bool IsValid(int margin, int minimum, int maximum)
	{
		return margin >= minimum && margin <= maximum;
	}

	/// <summary>兼容旧调用：按保守默认区间校验。</summary>
	internal static bool IsValid(int margin)
	{
		return IsValid(margin, DefaultMinimum, DefaultMaximum);
	}
}
