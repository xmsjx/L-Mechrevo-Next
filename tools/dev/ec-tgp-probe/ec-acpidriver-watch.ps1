# 只读观察：通过 \\.\ACPIDriver 每秒读 0x740..0x746，同时读 nvidia-smi；只打印变化行。
# 用途：在 OpenRevo 控制台里切换模式，观察 EC 的 DB/TGP 字节到底变不变（写 issue 的证据）。
# 日志：%TEMP%\ec-db-watch.txt
# 用法（管理员）：powershell -NoProfile -ExecutionPolicy Bypass -File ec-acpidriver-watch.ps1 [-Seconds 90]

param(
    [int]$Seconds = 90,
    [int]$IntervalMs = 1000,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-watch.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

$src = @'
using System;
using System.Runtime.InteropServices;
public static class AcpiEcWatch
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);
    const string DevicePath = "\\\\.\\ACPIDriver";
    public static IntPtr Open() { return CreateFile(DevicePath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); }
    public static void Close(IntPtr h) { CloseHandle(h); }
    public static int Read(IntPtr h, int address)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(4);
        IntPtr outBuf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuf, 0, address);
            int ret;
            if (!DeviceIoControl(h, 2621482120, inBuf, 4, outBuf, 16, out ret, IntPtr.Zero) || ret <= 0) return -1;
            return Marshal.ReadByte(outBuf);
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }
}
'@

try {
    Log "=== EC watch begin · $Seconds s ==="
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Log "不是管理员 ⇒ 退出"; exit 5 }

    Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop | Out-Null
    $h = [AcpiEcWatch]::Open()
    if ($h -eq [IntPtr]::Zero -or $h.ToInt64() -eq -1) { Log "打不开 ACPIDriver"; exit 6 }

    $smi = 'C:\Windows\System32\nvidia-smi.exe'
    $addrs = @(0x740, 0x741, 0x742, 0x743, 0x744, 0x745, 0x746, 0x747)
    $prev = $null

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $vals = @{}
        foreach ($a in $addrs) { $vals[$a] = [AcpiEcWatch]::Read($h, $a) }
        $smiLine = (& $smi --query-gpu=enforced.power.limit,power.draw,temperature.gpu --format=csv,noheader 2>$null)
        if ($smiLine) { $smiLine = $smiLine.Trim() } else { $smiLine = "n/a" }

        $line = ("GPU[{0}] · 0x743=0x{1:X2} · 0x744=0x{2:X2} · 0x745=0x{3:X2} · 0x746=0x{4:X2}" -f $smiLine, $vals[0x743], $vals[0x744], $vals[0x745], $vals[0x746])
        if ($null -eq $prev) {
            Log ("[起点] " + $line)
        }
        elseif ($line -ne $prev) {
            Log $line
        }
        $prev = $line
        Start-Sleep -Milliseconds $IntervalMs
    }
    [AcpiEcWatch]::Close($h)
    Log "=== done ==="
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    exit 1
}
