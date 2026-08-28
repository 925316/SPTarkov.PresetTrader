@echo off
setlocal

cd /d "%~dp0"

set "CONFIG=Release"
set "SLN=SPTarkov.PresetTrader.sln"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet not found in PATH
    pause
    exit /b 1
)

echo Building %SLN% (%CONFIG%)...
dotnet build "%SLN%" -c %CONFIG%
if errorlevel 1 (
    echo.
    echo [FAILED] Build failed, see errors above.
    pause
    exit /b 1
)

echo.
echo [DONE] Output: Build\%CONFIG%\SPT_Runtime\user\mods\PresetTrader
echo Copy the SPT_Runtime folder into your server install folder.
pause
