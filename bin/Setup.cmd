@echo off
setlocal

(fsutil dirty query %systemdrive% 1>nul 2>nul) || (echo Start-Process $env:ComSpec '/s /c "cd /d "%cd%" && "%~f0" %*"' -Verb RunAs -PassThru ^| Wait-Process> "%temp%\getadmin.ps1") && (powershell -ExecutionPolicy Bypass -File "%temp%\getadmin.ps1") && (exit /b)

echo Installing .NET...
powershell.exe -ExecutionPolicy Bypass -File "%~dp0dotnet-install.ps1" -Channel 10.0 -Runtime windowsdesktop -Architecture x64

echo Starting application...
start "" "%~dp0JeekTokenPlanUsage.exe"

endlocal
