# 构建「增量更新包」：把指定文件 + manifest.json 打进 zip。
#
# manifest.json 格式（更新器校验用）：
#   { "files": [ { "path": "L-Mechrevo.dll", "sha256": "<hex>", "size": 123456 }, ... ] }
# 更新器会逐条核对：同名条目必须存在且字节数一致，否则报「更新包文件缺失或大小不符」。
#
# 用法：
#   pwsh -File installer-src\make_update_zip.ps1 -SourceDir D:\openLM\rebuild\lm_fix_build `
#        -Output D:\openLM\dist\update\LM-beta23.8.3-update.zip `
#        -Files L-Mechrevo.dll,MechrevoLite.Core.dll -ExtraFiles D:\openLM\dist\update\更新说明.txt
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)]$Files,
    $ExtraFiles = @()
)

$ErrorActionPreference = 'Stop'

function Split-ParamList($value) {
    $out = @()
    foreach ($item in @($value)) {
        foreach ($part in ($item -split '[,\s]+')) {
            $t = $part.Trim()
            if ($t) { $out += $t }
        }
    }
    return $out
}

$Files = Split-ParamList $Files
$ExtraFiles = Split-ParamList $ExtraFiles
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Output = [System.IO.Path]::GetFullPath($Output)
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("lm-upd-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

function Copy-Checked([string]$fullPath, [string]$entryName) {
    if (-not (Test-Path -LiteralPath $fullPath)) { throw "缺少文件：$fullPath" }
    $dest = Join-Path $tmp $entryName
    $dir = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item -LiteralPath $fullPath -Destination $dest -Force
}

$manifestFiles = @()
foreach ($name in $Files) {
    Copy-Checked (Join-Path $SourceDir $name) $name
    $item = Get-Item -LiteralPath (Join-Path $SourceDir $name)
    $sha = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestFiles += [ordered]@{ path = ($name -replace '\\', '/'); sha256 = $sha; size = [long]$item.Length }
}

foreach ($extra in $ExtraFiles) {
    $entryName = Split-Path -Leaf $extra
    Copy-Checked $extra $entryName
}

$manifest = [ordered]@{ generated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); files = $manifestFiles }
$manifestJson = $manifest | ConvertTo-Json -Depth 5
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $tmp 'manifest.json'), $manifestJson, $utf8NoBom)

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($tmp, $Output, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $tmp -Recurse -Force

Write-Output ("[update-zip] " + (Split-Path -Leaf $Output))
foreach ($f in $manifestFiles) {
    Write-Output ("  {0}  size={1}  sha256={2}" -f $f.path, $f.size, $f.sha256.Substring(0, 16))
}
