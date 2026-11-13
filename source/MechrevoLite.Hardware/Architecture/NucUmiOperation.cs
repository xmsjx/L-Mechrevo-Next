namespace MechrevoLite.Hardware.Architecture;

/// <summary>NUC/UMI 本地目标实现；只做分类，不代表协议可用。</summary>
internal enum NucUmiOperation
{
	FanCurve = 0,
	FanBoost,
	ChargeLimit,
	PerformanceMode,
	KeyboardLight,
	Lightbar,
	LogoLight,
	HingeLight,
	DisplayEffect,
	GpuMode,
	PowerLimit
}

internal enum NucUmiOperationSupport
{
	Unknown = 0,
	Supported,
	Unsupported
}
