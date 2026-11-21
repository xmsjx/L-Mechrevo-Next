using MechrevoLite.Hardware;
using MechrevoLite.Hardware.FanCurve;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>曲线后端种类（界面口径，不引硬件类型）。</summary>
internal enum FanCurveMold
{
	None = 0,
	Gcu,
	Baolongda,
	NucUmi,
	Wujie,
	Ec
}

/// <summary>曲线窗口需要的机型事实与后端种类：由这里统一取值，界面不碰硬件类型。</summary>
internal static class FanCurveTarget
{
	internal static MechrevoHw? Hw => WpfBackend.HwOrProgram;

	internal static bool Available => Hw != null;

	internal static FanCurveBackendKind Kind => FanCurveBackendFactory.Resolve(Hw);

	internal static FanCurveMold Mold => Kind switch
	{
		FanCurveBackendKind.Gcu => FanCurveMold.Gcu,
		FanCurveBackendKind.Baolongda => FanCurveMold.Baolongda,
		FanCurveBackendKind.NucUmi => FanCurveMold.NucUmi,
		FanCurveBackendKind.Wujie => FanCurveMold.Wujie,
		FanCurveBackendKind.Ec => FanCurveMold.Ec,
		_ => FanCurveMold.None
	};

	internal static bool IsWujieSmuApu => Hw?.WujieSmuApu == true;

	internal static bool IsGcuConnected => Hw?.IsGcuConnected == true;

	/// <summary>该机是否有任何可用的曲线后端（固件/EC/本机闭环）。</summary>
	internal static bool LegacyFanCurve => Hw != null
		&& (Hw.IsGcuConnected || Hw.UsesBaolongda || Hw.WujieSmuApu || Hw.IsNucUmiFamily
			|| WpfBackend.LocalFanCurve());
}
