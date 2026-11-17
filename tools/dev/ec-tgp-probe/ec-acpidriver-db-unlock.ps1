# DB 解锁（走同方 ACPIDriver 通道，H2RAM 0x700–0x7FF；与 LM 限充同一套 IOCTL）
#   读 IOCTL = 2621482120 (0x9C40_2008) · 写 IOCTL = 2621482124
#   输入缓冲：int32 地址 + 字节值（写）；读取返回缓冲首字节。
# 只写 0x743（TGP/DB 控制字节）；每步回读；-Restore 回滚。
# 日志：%TEMP%\ec-db-acpi-log.txt
# 用法（管理员）：powershell -NoProfile -ExecutionPolicy Bypass -File ec-acpidriver-db-unlock.ps1 [-Restore] [-SameValueTest]

param(
    [switch]$Restore,
    [switch]$SameValueTest,
    [int]$Addr = 0x743,
    [int]$TargetValue = 7,
    [int]$MonitorSeconds = 15,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-acpi-log.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

$src = @'
using System;
using System.Runtime.InteropServices;

public static class AcpiEc
{
    const uint GENERIC_READ_WRITE = 0xC0000000;
    const uint OPEN_EXISTING = 3;
    const uint IOCTL_EC_READ = 2621482120;
    const uint IOCTL_EC_WRITE = 2621482124;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);

    const string DevicePath = "\\\\.\\ACPIDriver";

    public static IntPtr Open()
    {
        return CreateFile(DevicePath, GENERIC_READ_WRITE, 3, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }
    public static void Close(IntPtr h) { CloseHandle(h); }

    public static int Read(IntPtr h, int address)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(4);
        IntPtr outBuf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuf, 0, address);
            int ret;
            bool ok = DeviceIoControl(h, IOCTL_EC_READ, inBuf, 4, outBuf, 16, out ret, IntPtr.Zero);
            if (!ok || ret <= 0) return -1;
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
            return DeviceIoControl(h, IOCTL_EC_WRITE, inBuf, 5, outBuf, 16, out ret, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }
}
'@

try {
    Log "=== ACPIDriver DB unlock begin ==="
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Log "不是管理员 ⇒ 退出"; exit 5 }

    Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop | Out-Null
    Log "P/Invoke 已加载"

    $h = [AcpiEc]::Open()
    if ($h -eq [IntPtr]::Zero -or $h.ToInt64() -eq -1) {
        $lastErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Log ("❌ 打不开 ACPIDriver（驱动未装/未加载）。错误码=" + $lastErr)
        exit 6
    }
    Log "ACPIDriver 已打开"

    try {
        $v = [AcpiEc]::Read($h, $Addr)
        Log ("[before] 0x{0:X3}=0x{1:X2}" -f $Addr, $v)
        if ($v -lt 0) { Log "❌ 读 0x743 失败（通道读不到）"; exit 7 }
        $orig = $v

        if ($Restore) {
            $ok = [AcpiEc]::Write($h, $Addr, [byte]$orig)
            Start-Sleep -Milliseconds 300
            $rb = [AcpiEc]::Read($h, $Addr)
            Log ("回滚：写 0x{0:X2} ⇒ 返回={1} · 回读=0x{2:X2}" -f $orig, $ok, $rb)
            exit 0
        }

        $writeValue = if ($SameValueTest) { $orig } else { $TargetValue }
        Log ("=== 写 0x{0:X3}=0x{1:X2}（{2}）===" -f $Addr, $writeValue, ($(if ($SameValueTest) { '同值写测试' } else { 'DB 使能' })))
        $ok = [AcpiEc]::Write($h, $Addr, [byte]$writeValue)
        $wErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Log ("写调用返回：" + $ok + " · LastWin32Error=" + $wErr)
        Start-Sleep -Milliseconds 400
        $rb = [AcpiEc]::Read($h, $Addr)
        Log ("回读 0x{0:X3}=0x{1:X2}" -f $Addr, $rb)

        if (-not $SameValueTest -and $rb -ne $TargetValue) {
            Log "⚠️ 写入未生效（被固件夹回）⇒ 不再重试写其它字节"
        }

        $smi = 'C:\Windows\System32\nvidia-smi.exe'
        Log "=== 监视 $MonitorSeconds 秒（enforced/draw 有变化才打）==="
        $prev = ""
        for ($i = 0; $i -lt $MonitorSeconds; $i++) {
            $cur = (& $smi --query-gpu=enforced.power.limit,power.draw --format=csv,noheader 2>$null)
            if ($cur) {
                $cur = $cur.Trim()
                if ($cur -ne $prev) { Log ("  " + $cur); $prev = $cur }
            }
            Start-Sleep -Seconds 1
        }

        $final = [AcpiEc]::Read($h, $Addr)
        Log ("=== 结论：0x{0:X3}=0x{1:X2}（目标 0x{2:X2}）· 原值 0x{3:X2} ⇒ {4} ===" -f $Addr, $final, $TargetValue, $orig, ($(if ($final -eq $TargetValue) { '写入成功（DB 位已置）' } else { '写入未被接受' })))
        Log "=== done ==="
    }
    finally { [AcpiEc]::Close($h) }
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    Log ("位置：" + $_.InvocationInfo.PositionMessage)
    exit 1
}
