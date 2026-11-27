using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 双风扇转速所在的 EC 字（高字节在前、低字节在后）。
/// <para>地址由声明表的 fanRpm.params 给出：每台机的转速寄存器并不在同一个偏移上，
/// 声明缺失时本类不参与任何判断，调用方沿用既有的机型分支。</para>
/// </summary>
internal static class EcFanRpm
{
	/// <summary>本机是否声明了转速字地址。</summary>
	internal static bool InUse => !string.IsNullOrWhiteSpace(DeclarationRuntime.Param("fanRpm", "mainHigh"));

	internal static int MainHigh => DeclarationRuntime.RegisterAddress("fanRpm", "mainHigh", 0x464);

	internal static int MainLow => DeclarationRuntime.RegisterAddress("fanRpm", "mainLow", 0x465);

	internal static int SecondHigh => DeclarationRuntime.RegisterAddress("fanRpm", "secondHigh", 0x46C);

	internal static int SecondLow => DeclarationRuntime.RegisterAddress("fanRpm", "secondLow", 0x46B);

	/// <summary>
	/// 读双扇转速。主扇读不到就返回 false（交给机型分支），副扇读不到时留 -1。
	/// </summary>
	internal static bool TryRead(out int mainRpm, out int secondRpm)
	{
		mainRpm = -1;
		secondRpm = -1;
		if (!InUse)
		{
			return false;
		}
		if (!TryReadWord(MainHigh, MainLow, out int main))
		{
			return false;
		}
		mainRpm = main;
		if (TryReadWord(SecondHigh, SecondLow, out int second))
		{
			secondRpm = second;
		}
		Logger.WriteLineIfChanged("fan-rpm-declared", "[fan] 转速取自声明地址：主 0x" + MainHigh.ToString("X3") + "/0x" + MainLow.ToString("X3")
			+ " · 副 0x" + SecondHigh.ToString("X3") + "/0x" + SecondLow.ToString("X3"));
		return true;
	}

	private static bool TryReadWord(int high, int low, out int rpm)
	{
		rpm = -1;
		if (high <= 0 || low <= 0)
		{
			return false;
		}
		if (!AcpiDriverEc.TryReadByte(high, out byte h) || !AcpiDriverEc.TryReadByte(low, out byte l))
		{
			return false;
		}
		int value = (h << 8) | l;
		// 0xFFFF = 该地址无数据；超过 8000 RPM 属明显不是转速
		if (value == 0xFFFF || value > 8000)
		{
			return false;
		}
		rpm = value;
		return true;
	}
}
