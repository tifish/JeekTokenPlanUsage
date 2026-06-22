@echo off
setlocal

set PROJECT_NAME=JeekTokenPlanUsage

rem Stop any running instance, rebuild, then launch for testing.
taskkill /f /im "%PROJECT_NAME%.exe" >nul 2>nul

dotnet build --configuration Debug "%PROJECT_NAME%\%PROJECT_NAME%.csproj"
if errorlevel 1 exit /b %errorlevel%

start "" "bin\%PROJECT_NAME%.exe"

endlocal
