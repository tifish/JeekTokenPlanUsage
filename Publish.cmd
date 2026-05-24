@echo off
setlocal

set PROJECT_NAME=JeekTokenPlanUsage

rem Stop the running app so publish can replace locked files.
taskkill /f /im "%PROJECT_NAME%.exe" >nul 2>nul

if exist "bin" rd /s /q "bin"

dotnet publish --configuration Release "%PROJECT_NAME%.csproj"
if errorlevel 1 exit /b %errorlevel%

if exist "bin\runtimes" rd /s /q "bin\runtimes"
del /s /q "bin\*.pdb" >nul 2>nul

endlocal
