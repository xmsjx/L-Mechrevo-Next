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
		// 家族判定只有一处（FanChannel.FamilyOf），这里只做"家族 → 曲线后端"的映射：
		// 无界与 NUC/UMI 是**不同家族、不同写手**（无界 EC 表只收占空比；NUC/UMI 写整表），
		// 不能让两处各排一套 if，否则顺序一变就会把无界误落进整表写手。
		switch (FanChannel.FamilyOf(hw))
		{
			case FanChannel.Family.BaolongdaLocal:
				return FanCurveBackendKind.Baolongda;
			case FanChannel.Family.WujieTable:
				return FanCurveBackendKind.Wujie;
			case FanChannel.Family.NucUmiLocal:
				return FanCurveBackendKind.NucUmi;
			case FanChannel.Family.TongfangEc:
				// 家族已经判定"没有 GCU、走本机 EC" ⇒ 曲线就该写 EC 整表；
				// ⛔ 不能再拿实验开关当门槛，否则正常 EC 直连机型曲线会整条不下发。
				return FanCurveBackendKind.Ec;
			default:
				return GcuEcExperimental.LocalFanCurve ? FanCurveBackendKind.Ec : FanCurveBackendKind.Gcu;
		}
	}
}
