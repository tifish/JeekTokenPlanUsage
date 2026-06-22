@echo off
setlocal

set PROJECT_NAME=JeekTokenPlanUsage

rem Stop the running app so publish can replace locked files.
taskkill /f /im "%PROJECT_NAME%.exe" >nul 2>nul

if exist "bin" (
    rem Clean bin\ except the files committed to the repo (updater + setup scripts).
    powershell -NoProfile -Command "$keep='AutoUpdate.ps1','Setup.cmd','dotnet-install.ps1'; Get-ChildItem 'bin' -File | Where-Object { $keep -inotcontains $_.Name } | Remove-Item -Force -ErrorAction SilentlyContinue"
    for /d %%d in ("bin\*") do rd /s /q "%%d"
)

dotnet publish --configuration Release "%PROJECT_NAME%\%PROJECT_NAME%.csproj"
if errorlevel 1 exit /b %errorlevel%

if exist "bin\runtimes" rd /s /q "bin\runtimes"
del /s /q "bin\*.pdb" >nul 2>nul

if exist "Deploy.cmd" call "Deploy.cmd"

endlocal
