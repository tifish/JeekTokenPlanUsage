@echo off
setlocal

set PROJECT_NAME=JeekTokenPlanUsage

rem Stop the running app so the build can replace locked files.
taskkill /f /im "%PROJECT_NAME%.exe" >nul 2>nul

dotnet build --configuration Debug "%PROJECT_NAME%\%PROJECT_NAME%.csproj"
if errorlevel 1 exit /b %errorlevel%

endlocal
