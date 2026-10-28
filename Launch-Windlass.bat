@echo off
rem Windlass console launcher. The console shows the server log; Ctrl+C or src\Stop-Windlass.ps1 stops it.
rem   Launch-Windlass.bat serve --model D:\models\model.gguf      (Launch-Windlass.bat help lists every option)
rem Started from PowerShell 7, powershell.exe would inherit 7's module paths and load the wrong Microsoft.PowerShell.Utility
rem before Start-Windlass.ps1 runs. With PSModulePath unset, Windows PowerShell 5.1 builds its own default.
setlocal
set "PSModulePath="
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0src\Start-Windlass.ps1" %*
if errorlevel 1 pause
