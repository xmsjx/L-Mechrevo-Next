$src = @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class EcRead
{
    const uint IoctlEcRead = 0x9C40A488;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sec, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, uint isz, byte[] o, uint osz, out uint ret, IntPtr ov);
    public static string Read(int addr)
    {
        var h = CreateFileW(@"\\.\ACPIDriver", 0x80000000u | 0x40000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (h.IsInvalid) { return "open-fail:" + Marshal.GetLastWin32Error(); }
        var inp = BitConverter.GetBytes((uint)addr);
        var outp = new byte[4];
        uint ret;
        bool ok = DeviceIoControl(h, IoctlEcRead, inp, 4, outp, 4, out ret, IntPtr.Zero);
        h.Dispose();
        if (!ok) { return "read-fail:" + Marshal.GetLastWin32Error(); }
        return "0x" + addr.ToString("X3") + " = 0x" + BitConverter.ToUInt32(outp, 0).ToString("X2");
    }
}
'@
Add-Type -TypeDefinition $src -ErrorAction Stop
0x7C5, 0x7C6, 0x706, 0x741, 0x727, 0x767, 0x75B, 0x75C, 0x43E, 0x44F | ForEach-Object { [EcRead]::Read($_) }