# 只读观察 v2：扫描同方 EC H2RAM 窗口（默认 0x700–0x7FF 共 256 字节），
# 每秒对比一次，只打印「有变化的地址」+ nvidia-smi 的 enforced/draw/temp。
# 用途：在 LM（官方 GCU）或 OpenRevo 里切换模式，看除 0x743/0x745/0x746 之外还有哪些字节变。
# 日志：%TEMP%\ec-db-watch2.txt
# 用法（管理员）：powershell -NoProfile -ExecutionPolicy Bypass -File ec-acpidriver-watch2.ps1 -Seconds 90

param(
    [int]$Seconds = 90,
    [int]$IntervalMs = 1200,
    [int]$ScanStart = 0x700,
    [int]$ScanEnd = 0x7FF,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-watch2.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

$src = @'
using System;
using System.Runtime.InteropServices;
public static class AcpiEcScan
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

function Snapshot([IntPtr]$h, [int]$start, [int]$end) {
    $arr = New-Object int[] ($end - $start + 1)
    for ($a = $start; $a -le $end; $a++) { $arr[$a - $start] = [AcpiEcScan]::Read($h, $a) }
    return $arr
}

function GpuLine {
    $smi = 'C:\Windows\System32\nvidia-smi.exe'
    $line = & $smi --query-gpu=enforced.power.limit,power.draw,temperature.gpu,clocks.current.graphics --format=csv,noheader 2>$null
    if ($line) { return $line.Trim() } else { return "n/a" }
}

try {
    Log ("=== EC scan watch begin · 0x{0:X3}..0x{1:X3} · {2}s ===" -f $ScanStart, $ScanEnd, $Seconds)
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Log "不是管理员 ⇒ 退出"; exit 5 }

    Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop | Out-Null
    $h = [AcpiEcScan]::Open()
    if ($h -eq [IntPtr]::Zero -or $h.ToInt64() -eq -1) { Log "打不开 ACPIDriver"; exit 6 }

    $prev = Snapshot $h $ScanStart $ScanEnd
    Log ("[起点] GPU[{0}]" -f (GpuLine))
    # 初始快照：每行 16 字节（hex）
    for ($row = 0; $row -lt $prev.Length; $row += 16) {
        $base = $ScanStart + $row
        $cols = @()
        for ($i = 0; $i -lt 16 -and ($row + $i) -lt $prev.Length; $i++) {
            $v = $prev[$row + $i]
            $cols += $(if ($v -lt 0) { "--" } else { '{0:X2}' -f $v })
        }
        Log ("  {0:X3}: {1}" -f $base, ($cols -join ' '))
    }

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds $IntervalMs
        $cur = Snapshot $h $ScanStart $ScanEnd
        $changes = @()
        for ($i = 0; $i -lt $cur.Length; $i++) {
            if ($cur[$i] -ne $prev[$i]) {
                $addr = $ScanStart + $i
                $ov = if ($prev[$i] -lt 0) { "--" } else { '{0:X2}' -f $prev[$i] }
                $nv = if ($cur[$i] -lt 0) { "--" } else { '{0:X2}' -f $cur[$i] }
                $changes += ("0x{0:X3}:{1}→{2}" -f $addr, $ov, $nv)
            }
        }
        if ($changes.Count -gt 0) {
            Log ("GPU[{0}] · 变化: {1}" -f (GpuLine), ($changes -join ' · '))
        }
        $prev = $cur
    }
    [AcpiEcScan]::Close($h)
    Log "=== done ==="
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    exit 1
}
