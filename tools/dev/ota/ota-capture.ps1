<#
  获取本机在官方更新服务里的机器码，写入 ota-check.json。
  做法：把 ota.mechrevo.com 临时指向本机并监听 2080，等控制台发出检查更新请求，从请求路径里取出机器码，然后立即还原 hosts。
  用法：
    .\ota-capture.ps1                    手动模式：提示你打开控制台点“检查更新”
    .\ota-capture.ps1 -Launch            自动模式：脚本自己启动控制台
    .\ota-capture.ps1 -TimeoutSec 300    延长等待
  需要管理员权限（要改 hosts）。
#>
[CmdletBinding()]
param(
    [int]$Port = 2080,
    [switch]$Launch,
    [string]$ConsolePath = '',
    [int]$TimeoutSec = 180,
    [string]$ConfigPath = ''
)

$ErrorActionPreference = 'Stop'
$crlf = [string][char]13 + [string][char]10
$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$backup = Join-Path $PSScriptRoot 'hosts.bak'
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'ota-check.json' }

Copy-Item -LiteralPath $hostsPath -Destination $backup -Force

function Find-ConsoleExe {
    $dirs = Get-ChildItem 'C:\Program Files\WindowsApps' -Directory -Filter 'ControlCenter3_*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending
    foreach ($d in $dirs) {
        $exe = Join-Path $d.FullName 'GamingCenter3_Cross.exe'
        if (Test-Path -LiteralPath $exe) { return $exe }
    }
    foreach ($c in @('C:\Program Files\OEM\机械革命电竞控制台\ControlCenterU.exe', 'C:\Program Files\OEM\机械革命电竞控制台\GamingCenter3.exe')) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return ''
}

function Get-PortOwner([int]$p) {
    try {
        $c = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction Stop
        if ($c) { return ($c | Select-Object -First 1).OwningProcess }
    } catch { }
    return 0
}

$busy = Get-PortOwner $Port
if ($busy) { throw ('端口 ' + $Port + ' 已被进程 ' + $busy + ' 占用，先关掉它再运行') }

$changed = $false
$launched = $null
$listener = $null
$captured = $null

try {
    Write-Host '1/4 临时把 ota.mechrevo.com 指向 127.0.0.1'
    $has = (Get-Content -LiteralPath $hostsPath -ErrorAction Stop) | Select-String -Pattern 'ota\.mechrevo\.com' -Quiet
    if (-not $has) {
        Add-Content -LiteralPath $hostsPath -Value '127.0.0.1 ota.mechrevo.com'
        $changed = $true
    } else {
        Write-Host '     hosts 里已有该记录，沿用'
    }
    & ipconfig /flushdns | Out-Null

    Write-Host '2/4 在本机 2080 端口等待请求'
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, $Port)
    $listener.Start()

    if ($Launch) {
        $exe = $ConsolePath
        if (-not $exe) { $exe = Find-ConsoleExe }
        if (-not $exe) { throw '没找到控制台程序，请用 -ConsolePath 指定，或不加 -Launch 手动打开控制台' }
        Write-Host ('     启动控制台：' + $exe)
        $launched = Start-Process -FilePath $exe -PassThru -ErrorAction Stop
    } else {
        Write-Host '     请现在打开官方控制台并点一次“检查更新”'
    }

    Write-Host ('     等待请求（最多 ' + $TimeoutSec + ' 秒）')
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $buf = New-Object byte[] 8192

    while ((Get-Date) -lt $deadline -and -not $captured) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 150; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 8000
            $n = $stream.Read($buf, 0, $buf.Length)
            if ($n -le 0) { continue }
            $head = [System.Text.Encoding]::UTF8.GetString($buf, 0, $n)
            $firstLine = ($head -split $crlf)[0]
            Write-Host ('     收到：' + $firstLine)
            $m = [regex]::Match($head, '(?i)GET\s+/(APup|biosUp|vbiosUp|LbUp)/([^/\s]*)/([0-9a-f]{8})')
            if ($m.Success) {
                $captured = [pscustomobject]@{
                    Channel = $m.Groups[1].Value
                    Segment = $m.Groups[2].Value
                    Code    = $m.Groups[3].Value
                    Raw     = $firstLine
                }
            }
            $bytes = [System.Text.Encoding]::UTF8.GetBytes('{}')
            $resp = 'HTTP/1.1 200 OK' + $crlf + 'Content-Type: application/json' + $crlf + 'Content-Length: ' + $bytes.Length + $crlf + 'Connection: close' + $crlf + $crlf
            $rb = [System.Text.Encoding]::ASCII.GetBytes($resp)
            $stream.Write($rb, 0, $rb.Length)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
        } catch {
            Write-Host ('     请求处理出错：' + $_.Exception.Message)
        } finally {
            $client.Close()
        }
    }

    if (-not $captured) { throw '没有等到更新请求，检查控制台是否真的点了“检查更新”' }

    Write-Host '3/4 解析机器码'
    $cfg = $null
    if (Test-Path -LiteralPath $ConfigPath) { $cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json }
    $out = [ordered]@{}
    $out['machine_code'] = $captured.Code
    if ($captured.Channel -eq 'APup') { $out['app_type'] = [int]$captured.Segment }
    elseif ($cfg -and $cfg.PSObject.Properties['app_type']) { $out['app_type'] = [int]$cfg.app_type }
    else { $out['app_type'] = 17 }
    if ($captured.Channel -eq 'biosUp') { $out['cm'] = $captured.Segment }
    elseif ($cfg -and $cfg.PSObject.Properties['cm']) { $out['cm'] = [string]$cfg.cm }
    else { $out['cm'] = '' }
    ($out | ConvertTo-Json) | Set-Content -LiteralPath $ConfigPath -Encoding utf8

    Write-Host ('     机器码：' + $captured.Code + '   通道：' + $captured.Channel + '   段值：' + $captured.Segment)
    Write-Host ('     已写入：' + $ConfigPath)
    Write-Host '4/4 完成'
}
finally {
    if ($listener) { try { $listener.Stop() } catch { } }
    if ($changed) {
        try {
            $lines = Get-Content -LiteralPath $hostsPath | Where-Object { $_ -notmatch 'ota\.mechrevo\.com' }
            Set-Content -LiteralPath $hostsPath -Value $lines
            Write-Host 'hosts 已还原'
        } catch {
            Write-Host ('hosts 还原失败，请手动删除那行：' + $_.Exception.Message)
        }
        & ipconfig /flushdns | Out-Null
    }
    if ($launched) {
        try { if (-not $launched.HasExited) { $launched.Kill() } } catch { }
    }
}
