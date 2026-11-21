using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 0x767 锁存/触发字节的唯一写入口。
/// <para>这个字节是多通道共享的命令口：bit0 是快捷开关的翻转位（Win 键锁等），
/// bit1-2 不在我们这边（实测写 0x27 会打开强冷，所以这两位一律不碰），
/// bit4 是 USB 充电翻转脉冲，bit5 是键盘灯锁存，bit7 是硬件彩虹。
/// 每路只能改自己的位，读-改-写整对原子化，避免互相把命令位抹掉。</para>
/// </summary>
internal static class EcTriggerByte
{
	internal const int Address = 1895;
	internal const byte Latch = 0x20;
	internal const byte Rainbow = 0x80;
	internal const byte UsbPulse = 0x10;
	internal const byte QuickSwitchLatch = 0x01;

	/// <summary>快捷开关命令位：独占者是相应的快捷开关脉冲，别路一律原样写回。</summary>
	private const byte SharedCommandBits = 0x17;

	private static readonly object Gate = new object();

	/// <summary>
	/// 纯位运算：只改 setBits / clearBits 指定的位，其余位保持 current 原值。
	/// <paramref name="ownUsbPulse" /> / <paramref name="ownQuickSwitch" /> 表示本次调用是该位的属主。
	/// </summary>
	internal static byte Compose(byte current, byte setBits, byte clearBits, bool ownUsbPulse, bool ownQuickSwitch)
	{
		byte owned = 0;
		if (ownUsbPulse)
		{
			owned |= UsbPulse;
		}
		if (ownQuickSwitch)
		{
			owned |= QuickSwitchLatch;
		}
		byte keep = (byte)(current & (SharedCommandBits & ~owned));
		byte target = (byte)((current | setBits) & ~clearBits & ~(SharedCommandBits & ~owned));
		return (byte)(keep | target);
	}

	/// <summary>按当前值算出目标值并写回（读-改-写整对加锁）。</summary>
	internal static bool TryApply(byte setBits, byte clearBits, bool ownUsbPulse = false, bool ownQuickSwitch = false)
	{
		lock (Gate)
		{
			if (!AcpiDriverEc.TryReadByte(Address, out byte current))
			{
				return false;
			}
			byte next = Compose(current, setBits, clearBits, ownUsbPulse, ownQuickSwitch);
			if (next == current)
			{
				return true;
			}
			return AcpiDriverEc.TryWriteByte(Address, next);
		}
	}

	/// <summary>当前值（诊断用）。</summary>
	internal static bool TryRead(out byte value) => AcpiDriverEc.TryReadByte(Address, out value);
}
