namespace MechrevoLite.Hardware.Architecture;

/// <summary>需要统一门控的功能标识。</summary>
internal enum FeatureId
{
	LogoLight = 0,
	Lightbar,
	KeyboardLight,
	HingeLight,
	SyncLight,
	LocalDimming,
	LcdOverdrive,
	DisplayEffect,
	ColorCalibration,
	FanCurve,
	FanBoost,
	ChargeLimit,
	PerformanceMode,
	RefreshRate,

    /// <summary>能否在独显/混合之间切换。</summary>
    GpuModeSwitch,

    /// <summary>免重启热切换。</summary>
    GpuHotSwap,

    /// <summary>仅集显输出。</summary>
    IgpuOnly,

    /// <summary>是否有可编辑的自定义性能档。</summary>
    PerformanceProfile,

    /// <summary>能否切换到涡轮/狂暴档。</summary>
    TurboMode,

    /// <summary>静音↔狂暴 三态支持结论。</summary>
    SilentTurbo,

    /// <summary>CPU 性能调优与风扇设置的统一入口（PL/温度墙/风扇设置）。</summary>
    PerfTuning
}
