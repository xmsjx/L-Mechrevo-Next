namespace MechrevoLite.Hardware;

/// <summary>
/// 入口与行的显示判据：只读声明表、硬件能力位与供电状态，界面只负责显示。
/// 换界面时从这里取结论，不会因为界面重写而丢。
/// </summary>
public static class FeatureVisibility
{
	/// <summary>自定义档的功耗设定值是否可写（声明表给出可写地址）。</summary>
	public static bool CustomPowerSettings
	{
		get
		{
			try
			{
				return PlCustomService.IsSupported;
			}
			catch
			{
				return false;
			}
		}
	}

	/// <summary>顶部模式卡片的「自定义」入口。</summary>
	public static bool CustomMode(MechrevoHw hw, bool ecModeActive, bool gcuConnected)
	{
		if (hw == null)
		{
			return false;
		}

		try
		{
			// 声明表给出了可写的功耗设定值。
			if (PlCustomService.IsSupported)
			{
				return true;
			}

			// 走模式表的机型、走本机 SMU 的核显机型、宝龙达口径，各自有自己的自定义档通道。
			if (hw.IsNucUmiFamily || hw.SmuCustomApu || hw.UsesBaolongda)
			{
				return true;
			}

			// 能力位：本机有任意一项可调就已经够开这个档。
			if (hw.Pl1Adjustable || hw.Pl2Adjustable || hw.Pl4Adjustable || hw.TccAdjustable || hw.GpuTgpAdjustable)
			{
				return true;
			}

			// EC 本地自定义档：看有没有可写的功耗通道，不看有没有独显。
			if (ecModeActive && !gcuConnected
				&& (PlCustomService.IsSupported || hw.DgpuGeneration != Gpu.DgpuGenerationKind.NoDgpu))
			{
				return true;
			}

			// 机主开了「电池下也允许调性能」时不禁用这个入口。
			return AppConfig.Is("perf_allow_on_battery");
		}
		catch
		{
			return false;
		}
	}

	/// <summary>自定义档里的 fPPT（PL4）行：只看本机有没有可写的 PL4 通道。</summary>
	public static bool Pl4Row(MechrevoHw hw)
	{
		if (hw == null)
		{
			return false;
		}

		try
		{
			// 只开放 PL1/PL2 的平台不出这一行。
			if (IsOnlyPl12Platform(hw))
			{
				return false;
			}

			// 能力位：本机能调 PL4。
			if (hw.Pl4Adjustable)
			{
				return true;
			}

			// 声明表给出了 PL4 地址。
			if (PlCustomService.HasPl4)
			{
				return true;
			}

			// EC 直连口径的功耗配置：同方 EC 的 PL4 是独立一路，与有没有独显无关。
			if (GcuEcExperimental.LocalPowerConfig
				&& !hw.UsesBaolongda
				&& !hw.IsNucUmiFamily
				&& !hw.SmuCustomApu)
			{
				return true;
			}

			return hw.IsGcuConnected && hw.IsTongfangFamily;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>显卡相关行（动态加速 / 基础 TGP / GPU 温度卡）：本机有独显才显示。</summary>
	public static bool DgpuRows(MechrevoHw hw)
	{
		if (hw == null)
		{
			return false;
		}

		try
		{
			if (IsOnlyPl12Platform(hw))
			{
				return false;
			}

			return hw.DgpuGeneration != Gpu.DgpuGenerationKind.NoDgpu;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>强冷（风扇强化）：看本机有没有这一路，和有没有独显无关。</summary>
	public static bool FanBoost(MechrevoHw hw)
	{
		if (hw == null)
		{
			return false;
		}

		try
		{
			if (IsOnlyPl12Platform(hw))
			{
				return false;
			}

			if (!DeclarationRuntime.FeatureBlocked("fanBoost", "ec"))
			{
				return true;
			}

			return hw.SupportsFanBoost;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>风扇卡片：强冷、风扇曲线、风扇分数各自成行，卡片本身只要有一路就显示。</summary>
	public static bool FanCard(MechrevoHw hw)
	{
		if (hw == null)
		{
			return false;
		}

		try
		{
			if (IsOnlyPl12Platform(hw))
			{
				return false;
			}

			if (!DeclarationRuntime.FeatureBlocked("fanCurve", "ec"))
			{
				return true;
			}
		}
		catch
		{
		}

		return FanBoost(hw) || hw.SupportsFanRespective;
	}

	// 只开 PL1/PL2 的平台（Intel 星耀本地口径）。
	private static bool IsOnlyPl12Platform(MechrevoHw hw)
	{
		try
		{
			return hw != null && !hw.IsNucUmiFamily && CpuVendor.IsIntel && XingyaoPmc2.IsXingyaoPlatform();
		}
		catch
		{
			return false;
		}
	}
}
