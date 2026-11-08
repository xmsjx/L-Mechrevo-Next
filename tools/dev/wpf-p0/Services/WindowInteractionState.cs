using System;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>窗口拖动/缩放期间暂停周期刷新，避免 UI 心跳插入移动过程。</summary>
internal static class WindowInteractionState
{
    private static long _activeUntilTick;

    internal static bool Active => Environment.TickCount64 < _activeUntilTick;

    internal static void Note()
    {
        _activeUntilTick = Environment.TickCount64 + 350;
    }

    internal static void Clear()
    {
        _activeUntilTick = 0;
    }
}
