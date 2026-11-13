namespace MechrevoLite.Hardware;

/// <summary>
/// HID 键盘设备打分（纯函数，离线可断言）。
/// <para>用途：同一条链路上同时存在单键 RGB 键盘（0x6000~0x600B）与灯带/Logo/铰链（0x7000~0x7002），
/// 必须保证键盘帧只发给键盘设备；否则"键盘看起来一直不亮"。</para>
/// </summary>
internal static class HidKeyboardScoring
{
	internal const ushort PerKeyKeyboardFirstProductId = 0x6000;

	internal const ushort PerKeyKeyboardLastProductId = 0x600B;

	/// <summary>灯带/Logo/铰链控制器：⛔ 不是键盘控件，打分一律 0。</summary>
	internal static bool IsLightingAuxiliary(ushort productId)
	{
		return productId == 0x7000 || productId == 0x7001 || productId == 0x7002;
	}

	/// <summary>单键 RGB 键盘 PID 段。</summary>
	internal static bool IsPerKeyKeyboard(ushort productId)
	{
		return productId >= PerKeyKeyboardFirstProductId && productId <= PerKeyKeyboardLastProductId;
	}

	/// <summary>
	/// 候选打分：0 = 不能用。usagePage 0xFF03 优先，其次 interface 1；单键键盘加权，
	/// 灯带/Logo/铰链直接排除。
	/// </summary>
	internal static int Score(ushort productId, ushort usagePage, int interfaceNumber, ushort outputReportLength, ushort featureReportLength)
	{
		if (IsLightingAuxiliary(productId))
		{
			return 0;
		}
		int num = ((usagePage == 0xFF03) ? 30 : ((interfaceNumber == 1) ? 20 : 0));
		if (num == 0)
		{
			return 0;
		}
		if (IsPerKeyKeyboard(productId))
		{
			// 键盘优先：保证与灯带设备同分时也选键盘（不再依赖枚举顺序）。
			num += 10;
		}
		if (outputReportLength == 65)
		{
			num += 2;
		}
		if (featureReportLength == 9)
		{
			num += 2;
		}
		return num;
	}
}
