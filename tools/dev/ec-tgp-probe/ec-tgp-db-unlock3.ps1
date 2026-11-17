# DB 解锁 v3：写方法探测。方法签名已知：
#   GetULong(Data:UInt32) / SetULong(Data:UInt32) / FireULong(Hack:UInt32)
#   GetSetULong(Data:UInt64,Return:UInt32) / GetButton(Data:UInt64,Return:UInt32)
# ⇒ 写只能用 32 位（SetULong / FireULong），需要找出「地址+值」在 32 位里的排布。
# 本脚本只针对 0x743 一个地址做候选编码尝试，每次写完立刻回读；成功即停。
# 日志：%TEMP%\ec-db-log3.txt
# 用法（管理员）：powershell -NoProfile -ExecutionPolicy Bypass -File ec-tgp-db-unlock3.ps1

param(
    [string]$LogPath = "",
    [int]$Addr = 0x743,
    [int]$TargetValue = 7
)

$ErrorActionPreference = "Continue"
$script:LogFile = if ($LogPath) { $LogPath } else { Join-Path $env:TEMP 'ec-db-log3.txt' }
Remove-Item $script:LogFile -ErrorAction SilentlyContinue

function Log([string]$msg) {
    $line = ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg)
    Write-Host $line
    try { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

try {
    Log "=== v3 begin ==="
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Log "不是管理员 ⇒ 退出"; exit 5 }

    $cimAll = @(Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcpiTest_MULong' -ErrorAction Stop)
    $cim = $cimAll | Where-Object { $_.InstanceName -like '*PNP0C14\1_1*' } | Select-Object -First 1
    if (-not $cim) { $cim = $cimAll | Select-Object -First 1 }
    Log ("实例：" + $cim.InstanceName)

    # 读：GetSetULong(Data = (1<<40) | addr) —— 已验证可用
    function Read-Ec([int]$a) {
        $data = [uint64](1099511627776 + $a)   # (1<<40) + addr
        $r = Invoke-CimMethod -InputObject $cim -MethodName GetSetULong -Arguments @{ Data = $data }
        if ($null -eq $r) { return $null }
        return [uint64]$r.Return
    }
    function Show([string]$tag) {
        $v = Read-Ec $Addr
        Log ("[{0}] 0x{1:X3}=0x{2:X4}（低字节 0x{3:X2}）" -f $tag, $Addr, $v, ($v -band 0xFF))
        return $v
    }

    $before = Show "before"
    $orig = [int]($before -band 0xFF)
    Log ("原值=0x{0:X2}" -f $orig)

    # 候选编码（全部把地址放在低 16 位，值放高位 —— ⛔ 不尝试反向，避免写到别的地址）
    $candidates = @(
        @{ Method = 'SetULong';  Value = [uint32](($TargetValue -shl 16) -bor $Addr);  Note = 'SetULong  value<<16 | addr' },
        @{ Method = 'SetULong';  Value = [uint32](($TargetValue -shl 24) -bor $Addr);  Note = 'SetULong  value<<24 | addr' },
        @{ Method = 'SetULong';  Value = [uint32](($TargetValue -shl 8)  -bor $Addr);  Note = 'SetULong  value<<8  | addr' },
        @{ Method = 'FireULong'; Value = [uint32](($TargetValue -shl 16) -bor $Addr);  Note = 'FireULong value<<16 | addr' },
        @{ Method = 'FireULong'; Value = [uint32](($TargetValue -shl 24) -bor $Addr);  Note = 'FireULong value<<24 | addr' }
    )

    $ok = $false
    foreach ($c in $candidates) {
        Log ("=== 尝试 {0} · Data=0x{1:X8} ===" -f $c.Note, $c.Value)
        try {
            $r = Invoke-CimMethod -InputObject $cim -MethodName $c.Method -Arguments @{ Data = $c.Value } -ErrorAction Stop
            Log ("  调用返回：" + ($r.Return))
        }
        catch { Log ("  调用失败：" + $_.Exception.Message) }
        Start-Sleep -Milliseconds 350
        $now = Show ("after/" + $c.Method)
        $low = [int]($now -band 0xFF)
        if ($low -eq $TargetValue) {
            Log ("✅ 成功：0x{0:X3} 变成 0x{1:X2}（{2}）" -f $Addr, $low, $c.Note)
            $ok = $true
            break
        }
        Log ("  ⇒ 回读 0x{0:X2}，未生效" -f $low)
    }

    # GPU 监视 8 秒
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
    Log ("=== 结论：0x{0:X3}=0x{1:X2}（目标 0x{2:X2}）⇒ {3} · 原值 0x{4:X2} · 回滚用 -Restore（写回原值需要同款编码）===" -f $Addr, $finalLow, $TargetValue, ($(if ($ok) { '写入成功' } else { '全部候选失败' })), $orig)

    if ($ok -and $finalLow -ne $orig) {
        Log ("⚠️ 值已改变（原 0x{0:X2}）⇒ 回滚：切一次性能模式/重启 LM（固件会重写），或告知我按成功的编码写回" -f $orig)
    }

    Log "=== done ==="
    exit 0
}
catch {
    Log ("异常：" + $_.Exception.GetType().Name + " · " + $_.Exception.Message)
    Log ("位置：" + $_.InvocationInfo.PositionMessage)
    Log "=== failed ==="
    exit 1
}
