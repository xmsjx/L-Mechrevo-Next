using System;
using System.Drawing;

namespace MechrevoLite.Hardware;

/// <summary>
/// A 面 EC Logo 下发（与灯带同一路）：颜色 0x769-0x76B、效果与亮度档 0x7AB、提交位 0x78C、触发位 0x767。
/// <para>每个字节都按位读-改-写，只动自己那几位；任一字节读不到就整条不写。
/// 0x7AB 只动 bit0（使能）与 bit7-5（亮度档），bit1-4 是其它通道的状态位，一律保持原值。</para>
/// </summary>
internal static class EcLogoLight
{
	// 三个地址可被声明表覆盖（channelProfiles 里的 logoLight.params），没声明就用默认值。
	internal static int ModeIndexAddress => DeclarationRuntime.RegisterAddress("logoLight", "modeIndex", 0x7AB);
	internal static int BacklightWhiteAddress => DeclarationRuntime.RegisterAddress("logoLight", "white", 0x78C);
	internal static int RgbControlAddress => DeclarationRuntime.RegisterAddress("logoLight", "rgbControl", 0x7C5);
	internal const int RgbRedAddress = 0x769;
	internal const int RgbGreenAddress = 0x76A;
	internal const int RgbBlueAddress = 0x76B;

	/// <summary>0x78C bit4 = 提交；bit1 = 电源轨（0 = 使能）；bit0 = 单色白键盘。</summary>
	internal const byte CommitBit = 0x10;
	internal const byte PowerRailBit = 0x02;

	/// <summary>0x7AB bit7-5 亮度档：1..4 对应 32/64/96/128。</summary>
	private static readonly byte[] BrightnessMap = { 0, 32, 64, 96, 128 };

	/// <summary>呼吸只有 7 个固定色，按最近色选码（1 白 2 红 3 绿 4 蓝 5 黄 6 紫 7 青）。</summary>
	private static readonly (byte Code, int R, int G, int B)[] Palette =
	{
		(1, 255, 255, 255),
		(2, 255, 0, 0),
		(3, 0, 255, 0),
		(4, 0, 0, 255),
		(5, 255, 255, 0),
		(6, 136, 0, 255),
		(7, 0, 255, 255),
	};

	/// <summary>一条写操作的最终目标值（纯计算，不碰硬件；便于核对）。</summary>
	internal readonly struct Plan
	{
		internal Plan(byte modeIndex, byte backlightWhite, byte rgbControl, int red, int green, int blue, byte triggerSet, byte triggerClear)
		{
			ModeIndex = modeIndex;
			BacklightWhite = backlightWhite;
			RgbControl = rgbControl;
			Red = red;
			Green = green;
			Blue = blue;
			TriggerSet = triggerSet;
			TriggerClear = triggerClear;
		}

		internal byte ModeIndex { get; }
		internal byte BacklightWhite { get; }
		internal byte RgbControl { get; }
		internal int Red { get; }
		internal int Green { get; }
		internal int Blue { get; }
		internal byte TriggerSet { get; }
		internal byte TriggerClear { get; }

		public override string ToString()
		{
			return "0x7AB=" + ModeIndex.ToString("X2") + " 0x78C=" + BacklightWhite.ToString("X2")
				+ " 0x7C5=" + RgbControl.ToString("X2")
				+ " rgb=(" + Red + "," + Green + "," + Blue + ") 0x767+=" + TriggerSet.ToString("X2") + " -=" + TriggerClear.ToString("X2");
		}
	}

	/// <summary>按当前寄存器值与请求算出要写的字节，纯计算。</summary>
	internal static Plan Compose(byte mode, byte white, byte rgbControl, string effect, bool on, int level, int colorArgb)
	{
		int brightness = Math.Clamp(level, 0, 4);
		if (!on || brightness <= 0)
		{
			return new Plan(
				(byte)((mode & 0x1F) | 0x01),
				(byte)(white | CommitBit),
				(byte)(rgbControl & 0xF8),
				0,
				0,
				0,
				(byte)(EcTriggerByte.Logo | EcTriggerByte.Latch),
				EcTriggerByte.Rainbow);
		}

		byte committed = (byte)((white & ~PowerRailBit) | CommitBit);
		byte targetMode = (byte)((mode & 0x1F) | BrightnessMap[brightness] | 0x01);
		int style = StyleOf(effect);
		if (style == 2)
		{
			byte code = NearestPalette(colorArgb);
			return new Plan(targetMode, committed, (byte)((rgbControl & 0xF8) | code), 0, 0, 0,
				(byte)(EcTriggerByte.Logo | EcTriggerByte.Latch), EcTriggerByte.Rainbow);
		}
		if (style == 0)
		{
			return new Plan(targetMode, committed, (byte)(rgbControl & 0xF8), 0, 0, 0,
				(byte)(EcTriggerByte.Logo | EcTriggerByte.Latch | EcTriggerByte.Rainbow), 0);
		}

		Color color = Color.FromArgb(colorArgb);
		return new Plan(targetMode, committed, (byte)(rgbControl & 0xF8),
			color.R * 50 / 255, color.G * 50 / 255, color.B * 50 / 255,
			(byte)(EcTriggerByte.Logo | EcTriggerByte.Latch), EcTriggerByte.Rainbow);
	}

	internal static bool TryApply(string effect, bool on, int brightness, int speed, int colorArgb, out string detail, bool dryRun = false)
	{
		if (DeclarationRuntime.FeatureBlocked("logoLight", "ec", "hid", "mqtt"))
		{
			detail = "声明表判定本机无 Logo 灯，未写任何寄存器";
			return false;
		}
		if (!AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte mode)
			|| !AcpiDriverEc.TryReadByte(BacklightWhiteAddress, out byte white)
			|| !AcpiDriverEc.TryReadByte(RgbControlAddress, out byte rgbControl))
		{
			detail = "EC Logo 寄存器读不到（0x7AB/0x78C/0x7C5）";
			Logger.WriteLine("[ec-logo] " + detail);
			return false;
		}

		Plan plan = Compose(mode, white, rgbControl, effect, on, brightness, colorArgb);
		if (dryRun)
		{
			detail = "EC Logo 试运行（未写入）：" + plan;
			Logger.WriteLine("[ec-logo] " + detail);
			return true;
		}

		// 0x78C 的 bit4 是提交标记，必须最后写；0x767 触发要在亮度档之前，先触发后写亮度会让新亮度等下一次刷新才生效。
		bool ok;
		if (!on || brightness <= 0)
		{
			ok = AcpiDriverEc.TryWriteByte(ModeIndexAddress, plan.ModeIndex)
				& AcpiDriverEc.TryWriteByte(BacklightWhiteAddress, plan.BacklightWhite)
				& AcpiDriverEc.TryWriteByte(RgbRedAddress, 0)
				& AcpiDriverEc.TryWriteByte(RgbGreenAddress, 0)
				& AcpiDriverEc.TryWriteByte(RgbBlueAddress, 0)
				& EcTriggerByte.TryApply(plan.TriggerSet, plan.TriggerClear)
				& AcpiDriverEc.TryWriteByte(RgbControlAddress, plan.RgbControl);
		}
		else
		{
			ok = AcpiDriverEc.TryWriteByte(BacklightWhiteAddress, plan.BacklightWhite)
				& AcpiDriverEc.TryWriteByte(RgbControlAddress, plan.RgbControl)
				& AcpiDriverEc.TryWriteByte(RgbRedAddress, Math.Clamp(plan.Red, 0, 50))
				& AcpiDriverEc.TryWriteByte(RgbGreenAddress, Math.Clamp(plan.Green, 0, 50))
				& AcpiDriverEc.TryWriteByte(RgbBlueAddress, Math.Clamp(plan.Blue, 0, 50))
				& EcTriggerByte.TryApply(plan.TriggerSet, plan.TriggerClear)
				& AcpiDriverEc.TryWriteByte(ModeIndexAddress, plan.ModeIndex)
				& AcpiDriverEc.TryWriteByte(BacklightWhiteAddress, plan.BacklightWhite);
		}

		string mismatch = Verify(plan, on && brightness > 0);
		if (mismatch.Length > 0)
		{
			ok = false;
		}
		detail = "EC Logo " + (on && brightness > 0 ? "ON" : "OFF") + " ⇒ " + (ok ? "OK" : "FAIL")
			+ (mismatch.Length > 0 ? " · " + mismatch : string.Empty) + "（" + plan + "）";
		Logger.WriteLine("[ec-logo] " + detail);
		return ok;
	}

	/// <summary>
	/// 回读核对：只比会保留的位。0x7AB 比 bit5-7 亮度档与 bit0 模式位；
	/// 0x78C 比 bit1 电源轨与 bit0 单色位，bit4 是固件自己会清的更新标记，不参与比较。
	/// 回读不符说明 EC 没接受这次写入，界面不能报成功。
	/// </summary>
	private static string Verify(Plan plan, bool expectOn)
	{
		if (!AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte modeAfter)
			|| !AcpiDriverEc.TryReadByte(BacklightWhiteAddress, out byte whiteAfter))
		{
			return "回读失败";
		}
		bool modeOk = (modeAfter & 0xE1) == (plan.ModeIndex & 0xE1);
		bool whiteOk = (whiteAfter & 0x03) == (plan.BacklightWhite & 0x03);
		if (modeOk && whiteOk)
		{
			return string.Empty;
		}
		return "回读不符（期望 0x7AB=" + (plan.ModeIndex & 0xE1).ToString("X2")
			+ " 0x78C=" + (plan.BacklightWhite & 0x03).ToString("X2")
			+ "，实际 0x7AB=" + modeAfter.ToString("X2") + " 0x78C=" + whiteAfter.ToString("X2")
			+ (expectOn ? "）" : "）");
	}

	/// <summary>0 = 硬件彩虹，1 = 固定色，2 = 7 色呼吸。</summary>
	private static int StyleOf(string effect)
	{
		if (string.Equals(effect, "Breathing", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (string.Equals(effect, "Rainbow", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		return 1;
	}

	private static byte NearestPalette(int colorArgb)
	{
		Color color = Color.FromArgb(colorArgb);
		byte best = 1;
		int bestDistance = int.MaxValue;
		foreach ((byte code, int r, int g, int b) in Palette)
		{
			int dr = color.R - r;
			int dg = color.G - g;
			int db = color.B - b;
			int distance = dr * dr + dg * dg + db * db;
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = code;
			}
		}
		return best;
	}
}
