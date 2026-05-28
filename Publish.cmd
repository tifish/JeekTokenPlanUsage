@echo off
setlocal

set PROJECT_NAME=JeekTokenPlanUsage

rem Stop the running app so publish can replace locked files.
taskkill /f /im "%PROJECT_NAME%.exe" >nul 2>nul

if exist "bin" (
    rem Clean bin\ except AutoUpdate.ps1, which is committed to the repo.
    powershell -NoProfile -Command "Get-ChildItem 'bin' -File | Where-Object { $_.Name -ine 'AutoUpdate.ps1' } | Remove-Item -Force -ErrorAction SilentlyContinue"
    for /d %%d in ("bin\*") do rd /s /q "%%d"
)

dotnet publish --configuration Release "%PROJECT_NAME%.csproj"
if errorlevel 1 exit /b %errorlevel%

if exist "bin\runtimes" rd /s /q "bin\runtimes"
del /s /q "bin\*.pdb" >nul 2>nul

if exist "Deploy.cmd" call "Deploy.cmd"

endlocal
