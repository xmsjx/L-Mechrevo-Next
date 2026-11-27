using System;

namespace MechrevoLite.Mode;

public static class PowerModeMapping
{
	/// <summary>
	/// 本机是否声明了「静音=工作模式节能 / 平衡=工作模式标准 / 狂暴=渲染模式」这套三档映射。
	/// <para>未声明 ⇒ false，全部换算回到原有行为；声明位只在机型档案里写，代码不猜。</para>
	/// </summary>
	public static bool UsesOfficePresetMapping => MechrevoLite.Hardware.DeclarationRuntime.Flag("officePreset", "enabled");

	/// <summary>
	/// 该档位对应的工作模式预设：静音(2)=eco、平衡(0)=std，其余档位无预设。
	/// <para>只在声明了本映射时返回；否则恒 null（调用方不会记录/不会下发）。</para>
	/// </summary>
	public static string? PresetForVisualMode(int visualMode)
	{
		if (!UsesOfficePresetMapping)
		{
			return null;
		}
		switch (visualMode)
		{
		case 2:
			return PerfModeStore.OfficePresetEco;
		case 0:
			return PerfModeStore.OfficePresetStd;
		default:
			return null;
		}
	}

	/// <summary>
	/// 带预设的视觉档 → 控制台模式换算：声明本映射时平衡/静音都落在办公档（差别在预设参数），狂暴落在渲染档；
	/// 未声明时与 <see cref="FromVisualMode" /> 逐位一致。
	/// </summary>
	public static ConsoleOperatingMode FromVisualModeWithPreset(int visualMode)
	{
		if (!UsesOfficePresetMapping)
		{
			return FromVisualMode(visualMode);
		}
		return visualMode switch
		{
			0 => ConsoleOperatingMode.Office, 
			1 => ConsoleOperatingMode.Gaming, 
			2 => ConsoleOperatingMode.Office, 
			3 => ConsoleOperatingMode.Customize, 
			_ => throw new ArgumentOutOfRangeException("visualMode", visualMode, "unknown visual mode"), 
		};
	}

	public static ConsoleOperatingMode FromVisualMode(int visualMode)
	{
		return visualMode switch
		{
			0 => ConsoleOperatingMode.Gaming, 
			1 => ConsoleOperatingMode.Turbo, 
			2 => ConsoleOperatingMode.Office, 
			3 => ConsoleOperatingMode.Customize, 
			_ => throw new ArgumentOutOfRangeException("visualMode", visualMode, "unknown visual mode"), 
		};
	}

	public static int ToVisualMode(ConsoleOperatingMode mode)
	{
		return mode switch
		{
			ConsoleOperatingMode.Office => 2, 
			ConsoleOperatingMode.Gaming => 0, 
			ConsoleOperatingMode.Turbo => 1, 
			ConsoleOperatingMode.Customize => 3, 
			_ => throw new ArgumentOutOfRangeException("mode", mode, "unknown console mode"), 
		};
	}

	public static bool TryToVendor(ConsoleOperatingMode mode, out VendorSysPowerMode vendor)
	{
		switch (mode)
		{
		case ConsoleOperatingMode.Office:
			vendor = VendorSysPowerMode.Balanced;
			return true;
		case ConsoleOperatingMode.Gaming:
			vendor = VendorSysPowerMode.Performance;
			return true;
		case ConsoleOperatingMode.Turbo:
			vendor = VendorSysPowerMode.Benchmark;
			return true;
		default:
			vendor = (VendorSysPowerMode)0;
			return false;
		}
	}
}
