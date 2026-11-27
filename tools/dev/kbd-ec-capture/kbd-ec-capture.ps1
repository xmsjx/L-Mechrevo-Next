# 键盘灯 EC 抓取（只读）：通过 \.ACPIDriver 窗口通道轮询键盘灯相关寄存器，只打印变化的行。
# 用途：机主按 Fn 亮度键、或点原厂控制台的键盘背光开关/亮度，抓出固件真正写了哪些字节。
# 安全：只读，只用 0x700-0x7FF 窗口通道；不做 EC 端口 I/O，不写任何寄存器。
# 用法（管理员）：powershell -NoProfile -ExecutionPolicy Bypass -File kbd-ec-capture.ps1 [-Seconds 0] [-IntervalMs 150]
#   -Seconds 0 = 一直抓到 Ctrl+C；-Regs 可换地址表（十六进制，逗号分隔）

param(
    [int]$Seconds = 0,
    [int]$IntervalMs = 150,
    [string]$LogPath = "",
    [string]$Regs = "0x78C,0x767,0x7AB,0x7C5,0x766,0x78E,0x740"
)

$ErrorActionPreference = "Continue"

# ---- 提权：不是管理员就自我重启一次 ----
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要管理员权限，正在重新以管理员启动…" -ForegroundColor Yellow
    $argsLine = "-NoProfile -ExecutionPolicy Bypass -NoExit -File ""$PSCommandPath"" -Seconds $Seconds -IntervalMs $IntervalMs -Regs $Regs"
    try { Start-Process -FilePath "powershell.exe" -ArgumentList $argsLine -Verb RunAs | Out-Null } catch { Write-Host ("提权失败：" + $_.Exception.Message) }
    exit 0
}

$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $PSScriptRoot ("kbd-ec-capture-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt") }
try { Remove-Item $script:LogFile -ErrorAction SilentlyContinue } catch { }

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

$src = @"
using System;
using System.Runtime.InteropServices;
public static class KbdEcIo
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
"@

# 0x78C 的位含义（与真机抓包一致）：bit1=关灯位 · bit5=档1 · bit6=档2 · bit4=提交位（部分机型才有）
function Describe([int]$addr, [int]$v) {
    if ($v -lt 0) { return "读不到" }
    if ($addr -eq 0x78C) {
        $lvl = (($v -shr 5) -band 0x03)
        $bits = @()
        $bits += ("关灯=" + $(if (($v -band 0x02) -ne 0) { "1" } else { "0" }))
        $bits += ("档位=" + $lvl)
        if (($v -band 0x10) -ne 0) { $bits += "提交位=1" }
        if (($v -band 0x01) -ne 0) { $bits += "bit0=1" }
        return ("0x{0:X2} [{1}]" -f $v, ($bits -join " "))
    }
    return ("0x{0:X2}" -f $v)
}

function BitDiff([int]$old, [int]$new) {
    $d = $old -bxor $new
    if ($d -eq 0) { return "" }
    $out = @()
    for ($b = 0; $b -lt 8; $b++) {
        $m = 1 -shl $b
        if (($d -band $m) -ne 0) { $out += ("bit" + $b + $(if (($new -band $m) -ne 0) { "+" } else { "-" })) }
    }
    return ($out -join " ")
}

try {
    Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop | Out-Null
    $addrs = @()
    foreach ($r in ($Regs -split ",")) { $addrs += [Convert]::ToInt32($r.Trim(), 16) }
    foreach ($a in $addrs) {
        if ($a -lt 0x700 -or $a -gt 0x7FF) { Log ("地址 0x{0:X} 不在 0x700-0x7FF 窗口内，拒绝读取" -f $a); exit 7 }
    }

    $h = [KbdEcIo]::Open()
    if ($h -eq [IntPtr]::Zero -or $h.ToInt64() -eq -1) { Log "打不开 \\.\ACPIDriver（驱动没装或未加载）⇒ 退出"; exit 6 }

    Log ("=== 键盘灯 EC 抓取开始 · 轮询 {0} ms · 寄存器: {1} ===" -f $IntervalMs, (($addrs | ForEach-Object { "0x{0:X3}" -f $_ }) -join " "))
    Log "操作提示：现在按键盘 Fn 亮度键 1~4 次、再点原厂控制台的键盘背光开关与亮度，抓完按 Ctrl+C（或等到 -Seconds 到点）"

    $prev = @{}
    foreach ($a in $addrs) { $prev[$a] = -2 }
    $stats = @{}
    foreach ($a in $addrs) { $stats[$a] = @{ Changes = 0; Values = @{} } }

    $deadline = if ($Seconds -gt 0) { (Get-Date).AddSeconds($Seconds) } else { [DateTime]::MaxValue }
    try {
        while ((Get-Date) -lt $deadline) {
            $changed = @()
            foreach ($a in $addrs) {
                $v = [KbdEcIo]::Read($h, $a)
                $p = $prev[$a]
                if ($v -ne $p) {
                    if ($p -eq -2) {
                        Log ("[起点] 0x{0:X3} = {1}" -f $a, (Describe $a $v))
                    } else {
                        $diff = BitDiff $p $v
                        $line = ("0x{0:X3} {1} -> {2}" -f $a, (Describe $a $p), (Describe $a $v))
                        if ($diff) { $line += "   变化: " + $diff }
                        $changed += $line
                    }
                    $prev[$a] = $v
                    if ($v -ge 0 -and $p -ne -2) {
                        $stats[$a].Changes++
                        $key = ("0x{0:X2}" -f $v)
                        if ($stats[$a].Values.ContainsKey($key)) { $stats[$a].Values[$key]++ } else { $stats[$a].Values[$key] = 1 }
                    }
                }
            }
            foreach ($c in $changed) { Log $c }
            Start-Sleep -Milliseconds $IntervalMs
        }
    }
    catch { Log ("轮询中断：" + $_.Exception.Message) }
    finally {
        try { [KbdEcIo]::Close($h) } catch { }
        Log "=== 汇总 ==="
        foreach ($a in $addrs) {
            $s = $stats[$a]
            $vals = ($s.Values.GetEnumerator() | Sort-Object -Property Value -Descending | ForEach-Object { $_.Key + "×" + $_.Value }) -join "  "
            Log ("0x{0:X3} 变化 {1} 次 · 出现值: {2}" -f $a, $s.Changes, $(if ($vals) { $vals } else { "（无变化）" }))
        }
        Log ("日志文件：" + $script:LogFile)
        Log "把上面这份日志整份发回即可（不需要截图）"
    }
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    exit 1
}
