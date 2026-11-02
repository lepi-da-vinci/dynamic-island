@echo off
title Windows Dynamic Island - Aktivasi Kontrol Smart Charge
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Meminta izin Administrator untuk mendaftarkan tugas sistem...
    powershell -NoProfile -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~f0\"\"' -Verb runas"
    exit /b
)
cd /d "%~dp0"
echo ======================================================================
echo    Windows Dynamic Island - Aktivasi Kontrol Hardware Smart Charge
echo ======================================================================
echo.
echo Mendaftarkan tugas sistem kontrol baterai ACPI WMI (80%% dan 100%%)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup_battery_tasks.ps1"
echo.
echo ======================================================================
echo Aktivasi selesai! Mulai sekarang, tombol 80%% dan 100%% di Dynamic Island
echo akan langsung mengontrol arus pengisian baterai secara instan & senyap.
echo ======================================================================
echo.
pause
