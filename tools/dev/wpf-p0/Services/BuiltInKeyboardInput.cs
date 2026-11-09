using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>区分内置键盘与外接键盘的原始输入监听，供 HID 按键效果选择输入来源。</summary>
internal static class BuiltInKeyboardInput
{
    private const int WmInput = 0x00FF;
    private const uint RidInputSink = 0x00000100;
    private const uint RimTypeKeyboard = 1;
    private const uint GetInputData = 0x10000003;
    private const uint GetDeviceName = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceListItem
    {
        public IntPtr Device;
        public uint Type;
    }

    private static readonly object Gate = new();
    private static readonly bool[] KeysDown = new bool[256];
    private static readonly Dictionary<IntPtr, bool> AcceptedDevices = new();
    private static bool _registered;
    private static bool _hasAcpiKeyboard;

    internal static void Initialize(HwndSource source)
    {
        if (_registered || source == null)
        {
            return;
        }
        try
        {
            RefreshDeviceFilter();
            RawInputDevice[] devices =
            {
                new() { UsagePage = 1, Usage = 6, Flags = RidInputSink, Target = source.Handle }
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            {
                return;
            }
            source.AddHook(WndProc);
            _registered = true;
        }
        catch
        {
        }
    }

    internal static bool IsKeyDown(int virtualKey)
    {
        if (virtualKey <= 0 || virtualKey >= KeysDown.Length)
        {
            return false;
        }
        lock (Gate)
        {
            return KeysDown[virtualKey];
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        GetRawInputData(lParam, GetInputData, IntPtr.Zero, ref size, headerSize);
        if (size < headerSize)
        {
            return IntPtr.Zero;
        }
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = GetRawInputData(lParam, GetInputData, buffer, ref size, headerSize);
            if (read == uint.MaxValue || read < headerSize)
            {
                return IntPtr.Zero;
            }
            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeKeyboard || !IsBuiltIn(header.Device))
            {
                return IntPtr.Zero;
            }
            RawKeyboard keyboard = Marshal.PtrToStructure<RawKeyboard>(IntPtr.Add(buffer, (int)headerSize));
            int vk = keyboard.VirtualKey;
            if (vk <= 0 || vk >= KeysDown.Length)
            {
                return IntPtr.Zero;
            }
            bool up = (keyboard.Flags & 1) != 0;
            lock (Gate)
            {
                KeysDown[vk] = !up;
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return IntPtr.Zero;
    }

    private static bool IsBuiltIn(IntPtr device)
    {
        lock (Gate)
        {
            if (AcceptedDevices.TryGetValue(device, out bool accepted))
            {
                return accepted;
            }
        }
        RefreshDeviceFilter();
        lock (Gate)
        {
            return AcceptedDevices.TryGetValue(device, out bool accepted)
                ? accepted
                : !_hasAcpiKeyboard;
        }
    }

    private static void RefreshDeviceFilter()
    {
        Dictionary<IntPtr, string> names = new();
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<DeviceListItem>();
        if (GetRawInputDeviceList(null, ref count, size) != uint.MaxValue && count > 0)
        {
            DeviceListItem[] items = new DeviceListItem[count];
            if (GetRawInputDeviceList(items, ref count, size) != uint.MaxValue)
            {
                for (int i = 0; i < count; i++)
                {
                    if (items[i].Type == RimTypeKeyboard)
                    {
                        names[items[i].Device] = ReadDeviceName(items[i].Device);
                    }
                }
            }
        }
        bool hasAcpi = false;
        foreach (string name in names.Values)
        {
            if (IsAcpiName(name))
            {
                hasAcpi = true;
                break;
            }
        }
        lock (Gate)
        {
            AcceptedDevices.Clear();
            foreach (KeyValuePair<IntPtr, string> pair in names)
            {
                AcceptedDevices[pair.Key] = !hasAcpi || IsAcpiName(pair.Value);
            }
            _hasAcpiKeyboard = hasAcpi;
        }
    }

    private static string ReadDeviceName(IntPtr device)
    {
        uint size = 1024;
        StringBuilder name = new((int)size);
        return GetRawInputDeviceInfo(device, GetDeviceName, name, ref size) != uint.MaxValue
            ? name.ToString()
            : string.Empty;
    }

    private static bool IsAcpiName(string name)
        => !string.IsNullOrEmpty(name)
            && name.StartsWith("\\\\?\\ACPI#", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr handle, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(DeviceListItem[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
}
