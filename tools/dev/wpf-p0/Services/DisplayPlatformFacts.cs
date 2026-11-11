using MechrevoLite;
using MechrevoLite.Display;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>显示组平台事实：只读 Win32/WMI，不代表后端能力。</summary>
internal readonly record struct DisplayPlatformFacts(
    bool WindowsRefreshAvailable,
    int CurrentHz,
    int MaxHz,
    bool WmiBrightnessAvailable)
{
    internal static DisplayPlatformFacts Read(string? screen = null)
    {
        int max = MechrevoLite.ScreenGateProbe.Win32MaxRefreshRate();
        int current = ScreenNative.GetRefreshRate(screen);
        bool brightness = MechrevoLite.ScreenGateProbe.WmiBrightnessReadable();
        return new DisplayPlatformFacts(
            WindowsRefreshAvailable: max > 0,
            CurrentHz: current,
            MaxHz: max,
            WmiBrightnessAvailable: brightness);
    }

    /// <summary>探测失败时保留显示，只有明确单档才隐藏自动刷新。</summary>
    internal bool AutoRefreshVisible => MaxHz <= 0 || MaxHz > ScreenControl.MIN_RATE;

    /// <summary>轻量读取：只需要刷新率，用于可见性判断。</summary>
    internal static bool IsAutoRefreshVisible()
    {
        int max = MechrevoLite.ScreenGateProbe.Win32MaxRefreshRate();
        return max <= 0 || max > ScreenControl.MIN_RATE;
    }
}
