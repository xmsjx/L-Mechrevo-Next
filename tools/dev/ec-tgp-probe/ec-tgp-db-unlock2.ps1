# DB 解锁 v2 探测：先枚举 AcpiTest_MULong 的方法/参数，再逐个尝试写 0x743=0x07，每步回读。
# 只动 0x743（不改其它 EC 字节）；全部失败会明确写“固件拒绝/方法不对”。
# 日志：%TEMP%\ec-db-log2.txt（无论成败都写）
# 用法（管理员）：powershell -NoProfile -ExecutionPolicy Bypass -File ec-tgp-db-unlock2.ps1

param(
    [string]$LogPath = "",
    [int]$TargetValue = 7
)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-log2.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

$ADDR = 0x743

function New-Cmd([int]$addr, [int]$value, [int]$offset, [int]$cmd) {
    return ([uint64]$cmd -shl 56) -bor ([uint64]$offset -shl 40) -bor ([uint64]($value -band 0xFF) -shl 32) -bor [uint64]$addr
}

try {
    Log "=== v2 begin ==="
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Log "不是管理员 ⇒ 退出"
        exit 5
    }
    Log "管理员=是 · user=$($id.Name)"

    $cimAll = @(Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcpiTest_MULong' -ErrorAction Stop)
    Log ("MULong 实例数=$($cimAll.Count)")
    if ($cimAll.Count -eq 0) { Log "没有 AcpiTest_MULong"; exit 6 }
    $cim = $cimAll | Where-Object { $_.InstanceName -like '*PNP0C14\1_1*' } | Select-Object -First 1
    if (-not $cim) { $cim = $cimAll | Select-Object -First 1 }
    Log ("使用实例：" + $cim.InstanceName)

    # ── ① 枚举方法/参数 ─────────────────────────────
    Log "=== 枚举方法 ==="
    $cls = Get-CimClass -Namespace 'root\WMI' -ClassName 'AcpiTest_MULong'
    foreach ($m in $cls.CimClassMethods) {
        $ps = @()
        foreach ($prm in $m.Parameters) {
            $ps += ($prm.Name + ':' + $prm.CimType)
        }
        Log ("  方法 {0} ({1})" -f $m.Name, ($ps -join ', '))
    }

    function Read-Ec([int]$addr) {
        $data = New-Cmd $addr 0 1 0
        $r = Invoke-CimMethod -InputObject $cim -MethodName GetSetULong -Arguments @{ Data = [uint64]$data }
        if ($null -eq $r) { return $null }
        return [uint64]$r.Return
    }

    function Show-743([string]$tag) {
        $v = Read-Ec $ADDR
        Log ("[{0}] 0x743=0x{1:X4}（低字节 0x{2:X2}）" -f $tag, $v, ($v -band 0xFF))
        return $v
    }

    $before = Show-743 "before"
    $orig = [int]($before -band 0xFF)
    Log ("原值 0x743=0x{0:X2}" -f $orig)

    function Get-GpuEnforced {
        $smi = 'C:\Windows\System32\nvidia-smi.exe'
        if (-not (Test-Path $smi)) { return "n/a" }
        $line = & $smi --query-gpu=enforced.power.limit,power.draw --format=csv,noheader 2>$null
        if (-not $line) { return "n/a" }
        return ($line.Trim())
    }
    Log ("GPU 现在：" + (Get-GpuEnforced))

    # ── ② 逐个尝试写方法/编码组合 ──────────────────
    $attempts = @(
        @{ Method = 'SetULong';    Offset = 1; Cmd = 0; Note = 'SetULong offset=1 cmd=0' },
        @{ Method = 'SetULong';    Offset = 0; Cmd = 0; Note = 'SetULong offset=0 cmd=0' },
        @{ Method = 'FireULong';   Offset = 1; Cmd = 0; Note = 'FireULong offset=1 cmd=0' },
        @{ Method = 'GetSetULong'; Offset = 1; Cmd = 1; Note = 'GetSetULong offset=1 cmd=1' },
        @{ Method = 'GetSetULong'; Offset = 0; Cmd = 0; Note = 'GetSetULong offset=0 cmd=0' }
    )

    $success = $false
    foreach ($a in $attempts) {
        $val = [uint64](New-Cmd $ADDR $TargetValue $a.Offset $a.Cmd)
        Log ("=== 尝试 {0} · Data=0x{1:X16} ===" -f $a.Note, $val)
        try {
            $r = Invoke-CimMethod -InputObject $cim -MethodName $a.Method -Arguments @{ Data = $val } -ErrorAction Stop
            Log ("  调用返回：" + ($r.Return) + " · 状态=" + $r.CimClass)
        }
        catch {
            Log ("  调用失败：" + $_.Exception.Message)
        }
        Start-Sleep -Milliseconds 400
        $now = Show-743 ("after/" + $a.Method)
        $low = [int]($now -band 0xFF)
        if ($low -eq $TargetValue) {
            Log ("✅ 成功：0x743 已变成 0x{0:X2}（方法={1} offset={2} cmd={3}）" -f $low, $a.Method, $a.Offset, $a.Cmd)
            $success = $true
            break
        }
        Log ("  ⇒ 回读 0x{0:X2}，未生效" -f $low)
    }

    # ── ③ 监视 10 秒 ───────────────────────────────
    Log "=== 监视 10 秒（看 enforced/draw 是否变化）==="
    $prev = Get-GpuEnforced
    Log ("  起点：" + $prev)
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 1
        $cur = Get-GpuEnforced
        if ($cur -ne $prev) { Log ("  " + $prev + " → " + $cur); $prev = $cur }
    }

    $final = Show-743 "final"
    $finalLow = [int]($final -band 0xFF)
    Log ("=== 结论：0x743=0x{0:X2} · 目标=0x{1:X2} ⇒ {2} · 原值=0x{3:X2} ===" -f $finalLow, $TargetValue, ($(if ($success) { '写入成功' } else { '全部失败（固件拒绝或方法/编码都不对）' })), $orig)
    Log "=== done ==="
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    Log ("位置：" + $_.InvocationInfo.PositionMessage)
    Log "=== failed ==="
    exit 1
}
