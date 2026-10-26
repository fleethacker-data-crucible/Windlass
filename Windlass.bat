@echo off
rem Windlass console launcher. The console shows the server log; Ctrl+C or src\Stop-Windlass.ps1 stops it.
rem   Windlass.bat serve --model D:\models\model.gguf      (Windlass.bat help lists every option)
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0src\Start-Windlass.ps1" %*
if errorlevel 1 pause
