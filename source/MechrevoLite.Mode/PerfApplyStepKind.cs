namespace MechrevoLite.Mode;

public enum PerfApplyStepKind
{
	SwitchBuiltIn,
	SwitchTurboSubMode,
	SwitchFirmwareSlot,
	WriteFirmwareField,
	WriteFanCurve,
	ApplyFanBoost,
	ApplyPowerPlan,
	ApplyCpuBoost,
	ApplyPowerOverlay,
	ApplyRefreshRate,
	// <summary>处理器最大频率（MHz）：Number = 插电侧，Curve[0] = 电池侧；-1 表示该侧不动</summary>
	ApplyProcessorMaxFreq,
	ApplyGpuOverclock,
	// <summary>  显卡功耗上限（NVML SetPowerManagementLimit · 毫瓦换算在 NvmlHelper 内）</summary>
	ApplyGpuPowerLimit
}
