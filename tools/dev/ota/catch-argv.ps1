param(
    [int]$Seconds = 180,
    [string]$LogPath = 'D:\openLM\tmp-ota\argv-capture.log'
)
$ErrorActionPreference = 'Continue'
$deadline = (Get-Date).AddSeconds($Seconds)
$seen = @{}
"=== argv capture: waiting for BIOS_OTA.exe (up to $Seconds s) ==="
while ((Get-Date) -lt $deadline) {
    $procs = Get-CimInstance Win32_Process -Filter "Name='BIOS_OTA.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        $key = [string]$p.ProcessId
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff') + '  pid=' + $p.ProcessId + '  ppid=' + $p.ParentProcessId + '  cmd=' + $p.CommandLine
            Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
            Write-Output $line
        }
    }
    Start-Sleep -Milliseconds 40
}
"=== done: $($seen.Count) hit(s), log at $LogPath ==="
