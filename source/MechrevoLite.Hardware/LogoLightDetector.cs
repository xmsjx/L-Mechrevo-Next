using System;

namespace MechrevoLite.Hardware;

/// <summary>Logo 灯的硬件形态。</summary>
internal enum LogoLightKind
{
	/// <summary>判不出来（没有 7001 设备 / 能力字节读不到）。调用方回退旧判据。</summary>
	Unknown = 0,

	/// <summary>字母形（USB lettering 流灯 Logo）。</summary>
	Lettering = 1,

	/// <summary>图标形（原 Logo 通道）。</summary>
	Icon = 2
}

/// <summary>
/// Logo 灯光形态判定：按 ITE 0xA2 能力字节里的 lettering 位区分"字母形 / 图标形"。
/// <para>判据只在拿到能力字节时生效；拿不到就返回 <see cref="LogoLightKind.Unknown"/>，
/// 由调用方回退到原有判据，保证探测失败时行为与改前一致。</para>
/// </summary>
internal static class LogoLightDetector
{
	internal static LogoLightKind Detect(MechrevoHw? hw)
	{
		try
		{
			IteLightingCapability? capability = hw?.IteCapability;
			if (capability == null)
			{
				Logger.WriteLineIfChanged("logo-kind", "[logo] 形态判定：无 7001 能力字节 ⇒ 未知（沿用旧判据）");
				return LogoLightKind.Unknown;
			}
			LogoLightKind kind = capability.Value.LetteringLogo ? LogoLightKind.Lettering : LogoLightKind.Icon;
			Logger.WriteLineIfChanged("logo-kind", "[logo] 形态判定：" + Describe(kind) + " · " + capability.Value.Describe());
			return kind;
		}
		catch (Exception ex)
		{
			Logger.WriteLineIfChanged("logo-kind", "[logo] 形态判定失败：" + ex.GetType().Name + " ⇒ 未知（沿用旧判据）");
			return LogoLightKind.Unknown;
		}
	}

	internal static string Describe(LogoLightKind kind)
	{
		return kind switch
		{
			LogoLightKind.Lettering => "字母形（lettering）",
			LogoLightKind.Icon => "图标形（原通道）",
			_ => "未知"
		};
	}
}
