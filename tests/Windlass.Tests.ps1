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
