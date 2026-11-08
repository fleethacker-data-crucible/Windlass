# Pester 5 entry point (edge-ops CI: Invoke-Pester on tests\). Windows PowerShell 5.1. ASCII only.
# The suite itself is C# (tests\Tests.cs); it runs once in a child powershell.exe (Add-Type types cannot be reloaded
# in one session) and each C# test becomes one Pester test.
BeforeDiscovery {
    $script:runner = Join-Path $PSScriptRoot 'Run-Tests.ps1'
    $script:output = @(& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $script:runner 2>&1 | ForEach-Object { [string]$_ })
    $script:cases = New-Object System.Collections.Generic.List[hashtable]
    for ($i = 0; $i -lt $script:output.Count; $i++) {
        $line = $script:output[$i]
        if ($line -match '^(PASS|FAIL)  (.+)$') {
            $detail = New-Object System.Collections.Generic.List[string]
            for ($j = $i + 1; $j -lt $script:output.Count -and $script:output[$j] -notmatch '^(PASS|FAIL)  ' -and $script:output[$j] -notmatch '^\d+ passed'; $j++) { $detail.Add($script:output[$j]) }
            $script:cases.Add(@{ Name = $Matches[2]; Passed = ($Matches[1] -eq 'PASS'); Detail = ($detail -join "`n") })
        }
    }
    if ($script:cases.Count -eq 0) { $script:cases.Add(@{ Name = 'the C# suite ran'; Passed = $false; Detail = ($script:output -join "`n") }) }
}

Describe 'Windlass' {
    It '<Name>' -ForEach $script:cases {
        $Passed | Should -BeTrue -Because $Detail
    }
}

Describe 'Start-Windlass.ps1' {
    It 'passes its locality checks and compiles' {
        $start = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Start-Windlass.ps1'
        $out = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $start check 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($out -join "`n")
    }
}

Describe 'Tray.ps1' {
    BeforeAll {
        $script:srcDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
        . (Join-Path $script:srcDir 'Tray.ps1')
    }
    It 'is ASCII only and parses' {
        $path = Join-Path $script:srcDir 'Tray.ps1'
        $bytes = [IO.File]::ReadAllBytes($path)
        @($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors)
        @($errors).Count | Should -Be 0
    }
    It 'shows the icon for an interactive serve --open and tells the engine --tray' {
        $plan = Get-WindlassTrayPlan -CliArgs @('serve', '--open') -Interactive $true
        $plan.Tray | Should -BeTrue
        $plan.Args | Should -Contain '--tray'
        $plan.Args | Should -Contain '--open'
    }
    It 'does not add --tray twice' {
        $plan = Get-WindlassTrayPlan -CliArgs @('serve', '--open', '--tray') -Interactive $true
        @($plan.Args | Where-Object { $_ -eq '--tray' }).Count | Should -Be 1
    }
    It 'keeps the plain console behaviour <Name>' -ForEach @(
        @{ Name = 'for serve without --open'; CliArgs = @('serve', '--model', 'a.gguf'); Interactive = $true },
        @{ Name = 'with --no-tray'; CliArgs = @('serve', '--open', '--no-tray'); Interactive = $true },
        @{ Name = 'with --no-ui'; CliArgs = @('serve', '--open', '--no-ui'); Interactive = $true },
        @{ Name = 'outside a desktop session'; CliArgs = @('serve', '--open'); Interactive = $false },
        @{ Name = 'for other commands'; CliArgs = @('info', '--model', 'a.gguf', '--open'); Interactive = $true }
    ) {
        $plan = Get-WindlassTrayPlan -CliArgs $CliArgs -Interactive $Interactive
        $plan.Tray | Should -BeFalse
        $plan.Args | Should -Not -Contain '--tray'
    }
    It 'keeps the tooltip within the 63 characters NotifyIcon allows' {
        (Get-WindlassTrayTip 'Running on 127.0.0.1:8777, model: ' + ('x' * 200)).Length | Should -BeLessOrEqual 63
        Get-WindlassTrayTip 'starting' | Should -Be 'Windlass - starting'
    }
    It 'picks the first installed browser for the app window, else none' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('windlass-tray-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $tmp | Out-Null
        try {
            $edge = Join-Path $tmp 'msedge.exe'; $chrome = Join-Path $tmp 'chrome.exe'
            Set-Content -LiteralPath $edge -Value 'x'; Set-Content -LiteralPath $chrome -Value 'x'
            Get-WindlassBrowser -Candidates @((Join-Path $tmp 'missing.exe'), $edge, $chrome) | Should -Be $edge
            Get-WindlassBrowser -Candidates @((Join-Path $tmp 'missing.exe'), $chrome) | Should -Be $chrome
            Get-WindlassBrowser -Candidates @((Join-Path $tmp 'missing.exe')) | Should -BeNullOrEmpty
        } finally { Remove-Item -LiteralPath $tmp -Recurse -Force }
    }
    It 'ships tray.ico with the 16, 20, 24 and 32 pixel frames' {
        $ico = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\brand\tray.ico'
        $b = [IO.File]::ReadAllBytes($ico)
        [BitConverter]::ToUInt16($b, 2) | Should -Be 1
        $n = [BitConverter]::ToUInt16($b, 4)
        $sizes = @(for ($i = 0; $i -lt $n; $i++) { [int]$b[6 + 16 * $i] })
        foreach ($s in @(16, 20, 24, 32)) { $sizes | Should -Contain $s }
    }
    It 'asks the FleetHacker tray kit for the Windlass icon, with tray.ico as the fallback' {
        Mock Get-FleetTrayIcon { $null }
        $ico = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\brand\tray.ico'
        [void](Get-WindlassTrayIcon -IconPath $ico)
        Should -Invoke Get-FleetTrayIcon -Times 1 -Exactly -ParameterFilter { $App -eq 'windlass' -and $FallbackPath -eq $ico }
        $body = (Get-Command Start-WindlassTray).Definition
        $body | Should -Match 'Get-WindlassTrayIcon -IconPath \$IconPath'
        $body | Should -Not -Match 'SmallIconSize'
    }
    It 'draws the icon at the tray size' {
        $i = Get-WindlassTrayIcon -IconPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\brand\tray.ico')
        try { $i.Width | Should -Be (Get-FleetTraySize) } finally { $i.Dispose() }
    }
    It 'keeps the vendored kit ASCII, recorded and out of the engine build' {
        $kit = Join-Path $script:srcDir 'vendor\fleet-tray'
        foreach ($f in @('FleetTray.ps1', 'FleetTrayArt.cs')) {
            @([IO.File]::ReadAllBytes((Join-Path $kit $f)) | Where-Object { $_ -gt 127 }).Count | Should -Be 0 -Because $f
        }
        Get-Content -LiteralPath (Join-Path $kit 'README.md') -Raw | Should -Match 'ui-kit/tray v1\.0\.0, commit c2172f7'
        # Start-Windlass.ps1 and Run-Tests.ps1 compile src\*.cs (not recursive) with the BCL only; the kit needs System.Drawing.
        @(Get-ChildItem -LiteralPath $script:srcDir -Filter *.cs -File | Where-Object { $_.Name -eq 'FleetTrayArt.cs' }) | Should -BeNullOrEmpty
    }
    It 'is wired into Start-Windlass.ps1 and the C# stays free of banned calls' {
        $start = Get-Content -LiteralPath (Join-Path $script:srcDir 'Start-Windlass.ps1') -Raw
        $start | Should -Match 'Tray\.ps1'
        $start | Should -Match 'Start-WindlassTray'
        $cs = Get-Content -LiteralPath (Join-Path $script:srcDir 'TrayHost.cs') -Raw
        $cs | Should -Not -Match 'Process\.Start|DllImport|WebRequest|Socket'
    }
}
