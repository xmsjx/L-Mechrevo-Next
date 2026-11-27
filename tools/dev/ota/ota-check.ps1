<#
  Query the vendor update channels (BIOS / control center / VBIOS), compare versions,
  optionally download the payload and verify its SHA256.

  Usage:
    .\ota-check.ps1                       query all channels and compare versions
    .\ota-check.ps1 -Menu                 interactive menu: pick a channel, then download on demand
    .\ota-check.ps1 -Download             download payloads next to this script and verify
    .\ota-check.ps1 -Force                download the current server payload even when up to date
  .\ota-check.ps1 -Download -MakeExe    also save a .EXE copy of the downloaded payload
    .\ota-check.ps1 -Channel BIOS         limit to one channel: BIOS / CC / VBIOS
    .\ota-check.ps1 -Download -OutDir D:\ota
    .\ota-check.ps1 -MachineCode a1b2c3d4 -AppType 17 -Cm MRO
    .\ota-check.ps1 -Raw                  also print the raw manifest text

  The machine code is read from the local firmware inventory (SMBIOS type 45, the
  "BIOS Firmware" entry, first field of its inventory id). Only when that is
  unavailable does it fall back to machine_code in ota-check.json.
  Read-only except when downloading.
#>
[CmdletBinding()]
param(
    [string]$MachineCode = '',
    [int]$AppType = 0,
    [string]$Cm = '',
    [string]$Channel = '',
    [switch]$Raw,
    [switch]$Download,
    [switch]$Force,
    [switch]$MakeExe,
    [switch]$Menu,
    [string]$OutDir = '',
    [int]$TimeoutSec = 20,
    [int]$Retry = 4
)

$ErrorActionPreference = 'Stop'
$OtaHost = 'ota.mechrevo.com'
$OtaPort = 2080
$OtaUa = 'ota/quarterly'

$cfgPath = Join-Path $PSScriptRoot 'ota-check.json'
$cfg = $null
if (Test-Path -LiteralPath $cfgPath) { $cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json }

$MachineCodeSource = ''
if ($MachineCode) { $MachineCodeSource = 'argument' }
if ($AppType -le 0) { if ($cfg -and $cfg.app_type) { $AppType = [int]$cfg.app_type } else { $AppType = 17 } }
if (-not $OutDir) { $OutDir = $PSScriptRoot }
if ($Force) { $Download = $true }

function Get-SmbiosMachineCode {
    try {
        if (-not ('OtaSmbios.Native' -as [type])) {
            Add-Type -Namespace OtaSmbios -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true)]
public static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[] buffer, uint size);
'@
        }
        $size = [OtaSmbios.Native]::GetSystemFirmwareTable(0x52534D42, 0, $null, 0)
        if ($size -le 8) { return '' }
        $buf = New-Object byte[] $size
        [void][OtaSmbios.Native]::GetSystemFirmwareTable(0x52534D42, 0, $buf, $size)
        $end = 8 + [int][BitConverter]::ToUInt32($buf, 4)
        $i = 8
        $fallback = ''
        while ($i -lt $end - 4) {
            $type = $buf[$i]
            $len = $buf[$i + 1]
            if ($len -lt 4) { break }
            $j = $i + $len
            while ($j -lt $end - 1 -and -not ($buf[$j] -eq 0 -and $buf[$j + 1] -eq 0)) { $j++ }
            $strings = @()
            $p = $i + $len
            while ($p -lt $j) {
                $e = $p
                while ($e -lt $j -and $buf[$e] -ne 0) { $e++ }
                $strings += [System.Text.Encoding]::ASCII.GetString($buf, $p, $e - $p)
                $p = $e + 1
            }
            $j = $j + 2
            if ($type -eq 45 -and $strings.Count -ge 3) {
                $nameIdx = $buf[$i + 4]
                $idIdx = $buf[$i + 7]
                $nm = if ($nameIdx -ge 1 -and $nameIdx -le $strings.Count) { $strings[$nameIdx - 1] } else { '' }
                $fid = if ($idIdx -ge 1 -and $idIdx -le $strings.Count) { $strings[$idIdx - 1] } else { '' }
                if ($fid) {
                    try {
                        $code = '{0:x8}' -f [Convert]::ToUInt32(($fid -split '-')[0], 16)
                        if ($nm -eq 'BIOS Firmware') { return $code }
                        if (-not $fallback) { $fallback = $code }
                    } catch { }
                }
            }
            $i = $j
        }
        return $fallback
    } catch {
        return ''
    }
}

function Get-BiosVersion {
    try { return ([string](Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop).SMBIOSBIOSVersion).Trim() } catch { return '' }
}

function Get-ProjectFamily([string]$version) {
    if ($version -match '^[0-9]+\.[0-9]+\.[0-9]+([A-Za-z]+)[0-9]+') { return $Matches[1].ToUpperInvariant() }
    if ($version -match '([A-Za-z]{2,})$') { return $Matches[1].ToUpperInvariant() }
    return 'MRO'
}

function Get-DisplayDeviceId {
    try {
        foreach ($h in (Get-CimInstance -ClassName Win32_PnPEntity -Filter "PNPClass='Display'" -ErrorAction Stop).HardwareID) {
            if ($h -match 'VEN_10DE&DEV_([0-9A-Fa-f]{4})') { return $Matches[1].ToUpperInvariant() }
        }
    } catch { }
    return ''
}

function Get-ControlVersion {
    foreach ($k in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (-not (Test-Path $k)) { continue }
        $e = Get-ChildItem -Path $k -ErrorAction SilentlyContinue | ForEach-Object {
            Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
        } | Where-Object { $_.DisplayVersion -and (($_.InstallLocation -like '*\OEM\*') -or ($_.DisplayIcon -like '*\OEM\*') -or ($_.DisplayName -match 'GamingCenter|Control ?Center')) } |
            Sort-Object DisplayVersion -Descending | Select-Object -First 1
        if ($e) { return $e.DisplayVersion }
    }
    return ''
}

function Get-Manifest([string]$path) {
    $url = 'http://' + $OtaHost + ':' + $OtaPort + $path
    $last = ''
    for ($n = 0; $n -lt $Retry; $n++) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec $TimeoutSec -Headers @{ 'User-Agent' = $OtaUa }
            return [pscustomobject]@{ Ok = $true; Url = $url; Text = $r.Content; Error = '' }
        } catch {
            $last = $_.Exception.Message
            if ($_.Exception.Response) {
                $sc = [int]$_.Exception.Response.StatusCode
                if ($sc -eq 404) { return [pscustomobject]@{ Ok = $false; Url = $url; Text = ''; Error = 'no content on server (404)' } }
                if ($sc -eq 403) { return [pscustomobject]@{ Ok = $false; Url = $url; Text = ''; Error = 'directory only (403)' } }
                $last = 'http ' + $sc
            }
            Start-Sleep -Milliseconds 400
        }
    }
    return [pscustomobject]@{ Ok = $false; Url = $url; Text = ''; Error = $last }
}

function Compare-Ver([string]$a, [string]$b) {
    if ($a -notmatch '^[0-9]+\.[0-9]+\.[0-9]+') { return 0 }
    $x = $Matches[0].Split('.')
    if ($b -notmatch '^[0-9]+\.[0-9]+\.[0-9]+') { return 0 }
    $y = $Matches[0].Split('.')
    for ($k = 0; $k -lt 3; $k++) {
        if ([int]$x[$k] -lt [int]$y[$k]) { return -1 }
        if ([int]$x[$k] -gt [int]$y[$k]) { return 1 }
    }
    return 0
}

function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() }
    finally { $fs.Dispose(); $sha.Dispose() }
}

function Test-PeImage([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $hdr = New-Object byte[] 64
        if ($fs.Read($hdr, 0, 64) -lt 64) { return $false }
        if (-not ($hdr[0] -eq 0x4D -and $hdr[1] -eq 0x5A)) { return $false }
        $peOff = [BitConverter]::ToInt32($hdr, 0x3C)
        if ($peOff -le 0 -or $peOff -gt 4096) { return $false }
        $fs.Position = $peOff
        $sig = New-Object byte[] 4
        if ($fs.Read($sig, 0, 4) -lt 4) { return $false }
        return ($sig[0] -eq 0x50 -and $sig[1] -eq 0x45 -and $sig[2] -eq 0 -and $sig[3] -eq 0)
    } finally { $fs.Dispose() }
}

function Save-WithExeName([string]$path, [bool]$ask) {
    $dir = Split-Path -Parent $path
    $name = [System.IO.Path]::GetFileNameWithoutExtension($path)
    $ext = [System.IO.Path]::GetExtension($path)
    if ($ext -ieq '.exe') { return }
    if (-not (Test-PeImage $path)) { Write-Host ('  not a PE image, keep name as is: ' + (Split-Path -Leaf $path)); return }
    $target = Join-Path $dir ($name + '.EXE')
    if (Test-Path -LiteralPath $target) {
        if ($ask) {
            $over = Read-Host ('  ' + (Split-Path -Leaf $target) + ' exists, overwrite? (y/N)')
            if ($null -eq $over -or ([string]$over).Trim() -notmatch '^(y|Y|yes|YES)$') { Write-Host '  skipped exe copy'; return }
        }
        Remove-Item -LiteralPath $target -Force
    }
    Copy-Item -LiteralPath $path -Destination $target -Force
    $h = Get-Sha256 $target
    Write-Host ('  exe  : ' + $target)
    Write-Host ('  sha256: ' + $h + '  (same content as the downloaded file)')
}

function Save-Payload($cand) {
    if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
    $url = 'http://' + $OtaHost + ':' + $OtaPort + $cand.Path
    $dest = Join-Path $OutDir (Split-Path $cand.Path -Leaf)
    Write-Host ('downloading ' + $url)
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -TimeoutSec 900 -Headers @{ 'User-Agent' = $OtaUa }
    Write-Host ('  saved : ' + $dest)
    if ($cand.Sha) {
        $actual = Get-Sha256 $dest
        $ok = ($actual -eq $cand.Sha.ToLowerInvariant())
        Write-Host ('  sha256: ' + $actual + '  match: ' + $ok)
        if (-not $ok) { return [pscustomobject]@{ Ok = $false; Path = $dest } }
    }
    return [pscustomobject]@{ Ok = $true; Path = $dest }
}

# --- local facts ----------------------------------------------------------
if (-not $MachineCode) {
    $MachineCode = Get-SmbiosMachineCode
    if ($MachineCode) { $MachineCodeSource = 'SMBIOS type45' }
}
if (-not $MachineCode -and $cfg -and $cfg.machine_code) {
    $MachineCode = [string]$cfg.machine_code
    $MachineCodeSource = 'ota-check.json'
}

$biosVersion = Get-BiosVersion
if (-not $Cm) { $Cm = Get-ProjectFamily $biosVersion }
$ccVersion = Get-ControlVersion
$vdev = Get-DisplayDeviceId

$localMap = @{ 'BIOS' = $biosVersion; 'CC' = $ccVersion; 'VBIOS' = '' }
$channels = @(
    [pscustomobject]@{ Key = 'BIOS';  Label = 'BIOS';           Path = '/biosUp/' + $Cm + '/' + $MachineCode + '/' + $Cm + $MachineCode + '.json'; Local = $biosVersion },
    [pscustomobject]@{ Key = 'CC';    Label = 'Control center'; Path = '/APup/' + $AppType + '/' + $MachineCode + '/APUpdate.json'; Local = $ccVersion },
    [pscustomobject]@{ Key = 'VBIOS'; Label = 'VBIOS';          Path = $(if ($vdev) { '/vbiosUp/' + $Cm + '/' + $vdev + '.json' } else { '' }); Local = '' }
)

function Invoke-Channel($ch) {
    Write-Host ''
    Write-Host ('--- ' + $ch.Label + ' ---')
    if (-not $ch.Path) { Write-Host '  skipped: no display adapter id found'; return $null }
    Write-Host ('  path : ' + $ch.Path)
    $m = Get-Manifest $ch.Path
    if (-not $m.Ok) { Write-Host ('  result: ' + $m.Error); return $null }
    if ($Raw) { Write-Host $m.Text }
    try { $j = $m.Text | ConvertFrom-Json } catch { Write-Host '  result: unparsable manifest'; return $null }

    $remote = ''; $file = ''; $sha = ''
    foreach ($p in 'bios_version', 'cc_version', 'vbios_version', 'version') { if (-not $remote -and $j.$p) { $remote = [string]$j.$p } }
    foreach ($p in 'bios_path', 'cc_path', 'vbios_path', 'path') { if (-not $file -and $j.$p) { $file = [string]$j.$p } }
    foreach ($p in 'bios_sha256', 'cc_sha256', 'vbios_sha256', 'sha256') { if (-not $sha -and $j.$p) { $sha = [string]$j.$p } }
    Write-Host ('  remote: ' + $remote)
    if ($file) { Write-Host ('  file  : ' + $file) }
    if ($sha) { Write-Host ('  sha256: ' + $sha) }

    $verdict = 'unknown'
    if ($ch.Local) {
        $c = Compare-Ver $ch.Local $remote
        $verdict = if ($c -ge 0) { 'up to date (or newer)' } else { 'update available' }
        Write-Host ('  local : ' + $ch.Local + '   verdict: ' + $verdict)
    }
    if (-not $file) { return $null }
    return [pscustomobject]@{ Name = $ch.Label; Path = $file; Sha = $sha; Verdict = $verdict }
}

function Select-Channels([string]$sel) {
    $sel = ([string]$sel).Trim().ToUpperInvariant()
    if (-not $sel) { return @() }
    if ($sel -eq 'A' -or $sel -eq '4' -or $sel -eq 'ALL') { return $channels }
    $out = @()
    foreach ($part in ($sel -split '[, ]+')) {
        switch ($part) {
            '1' { $out += $channels[0] }
            '2' { $out += $channels[1] }
            '3' { $out += $channels[2] }
            'B' { $out += $channels[0] }
            'C' { $out += $channels[1] }
            'V' { $out += $channels[2] }
        }
    }
    return $out
}

function Show-Header {
    Write-Host ('local BIOS      : ' + $(if ($biosVersion) { $biosVersion } else { 'unknown' }))
    Write-Host ('local control   : ' + $(if ($ccVersion) { $ccVersion } else { 'unknown' }))
    Write-Host ('machine code    : ' + $(if ($MachineCode) { $MachineCode } else { 'unavailable' }) + '   (source: ' + $(if ($MachineCodeSource) { $MachineCodeSource } else { 'none' }) + ')   cm: ' + $Cm + '   appType: ' + $AppType)
    if ($vdev) { Write-Host ('display dev id  : ' + $vdev) }
}

# --- main -----------------------------------------------------------------
if (-not $MachineCode) {
    Write-Host 'cannot read the firmware inventory id on this machine; pass -MachineCode explicitly'
    if ($Menu) { Write-Host ''; [void](Read-Host 'press enter to close') }
    exit 2
}

if ($Menu) {
    $pending = @()
    while ($true) {
        Write-Host ''
        Write-Host '================================================'
        Show-Header
        Write-Host '------------------------------------------------'
        Write-Host '  1  download BIOS            (/biosUp)'
        Write-Host '  2  download Control center  (/APup)'
        Write-Host '  3  download VBIOS           (/vbiosUp)'
        Write-Host '  4  list all channels (no download)'
        Write-Host ''
        Write-Host '  downloads are saved next to this script; the script never runs them.'
        Write-Host '  after a download you can save an extra copy with .EXE extension.'
        Write-Host '  0  exit'
        $sel = Read-Host 'select'
        if ($null -eq $sel) { break }
        $sel = ([string]$sel).Trim()
        if ($sel -eq '' -or $sel -eq '0') { break }
        $picked = Select-Channels $sel
        if (-not $picked -or $picked.Count -eq 0) { Write-Host 'unknown selection'; continue }
        foreach ($ch in $picked) {
            $cand = Invoke-Channel $ch
            if (-not $cand) { continue }
            if ($sel -eq '4' -or $sel -eq 'A') { continue }
            $upToDate = ($cand.Verdict -eq 'up to date (or newer)')
            $prompt = if ($upToDate) { 'already up to date; force download anyway? (y/N)' } else { 'download ' + $cand.Path + ' ? (Y/n)' }
            $ans = Read-Host $prompt
            if ($null -eq $ans) { continue }
            $ans = ([string]$ans).Trim()
            if ($upToDate) {
                if ($ans -notmatch '^(y|Y|yes|YES)$') { Write-Host '  skipped'; continue }
            } else {
                if ($ans -match '^(n|N|no|NO)$') { Write-Host '  skipped'; continue }
            }
            $res = Save-Payload $cand
            if ($res.Ok) {
                $ans2 = Read-Host 'save a copy with .EXE extension? (Y/n)'
                if ($null -eq $ans2) { continue }
                if (([string]$ans2).Trim() -notmatch '^(n|N|no|NO)$') { Save-WithExeName $res.Path $true }
            }
        }
    }
    exit 0
}

Show-Header
$list = $channels
if ($Channel) {
    $list = Select-Channels $(if ($Channel -eq 'CC') { '2' } elseif ($Channel -eq 'VBIOS') { '3' } else { '1' })
}
$needDownload = @()
foreach ($ch in $list) {
    $cand = Invoke-Channel $ch
    if (-not $cand) { continue }
    if ($Download -and ($Force -or $cand.Verdict -ne 'up to date (or newer)')) { $needDownload += $cand }
}
if ($Download) {
    if (-not $needDownload) { Write-Host ''; Write-Host 'nothing to download' }
    foreach ($c in $needDownload) {
        $res = Save-Payload $c
        if ($res.Ok -and $MakeExe) { Save-WithExeName $res.Path $false }
    }
}
exit 0
