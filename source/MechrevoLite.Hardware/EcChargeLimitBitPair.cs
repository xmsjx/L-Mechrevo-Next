using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 位对式充电上限（部分平台的固件用同一字节里的两位表示档位，而不是写百分数）。
/// <para>位组合 → 档位：两位全 0 = 100%（高容量）、低位 = 80%、高位 = 60%。</para>
/// <para>写入方式：读该字节 → 只改这两位 → 写回并回读确认，其余位保持原样。</para>
/// <para>是否启用由声明表的 chargeLimit.params.scheme = "bitpair" 决定；没声明时本类不参与任何判断。</para>
/// </summary>
internal static class EcChargeLimitBitPair
{
	internal const string SchemeName = "bitpair";

	/// <summary>档位方案名（声明表 chargeLimit.params.scheme）；未声明为 null。</summary>
	internal static string? Scheme => DeclarationRuntime.Param("chargeLimit", "scheme");

	/// <summary>本机是否走位对式方案。</summary>
	internal static bool InUse => string.Equals(Scheme, SchemeName, StringComparison.OrdinalIgnoreCase);

	/// <summary>档位字节（默认 0x7A6）。</summary>
	internal static int Register => DeclarationRuntime.RegisterAddress("chargeLimit", "reg", 1958);

	/// <summary>能力标志字节（默认 0x78E）。</summary>
	internal static int SupportRegister => DeclarationRuntime.RegisterAddress("chargeLimit", "support", 1934);

	/// <summary>低位所在的位号（默认 4）。</summary>
	internal static int BitLow => BitIndex("bitLow", 4);

	/// <summary>高位所在的位号（默认 5）。</summary>
	internal static int BitHigh => BitIndex("bitHigh", 5);

	/// <summary>能力标志所在位号（默认 3）。</summary>
	internal static int SupportBit => BitIndex("supportBit", 3);

	/// <summary>把任意请求百分比折算成最接近的一档。</summary>
	internal static int LevelFor(int percent)
	{
		if (percent >= 90)
		{
			return 100;
		}
		if (percent >= 70)
		{
			return 80;
		}
		return 60;
	}

	/// <summary>档位对应的两位取值（低位, 高位）。</summary>
	internal static (bool Low, bool High) BitsFor(int level)
	{
		switch (level)
		{
		case 80:
			return (true, false);
		case 60:
			return (false, true);
		default:
			return (false, false);
		}
	}

	/// <summary>
	/// 本机是否支持该功能：能力标志位置 1 为支持。
	/// <para>读不到该字节时按"无法判断、允许尝试"处理（与既有通道可用性口径一致）；读到且为 0 则判不支持。</para>
	/// </summary>
	internal static bool IsSupported()
	{
		if (!EcChargeLimit.TryReadByteAt(SupportRegister, out int raw) || raw < 0)
		{
			Logger.WriteLineIfChanged("charge-limit-bitpair-support-unknown",
				$"限充可用性：位对式方案读不到能力标志 0x{SupportRegister:X3} ⇒ 按可尝试处理（未写任何 EC）");
			return true;
		}
		bool supported = ((raw >> SupportBit) & 1) == 1;
		Logger.WriteLineIfChanged("charge-limit-bitpair-support",
			$"限充可用性：位对式方案能力标志 0x{SupportRegister:X3}=0x{raw:X2} bit{SupportBit} ⇒ " + (supported ? "支持" : "不支持"));
		return supported;
	}

	/// <summary>读当前档位（100/80/60）；读不到返回 -1。</summary>
	internal static int ReadPercent()
	{
		int register = Register;
		if (!EcChargeLimit.TryReadByteAt(register, out int raw) || raw < 0)
		{
			return -1;
		}
		bool low = ((raw >> BitLow) & 1) == 1;
		bool high = ((raw >> BitHigh) & 1) == 1;
		if (low && high)
		{
			Logger.WriteLineIfChanged("charge-limit-bitpair-both",
				$"限充回读：0x{register:X3}=0x{raw:X2} 两位同时为 1（无对应档位）⇒ 按不限充处理");
			return 100;
		}
		if (low)
		{
			return 80;
		}
		if (high)
		{
			return 60;
		}
		return 100;
	}

	/// <summary>
	/// 写档位：读-改-写单个字节，只动两位，其余位原样保留；写完回读确认两位已落盘。
	/// <para><paramref name="appliedPercent"/> 返回实际生效的档位（不是请求值）。</para>
	/// <para><paramref name="refusedByFirmware"/> 为真表示按能力标志判定本机不支持，主动拒绝写入。</para>
	/// </summary>
	internal static bool TrySet(int percent, out int appliedPercent, out bool refusedByFirmware)
	{
		appliedPercent = -1;
		refusedByFirmware = false;
		int level = LevelFor(percent);
		int register = Register;
		if (!EcChargeLimit.TryReadByteAt(register, out int current) || current < 0)
		{
			Logger.WriteLine($"限充写入（位对式）：读不到 0x{register:X3} ⇒ 请求 {percent}%（档位 {level}%）未下发");
			return false;
		}
		if (!IsSupported())
		{
			refusedByFirmware = true;
			Logger.WriteLine($"限充写入（位对式）：能力标志判定本机不支持 ⇒ 请求 {percent}% 未下发，⛔ 未写任何寄存器");
			return false;
		}
		(bool low, bool high) = BitsFor(level);
		int value = SetBit(SetBit(current, BitLow, low), BitHigh, high);
		if (value == current)
		{
			appliedPercent = level;
			Logger.WriteLineIfChanged("charge-limit-bitpair-same",
				$"限充写入（位对式）：0x{register:X3} 已是 0x{current:X2}（档位 {level}%）⇒ 无需写入");
			return true;
		}
		if (!EcChargeLimit.TryWriteByteAt(register, value))
		{
			Logger.WriteLine($"限充写入（位对式）：写 0x{register:X3}=0x{value:X2} 失败 ⇒ 请求 {percent}%（档位 {level}%）未生效");
			return false;
		}
		if (!EcChargeLimit.TryReadByteAt(register, out int after) || after < 0)
		{
			Logger.WriteLine($"限充写入（位对式）：写后回读 0x{register:X3} 失败 ⇒ 无法确认，按失败处理");
			return false;
		}
		int mask = (1 << BitLow) | (1 << BitHigh);
		if ((after & mask) != (value & mask))
		{
			Logger.WriteLine($"限充写入（位对式）：回读不一致（写 0x{value:X2}，读回 0x{after:X2}）⇒ 按失败处理");
			return false;
		}
		appliedPercent = level;
		Logger.WriteLine($"限充写入（位对式）：0x{register:X3} 0x{current:X2} → 0x{after:X2}，档位 {level}%（请求 {percent}%）");
		return true;
	}

	private static int SetBit(int value, int bit, bool on)
	{
		if (bit < 0 || bit > 7)
		{
			return value;
		}
		if (on)
		{
			return value | (1 << bit);
		}
		return value & ~(1 << bit);
	}

	private static int BitIndex(string key, int fallback)
	{
		string? text = DeclarationRuntime.Param("chargeLimit", key);
		if (string.IsNullOrWhiteSpace(text))
		{
			return fallback;
		}
		if (!int.TryParse(text.Trim(), out int bit) || bit < 0 || bit > 7)
		{
			Logger.WriteLineIfChanged("charge-limit-bitpair-bit-" + key,
				$"限充（位对式）：声明 chargeLimit.{key}=\"{text}\" 不是 0-7 的位号 ⇒ 用默认 {fallback}");
			return fallback;
		}
		return bit;
	}
}
