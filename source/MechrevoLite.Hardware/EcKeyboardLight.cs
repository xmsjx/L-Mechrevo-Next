using System;
using System.Runtime.InteropServices;

namespace MechrevoLite.Hardware;

/// <summary>
/// 2026-10-22（无界白键盘 · EC 直控）：白色背光键盘在 EC 的 H2RAM 0x78C 上。
/// <para>实测（Fn 亮度键循环 0x01 → 0x21 → 0x41 → 0x01，视觉为“关/暗/亮”）：</para>
/// <list type="bullet">
/// <item>bit5~7 = 亮度档：<c>0=关、1=暗、2=亮</c>（只有两档亮度）；</item>
/// <item>bit4 = 写入时置 1 的“亮度更新”标记（写完由固件自清）；</item>
/// <item>低 5 位保留（bit0 常为 1）。</item>
/// </list>
/// <para>⚠️ 只走 <c>\\.\ACPIDriver</c>（H2RAM 0x700~0x7FF），读-改-写保留其它位；不碰别的寄存器。</para>
/// <para>⚠️ 仅当机型是「无界 + GCU 不报键盘通道（solution=UNKNOWN）」时才由上层调用。</para>
/// </summary>
internal static class EcKeyboardLight
{
	/// <summary>键盘背光寄存器（H2RAM）。</summary>
	internal const int Register = 0x78C;

	private const uint IoctlEcRead = 2621482120u;

	private const uint IoctlEcWrite = 2621482124u;

	private const string DevicePath = "\\\\.\\ACPIDriver";

	/// <summary>bit1 = 电源禁用：1=关、0=开。关灯只置这一位，其它位保留。</summary>
	private const byte PowerDisableBit = 0x02;

	/// <summary>bit0 = 灯使能：开灯时必须置 1。</summary>
	private const byte EnableBit = 0x01;

	/// <summary>bit4 = 软件接管位：不置这一位时，固件忽略外部写进来的亮度/开关。</summary>
	private const byte SoftwareTakeoverBit = 0x10;

	/// <summary>亮度在 bit5~6：1=暗（0x20）、2=亮（0x40）。</summary>
	private const int BrightnessShift = 5;

	/// <summary>触发字节：写完 0x78C 后必须给它一个 0x20 脉冲，固件才真正刷新背光。</summary>
	private const int TriggerRegister = 0x767;

	private const byte TriggerPulse = 0x20;

	/// <summary>模式/灯效索引寄存器（部分机型用它承载背光档位）。</summary>
	private const int ModeIndexRegister = 0x7AB;

	internal const int MaxBrightnessLevel = 2;

	/// <summary>对外暴露的亮度档数（白光键盘两档）。</summary>
	internal static int DeclaredLevels => MaxBrightnessLevel;

	/// <summary>声明表要求隐藏亮度调节时返回 true（该机型亮度写不动）。</summary>
	internal static bool BrightnessHidden => DeclarationRuntime.Flag("keyboardBacklight", "hideBrightness");

	/// <summary>声明为“只有白光键盘”的机型：亮度只有 1~2 档，不能按单区 RGB 的那套（0~4 档 + 颜色）重放。</summary>
	internal static bool IsWhiteOnly => DeclarationRuntime.Flag("keyboardBacklight", "whiteOnly");

	/// <summary>写入后是否给触发字节发脉冲（声明 latch=0 表示该机型不发）。</summary>
	internal static bool LatchEnabled => !string.Equals(
		(DeclarationRuntime.Param("keyboardBacklight", "latch") ?? "").Trim(), "0", StringComparison.Ordinal);

	/// <summary>亮度键由固件自己写 EC（声明 hotkey=0）：软件只读档位显示，不再回写。</summary>
	internal static bool HotkeyByFirmware => string.Equals(
		(DeclarationRuntime.Param("keyboardBacklight", "hotkey") ?? "").Trim(), "0", StringComparison.Ordinal);

	private static readonly object Gate = new object();

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(nint handle, uint code, nint inBuffer, int inSize, nint outBuffer, int outSize, out int returned, nint overlapped);

	/// <summary>通道是否可用（能按读写方式打开 \\.\ACPIDriver）。只读探测，不写寄存器。</summary>
	internal static bool IsChannelAvailable()
	{
		try
		{
			nint handle = CreateFile(DevicePath, 3221225472u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (handle == new IntPtr(-1))
			{
				return false;
			}
			CloseHandle(handle);
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>只读当前寄存器值；读不到返回 -1。</summary>
	internal static int ReadRegister()
	{
		lock (Gate)
		{
			nint handle = CreateFile(DevicePath, 3221225472u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (handle == new IntPtr(-1))
			{
				return -1;
			}
			try
			{
				return Read(handle, Register);
			}
			finally
			{
				CloseHandle(handle);
			}
		}
	}

	/// <summary>读当前状态（开/关 + 亮度档 0~2）。</summary>
	internal static bool TryGet(out bool on, out int brightnessLevel)
	{
		on = false;
		brightnessLevel = -1;
		int value = ReadRegister();
		if (value < 0)
		{
			return false;
		}
		brightnessLevel = (value >> BrightnessShift) & 0x07;
		// bit1=1 表示电源禁用；亮度位不能单独当开关。
		on = (value & PowerDisableBit) == 0 && brightnessLevel > 0;
		return true;
	}

	/// <summary>开/关灯：关=亮度码 0；开=恢复到上次亮度（原来是关则用最亮档）。</summary>
	internal static bool TrySetPower(bool on)
	{
		return TryApply(on, null);
	}

	/// <summary>设亮度档：0=关、1=暗、2=亮（实测只有两档亮度）。</summary>
	internal static bool TrySetBrightness(int level0to2)
	{
		if (level0to2 < 0 || level0to2 > MaxBrightnessLevel)
		{
			return false;
		}
		return TryApply(level0to2 > 0, level0to2);
	}

	/// <summary>同时设开/关与亮度（读-改-写）；只改 bit4~7。</summary>
	internal static bool TryApply(bool? on, int? brightnessLevel0to2)
	{
		if (brightnessLevel0to2.HasValue && (brightnessLevel0to2.Value < 0 || brightnessLevel0to2.Value > MaxBrightnessLevel))
		{
			return false;
		}
		lock (Gate)
		{
			nint handle = CreateFile(DevicePath, 3221225472u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (handle == new IntPtr(-1))
			{
				Logger.WriteError("[kbd-ec] 键盘背光：\\\\.\\ACPIDriver 打不开（UWACPIDriver 未装/未运行或权限不足）");
				return false;
			}
			try
			{
				int before = Read(handle, Register);
				if (before < 0)
				{
					Logger.WriteError("[kbd-ec] 键盘背光：读 0x78C 失败（EC 通道不通）");
					return false;
				}
				int currentLevel = (before >> BrightnessShift) & 0x03;
				int targetLevel = currentLevel;
				if (on.HasValue)
				{
					if (on.Value)
					{
						targetLevel = (currentLevel > 0) ? currentLevel : MaxBrightnessLevel;
					}
					else
					{
						targetLevel = 0;
					}
				}
				if (brightnessLevel0to2.HasValue)
				{
					targetLevel = brightnessLevel0to2.Value;
				}
				if (targetLevel > MaxBrightnessLevel)
				{
					targetLevel = MaxBrightnessLevel;
				}
				// 低 4 位作基底并清 bit1；开灯 = 基底 + bit0 使能 + 亮度位（1→0x20、2→0x40），
				// 关灯 = 基底只置 bit1。写完再给触发字节一个 0x20 脉冲，否则固件不刷新。
				// 写法选择（config.json 的 white_kbd_scheme，留空 = 默认位域写法）：
				//   wujie   = 档位写 0x7AB 的 bit5~7，0x78C 只做提交
				//   percent = 0x78C 直接写亮度百分比
				string scheme = (MechrevoLite.AppConfig.GetString("white_kbd_scheme") ?? "").Trim().ToLowerInvariant();
				if (scheme == "wujie" || scheme == "percent")
				{
					int beforeIndex = Read(handle, ModeIndexRegister);
					bool wrote;
					if (scheme == "percent")
					{
						int pct = ((targetLevel <= 0) ? 0 : ((targetLevel == 1) ? 33 : 100));
						wrote = Write(handle, Register, (byte)pct);
					}
					else
					{
						int wantIndex = ((targetLevel <= 0) ? 0 : (((targetLevel & 0x07) << 5) | 0x01));
						int indexValue = ((beforeIndex < 0) ? wantIndex : ((beforeIndex & 0x1F) | (wantIndex & 0xE1)));
						wrote = Write(handle, ModeIndexRegister, (byte)indexValue);
						wrote &= Write(handle, Register, (byte)((before & 0x0F) | ((targetLevel > 0) ? SoftwareTakeoverBit : (SoftwareTakeoverBit | PowerDisableBit))));
					}
					PulseTrigger(handle);
					int afterRaw = Read(handle, Register);
					Logger.WriteError("[kbd-ec] 键盘背光（" + scheme + "）：0x78C 0x" + before.ToString("X2") + " -> 0x" + afterRaw.ToString("X2")
						+ "（" + DescribeLevel(targetLevel) + " · 0x7AB 0x" + ((beforeIndex < 0) ? "?" : beforeIndex.ToString("X2")) + "）"
						+ (wrote ? " 写=成功" : " 写=失败"));
					return wrote;
				}
				// 机型声明 `keyboardBacklight.params.scheme` = "pf"：
				// 亮度 = 档位位（从 bit5 起）+ bit4 提交位，电源只动 bit1，bit0 及其它低位保持原样。
				// 只写档位位不置 bit4，固件不会把新档位应用到灯上（回读一致但灯不变）。
				string declaredScheme = (DeclarationRuntime.Param("keyboardBacklight", "scheme") ?? "").Trim().ToLowerInvariant();
				if (declaredScheme == "pf")
				{
					bool pfWantOn = targetLevel > 0;
					// 位运算同原厂控制台：置提交位、清档位位、再写档位。
					// 关灯不置 bit1：本机固件自己关灯写 0x01（只清档位位），保持一条写法。
					int pfValue = ((before | SoftwareTakeoverBit) & 0x1F & ~PowerDisableBit)
						| ((targetLevel & 0x03) << BrightnessShift);
					bool pfWritten = Write(handle, Register, (byte)pfValue);
					if (LatchEnabled)
					{
						PulseTrigger(handle);
					}
					System.Threading.Thread.Sleep(10);
					int pfAfter = Read(handle, Register);
					int pfLevel = (pfAfter < 0) ? (-1) : ((pfAfter >> BrightnessShift) & 0x03);
					bool pfPowerOn = pfAfter >= 0 && (pfAfter & PowerDisableBit) == 0;
					bool pfOk = pfWritten && (pfWantOn ? (pfLevel == targetLevel && pfPowerOn) : !pfPowerOn);
					Logger.WriteError("[kbd-ec] 键盘背光：0x78C 0x" + before.ToString("X2") + " -> 0x" + pfValue.ToString("X2")
						+ "（" + DescribeLevel(targetLevel) + " · pf）"
						+ " ⇒ " + (pfOk ? "回读一致" : ("未确认（写=" + (pfWritten ? "成功" : "失败") + " · 回读档=" + (pfLevel < 0 ? "读不到" : pfLevel.ToString()) + "）")));
					if (targetLevel == 0)
					{
						NucKeyboardLighting.StopCarousel();
					}
					return pfOk;
				}
				// 与原厂控制台逐位一致：读取-改-写，bit4 恒置 1，档位在 bit5~7（1→0x20、2→0x40、3→0x60、4→0x80），
				// 电源只动 bit1（1=关），bit0 与其它低位保持原样；写完后不发 0x767 脉冲。
				int value;
				if (targetLevel > 0)
				{
					value = (before & ~PowerDisableBit & ~0xE0) | SoftwareTakeoverBit | ((targetLevel & 0x07) << BrightnessShift);
				}
				else
				{
					value = before | PowerDisableBit;
				}
				bool written = Write(handle, Register, (byte)value);
				System.Threading.Thread.Sleep(10);
				int after = Read(handle, Register);
				int afterLevel = (after < 0) ? (-1) : ((after >> BrightnessShift) & 0x03);
				bool afterPowerOn = after >= 0 && (after & PowerDisableBit) == 0;
				bool wantPowerOn = targetLevel > 0;
				// 开灯要“档位对得上 + 电源未禁”；关灯只看“电源禁用位是否置上”，亮度位保留不算失败。
				bool ok = written && (wantPowerOn ? (afterLevel == targetLevel && afterPowerOn) : !afterPowerOn);
				Logger.WriteError("[kbd-ec] 键盘背光：0x78C 0x" + before.ToString("X2") + " -> 0x" + value.ToString("X2")
					+ "（" + DescribeLevel(targetLevel) + "）"
					+ " ⇒ " + (ok ? "回读一致" : ("未确认（写=" + (written ? "成功" : "失败") + " · 回读档=" + (afterLevel < 0 ? "读不到" : afterLevel.ToString()) + "）")));
				if (targetLevel == 0 && ok)
				{
					// 关灯成功 ⇒ 让 EC 单区轮播停下，否则软件循环会继续写 RGB/锁存把灯点回来。
					NucKeyboardLighting.StopCarousel();
				}
				return ok;
			}
			finally
			{
				CloseHandle(handle);
			}
		}
	}

	/// <summary>给触发字节一个 0x20 脉冲（写一次即由固件自清）。</summary>
	private static void PulseTrigger(nint handle)
	{
		int current = Read(handle, TriggerRegister);
		if (current < 0)
		{
			return;
		}
		int target = current | TriggerPulse;
		bool written = Write(handle, TriggerRegister, (byte)target);
		// 固件清这一位很慢（实测 0.17~0.65 秒），只看 10ms 会把正常接收误判成被拒，
		// 所以先看 10ms，再看 300ms，仍没清才说没被吃掉。
		System.Threading.Thread.Sleep(10);
		int after = Read(handle, TriggerRegister);
		if (after >= 0 && (after & TriggerPulse) != 0)
		{
			System.Threading.Thread.Sleep(300);
			after = Read(handle, TriggerRegister);
		}
		Logger.WriteError("[kbd-ec] 0x767 脉冲 0x" + current.ToString("X2") + " -> 0x" + target.ToString("X2")
			+ " 写入=" + written + " · 回读=" + (after < 0 ? "读不到" : "0x" + after.ToString("X2"))
			+ (after >= 0 && (after & TriggerPulse) == 0 ? "（固件已清 · 已接收）" : "（bit5 未清 · 未被吃掉）"));
	}

	private static string DescribeLevel(int level)
	{
		return level switch
		{
			0 => "关",
			1 => "暗",
			2 => "亮",
			_ => level.ToString()
		};
	}

	private static int Read(nint handle, int address)
	{
		nint inBuf = Marshal.AllocHGlobal(4);
		nint outBuf = Marshal.AllocHGlobal(16);
		try
		{
			Marshal.WriteInt32(inBuf, 0, address);
			int returned;
			return (DeviceIoControl(handle, IoctlEcRead, inBuf, 4, outBuf, 16, out returned, IntPtr.Zero) && returned > 0) ? Marshal.ReadByte(outBuf) : (-1);
		}
		finally
		{
			Marshal.FreeHGlobal(inBuf);
			Marshal.FreeHGlobal(outBuf);
		}
	}

	private static bool Write(nint handle, int address, byte value)
	{
		nint inBuf = Marshal.AllocHGlobal(8);
		nint outBuf = Marshal.AllocHGlobal(16);
		try
		{
			Marshal.WriteInt32(inBuf, 0, address);
			Marshal.WriteByte(inBuf, 4, value);
			int returned;
			return DeviceIoControl(handle, IoctlEcWrite, inBuf, 5, outBuf, 16, out returned, IntPtr.Zero);
		}
		finally
		{
			Marshal.FreeHGlobal(inBuf);
			Marshal.FreeHGlobal(outBuf);
		}
	}
}
