using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 风扇环形填充比例：以"占空比"为准，保证环形与界面文字用的是同一个数。
/// <para>⚠️ 占空比未知（≤0）时不返回比例，调用方退回"转速 / 转速上限"的旧算法。</para>
/// </summary>
internal static class FanRingFraction
{
	/// <summary>占空比换填充比例；未知返回 null。</summary>
	internal static double? FromDuty(int dutyPercent)
	{
		if (dutyPercent <= 0)
		{
			return null;
		}
		return dutyPercent >= 100 ? 1.0 : dutyPercent / 100.0;
	}
}
