@echo off
setlocal

set PROJECT_NAME=JeekTokenPlanUsage

if exist "bin" rd /s /q "bin"

dotnet publish --configuration Release "%PROJECT_NAME%.csproj"
if errorlevel 1 exit /b %errorlevel%

if exist "bin\runtimes" rd /s /q "bin\runtimes"

endlocal
