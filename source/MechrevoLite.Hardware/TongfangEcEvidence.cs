using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 同方 EC 布局的只读证据：<c>\\.\ACPIDriver</c> 能读到本家族寄存器的合理值。
/// <para>与当前哪块显卡在跑无关 —— 核显模式、dGPU 被停用或摘出设备树时同样成立，
/// 因此可以作为机型分流的正面判据，不依赖显卡枚举。</para>
/// <para>⛔ 全程只读：不写 EC / 注册表 / NVRAM，不改任何状态。</para>
/// </summary>
internal static class TongfangEcEvidence
{
	private const int ProjectIdRegister = 0x740;

	private const int BacklightWhiteRegister = 0x78C;

	private const int ChargeUpperRegister = 0x7B9;

	private const int ChargeLowerRegister = 0x7D0;

	private const int MinimumHits = 2;

	private static int _cached = -1;

	private static string _describe = "";

	private static readonly object Gate = new object();

	/// <summary>本机 EC 布局是否呈同方特征。</summary>
	internal static bool IsPresent
	{
		get
		{
			Evaluate();
			return _cached == 1;
		}
	}

	/// <summary>判据明细（写日志/诊断用）。</summary>
	internal static string Describe
	{
		get
		{
			Evaluate();
			return _describe;
		}
	}

	/// <summary>清缓存（诊断重采 / 自检时用）。</summary>
	internal static void Reset()
	{
		lock (Gate)
		{
			_cached = -1;
			_describe = "";
		}
	}

	/// <summary>
	/// 与显卡、NVRAM 都无关的环境证据：本家族的 EC 驱动设备 <c>ACPI\INOU0000</c> 与
	/// <c>ACPIDriver</c> 服务是否在册。纯粹的在线设备事实。
	/// </summary>
	private static int EnvironmentPoints()
	{
		int points = 0;
		try
		{
			using (Microsoft.Win32.RegistryKey? dev = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\ACPI\\INOU0000"))
			{
				if (dev != null)
				{
					points++;
				}
			}
		}
		catch
		{
		}
		try
		{
			using (Microsoft.Win32.RegistryKey? svc = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\ACPIDriver"))
			{
				if (svc != null)
				{
					points++;
				}
			}
		}
		catch
		{
		}
		return points;
	}

	private static void Evaluate()
	{
		lock (Gate)
		{
			if (_cached >= 0)
			{
				return;
			}
			int hits = 0;
			bool okWhite = AcpiDriverEc.TryReadByte(BacklightWhiteRegister, out byte white);
			bool whitePlausible = okWhite && white != 0xFF;
			if (whitePlausible)
			{
				hits++;
			}
			bool okUpper = AcpiDriverEc.TryReadByte(ChargeUpperRegister, out byte upper);
			bool upperPlausible = okUpper && upper != 0xFF && (upper & 0x7F) <= 100;
			if (upperPlausible)
			{
				hits++;
			}
			bool okLower = AcpiDriverEc.TryReadByte(ChargeLowerRegister, out byte lower);
			bool lowerPlausible = okLower && lower != 0xFF && lower <= 100;
			if (lowerPlausible)
			{
				hits++;
			}
			bool okProject = AcpiDriverEc.TryReadByte(ProjectIdRegister, out byte project);
			bool projectPlausible = okProject && project != 0xFF && project != 0x00;
			if (projectPlausible)
			{
				hits++;
			}
			int env = EnvironmentPoints();
			_cached = ((hits >= MinimumHits || (hits >= 1 && env >= 1) || env >= 2) ? 1 : 0);
			_describe = string.Concat(
				"命中 ", hits.ToString(), "/4", " · 环境证据 ", env.ToString(), "/2",
				"（0x78C=", okWhite ? white.ToString("X2") : "读不到",
				" · 0x7B9=", okUpper ? upper.ToString("X2") : "读不到",
				" · 0x7D0=", okLower ? lower.ToString("X2") : "读不到",
				" · 0x740=", okProject ? project.ToString("X2") : "读不到", "）");
			Logger.WriteError("[同方EC] 布局证据：" + _describe + " ⇒ " + (_cached == 1 ? "成立" : "不成立（只读，未写任何寄存器）"));
		}
	}
}
