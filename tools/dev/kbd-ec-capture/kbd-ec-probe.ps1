# 键盘灯 EC 探测：窗口扫描（只读）与单字节写入测试（需显式确认）。
# 安全：只用 0x700-0x7FF 窗口通道；不做 EC 端口 I/O。
# 用法（管理员）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File kbd-ec-probe.ps1 -Mode window [-Seconds 60] [-IntervalMs 200]
#   powershell -NoProfile -ExecutionPolicy Bypass -File kbd-ec-probe.ps1 -Mode write -Addr 0x78C -Value 0x50 -IUnderstandWrite

param(
    [ValidateSet("window", "write")]
    [string]$Mode = "window",
    [int]$Seconds = 60,
    [int]$IntervalMs = 200,
    [string]$Addr = "0x78C",
    [string]$Value = "0x41",
    [string]$LogPath = "",
    [switch]$IUnderstandWrite
)

$ErrorActionPreference = "Continue"

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要管理员权限，正在重新以管理员启动…" -ForegroundColor Yellow
    $argsLine = "-NoProfile -ExecutionPolicy Bypass -NoExit -File ""$PSCommandPath"" -Mode $Mode -Seconds $Seconds -IntervalMs $IntervalMs -Addr $Addr -Value $Value"
    if ($IUnderstandWrite) { $argsLine += " -IUnderstandWrite" }
    try { Start-Process -FilePath "powershell.exe" -ArgumentList $argsLine -Verb RunAs | Out-Null } catch { Write-Host ("提权失败：" + $_.Exception.Message) }
    exit 0
}

$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $PSScriptRoot ("kbd-ec-probe-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt") }
try { Remove-Item $script:LogFile -ErrorAction SilentlyContinue } catch { }

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

$src = @"
using System;
using System.Runtime.InteropServices;
public static class KbdEcIo2
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
            if (!DeviceIoControl(h, 2621482120u, inBuf, 4, outBuf, 16, out ret, IntPtr.Zero) || ret <= 0) return -1;
            return Marshal.ReadByte(outBuf);
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }
    public static bool Write(IntPtr h, int address, int value)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(inBuf, 0, address);
            Marshal.WriteInt32(inBuf, 4, value);
            int ret;
            return DeviceIoControl(h, 2621482124u, inBuf, 8, IntPtr.Zero, 0, out ret, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(inBuf); }
    }
}
"@

Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop | Out-Null

$h = [KbdEcIo2]::Open()
if ($h -eq [IntPtr]::Zero -or $h.ToInt64() -eq -1) { Log "打不开 \\.\ACPIDriver（驱动没装或未加载）⇒ 退出"; exit 6 }

function Reg($text) { return [Convert]::ToInt32($text.Trim(), 16) }

try {
    if ($Mode -eq "write") {
        $a = Reg $Addr
        $v = Reg $Value
        if ($a -lt 0x700 -or $a -gt 0x7FF) { Log ("地址 0x{0:X} 不在窗口内，拒绝写入" -f $a); exit 7 }
        if (-not $IUnderstandWrite) { Log "写入测试需要显式加 -IUnderstandWrite（会真的改 EC 寄存器）"; exit 8 }
        $before = [KbdEcIo2]::Read($h, $a)
        Log ("写入前 0x{0:X3} = 0x{1:X2}" -f $a, $before)
        $ok = [KbdEcIo2]::Write($h, $a, $v)
        Log ("写入 0x{0:X3} <= 0x{1:X2} ⇒ {2}" -f $a, $v, $ok)
        foreach ($wait in @(50, 200, 1000, 2500)) {
            Start-Sleep -Milliseconds $wait
            $after = [KbdEcIo2]::Read($h, $a)
            Log ("  +{0}ms 回读 0x{1:X3} = 0x{2:X2}" -f $wait, $a, $after)
        }
        Log "观察：键盘灯有没有变化；回读若与写入值不同，说明被固件改写/清位"
        exit 0
    }

    $addrs = 0x700..0x7FF
    Log ("=== 窗口扫描开始 · 0x700-0x7FF · 轮询 {0} ms · {1} 秒 ===" -f $IntervalMs, $Seconds)
    Log "操作提示：现在按键盘 Fn 亮度键两三次；除 0x78C 以外变化的地址就是我们要找的生效/锁存动作"
    $prev = @{}
    foreach ($a in $addrs) { $prev[$a] = -2 }
    $deadline = (Get-Date).AddSeconds($Seconds)
    $changedTotal = 0
    while ((Get-Date) -lt $deadline) {
        $lines = @()
        foreach ($a in $addrs) {
            $v = [KbdEcIo2]::Read($h, $a)
            $p = $prev[$a]
            if ($v -ne $p) {
                if ($p -ne -2) {
                    $lines += ("0x{0:X3}  0x{1:X2} -> 0x{2:X2}" -f $a, $p, $v)
                    $changedTotal++
                }
                $prev[$a] = $v
            }
        }
        foreach ($l in $lines) { Log $l }
        Start-Sleep -Milliseconds $IntervalMs
    }
    Log ("=== 扫描结束 · 共记录 {0} 次变化 ===" -f $changedTotal)
}
finally {
    try { [KbdEcIo2]::Close($h) } catch { }
}
