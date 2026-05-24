@echo off
setlocal

set PROJECT_NAME=JeekTokenPlanUsage

dotnet publish --configuration Release "%PROJECT_NAME%.csproj"
if errorlevel 1 exit /b %errorlevel%

if exist "%PROJECT_NAME%.7z" del "%PROJECT_NAME%.7z"

pushd bin
7z a -r "..\%PROJECT_NAME%.7z" *
set PACK_ERR=%errorlevel%
popd
if not "%PACK_ERR%"=="0" exit /b %PACK_ERR%

echo.
echo Published: %PROJECT_NAME%.7z
endlocal
