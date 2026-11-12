using MechrevoLite.Hardware;

namespace MechrevoLite.Hardware.FanCurve;

/// <summary>风扇曲线后端类型。</summary>
internal enum FanCurveBackendKind
{
	None = 0,
	Gcu,
	Baolongda,
	NucUmi,
	Wujie,
	Ec
}

/// <summary>按当前硬件事实选择风扇曲线后端；只做分类，不执行读写。</summary>
internal static class FanCurveBackendFactory
{
	internal static FanCurveBackendKind Resolve(MechrevoHw? hw)
	{
		if (hw == null)
		{
			return FanCurveBackendKind.None;
		}
		if (hw.UsesBaolongda)
		{
			return FanCurveBackendKind.Baolongda;
		}
		if (hw.IsNucUmiFamily)
		{
			return FanCurveBackendKind.NucUmi;
		}
		if (hw.WujieSmuApu)
		{
			return FanCurveBackendKind.Wujie;
		}
		if (hw.IsGcuConnected)
		{
			return FanCurveBackendKind.Gcu;
		}
		return GcuEcExperimental.Enabled ? FanCurveBackendKind.Ec : FanCurveBackendKind.None;
	}
}
