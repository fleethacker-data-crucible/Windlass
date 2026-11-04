# Windlass notification-area icon. Windows PowerShell 5.1 only. ASCII only. Dot-sourced by Start-Windlass.ps1.
# While `serve --open` runs, a tray icon keeps Windlass visible and alive: the engine runs on a background thread
# (Windlass.TrayHost, src\TrayHost.cs) and this thread runs the Windows message loop. Closing the browser window or tab
# does not stop the engine or unload the model, because Fairlead and other programs use the local API. "Quit Windlass"
# on the icon stops the engine and removes the icon. WinForms NotifyIcon only; no extra DLLs, no installer.

# The browser that shows Windlass as an app window (Edge, then Chrome, as the Fleet UI standard does). $null when
# neither is installed. -Candidates is for tests.
function Get-WindlassBrowser {
    param([string[]]$Candidates)
    if (-not $Candidates) {
        $Candidates = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
            (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
            (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
            (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe')
        )
    }
    foreach ($c in $Candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

# Opens Windlass's UI in an Edge/Chrome --app window, or in the default browser when neither is installed.
function Open-WindlassWindow {
    param([Parameter(Mandatory)][string]$Url, [string]$Browser)
    if (-not $Browser) { $Browser = Get-WindlassBrowser }
    if ($Browser) { Start-Process -FilePath $Browser -ArgumentList @("--app=$Url") }
    else { Start-Process $Url }
}

# Decides whether this start shows the icon and what the engine is told. Returns an object: Tray (bool) and Args (the
# arguments for the engine: --tray added when the icon is shown). The icon is for `serve --open` at a desktop session;
# --no-tray, a service session or a serve without --open keep the plain console behaviour.
function Get-WindlassTrayPlan {
    param([string[]]$CliArgs, [bool]$Interactive = [Environment]::UserInteractive)
    $list = @($CliArgs)
    $serve = ($list.Count -gt 0 -and $list[0] -eq 'serve')
    $tray = $serve -and $Interactive -and ($list -contains '--open') -and -not ($list -contains '--no-tray') -and -not ($list -contains '--no-ui')
    if ($tray -and -not ($list -contains '--tray')) { $list += '--tray' }
    return [pscustomobject]@{ Tray = [bool]$tray; Args = [string[]]$list }
}

# The text of the icon's tooltip: a NotifyIcon allows 63 characters.
function Get-WindlassTrayTip {
    param([string]$Status)
    $t = 'Windlass - ' + $Status
    if ($t.Length -gt 63) { $t = $t.Substring(0, 60) + '...' }
    return $t
}

# Shows the icon and runs the engine until it stops. Returns the engine's exit code.
function Start-WindlassTray {
    param(
        [Parameter(Mandatory)][string[]]$CliArgs,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$Version,
        [string]$IconPath
    )
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    [Windlass.TrayHost]::Start($CliArgs, $Root, $ScriptPath, $Version)

    $script:trayOpened = $false
    $script:trayQuitting = $false

    $icon = New-Object System.Windows.Forms.NotifyIcon
    if ($IconPath -and (Test-Path -LiteralPath $IconPath)) {
        $icon.Icon = New-Object System.Drawing.Icon($IconPath, [System.Windows.Forms.SystemInformation]::SmallIconSize)
    } else {
        $icon.Icon = [System.Drawing.SystemIcons]::Application
    }
    $icon.Text = Get-WindlassTrayTip 'starting'

    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $open = New-Object System.Windows.Forms.ToolStripMenuItem '&Open Windlass'
    $open.Font = New-Object System.Drawing.Font($open.Font, [System.Drawing.FontStyle]::Bold)
    $status = New-Object System.Windows.Forms.ToolStripMenuItem 'Windlass is starting'
    $status.Enabled = $false
    $quit = New-Object System.Windows.Forms.ToolStripMenuItem '&Quit Windlass'
    [void]$menu.Items.Add($open)
    [void]$menu.Items.Add($status)
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    [void]$menu.Items.Add($quit)
    $icon.ContextMenuStrip = $menu

    $openWindow = {
        if ([Windlass.TrayHost]::Listening) {
            Open-WindlassWindow -Url ('http://127.0.0.1:' + [Windlass.TrayHost]::Port + '/')
        }
    }
    $open.add_Click($openWindow)
    $icon.add_DoubleClick($openWindow)

    $quit.add_Click({
        $answer = [System.Windows.Forms.MessageBox]::Show(
            'Quit Windlass? Programs that use it (such as Fairlead) will stop working.',
            'Windlass', [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question, [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
        if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) {
            $script:trayQuitting = $true
            $icon.Text = Get-WindlassTrayTip 'stopping'
            [void][Windlass.TrayHost]::Quit(30000)
        }
    }.GetNewClosure())

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 1000
    $timer.add_Tick({
        if (-not [Windlass.TrayHost]::Running) {
            $timer.Stop()
            [System.Windows.Forms.Application]::ExitThread()
            return
        }
        $line = [Windlass.TrayHost]::StatusLine()
        $status.Text = $line
        $icon.Text = Get-WindlassTrayTip $line
        # The first time the server answers, open the UI; the icon is what keeps the engine alive from here on.
        if (-not $script:trayOpened -and [Windlass.TrayHost]::Listening) {
            $script:trayOpened = $true
            & $openWindow
        }
    }.GetNewClosure())

    $icon.Visible = $true
    $timer.Start()
    try {
        [System.Windows.Forms.Application]::Run()
    } finally {
        $timer.Stop()
        $icon.Visible = $false
        $icon.Dispose()
        $menu.Dispose()
        if ([Windlass.TrayHost]::Running) { [void][Windlass.TrayHost]::Quit(30000) }
    }
    $code = [Windlass.TrayHost]::ExitCode
    if ($code -lt 0) { return 0 }
    return $code
}
