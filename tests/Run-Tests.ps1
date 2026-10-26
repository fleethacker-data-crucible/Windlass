# Runs the Windlass test suite. Windows PowerShell 5.1 only. ASCII only.
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1 [-Filter <part of a test name>]
# Compiles src\*.cs and tests\*.cs in memory (C# 5, like Start-Windlass.ps1) and runs Windlass.Tests.Runner.
# Prints one PASS/FAIL line per test; the exit code is the number of failures. Needs no model and no network:
# the engine tests build tiny GGUF models and compare them with tests\fixtures\llama-cpp-reference.txt, and the API
# tests use loopback port 18777 (the CI range in edge-ops PORTS.md).
param([string]$Filter = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($PSVersionTable.PSVersion.Major -ne 5) { Write-Host "Run with Windows PowerShell 5.1 (powershell.exe), not $($PSVersionTable.PSVersion)."; exit 1 }
$env:TEMP = Join-Path $root 'temp\sys'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null

$cp = New-Object System.CodeDom.Compiler.CompilerParameters
$cp.GenerateInMemory = $true
$cp.CompilerOptions = '/unsafe /optimize+'
foreach ($r in @('System.dll', 'System.Core.dll')) { [void]$cp.ReferencedAssemblies.Add($r) }
$paths = @(Get-ChildItem -LiteralPath (Join-Path $root 'src'), $PSScriptRoot -Filter *.cs -File | ForEach-Object { $_.FullName })
Add-Type -Path $paths -CompilerParameters $cp
$f = if ($Filter) { $Filter } else { $null }
exit ([Windlass.Tests.Runner]::Run($root, $f))
