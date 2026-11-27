using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 五点风扇曲线：五个连续的占空比字节，温度点由固件固定，没有写口。
/// <para>只写这五个字节，不碰档位字节、不碰斜坡、不碰风扇隔离位；写入失败或回读不一致一律还原原值。</para>
/// </summary>
internal static class EcFanFivePoint
{
	private const int PointCount = 5;

	/// <summary>本机是否声明了五点曲线写法。</summary>
	internal static bool InUse => DeclarationRuntime.Flag("fanCurve", "fivePoint");

	internal static int Count => PointCount;

	internal static int BaseAddress => DeclarationRuntime.RegisterAddress("fanCurve", "base", 1859);

	/// <summary>读当前五个占空比。</summary>
	internal static bool TryRead(out int[] duties)
	{
		duties = new int[PointCount];
		for (int i = 0; i < PointCount; i++)
		{
			if (!AcpiDriverEc.TryReadByte(BaseAddress + i, out byte value))
			{
				return false;
			}
			duties[i] = value > 100 ? 100 : value;
		}
		return true;
	}

	/// <summary>把 16 段曲线按等距取 5 个代表点（温度轴由固件固定，只能取占空比）。</summary>
	internal static bool IsFiveLength(int[] duties) => duties != null && duties.Length == PointCount;

	/// <summary>界面直接给 5 个点时按原样使用（只做限幅与单调修正）。</summary>
	internal static int[] Direct(int[] duties)
	{
		int[] direct = new int[PointCount];
		for (int i = 0; i < PointCount; i++)
		{
			direct[i] = Math.Clamp(duties[i], 0, 100);
			if (i > 0 && direct[i] < direct[i - 1])
			{
				direct[i] = direct[i - 1];
			}
		}
		return direct;
	}

	internal static int[] Sample(byte[] temps, int[] duties)
	{
		if (IsFiveLength(duties))
		{
			return Direct(duties);
		}
		int valid = 0;
		if (temps != null && duties != null)
		{
			int limit = Math.Min(temps.Length, duties.Length);
			for (int i = 0; i < limit; i++)
			{
				if (temps[i] == 0 || temps[i] == byte.MaxValue || duties[i] == 255)
				{
					continue;
				}
				valid = i + 1;
			}
		}
		int[] result = new int[PointCount];
		if (valid <= 0)
		{
			return result;
		}
		for (int i = 0; i < PointCount; i++)
		{
			int source = (int)Math.Round(i * (valid - 1) / (double)(PointCount - 1));
			result[i] = Math.Clamp(duties[source], 0, 100);
		}
		for (int i = 1; i < PointCount; i++)
		{
			if (result[i] < result[i - 1])
			{
				result[i] = result[i - 1];
			}
		}
		return result;
	}

	/// <summary>写入五个占空比并回读确认；任一失败都还原原值。</summary>
	internal static bool TrySet(int[] duties, out string detail)
	{
		if (duties == null || duties.Length != PointCount)
		{
			detail = "五点曲线必须是 5 段";
			return false;
		}
		int[] target = new int[PointCount];
		for (int i = 0; i < PointCount; i++)
		{
			target[i] = Math.Clamp(duties[i], 0, 100);
		}
		if (!TryRead(out int[] before))
		{
			detail = "读不到当前五点值，未写入";
			return false;
		}
		for (int i = 0; i < PointCount; i++)
		{
			if (!AcpiDriverEc.TryWriteByte(BaseAddress + i, (byte)target[i]))
			{
				Restore(before);
				detail = "第 " + (i + 1) + " 点写入失败，已还原原值";
				return false;
			}
		}
		if (!TryRead(out int[] after))
		{
			detail = "写后回读失败（写入值 " + string.Join("/", target) + "）";
			return false;
		}
		for (int i = 0; i < PointCount; i++)
		{
			if (after[i] != target[i])
			{
				Restore(before);
				detail = "回读不一致（写 " + string.Join("/", target) + "，读回 " + string.Join("/", after) + "），已还原原值";
				return false;
			}
		}
		detail = "五点 " + string.Join("/", target) + " ⇒ 回读 " + string.Join("/", after);
		return true;
	}

	private static void Restore(int[] values)
	{
		for (int i = 0; i < values.Length && i < PointCount; i++)
		{
			AcpiDriverEc.TryWriteByte(BaseAddress + i, (byte)values[i]);
		}
	}
}
