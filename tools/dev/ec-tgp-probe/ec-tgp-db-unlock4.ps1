# DB 解锁 v4：SetULong 单独写无效 ⇒ 试「两步协议」与 FireULong 的 Hack 参数。
#   已知签名：GetULong(Data:UInt32) / SetULong(Data:UInt32) / FireULong(Hack:UInt32)
#            GetSetULong(Data:UInt64,Return:UInt32) / GetButton(Data:UInt64,Return:UInt32)
# 只针对 0x743，每次写完回读；成功即停。日志：%TEMP%\ec-db-log4.txt
# 用法（管理员）：powershell -NoProfile -ExecutionPolicy Bypass -File ec-tgp-db-unlock4.ps1

param([string]$LogPath = "", [int]$Addr = 0x743, [int]$TargetValue = 7)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-log4.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

try {
    Log "=== v4 begin ==="
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Log "不是管理员 ⇒ 退出"; exit 5 }

    $cimAll = @(Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcpiTest_MULong' -ErrorAction Stop)
    $cim = $cimAll | Where-Object { $_.InstanceName -like '*PNP0C14\1_1*' } | Select-Object -First 1
    if (-not $cim) { $cim = $cimAll | Select-Object -First 1 }
    Log ("实例：" + $cim.InstanceName)

    function Read-Ec([int]$a) {
        $r = Invoke-CimMethod -InputObject $cim -MethodName GetSetULong -Arguments @{ Data = [uint64](1099511627776 + $a) }
        if ($null -eq $r) { return $null }
        return [uint64]$r.Return
    }
    function Show([string]$tag) {
        $v = Read-Ec $Addr
        Log ("[{0}] 0x{1:X3}=0x{2:X4}（低字节 0x{3:X2}）" -f $tag, $Addr, $v, ($v -band 0xFF))
        return $v
    }
    function Call([string]$method, [string]$pname, [object]$value) {
        try {
            $r = Invoke-CimMethod -InputObject $cim -MethodName $method -Arguments @{ $pname = $value } -ErrorAction Stop
            $ret = if ($null -ne $r) { $r.Return } else { "null" }
            Log ("  {0}({1}=0x{2:X}) → 返回 {3}" -f $method, $pname, $value, $ret)
            return $ret
        }
        catch {
            Log ("  {0}({1}=0x{2:X}) 失败：{3}" -f $method, $pname, $value, $_.Exception.Message)
            return $null
        }
    }

    $before = Show "before"
    $orig = [int]($before -band 0xFF)
    Log ("原值=0x{0:X2}" -f $orig)

    $ok = $false

    # ── 方案 A：两步协议 —— GetULong(addr) 设地址，再 SetULong(value) 写值 ──
    Log "=== 方案 A：GetULong(addr) → SetULong(value) ==="
    Call 'GetULong' 'Data' ([uint32]$Addr) | Out-Null
    Call 'SetULong' 'Data' ([uint32]$TargetValue) | Out-Null
    Start-Sleep -Milliseconds 400
    $v = Show "A/after"
    if ([int]($v -band 0xFF) -eq $TargetValue) { $ok = $true; Log "✅ 方案 A 成功" }

    # ── 方案 B：FireULong(Hack=addr) 再 SetULong(value) ──
    if (-not $ok) {
        Log "=== 方案 B：FireULong(Hack=addr) → SetULong(value) ==="
        Call 'FireULong' 'Hack' ([uint32]$Addr) | Out-Null
        Call 'SetULong' 'Data' ([uint32]$TargetValue) | Out-Null
        Start-Sleep -Milliseconds 400
        $v = Show "B/after"
        if ([int]($v -band 0xFF) -eq $TargetValue) { $ok = $true; Log "✅ 方案 B 成功" }
    }

    # ── 方案 C：FireULong(Hack = 地址+值 组合) ──
    if (-not $ok) {
        foreach ($enc in @(
            @{ v = [uint32](($TargetValue -shl 16) -bor $Addr); n = 'value<<16|addr' },
            @{ v = [uint32](($TargetValue -shl 24) -bor $Addr); n = 'value<<24|addr' },
            @{ v = [uint32](($Addr -shl 8) -bor $TargetValue); n = 'addr<<8|value' },
            @{ v = [uint32](($Addr -shl 16) -bor $TargetValue); n = 'addr<<16|value' }
        )) {
            Log ("=== 方案 C · FireULong(Hack=0x{0:X8} · {1}) ===" -f $enc.v, $enc.n)
            Call 'FireULong' 'Hack' $enc.v | Out-Null
            Start-Sleep -Milliseconds 350
            $v = Show ("C/" + $enc.n)
            if ([int]($v -band 0xFF) -eq $TargetValue) { $ok = $true; Log ("✅ 方案 C 成功（{0}）" -f $enc.n); break }
        }
    }

    # ── GPU 监视 8 秒 ──
    $smi = 'C:\Windows\System32\nvidia-smi.exe'
    Log "=== 监视 8 秒 ==="
    $prev = ""
    for ($i = 0; $i -lt 8; $i++) {
        $cur = (& $smi --query-gpu=enforced.power.limit,power.draw --format=csv,noheader 2>$null)
        if ($cur -and $cur -ne $prev) { Log ("  " + $cur.Trim()); $prev = $cur }
        Start-Sleep -Seconds 1
    }

    $final = Show "final"
    $finalLow = [int]($final -band 0xFF)
    Log ("=== 结论：0x{0:X3}=0x{1:X2}（目标 0x{2:X2}）⇒ {3} · 原值 0x{4:X2} ===" -f $Addr, $finalLow, $TargetValue, ($(if ($ok) { '写入成功' } else { '四个方案都失败' })), $orig)
    Log "=== done ==="
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    Log ("位置：" + $_.InvocationInfo.PositionMessage)
    exit 1
}
