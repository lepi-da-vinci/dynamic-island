@echo off
set "BIN_DIR=%~dp0src\WindowsDynamicIsland\bin\Release\net10.0-windows10.0.19041.0"
if exist "%BIN_DIR%\WindowsDynamicIsland.exe" (
    cd /d "%BIN_DIR%"
    start "" "WindowsDynamicIsland.exe"
) else (
    cd /d "%~dp0"
    dotnet run --project src/WindowsDynamicIsland/WindowsDynamicIsland.csproj -c Release
)
