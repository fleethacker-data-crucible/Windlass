# FleetHacker shared PSScriptAnalyzer settings (Windows PowerShell 5.1 on Windows 10/11 desktop, profile win-48_x64_10.0.17763.0_5.1.17763.316_x64_4.0.30319.42000_framework).
# Usage: Invoke-ScriptAnalyzer -Path . -Recurse -Settings .\PSScriptAnalyzerSettings.psd1
@{
    Severity     = @('Error', 'Warning')
    IncludeDefaultRules = $true
    ExcludeRules = @(
        'PSAvoidUsingWriteHost'                        # console tools print status to the host on purpose
        # Style rules the reference repos (phoenix-lai, data-noodler) do not follow by design:
        'PSAvoidUsingEmptyCatchBlock'                  # best-effort `catch { }` around logging/cleanup
        'PSUseShouldProcessForStateChangingFunctions'  # internal helpers, not public cmdlets
        'PSUseSingularNouns'
        'PSUseApprovedVerbs'                           # e.g. Load-DataEngine
    )
    Rules = @{
        PSUseCompatibleSyntax = @{
            Enable         = $true
            TargetVersions = @('5.1')
        }
        PSUseCompatibleCommands = @{
            Enable         = $true
            # Clear-Host is a built-in function on 5.1 but missing from the profile data.
            IgnoreCommands = @('Clear-Host')
            TargetProfiles = @('win-48_x64_10.0.17763.0_5.1.17763.316_x64_4.0.30319.42000_framework')
        }
        # Legacy cmdlet-compat rule keyed by the classic 5.1 profile id.
        PSUseCompatibleCmdlets = @{
            compatibility = @('desktop-5.1.14393.206-windows')
        }
        # PSUseCompatibleTypes is intentionally off: the profiles only list types loaded by default,
        # so every WinForms/Drawing type loaded via Add-Type -AssemblyName is a false positive.
        PSAvoidUsingCmdletAliases       = @{ Enable = $true }
        PSUseBOMForUnicodeEncodedFile   = @{ Enable = $true }
    }
}
