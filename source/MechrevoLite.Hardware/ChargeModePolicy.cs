namespace MechrevoLite.Hardware;

/// <summary>充电上限档位与 EC 模式位的换算（纯函数，便于离线断言）。</summary>
internal static class ChargeModePolicy
{
	/// <summary>100%（不限充）对应的模式位。</summary>
	internal const int ModeBitsOff = 0x00;

	/// <summary>平衡档（约 76~94%）对应的模式位。</summary>
	internal const int ModeBitsBalanced = 0x10;

	/// <summary>工作站档（≤65%）对应的模式位。</summary>
	internal const int ModeBitsWorkstation = 0x20;

	/// <summary>
	/// 百分比换算成模式位：≥95% 视为不限充，≤65% 为工作站档，其余为平衡档。
	/// <para>⚠️ 与写入 EC 的算法必须完全一致，改这里就等于改下发。</para>
	/// </summary>
	internal static int ComputeModeBits(int percent)
	{
		if (percent >= 95)
		{
			return ModeBitsOff;
		}
		return percent <= 65 ? ModeBitsWorkstation : ModeBitsBalanced;
	}
}
