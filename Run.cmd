@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Build.ps1" -Configuration Debug -Launch
exit /b %errorlevel%
