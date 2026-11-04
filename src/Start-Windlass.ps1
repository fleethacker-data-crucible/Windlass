# Windlass host. Windows PowerShell 5.1 only. ASCII only (PS 5.1 reads BOM-less .ps1 as ANSI).
# Started by Launch-Windlass.bat:
#   powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File src\Start-Windlass.ps1 serve --model <file.gguf> [options]
#   ... -File src\Start-Windlass.ps1 check      locality checks and compile only, then exit
#   ... -File src\Start-Windlass.ps1            (no arguments, e.g. a double-click) is serve --open: the UI opens and loads the model you pick
# serve --open also opens the browser UI once the server answers; this script does that, the engine never starts a process.
# Runs the locality checks, picks a port in Windlass's block (8777-8779, policy exclusive, edge-ops PORTS.md),
# compiles src\*.cs in memory with Add-Type (C# 5, .NET Framework 4.8, no references beyond the BCL) and calls
# [Windlass.Program]::Run. No param() block on purpose: arguments such as --model reach $args unchanged.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here

if ($PSVersionTable.PSVersion.Major -ne 5) {
    Write-Host "Run Windlass with Windows PowerShell 5.1 (powershell.exe), not $($PSVersionTable.PSVersion)." -ForegroundColor Red
    exit 1
}

# Started from PowerShell 7, powershell.exe inherits 7's module paths and then loads 7's copy of
# Microsoft.PowerShell.Utility, which fails (Import-PowerShellDataFile "is not recognized"). Keep 5.1's paths only.
$env:PSModulePath = (@($env:PSModulePath -split ';' | Where-Object { $_ -and $_ -notmatch '\\(Documents|Program Files)\\PowerShell\\|\\WindowsApps\\Microsoft\.PowerShell_' }) + (Join-Path $PSHOME 'Modules') | Select-Object -Unique) -join ';'

# Temp files (Add-Type scratch) stay inside the tool folder.
$env:TEMP = Join-Path $root 'temp\sys'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null

$version = ([string](Get-Content -LiteralPath (Join-Path $root 'VERSION') -TotalCount 1 -ErrorAction SilentlyContinue)).Trim()
if (-not $version) { $version = '0.0.0' }
$cliArgs = @($args | ForEach-Object { [string]$_ })
. (Join-Path $here 'Tray.ps1')

# Opens the browser at $Url once the server there answers /__fleethacker/id. Runs in this process on its own thread, so it
# works while the engine blocks the main one; gives up after two minutes.
function Start-BrowserWhenReady([string]$Url) {
    $rs = [runspacefactory]::CreateRunspace()
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    [void]$ps.AddScript({
        param($Url)
        for ($i = 0; $i -lt 600; $i++) {
            Start-Sleep -Milliseconds 200
            try {
                $r = [System.Net.HttpWebRequest]::Create($Url + '__fleethacker/id')
                $r.Proxy = $null
                $r.Timeout = 1000
                $resp = $r.GetResponse()
                $resp.Close()
                Start-Process $Url
                return
            } catch { Write-Verbose 'Not up yet.' }
        }
    }).AddArgument($Url)
    [void]$ps.BeginInvoke()
}

# A double-click passes no arguments: serve with the browser UI, which asks which model to load.
if ($cliArgs.Count -eq 0 -and [Environment]::UserInteractive) { $cliArgs = @('serve', '--open') }
$command = if ($cliArgs.Count -gt 0) { $cliArgs[0] } else { '' }

# serve --open at a desktop session shows a notification-area icon (Tray.ps1): the engine then outlives the browser window and
# stops only on Quit (or End session, Ctrl+C, Stop-Windlass.ps1). --no-tray keeps the old behaviour: the tab owns the server.
$trayPlan = Get-WindlassTrayPlan -CliArgs $cliArgs
$cliArgs = @($trayPlan.Args)

# Locality checks: Windlass makes no outgoing connections and starts no processes. Only Server.cs may use
# HttpListener (bound to 127.0.0.1 and localhost). No P/Invoke, no dynamic assembly loading.
function Test-Locality {
    $failures = New-Object System.Collections.Generic.List[string]
    $banned = @('WebRequest', 'WebClient', 'HttpClient', 'TcpClient', 'TcpListener', 'UdpClient', 'Socket', 'Dns.',
                'Process.Start', 'DllImport', 'Assembly.Load', 'NativeLibrary', 'Marshal.GetDelegateForFunctionPointer')
    foreach ($f in Get-ChildItem -LiteralPath $here -Filter *.cs -File) {
        $text = [IO.File]::ReadAllText($f.FullName)
        foreach ($b in $banned) {
            if ($text.IndexOf($b, [StringComparison]::Ordinal) -ge 0) { $failures.Add("$($f.Name) uses '$b'") }
        }
        if ($f.Name -ne 'Server.cs' -and $text.IndexOf('HttpListener', [StringComparison]::Ordinal) -ge 0) { $failures.Add("$($f.Name) uses HttpListener (only Server.cs may)") }
    }
    $server = [IO.File]::ReadAllText((Join-Path $here 'Server.cs'))
    foreach ($m in [regex]::Matches($server, 'Prefixes\.Add\("([^"]*)"')) {
        if ($m.Groups[1].Value -notmatch '^http://(127\.0\.0\.1|localhost):$') { $failures.Add("Server.cs binds '$($m.Groups[1].Value)'; only http://127.0.0.1 and http://localhost are allowed") }
    }
    return , $failures
}

$failures = Test-Locality
if ($failures.Count -gt 0) {
    Write-Host 'Locality checks FAILED:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

# Port: the first free port in the block, unless --port is given. A running Windlass of the same version is reused
# (reported, then this copy exits); a different version is never stopped automatically (exclusive).
if ($command -eq 'serve' -and -not ($cliArgs | Where-Object { $_ -eq '--port' -or $_ -like '--port=*' })) {
    Import-Module (Join-Path $here 'FleetPort.psm1') -Force
    try {
        $fleet = Resolve-FleetPort -Tool 'windlass' -Version $version -NoListener
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }
    if ($fleet.Action -eq 'Reuse') {
        Write-Host "Windlass $($fleet.Existing.Version) is already running on http://127.0.0.1:$($fleet.Port)/v1 (pid $($fleet.Existing.Pid)). Stop it with src\Stop-Windlass.ps1 first."
        if ($cliArgs -contains '--open') {
            Write-Host 'Opening its browser UI.'
            Open-WindlassWindow -Url "http://127.0.0.1:$($fleet.Port)/"
        }
        exit 0
    }
    $cliArgs += @('--port', [string]$fleet.Port)
}

# serve --open: the browser UI opens when the server answers. The port is --port, or the one picked above.
if ($command -eq 'serve' -and ($cliArgs -contains '--open') -and -not $trayPlan.Tray) {
    $uiPort = 8777
    for ($i = 0; $i -lt $cliArgs.Count; $i++) {
        if ($cliArgs[$i] -eq '--port' -and ($i + 1) -lt $cliArgs.Count) { $uiPort = [int]$cliArgs[$i + 1] }
        elseif ($cliArgs[$i] -like '--port=*') { $uiPort = [int]($cliArgs[$i] -replace '^--port=', '') }
    }
    Start-BrowserWhenReady -Url "http://127.0.0.1:$uiPort/"
}

# Machine facts for the playbook (Playbook.cs): CPU, cores, RAM, battery, graphics. Read-only CIM queries; free RAM
# is read by the engine itself when it picks settings. Anything that fails is left out and the playbook uses defaults.
if (@('serve', 'run', 'probe', 'playbook') -contains $command) {
    $facts = @{}
    try {
        $cpu = @(Get-CimInstance Win32_Processor -ErrorAction Stop)
        $facts.cpu = ([string]$cpu[0].Name).Trim()
        $facts.physicalCores = [int](($cpu | Measure-Object -Property NumberOfCores -Sum).Sum)
        $facts.totalRam = [double](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory
    } catch { Write-Verbose "CPU/RAM facts unavailable: $($_.Exception.Message)" }
    try {
        $battery = @(Get-CimInstance Win32_Battery -ErrorAction Stop)
        $facts.hasBattery = $battery.Count -gt 0
        $facts.onBattery = @($battery | Where-Object { $_.BatteryStatus -eq 1 }).Count -gt 0
    } catch { Write-Verbose "Battery facts unavailable: $($_.Exception.Message)" }
    try { $facts.gpus = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { [string]$_.Name }) }
    catch { Write-Verbose "Graphics facts unavailable: $($_.Exception.Message)" }
    $env:WINDLASS_MACHINE = $facts | ConvertTo-Json -Compress
}

$cp = New-Object System.CodeDom.Compiler.CompilerParameters
$cp.GenerateInMemory = $true
$cp.CompilerOptions = '/unsafe /optimize+'
foreach ($r in @('System.dll', 'System.Core.dll')) { [void]$cp.ReferencedAssemblies.Add($r) }
$paths = @(Get-ChildItem -LiteralPath $here -Filter *.cs -File | ForEach-Object { $_.FullName })
Add-Type -Path $paths -CompilerParameters $cp

if ($command -eq 'check') {
    Write-Host "Windlass $version`: locality checks passed and the C# compiled."
    exit 0
}
if ($trayPlan.Tray) {
    exit (Start-WindlassTray -CliArgs ([string[]]$cliArgs) -Root $root -ScriptPath $PSCommandPath -Version $version -IconPath (Join-Path $root "assets\brand\tray.ico"))
}
exit ([Windlass.Program]::Run([string[]]$cliArgs, $root, $PSCommandPath, $version))
