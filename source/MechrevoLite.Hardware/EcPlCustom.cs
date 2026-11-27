using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 自定义档的三个功耗设定值（PL1 / PL2 / PL4）。
/// <para>固件按写入值执行，不会自行改回；是否启用由声明表的 plCustom.params 决定，没声明时本类不参与任何判断。</para>
/// </summary>
internal static class EcPlCustom
{
	/// <summary>本机是否有可直接写的功耗设定值寄存器。</summary>
	internal static bool InUse => !string.IsNullOrWhiteSpace(DeclarationRuntime.Param("plCustom", "pl1"));

	internal static int RegisterPl1 => DeclarationRuntime.RegisterAddress("plCustom", "pl1", 1923);

	internal static int RegisterPl2 => DeclarationRuntime.RegisterAddress("plCustom", "pl2", 1924);

	internal static int RegisterPl4 => DeclarationRuntime.RegisterAddress("plCustom", "pl4", 1925);

	internal static int Minimum => Number("min", 5);

	internal static int MaxPl1 => Number("maxPl1", 85);

	internal static int MaxPl2 => Number("maxPl2", 85);

	internal static int MaxPl4 => Number("maxPl4", 85);

	/// <summary>读当前三个设定值（读不到返回 false）。</summary>
	internal static bool TryRead(out int pl1, out int pl2, out int pl4)
	{
		pl1 = -1;
		pl2 = -1;
		pl4 = -1;
		if (!EcChargeLimit.TryReadByteAt(RegisterPl1, out pl1) || pl1 < 0)
		{
			return false;
		}
		if (!EcChargeLimit.TryReadByteAt(RegisterPl2, out pl2) || pl2 < 0)
		{
			return false;
		}
		if (!EcChargeLimit.TryReadByteAt(RegisterPl4, out pl4) || pl4 < 0)
		{
			return false;
		}
		return true;
	}

	/// <summary>
	/// 写三个设定值：先夹到各自上下限，逐个写入后整体回读确认。
	/// <para><paramref name="detail"/> 返回可直接显示的说明。</para>
	/// </summary>
	internal static bool TrySet(int pl1, int pl2, int pl4, out string detail)
	{
		int v1 = Clamp(pl1, Minimum, MaxPl1);
		int v2 = Clamp(pl2, Minimum, MaxPl2);
		int v4 = Clamp(pl4, Minimum, MaxPl4);
		if (!TryRead(out int old1, out int old2, out int old4))
		{
			detail = "读不到当前功耗设定值，未写入";
			return false;
		}
		if (!EcChargeLimit.TryWriteByteAt(RegisterPl1, v1)
			|| !EcChargeLimit.TryWriteByteAt(RegisterPl2, v2)
			|| !EcChargeLimit.TryWriteByteAt(RegisterPl4, v4))
		{
			detail = $"写入失败（目标 {v1}/{v2}/{v4} W）";
			return false;
		}
		if (!TryRead(out int now1, out int now2, out int now4))
		{
			detail = "写后回读失败，无法确认";
			return false;
		}
		if (now1 != v1 || now2 != v2 || now4 != v4)
		{
			detail = $"回读不一致（写 {v1}/{v2}/{v4}，读回 {now1}/{now2}/{now4}）";
			Logger.WriteErrorIfChanged("pl-custom-set", "[pl-custom] " + detail);
			return false;
		}
		detail = $"PL1 {old1}→{now1} W，PL2 {old2}→{now2} W，PL4 {old4}→{now4} W";
		Logger.WriteErrorIfChanged("pl-custom-set", "[pl-custom] 已写入：" + detail);
		return true;
	}

	private static int Clamp(int value, int min, int max)
	{
		if (value < min)
		{
			return min;
		}
		if (value > max)
		{
			return max;
		}
		return value;
	}

	private static int Number(string key, int fallback)
	{
		string? text = DeclarationRuntime.Param("plCustom", key);
		if (string.IsNullOrWhiteSpace(text))
		{
			return fallback;
		}
		if (!int.TryParse(text.Trim(), out int value) || value < 0 || value > 255)
		{
			Logger.WriteLineIfChanged("pl-custom-number-" + key,
				$"自定义档：声明 plCustom.{key}=\"{text}\" 不是 0-255 的整数 ⇒ 用默认 {fallback}");
			return fallback;
		}
		return value;
	}
}
