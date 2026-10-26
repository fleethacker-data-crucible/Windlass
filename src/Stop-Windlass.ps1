# Stops every running Windlass server. Windows PowerShell 5.1 only. ASCII only.
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\Stop-Windlass.ps1
# Finds servers on Windlass's port block (8777-8779) and on ports recorded in logs\windlass-server-<pid>.json (a
# --port outside the block), asks each to stop over POST /__fleethacker/shutdown, and only if that fails stops the
# pid it reported (verified by FleetPort.psm1). Stale runtime files (dead pid) are deleted.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
Import-Module (Join-Path $here 'FleetPort.psm1') -Force

$block = Get-FleetPortBlock -Tool 'windlass' -RegistryPath (Join-Path $here 'FleetPorts.psd1')
$ports = New-Object System.Collections.Generic.List[int]
for ($p = $block.Start; $p -le $block.End; $p++) { $ports.Add($p) }
$logs = Join-Path $root 'logs'
if (Test-Path -LiteralPath $logs) {
    foreach ($f in Get-ChildItem -LiteralPath $logs -Filter 'windlass-server-*.json' -File) {
        try { $rt = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json } catch { continue }
        if (-not (Get-Process -Id ([int]$rt.pid) -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $f.FullName -ErrorAction SilentlyContinue; continue }
        if (-not $ports.Contains([int]$rt.port)) { $ports.Add([int]$rt.port) }
    }
}

$stopped = 0
foreach ($p in $ports) {
    $id = Test-FleetSelf -Port $p -Tool 'windlass'
    if (-not $id -or -not $id.IsSelf) { continue }
    Write-Host "Stopping Windlass $($id.Version) on port $p (pid $($id.Pid))..."
    try {
        if (Stop-FleetInstance -Instance $id) { $stopped++ } else { Write-Host "  port $p did not stop" -ForegroundColor Yellow }
    } catch { Write-Host "  $($_.Exception.Message)" -ForegroundColor Yellow }
}
if ($stopped -eq 0) { Write-Host 'No running Windlass found.' } else { Write-Host "Stopped $stopped Windlass server(s)." }
