# DB 启用实验（完整版）：一次写入官方 GCU「狂暴」的那组 EC 字节，并回读 + 监视 enforced。
#   目标写入（官方 GCU 实测）：
#     0x743 = 0x07   DB 使能
#     0x745 = 0xFF   DB 目标
#     0x746 = 0x19   DB 最大 TGP = 25W
#     0x7D4 = 0xFF   第二份 DB 目标
#     0x7D5 = 0x19   第二份 DB max = 25W
#   通道：\\.\ACPIDriver 写 IOCTL 2621482124（与 LM 限充同一套），只读回读 2621482120。
#   进入时记录各字节原值；-Restore 原样写回。
# 日志：%TEMP%\ec-db-enable.txt
# 用法（管理员）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File ec-acpidriver-db-enable.ps1
#   ... -Restore      # 回滚
#   ... -WhatIf       # 只读，不写

param(
    [switch]$Restore,
    [switch]$WhatIf,
    [int]$SetAddr = -1,
    [int]$SetValue = -1,
    [int]$MonitorSeconds = 20,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-enable.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

$src = @'
using System;
using System.Runtime.InteropServices;
public static class AcpiEcRw
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
    public static bool Write(IntPtr h, int address, byte value)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(8);
        IntPtr outBuf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuf, 0, address);
            Marshal.WriteByte(inBuf, 4, value);
            int ret;
            return DeviceIoControl(h, 2621482124, inBuf, 5, outBuf, 16, out ret, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }
}
'@

# 官方 GCU 狂暴档的写入集合
$targets = [ordered]@{
    0x743 = 0x07
    0x745 = 0xFF
    0x746 = 0x19
    0x7D4 = 0xFF
    0x7D5 = 0x19
}

function GpuLine {
    $smi = 'C:\Windows\System32\nvidia-smi.exe'
    $l = & $smi --query-gpu=enforced.power.limit,power.draw,temperature.gpu,clocks.current.graphics --format=csv,noheader 2>$null
    if ($l) { return $l.Trim() } else { return "n/a" }
}

function DumpEc([IntPtr]$h) {
    $pairs = @()
    foreach ($a in $targets.Keys) {
        $v = [AcpiEcRw]::Read($h, [int]$a)
        $pairs += $(if ($v -lt 0) { ("0x{0:X3}=--" -f $a) } else { ("0x{0:X3}=0x{1:X2}" -f $a, $v) })
    }
    return ($pairs -join ' · ')
}

try {
    Log "=== DB enable begin ==="
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Log "不是管理员 ⇒ 退出"; exit 5 }

    Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop | Out-Null
    $h = [AcpiEcRw]::Open()
    if ($h -eq [IntPtr]::Zero -or $h.ToInt64() -eq -1) { Log "打不开 ACPIDriver"; exit 6 }

    try {
        $orig = @{}
        foreach ($a in $targets.Keys) { $orig[$a] = [AcpiEcRw]::Read($h, [int]$a) }
        Log ("[before] GPU[" + (GpuLine) + "]")
        Log ("[before] EC: " + (DumpEc $h))
        Log ("原值：" + (($orig.Keys | ForEach-Object { "0x{0:X3}=0x{1:X2}" -f $_, $orig[$_] }) -join ' · '))

        if ($WhatIf) { Log "[WhatIf] 只读，不写"; exit 0 }

        # 单字节写入模式（用于验证：只改某一个字节，固件会不会自己接管）
        if ($SetAddr -ge 0) {
            Log ("=== 单字节写入 0x{0:X3}=0x{1:X2} ===" -f $SetAddr, $SetValue)
            $ok = [AcpiEcRw]::Write($h, [int]$SetAddr, [byte]$SetValue)
            Log ("  写调用返回：" + $ok)
            Start-Sleep -Milliseconds 500
            $rb = [AcpiEcRw]::Read($h, [int]$SetAddr)
            Log ("  回读 0x{0:X3}=0x{1:X2}" -f $SetAddr, $rb)
            Log ("[after] GPU[" + (GpuLine) + "]")
            Log "=== 监视 $MonitorSeconds 秒（看固件会不会把它改回去）==="
            $prev = ""
            $deadline = (Get-Date).AddSeconds($MonitorSeconds)
            while ((Get-Date) -lt $deadline) {
                $cur = GpuLine + " · 0x{0:X3}=0x{1:X2}" -f $SetAddr, ([AcpiEcRw]::Read($h, [int]$SetAddr))
                if ($cur -ne $prev) { Log ("  " + $cur); $prev = $cur }
                Start-Sleep -Seconds 1
            }
            exit 0
        }

        if ($Restore) {
            Log "=== 回滚到原值 ==="
            foreach ($a in $targets.Keys) {
                if ($orig[$a] -ge 0) {
                    $ok = [AcpiEcRw]::Write($h, [int]$a, [byte]$orig[$a])
                    Log ("  写 0x{0:X3}=0x{1:X2} ⇒ {2}" -f $a, $orig[$a], $ok)
                }
            }
            Start-Sleep -Milliseconds 400
            Log ("[after-restore] GPU[" + (GpuLine) + "] · EC: " + (DumpEc $h))
            exit 0
        }

        Log "=== 写入官方 GCU 狂暴档字节集合 ==="
        foreach ($a in $targets.Keys) {
            # ⚠️ OrderedDictionary 的整数键索引会绑到“序号”重载 ⇒ 必须用 .Item([object]key)
            $w = [int]$targets.Item([object]$a)
            $ok = [AcpiEcRw]::Write($h, [int]$a, [byte]$w)
            Log ("  写 0x{0:X3}=0x{1:X2} ⇒ {2}" -f $a, $w, $ok)
            Start-Sleep -Milliseconds 60
        }
        Start-Sleep -Milliseconds 600
        Log ("[after-write] GPU[" + (GpuLine) + "] · EC: " + (DumpEc $h))

        Log "=== 监视 $MonitorSeconds 秒（enforced/draw 有变化才打；现在可跑烤机/游戏）==="
        $prev = ""
        $deadline = (Get-Date).AddSeconds($MonitorSeconds)
        while ((Get-Date) -lt $deadline) {
            $cur = GpuLine
            if ($cur -ne $prev) { Log ("  " + $cur); $prev = $cur }
            Start-Sleep -Seconds 1
        }

        $final = DumpEc $h
        Log ("[final] GPU[" + (GpuLine) + "] · EC: " + $final)
        Log ("回滚：本脚本 -Restore（会写回进入时的原值：" + (($orig.Keys | ForEach-Object { "0x{0:X3}=0x{1:X2}" -f $_, $orig[$_] }) -join ' · ') + "）")
        Log "=== done ==="
    }
    finally { [AcpiEcRw]::Close($h) }
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    Log ("位置：" + $_.InvocationInfo.PositionMessage)
    exit 1
}
