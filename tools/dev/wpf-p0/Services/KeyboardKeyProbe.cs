using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MechrevoLite;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 键盘按键探针：只读取按键的虚拟码/扫描码并记录，用于确认某颗实体键到底有没有到达本进程。
/// 不改写任何按键，不拦截输入。
/// </summary>
internal static class KeyboardKeyProbe
{
    private const int WhKeyboardLl = 13;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static LowLevelKeyboardProc? _proc;
    private static IntPtr _hook;
    private static int _started;
    private static int _total;
    private static readonly HashSet<uint> Seen = new();
    private static readonly object Gate = new();

    internal static void Start()
    {
        if (System.Threading.Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        try
        {
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Logger.WriteError("[rawkey] 键盘探针安装失败：" + Marshal.GetLastWin32Error());
                return;
            }
            Logger.WriteError("[rawkey] 键盘探针已启动（只记录，不拦截）");
        }
        catch (Exception ex)
        {
            Logger.WriteError("[rawkey] 键盘探针启动失败：" + ex.Message);
        }
    }

    internal static void Stop()
    {
        if (System.Threading.Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }
        try
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
            }
        }
        catch
        {
        }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                KbdLlHookStruct info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                int total = System.Threading.Interlocked.Increment(ref _total);
                bool first;
                lock (Gate)
                {
                    first = Seen.Add(info.VkCode);
                }
                if (first)
                {
                    Logger.WriteError("[rawkey] 首次收到按键 vk=0x" + info.VkCode.ToString("X2")
                        + " scan=0x" + info.ScanCode.ToString("X2")
                        + " flags=0x" + info.Flags.ToString("X2")
                        + " （累计按键 " + total + " 次）");
                }
            }
        }
        catch
        {
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
