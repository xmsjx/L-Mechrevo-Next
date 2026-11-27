param(
    [int]$Seconds = 150,
    [string]$Dir = 'D:\openLM\tmp-ota',
    [int]$Port = 13688
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Path $Dir -Force | Out-Null
$etl = Join-Path $Dir 'gcu-local.etl'
$txt = Join-Path $Dir 'gcu-local.txt'
if (Test-Path $etl) { Remove-Item $etl -Force }
if (Test-Path $txt) { Remove-Item $txt -Force }
& pktmon filter remove            | Out-Null
& pktmon filter add GCU -t TCP -p $Port | Out-Null
& pktmon start --capture --pkt-size 0 -f $etl | Out-Null
"=== capturing loopback tcp/$Port for $Seconds s ==="
Start-Sleep -Seconds $Seconds
& pktmon stop | Out-Null
& pktmon format $etl -o $txt | Out-Null
if (Test-Path $txt) {
    $len = (Get-Item $txt).Length
    "=== converted: $txt ($len bytes) ==="
    $hits = Select-String -LiteralPath $txt -Pattern '51821b7e' -SimpleMatch -ErrorAction SilentlyContinue
    "machine-code hits: " + @($hits).Count
} else { '=== conversion failed ===' }
