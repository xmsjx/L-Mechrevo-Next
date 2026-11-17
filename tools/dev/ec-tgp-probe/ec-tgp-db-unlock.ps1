# DB（Dynamic Boost）解锁实验 —— 写 EC 0x743 = 0x07，逐步写日志，异常也写日志。
# 用法（管理员）：pwsh -File ec-tgp-db-unlock.ps1 [-SameValueTest] [-Restore] [-WhatIf]
#   日志：%TEMP%\ec-db-log.txt（无论成败都会写）

param(
    [switch]$Restore,
    [switch]$WhatIf,
    [switch]$SameValueTest,
    [int]$TargetValue = 7,
    [int]$MonitorSeconds = 15,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-log.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

function Get-GpuPower {
    $smi = 'C:\Windows\System32\nvidia-smi.exe'
    if (-not (Test-Path $smi)) { return $null }
    $line = & $smi --query-gpu=enforced.power.limit,power.max_limit,power.draw --format=csv,noheader 2>$null
    if (-not $line) { return $null }
    $p = ($line -split ',') | ForEach-Object { $_.Trim() }
    return [pscustomobject]@{ Enforced = $p[0]; Max = $p[1]; Draw = $p[2] }
}

$ADDR_743 = 0x743
$ADDR_745 = 0x745

try {
    Log "=== begin · admin check ==="
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Log "❌ 不是管理员 ⇒ 退出"
        exit 5
    }
    Log "管理员=是 · user=$($id.Name)"

    Log "=== 枚举 AcpiTest_MULong ==="
    $cimAll = @(Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcpiTest_MULong' -ErrorAction Stop)
    Log ("MULong 实例数=$($cimAll.Count)")
    if ($cimAll.Count -eq 0) { Log "❌ 没有 AcpiTest_MULong"; exit 6 }
    $cim = $cimAll | Where-Object { $_.InstanceName -like '*PNP0C14\1_1*' } | Select-Object -First 1
    if (-not $cim) { $cim = $cimAll | Select-Object -First 1 }
    Log ("使用实例：" + $cim.InstanceName)

    function New-Cmd([int]$addr, [int]$value, [int]$offset = 1, [int]$cmd = 0) {
        return ([uint64]$cmd -shl 56) -bor ([uint64]$offset -shl 40) -bor ([uint64]($value -band 0xFF) -shl 32) -bor [uint64]$addr
    }

    function Read-Ec([int]$addr) {
        $data = New-Cmd $addr 0
        $r = Invoke-CimMethod -InputObject $cim -MethodName GetSetULong -Arguments @{ Data = [uint64]$data }
        if ($null -eq $r) { return $null }
        return [uint64]$r.Return
    }

    function Write-Ec([int]$addr, [int]$value) {
        $data = New-Cmd $addr $value
        Log ("  写入 0x{0:X3}=0x{1:X2} Data=0x{2:X16}" -f $addr, $value, $data)
        $r = Invoke-CimMethod -InputObject $cim -MethodName GetSetULong -Arguments @{ Data = [uint64]$data }
        Log ("  返回：" + ($r.Return))
        return $r.Return
    }

    function Show-State([string]$tag) {
        $g = Get-GpuPower
        $v743 = Read-Ec $ADDR_743
        $v745 = Read-Ec $ADDR_745
        $gpuText = if ($g) { "enforced=$($g.Enforced) max=$($g.Max) draw=$($g.Draw)" } else { "nvidia-smi N/A" }
        Log ("[{0}] {1} · 0x743=0x{2:X4} · 0x745=0x{3:X4}" -f $tag, $gpuText, $v743, $v745)
        return [pscustomobject]@{ V743 = $v743; V745 = $v745 }
    }

    Log "=== 读基线 ==="
    $before = Show-State "before"
    $orig743 = [int]($before.V743 -band 0xFF)
    $orig745 = [int]($before.V745 -band 0xFF)
    Log ("原值：0x743=0x{0:X2} · 0x745=0x{1:X2}" -f $orig743, $orig745)

    if ($WhatIf) { Log "[WhatIf] 只读，不写。"; exit 0 }

    if ($Restore) {
        Log "=== 回滚 ==="
        Write-Ec $ADDR_743 $orig743 | Out-Null
        Write-Ec $ADDR_745 $orig745 | Out-Null
        Start-Sleep -Milliseconds 400
        Show-State "restored" | Out-Null
        exit 0
    }

    $writeValue = if ($SameValueTest) { $orig743 } else { $TargetValue }
    Log ("=== 写 0x743=0x{0:X2}（{1}）===" -f $writeValue, ($(if ($SameValueTest) { '同值写测试' } else { 'DB 使能' })))
    Write-Ec $ADDR_743 $writeValue | Out-Null
    Start-Sleep -Milliseconds 400
    $after = Show-State "after-write"
    $readBack = [int]($after.V743 -band 0xFF)

    if (-not $SameValueTest -and $readBack -ne $TargetValue) {
        Log ("⚠️ 回读 0x743=0x{0:X2}（期望 0x{1:X2}）⇒ 再试 0x745=0xFF 后重写 0x743" -f $readBack, $TargetValue)
        Write-Ec $ADDR_745 0xFF | Out-Null
        Start-Sleep -Milliseconds 300
        Write-Ec $ADDR_743 $writeValue | Out-Null
        Start-Sleep -Milliseconds 400
        $after = Show-State "after-retry"
        $readBack = [int]($after.V743 -band 0xFF)
    }

    Log "=== 监视 $MonitorSeconds 秒（有变化才打）==="
    $prev = $after
    $deadline = (Get-Date).AddSeconds($MonitorSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        $g = Get-GpuPower
        if (-not $g) { continue }
        if ($g.Enforced -ne $prev.Enforced -or $g.Max -ne $prev.Max -or $g.Draw -ne $prev.Draw) {
            Log ("enforced: {0} → {1} · max: {2} → {3} · draw: {4} → {5}" -f $prev.Enforced, $g.Enforced, $prev.Max, $g.Max, $prev.Draw, $g.Draw)
            $prev = [pscustomobject]@{ Enforced = $g.Enforced; Max = $g.Max; Draw = $g.Draw; V743 = $prev.V743; V745 = $prev.V745 }
        }
    }

    $final = Show-State "final"
    $final743 = [int]($final.V743 -band 0xFF)
    Log ("=== 结论：0x743 回读=0x{0:X2} · 目标=0x{1:X2} ⇒ {2}" -f $final743, $TargetValue, ($(if ($final743 -eq $TargetValue) { '写入被接受（DB 位已置）' } else { '写入未被接受（固件夹回/编码不对）' })))
    Log ("原始值：0x743=0x{0:X2} 0x745=0x{1:X2} · 回滚：-Restore" -f $orig743, $orig745)
    Log "=== done ==="
    exit 0
}
catch {
    Log ("❌ 异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    Log ("   位置：" + $_.InvocationInfo.PositionMessage)
    Log "=== failed ==="
    exit 1
}
